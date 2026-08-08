using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
using VRage.GameServices;

namespace Shared.Transport;

// A raw-UDP implementation of the engine's peer-to-peer transport, used in
// place of Steam P2P so clients and dedicated servers can talk without Steam.
//
// The whole engine addresses peers by a single ulong (historically a Steam
// ID). Everything above IMyPeer2Peer is transport-agnostic, so the only job
// here is: map each ulong peer id to a UDP endpoint, deliver opaque byte[]
// payloads per channel, and honour the reliable/unreliable delivery hint.
//
// Reliability, ordering and fragmentation are provided by LiteNetLib. The
// same class serves both roles: a server binds and accepts many peers; a
// client connects to exactly one server. SE frames its own packets (magic
// byte, CRC, splitting) above this layer, so payloads are passed through
// untouched apart from a one-byte channel prefix.
public sealed class UdpPeer2Peer : IMyPeer2Peer, INetEventListener
{
    private const byte NodeLinkChannels = 16;

    // One received datagram waiting to be handed to the engine.
    private readonly struct Incoming
    {
        public readonly ulong Sender;
        public readonly byte[] Data;

        public Incoming(ulong sender, byte[] data)
        {
            Sender = sender;
            Data = data;
        }
    }

    private readonly NetManager m_manager;
    private readonly bool m_isServer;

    // Per-channel receive queues, keyed by the engine channel number. The
    // engine drains these from its network thread via IsPacketAvailable /
    // ReadPacket, so they must be thread-safe against the poll thread.
    private readonly ConcurrentDictionary<int, ConcurrentQueue<Incoming>> m_receiveQueues = new();

    // Bidirectional ulong <-> peer mapping. A client has a single entry for
    // the server; a server has one entry per connected client.
    private readonly ConcurrentDictionary<ulong, NetPeer> m_peersById = new();
    private readonly NodeLinkState<NetPeer> m_nodeLink = new();

    private Thread m_pollThread;
    private volatile bool m_running;

    // The server endpoint a client connects to (client role only).
    private IPEndPoint m_serverEndpoint;
    private ulong m_serverId;

    // Signalled once the client's single peer reaches Connected state, so the
    // join can wait for the link before sending the reliable handshake.
    private readonly ManualResetEventSlim m_clientConnected = new(false);

    public event Action<ulong> SessionRequest;
    public event Action<ulong, string> ConnectionFailed;
    public event Action<byte[]> NodeLinkMessageReceived;
    public event Action<bool> NodeLinkConnectionChanged;
    public event Action<ulong> NodeLinkClientDetached;

    public Func<ulong, byte[], bool> NodeLinkAttachValidator { get; set; }
    public bool IsNodeLinkConnected => m_nodeLink.TryGetLink(out NetPeer peer)
        && peer.ConnectionState == ConnectionState.Connected;

    public UdpPeer2Peer(bool isServer)
    {
        m_isServer = isServer;
        m_manager = new NetManager(this)
        {
            ChannelsCount = NodeLinkChannels,
            UnsyncedEvents = false,
            AutoRecycle = true,
            // A test network can push a lot of small reliable packets during
            // world streaming; keep the disconnect timeout generous so a busy
            // frame does not look like a dropped peer.
            DisconnectTimeout = 30000,
            ReconnectDelay = 500,
            MaxConnectAttempts = 20,
        };
    }

    // --- IMyPeer2Peer scalar/metadata members -----------------------------

    public int MTUSize => 1200;
    public int NetworkUpdateLatency => 2;
    public string DetailedStats => "";
    public IEnumerable<(string Name, double Value)> Stats { get { yield break; } }
    public IEnumerable<(string Client, IEnumerable<(string Stat, double Value)> Stats)> ClientStats
    {
        get { yield break; }
    }

    // SetServer is called by the engine after construction; the role is
    // already fixed here, so this only sanity-checks it.
    public void SetServer(bool server)
    {
        if (server != m_isServer)
            DirectTransport.LogError(
                $"UdpPeer2Peer role mismatch: constructed isServer={m_isServer}, SetServer({server})");
    }

    public void BeginFrameProcessing() { }
    public void EndFrameProcessing() { }
    public void SignalServerJoined() { }

    // --- Lifecycle --------------------------------------------------------

    // Server: bind the UDP socket and start accepting peers.
    public void StartServer(IPEndPoint bind)
    {
        if (m_running)
            return;

        if (!m_manager.Start(bind.Address, IPAddress.IPv6Any, bind.Port))
            throw new InvalidOperationException($"Failed to bind UDP transport to {bind}");

        StartPolling();
        DirectTransport.Log($"UDP transport listening on {bind}");
    }

    // Client: open the local socket (ephemeral port) but do not connect yet.
    public void StartClient()
    {
        if (m_running)
            return;

        if (!m_manager.Start())
            throw new InvalidOperationException("Failed to start UDP transport (client)");

        StartPolling();
        DirectTransport.Log("UDP transport started (client)");
    }

    // Client: connect to the server and block until the link is up (or the
    // timeout elapses). Returns true on success. The server id is the ulong
    // the engine will use to address the server.
    public bool ConnectToServer(IPEndPoint endpoint, ulong serverId, ulong localId, string localName, int timeoutMs)
    {
        m_serverEndpoint = endpoint;
        m_serverId = serverId;
        m_clientConnected.Reset();

        // Announce our peer id as a ulong; the server reads it back with
        // GetULong() in OnConnectionRequest. These two must use the same wire
        // type: if the client wrote a string here the server's GetULong() would
        // decode the string's bytes into a garbage id, register the peer under
        // it, and later fail to route replication addressed to the real id.
        var writer = new NetDataWriter();
        writer.Put(DirectTransport.ProtocolKey);
        writer.Put(localId);
        // A no-Steam client has no persona the server can look up, so it announces the name it wants to
        // be known by. A plain direct-transport server ignores the trailing field; the Gateway names the
        // player and the identity from it instead of from the numeric client id.
        if (!string.IsNullOrEmpty(localName))
            writer.Put(localName);
        m_manager.Connect(endpoint.Address.ToString(), endpoint.Port, writer);

        DirectTransport.Log($"Connecting to server {endpoint} as {localId}");
        return m_clientConnected.Wait(timeoutMs);
    }

    private void StartPolling()
    {
        m_running = true;
        m_pollThread = new Thread(PollLoop)
        {
            Name = "DirectTransport-Poll",
            IsBackground = true,
        };
        m_pollThread.Start();
    }

    private long m_nextLiveness;

    private void PollLoop()
    {
        while (m_running)
        {
            try
            {
                m_manager.PollEvents();
                // Periodic per-peer liveness: diagnoses one-way silence (a remote watchdog sees
                // nothing from us while we still receive) versus mutual socket death.
                long now = Environment.TickCount64;
                if (now >= m_nextLiveness)
                {
                    m_nextLiveness = now + 10000;
                    // Console, not DirectTransport.Log: the SE game log writer can stall minutes into
                    // a session, and this diagnostic must survive to the end of the run.
                    foreach (var pair in m_peersById)
                        Console.WriteLine($"[DirectTransport] UDP liveness peer={pair.Key} state={pair.Value.ConnectionState}"
                            + $" sinceLastMs={pair.Value.TimeSinceLastPacket:F0} ping={pair.Value.Ping}");
                    if (m_peersById.IsEmpty)
                        Console.WriteLine("[DirectTransport] UDP liveness: no peers");
                }
            }
            catch (Exception e)
            {
                DirectTransport.LogError("UDP transport poll error: " + e);
            }

            Thread.Sleep(1);
        }
    }

    public void Stop()
    {
        m_running = false;
        try { m_pollThread?.Join(500); } catch { }
        try { m_manager.Stop(); } catch { }
        m_peersById.Clear();
    }

    // --- Send / receive ---------------------------------------------------

    public bool SendPacket(ulong remoteUser, byte[] data, int byteCount, MyP2PMessageEnum msgType, int channel)
    {
        if (m_nodeLink.TryGetPeer(remoteUser, out NetPeer linkPeer))
        {
            bool linkReliable = msgType == MyP2PMessageEnum.Reliable
                || msgType == MyP2PMessageEnum.ReliableWithBuffering;
            DeliveryMethod linkDelivery = linkReliable
                ? DeliveryMethod.ReliableOrdered
                : DeliveryMethod.Unreliable;
            byte[] envelope = NodeLinkCodec.Write(
                NodeLinkMessage.Relay,
                remoteUser,
                checked((byte)channel),
                data,
                byteCount);
            if (!linkReliable && envelope.Length > linkPeer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable))
                linkDelivery = DeliveryMethod.ReliableUnordered;

            var relay = new PendingRelay(envelope, LinkChannel(remoteUser), linkDelivery);
            if (!m_nodeLink.SendOrQueue(remoteUser, relay, out PendingRelay immediate))
                return false;
            if (immediate.Data != null)
                linkPeer.Send(immediate.Data, immediate.Channel, immediate.Delivery);
            return true;
        }

        if (!m_peersById.TryGetValue(remoteUser, out NetPeer peer) || peer.ConnectionState != ConnectionState.Connected)
            return false;

        bool reliable = msgType == MyP2PMessageEnum.Reliable || msgType == MyP2PMessageEnum.ReliableWithBuffering;
        DeliveryMethod delivery = reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable;

        // Prefix the engine channel so the receiver can re-bucket it. A single
        // reliable-ordered LiteNetLib stream preserves per-channel ordering as
        // a subsequence, which is all the engine requires.
        int total = byteCount + 1;

        // The engine sizes unreliable messages (state sync) up to its reported
        // MTUSize, which can exceed LiteNetLib's per-datagram limit for the
        // link's current MTU. LiteNetLib only fragments the reliable channelled
        // methods; Unreliable/Sequenced throw TooBigPacketException instead. So
        // promote any oversized unreliable packet to a fragmenting reliable-
        // unordered delivery (Steam's unreliable path fragments large payloads
        // too, so the engine legitimately hands us over-MTU unreliable sends).
        // ReliableUnordered keeps these off the ReliableOrdered stream, avoiding
        // head-of-line blocking of the ordered reliable traffic; the engine
        // orders state sync by timestamp regardless of arrival order.
        if (!reliable && total > peer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable))
            delivery = DeliveryMethod.ReliableUnordered;

        var writer = new NetDataWriter(true, total);
        writer.Put((byte)channel);
        writer.Put(data, 0, byteCount);
        peer.Send(writer, delivery);
        return true;
    }

    public bool IsPacketAvailable(out uint msgSize, int channel)
    {
        if (m_receiveQueues.TryGetValue(channel, out var queue) && queue.TryPeek(out Incoming next))
        {
            msgSize = (uint)next.Data.Length;
            return true;
        }

        msgSize = 0u;
        return false;
    }

    public bool ReadPacket(byte[] buffer, ref uint dataSize, out ulong remoteUser, int channel)
    {
        if (m_receiveQueues.TryGetValue(channel, out var queue) && queue.TryDequeue(out Incoming next))
        {
            int length = Math.Min(next.Data.Length, buffer.Length);
            Buffer.BlockCopy(next.Data, 0, buffer, 0, length);
            dataSize = (uint)length;
            remoteUser = next.Sender;
            return true;
        }

        dataSize = 0u;
        remoteUser = 0uL;
        return false;
    }

    public bool AcceptSession(ulong remotePeerId) =>
        m_nodeLink.Contains(remotePeerId) || m_peersById.ContainsKey(remotePeerId);

    public bool CloseSession(ulong remotePeerId)
    {
        if (m_nodeLink.TryGetPeer(remotePeerId, out NetPeer linkPeer))
        {
            linkPeer.Send(
                NodeLinkCodec.Write(NodeLinkMessage.Detach, remotePeerId),
                LinkChannel(remotePeerId),
                DeliveryMethod.ReliableOrdered);
            m_nodeLink.Detach(remotePeerId);
            NodeLinkClientDetached?.Invoke(remotePeerId);
            return true;
        }

        if (m_peersById.TryRemove(remotePeerId, out NetPeer peer))
        {
            try { peer.Disconnect(); } catch { }
            return true;
        }

        return false;
    }

    public bool GetSessionState(ulong remoteUser, ref MyP2PSessionState state)
    {
        bool connected = m_nodeLink.TryGetPeer(remoteUser, out NetPeer linkPeer)
            ? linkPeer.ConnectionState == ConnectionState.Connected
            : m_peersById.TryGetValue(remoteUser, out NetPeer peer)
                && peer.ConnectionState == ConnectionState.Connected;

        // Consumers (MyMultiplayerClientBase.OnTick) only read ConnectionActive
        // and UsingRelay; the remote IP/port fields are left at their defaults.
        state = default;
        state.ConnectionActive = connected;
        state.UsingRelay = false;
        return connected;
    }

    // --- INetEventListener ------------------------------------------------

    // Server side: authorise an incoming client and read its announced id.
    public void OnConnectionRequest(ConnectionRequest request)
    {
        if (!m_isServer)
        {
            request.Reject();
            return;
        }

        try
        {
            string key = request.Data.GetString();
            if (key != DirectTransport.ProtocolKey)
            {
                DirectTransport.LogError($"Rejected peer {request.RemoteEndPoint}: bad protocol key");
                request.Reject();
                return;
            }

            ulong clientId = request.Data.GetULong();
            if (clientId == NodeLinkCodec.NodeLinkId)
            {
                AcceptNodeLink(request);
                return;
            }

            if (m_nodeLink.Contains(clientId))
            {
                DirectTransport.LogError($"Rejected physical peer {clientId}: id is attached through Gateway");
                request.Reject();
                return;
            }

            NetPeer peer = request.Accept();
            peer.Tag = clientId;
            m_peersById[clientId] = peer;
            DirectTransport.Log($"Accepted client {clientId} from {request.RemoteEndPoint}");

            // Let the engine register the session; its handler calls
            // AcceptSession, which we already satisfy via the map above.
            SessionRequest?.Invoke(clientId);
        }
        catch (Exception e)
        {
            DirectTransport.LogError("OnConnectionRequest failed: " + e);
            request.Reject();
        }
    }

    public void OnPeerConnected(NetPeer peer)
    {
        if (m_isServer)
            return;

        // Client: the single peer is the server.
        peer.Tag = m_serverId;
        m_peersById[m_serverId] = peer;
        m_clientConnected.Set();
        DirectTransport.Log($"Connected to server {peer.Address} (id {m_serverId})");
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        bool wasNodeLink = m_nodeLink.IsLink(peer);
        ulong[] attachedClients = m_nodeLink.Disconnect(peer);
        if (wasNodeLink)
        {
            NodeLinkConnectionChanged?.Invoke(false);
            foreach (ulong clientId in attachedClients)
            {
                NodeLinkClientDetached?.Invoke(clientId);
                ConnectionFailed?.Invoke(clientId, disconnectInfo.Reason.ToString());
            }
            DirectTransport.Log($"Gateway node link disconnected: {disconnectInfo.Reason}");
            return;
        }

        ulong id = peer.Tag is ulong tag ? tag : (m_isServer ? 0uL : m_serverId);
        if (id != 0uL)
            m_peersById.TryRemove(id, out _);

        DirectTransport.Log($"Peer {id} disconnected: {disconnectInfo.Reason}");
        if (id != 0uL)
            ConnectionFailed?.Invoke(id, disconnectInfo.Reason.ToString());
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        if (m_nodeLink.IsLink(peer))
        {
            ReceiveNodeLink(peer, reader);
            return;
        }

        ulong sender = peer.Tag is ulong tag ? tag : (m_isServer ? 0uL : m_serverId);

        int seChannel = reader.GetByte();
        byte[] payload = reader.GetRemainingBytes();

        var queue = m_receiveQueues.GetOrAdd(seChannel, _ => new ConcurrentQueue<Incoming>());
        queue.Enqueue(new Incoming(sender, payload));
    }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) =>
        DirectTransport.LogError($"UDP socket error from {endPoint}: {socketError}");

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }

    public bool SendNodeLinkMessage(byte[] payload)
    {
        if (payload == null || payload.Length == 0
            || !m_nodeLink.TryGetLink(out NetPeer peer)
            || peer.ConnectionState != ConnectionState.Connected)
            return false;

        peer.Send(
            NodeLinkCodec.Write(NodeLinkMessage.Global, 0, payload: payload, payloadLength: payload.Length),
            0,
            DeliveryMethod.ReliableOrdered);
        return true;
    }

    private void AcceptNodeLink(ConnectionRequest request)
    {
        if (request.Data.AvailableBytes == 0
            || request.Data.GetString() != NodeLinkCodec.ProtocolKey
            || request.Data.AvailableBytes == 0
            || !NodeLinkAuth.Accepts(request.Data.GetString())
            || request.Data.AvailableBytes != 0)
        {
            DirectTransport.LogError($"Rejected Gateway node link from {request.RemoteEndPoint}: join token not accepted");
            request.Reject();
            return;
        }

        NetPeer peer = request.Accept();
        peer.Tag = NodeLinkPeerTag.Instance;
        if (!m_nodeLink.TryConnect(peer))
        {
            DirectTransport.LogError($"Rejected duplicate Gateway node link from {request.RemoteEndPoint}");
            peer.Disconnect();
            return;
        }

        DirectTransport.Log($"Accepted Gateway node link from {request.RemoteEndPoint}");
        NodeLinkConnectionChanged?.Invoke(true);
    }

    private void ReceiveNodeLink(NetPeer peer, NetPacketReader reader)
    {
        NodeLinkPacket packet;
        try
        {
            packet = NodeLinkCodec.Read(reader.GetRemainingBytes());
        }
        catch (Exception exception)
        {
            DirectTransport.LogError("Rejected malformed Gateway node-link packet: " + exception.Message);
            peer.Disconnect();
            return;
        }

        switch (packet.Kind)
        {
            case NodeLinkMessage.Attach:
                if (m_peersById.ContainsKey(packet.ClientId))
                {
                    RejectAttach(peer, packet.ClientId, "client id already belongs to a physical peer");
                    return;
                }
                if (NodeLinkAttachValidator == null
                    || !NodeLinkAttachValidator(packet.ClientId, packet.Payload))
                {
                    RejectAttach(peer, packet.ClientId, "no current World Authority player binding");
                    return;
                }

                bool attached = m_nodeLink.Attach(packet.ClientId);
                if (attached)
                {
                    DirectTransport.Log($"Accepted client {packet.ClientId} through Gateway node link");
                    SessionRequest?.Invoke(packet.ClientId);
                }
                if (attached || m_nodeLink.Contains(packet.ClientId))
                    peer.Send(
                        NodeLinkCodec.Write(NodeLinkMessage.AttachAck, packet.ClientId),
                        LinkChannel(packet.ClientId),
                        DeliveryMethod.ReliableOrdered);
                return;

            case NodeLinkMessage.Detach:
                if (m_nodeLink.Detach(packet.ClientId))
                {
                    NodeLinkClientDetached?.Invoke(packet.ClientId);
                    ConnectionFailed?.Invoke(packet.ClientId, "Gateway detached client");
                }
                peer.Send(
                    NodeLinkCodec.Write(NodeLinkMessage.DetachAck, packet.ClientId),
                    LinkChannel(packet.ClientId),
                    DeliveryMethod.ReliableOrdered);
                return;

            case NodeLinkMessage.Credit:
                foreach (PendingRelay pending in m_nodeLink.GrantCredit(packet.ClientId, packet.Credit))
                    peer.Send(pending.Data, pending.Channel, pending.Delivery);
                return;

            case NodeLinkMessage.AttachAck:
            case NodeLinkMessage.DetachAck:
                DirectTransport.LogError("Rejected directionally invalid node-link acknowledgement");
                peer.Disconnect();
                return;

            case NodeLinkMessage.Global:
                NodeLinkMessageReceived?.Invoke(packet.Payload);
                return;

            case NodeLinkMessage.Relay:
                if (m_nodeLink.Contains(packet.ClientId))
                {
                    var queue = m_receiveQueues.GetOrAdd(packet.Channel, _ => new ConcurrentQueue<Incoming>());
                    queue.Enqueue(new Incoming(packet.ClientId, packet.Payload));
                }
                return;
        }
    }

    private static byte LinkChannel(ulong clientId) => (byte)(clientId % NodeLinkChannels);

    private static void RejectAttach(NetPeer peer, ulong clientId, string reason)
    {
        DirectTransport.LogError($"Rejected Gateway client {clientId}: {reason}");
        peer.Send(
            NodeLinkCodec.Write(NodeLinkMessage.Detach, clientId),
            LinkChannel(clientId),
            DeliveryMethod.ReliableOrdered);
    }

    private sealed class NodeLinkPeerTag
    {
        public static readonly NodeLinkPeerTag Instance = new();
        private NodeLinkPeerTag() { }
    }
}
