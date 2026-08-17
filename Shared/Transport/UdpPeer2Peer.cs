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

    private Thread m_pollThread;
    private volatile bool m_running;

    // The server endpoint a client connects to (client role only).
    private IPEndPoint m_serverEndpoint;
    private ulong m_serverId;

    // Signalled once the client's single peer reaches Connected state, so the
    // join can wait for the link before sending the reliable handshake.
    private readonly ManualResetEventSlim m_clientConnected = new(false);

    // Engine events queued for delivery on the engine's update thread. The
    // engine assumes IMyPeer2Peer events arrive there: Steam raises them from
    // SteamAPI.RunCallbacks() inside MySteamService.Update() and EOS routes
    // them through InvokeOnMainThread. The handlers rely on that -
    // MyMultiplayerClient.Peer2Peer_ConnectionFailed tears down the whole
    // session, GUI screens included, synchronously. Raising the events
    // straight from the LiteNetLib poll thread crashed the client with
    // "Thread unsafe access to GUI screens" followed by a render-thread NRE
    // whenever the server dropped the link. DispatchEngineEvents drains this
    // queue; DirectTransport hooks it to IMyGameService.OnUpdate, which fires
    // from MyGameService.Update() on the engine's update thread.
    private readonly ConcurrentQueue<Action> m_engineEvents = new();

    public event Action<ulong> SessionRequest;
    public event Action<ulong, string> ConnectionFailed;

    // Drain queued SessionRequest / ConnectionFailed events. Must be called
    // on the engine's update thread; see m_engineEvents.
    public void DispatchEngineEvents()
    {
        while (m_engineEvents.TryDequeue(out Action raise))
        {
            try
            {
                raise();
            }
            catch (Exception e)
            {
                DirectTransport.LogError("Engine event handler failed: " + e);
            }
        }
    }

    public UdpPeer2Peer(bool isServer)
    {
        m_isServer = isServer;
        m_manager = new NetManager(this)
        {
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

        // A fresh link must not deliver leftovers of a previous one: on a
        // rejoin after a disconnect the queues may still hold undrained
        // packets from the dead connection, which the new session would
        // misread as its own traffic.
        foreach (var queue in m_receiveQueues.Values)
            while (queue.TryDequeue(out _)) { }

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
        // Drop undelivered engine events: firing a stale ConnectionFailed
        // after the transport is gone would tear down whatever session
        // replaced this one.
        while (m_engineEvents.TryDequeue(out _)) { }
    }

    // --- Send / receive ---------------------------------------------------

    public bool SendPacket(ulong remoteUser, byte[] data, int byteCount, MyP2PMessageEnum msgType, int channel)
    {
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

    public bool AcceptSession(ulong remotePeerId) => m_peersById.ContainsKey(remotePeerId);

    public bool CloseSession(ulong remotePeerId)
    {
        if (m_peersById.TryRemove(remotePeerId, out NetPeer peer))
        {
            try { peer.Disconnect(); } catch { }
            return true;
        }

        return false;
    }

    public bool GetSessionState(ulong remoteUser, ref MyP2PSessionState state)
    {
        bool connected = m_peersById.TryGetValue(remoteUser, out NetPeer peer)
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
            NetPeer peer = request.Accept();
            peer.Tag = clientId;
            m_peersById[clientId] = peer;
            DirectTransport.Log($"Accepted client {clientId} from {request.RemoteEndPoint}");

            // Let the engine register the session on its update thread; its
            // handler calls AcceptSession, which we already satisfy via the
            // map above, so the deferred delivery loses nothing. Steam
            // delivers its SessionRequest callback the same way (from
            // RunCallbacks on the update thread, after packets may already
            // be arriving on the network thread).
            m_engineEvents.Enqueue(() => SessionRequest?.Invoke(clientId));
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
        ulong id = peer.Tag is ulong tag ? tag : (m_isServer ? 0uL : m_serverId);
        if (id != 0uL)
            m_peersById.TryRemove(id, out _);

        DirectTransport.Log($"Peer {id} disconnected: {disconnectInfo.Reason}");
        if (id != 0uL)
        {
            // Deliver on the engine's update thread. On the client the
            // handler (MyMultiplayerClient.Peer2Peer_ConnectionFailed) raises
            // HostLeft and unloads the session; invoked from this poll thread
            // it corrupts the GUI/render state and crashes the game.
            string reason = disconnectInfo.Reason.ToString();
            m_engineEvents.Enqueue(() => ConnectionFailed?.Invoke(id, reason));
        }
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
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
}
