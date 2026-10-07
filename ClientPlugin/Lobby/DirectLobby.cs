using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LiteNetLib.Utils;
using Sandbox.Engine.Networking;
using Shared.Transport;
using VRage.GameServices;
using VRageMath;

namespace ClientPlugin.Lobby;

// The lobby both ends of a DirectTransport friends game share, in place of a Steam lobby.
//
// The host owns the state: members, session data (world name, app version, mods, ...), lobby type
// and member limit. It sends a joiner the whole state once the UDP link is up, then every change
// to everyone, over the transport's control channel. The game drives a lobby session from these
// events alone: the host turns a member into a replication client on Entered, and a client
// unloads when the owner leaves. So a lost link is reported as the member, or the host, leaving.
//
// Everything the game sees happens on the update thread. Control messages arrive on the poll
// thread and are posted there, except the joiner's snapshot, see OnControl.
public sealed class DirectLobby : IMyLobby
{
    private enum Message : byte
    {
        Snapshot = 1,
        Data,
        MemberEntered,
        MemberLeft,
        Chat,
    }

    // The lobby of the running session, or the one being joined. One at a time, like Steam's
    // active lobby.
    public static DirectLobby Current { get; private set; }

    public bool IsHost { get; }
    public bool IsFull => m_members.Count >= MemberLimit;

    private readonly Dictionary<ulong, string> m_members = new();

    // Names outlive membership: the game asks for a leaving member's name while it handles the leave.
    private readonly Dictionary<ulong, string> m_names = new();
    private readonly Dictionary<string, string> m_data = new();
    private readonly ManualResetEventSlim m_snapshotReceived = new(false);

    public ulong LobbyId { get; private set; }
    public bool IsValid { get; private set; }
    public ulong OwnerId { get; private set; }
    public MyLobbyType LobbyType { get; set; }
    public ConnectionStrategy ConnectionStrategy => ConnectionStrategy.Normal;
    public int MemberLimit { get; set; }
    public int MemberCount => m_members.Count;
    public IEnumerable<ulong> MemberList => m_members.Keys.ToArray();

    public event KickedDelegate OnKicked
    {
        add { }
        remove { }
    }
    public event MyLobbyDataUpdated OnDataReceived;
    public event MessageReceivedDelegate OnChatReceived;
    public event MyLobbyChatUpdated OnChatUpdated;

    private DirectLobby(bool isHost)
    {
        IsHost = isHost;
    }

    // Host: the lobby MyMultiplayerLobby asked for. The owner is the only member until clients
    // connect.
    public static DirectLobby Host(MyLobbyType type, uint maxPlayers)
    {
        ulong self = MyGameService.OnlineUserId;
        var lobby = new DirectLobby(isHost: true)
        {
            LobbyId = self,
            OwnerId = self,
            LobbyType = type,
            MemberLimit = (int)maxPlayers,
            IsValid = true,
        };
        lobby.AddMember(self, MyGameService.UserName);
        Current = lobby;
        return lobby;
    }

    // Joiner: an empty lobby that fills from the host's snapshot. Valid once that arrived.
    public static DirectLobby Join()
    {
        var lobby = new DirectLobby(isHost: false);
        Current = lobby;
        return lobby;
    }

    public bool WaitForSnapshot(int timeoutMs) => m_snapshotReceived.Wait(timeoutMs) && IsValid;

    public string GetMemberName(ulong userId) =>
        m_names.TryGetValue(userId, out string name) ? name : "";

    // Steam semantics: a missing key reads as "". MyMultiplayerLobby.Tick relies on it to see that
    // the session tags (appVersion and the rest) were not written yet.
    public string GetData(string key) => m_data.TryGetValue(key, out string value) ? value : "";

    public bool SetData(string key, string value, bool important = true)
    {
        // Only the owner may change lobby data, as on Steam.
        if (!IsHost || !IsValid)
            return false;

        value ??= "";
        if (m_data.TryGetValue(key, out string old) && old == value)
            return true;

        m_data[key] = value;
        Broadcast(
            Write(
                Message.Data,
                w =>
                {
                    w.Put(key);
                    w.Put(value);
                }
            )
        );
        return true;
    }

    public bool DeleteData(string key) => SetData(key, "");

    // The data is always here: the snapshot brings it and changes follow.
    public bool RequestData() => IsValid;

    public bool SendChatMessage(
        string text,
        byte channel,
        long targetId = 0L,
        ChatMessageCustomData? customData = null,
        List<ulong> blockedUsers = null
    )
    {
        if (!IsValid)
            return false;

        byte[] message = WriteChat(MyGameService.OnlineUserId, text, channel, targetId, customData);
        if (IsHost)
            DirectTransport.Peer.Post(() => RelayChat(message));
        else
            DirectTransport.Peer.SendControl(OwnerId, message);
        return true;
    }

    // Session teardown (MyMultiplayerLobby/MyMultiplayerLobbyClient.Dispose) or a failed join.
    public void Leave()
    {
        if (Current == this)
            Current = null;
        if (!IsValid)
        {
            // A join that got the link but no snapshot: the link is still keyed by the placeholder.
            if (!IsHost)
                DirectTransport.Peer.CloseSession(DirectTransport.ServerId);
            m_snapshotReceived.Set();
            return;
        }

        IsValid = false;
        if (IsHost)
        {
            // Closing the links is how the clients learn that the host left.
            foreach (ulong member in m_members.Keys.ToArray())
            {
                if (member != OwnerId)
                    DirectTransport.Peer.CloseSession(member);
            }
        }
        else
        {
            DirectTransport.Peer.CloseSession(OwnerId);
        }

        LobbyMode.Log.Info($"Left lobby {LobbyId}");
    }

    // --- Transport events ----------------------------------------------------

    // Host, update thread: a client's UDP link is up. The snapshot goes out before the game hears
    // of the member, so it reaches the joiner before any game packet, and the joiner can only
    // send its world request after the host made it a replication client below.
    public void OnPeerAccepted(ulong id, string name)
    {
        if (!IsHost || !IsValid)
        {
            DirectTransport.Peer.CloseSession(id);
            return;
        }

        name = string.IsNullOrWhiteSpace(name) ? ClientIdentity.DefaultName : name;
        byte[] entered = Write(
            Message.MemberEntered,
            w =>
            {
                w.Put(id);
                w.Put(name);
            }
        );
        Broadcast(entered);

        AddMember(id, name);
        DirectTransport.Peer.SendControl(id, Write(Message.Snapshot, WriteSnapshot));

        LobbyMode.Log.Info($"Member {id} ('{name}') entered lobby {LobbyId}");
        OnChatUpdated?.Invoke(
            this,
            id,
            id,
            MyChatMemberStateChangeEnum.Entered,
            MyLobbyStatusCode.Success
        );
    }

    // Update thread: a link dropped. On the host that is a member leaving, on a client the host.
    public void OnPeerLost(ulong id)
    {
        if (!IsValid)
            return;

        if (IsHost)
        {
            if (id == OwnerId || !m_members.Remove(id))
                return;

            Broadcast(Write(Message.MemberLeft, w => w.Put(id)));
            LobbyMode.Log.Info($"Member {id} left lobby {LobbyId}");
            OnChatUpdated?.Invoke(
                this,
                id,
                id,
                MyChatMemberStateChangeEnum.Disconnected,
                MyLobbyStatusCode.Success
            );
            return;
        }

        if (id != OwnerId)
            return;

        // The game unloads the session, shows the host-left box and may start its reconnector.
        LobbyMode.Log.Info($"Lost the host of lobby {LobbyId}");
        m_members.Remove(id);
        OnChatUpdated?.Invoke(
            this,
            id,
            id,
            MyChatMemberStateChangeEnum.Disconnected,
            MyLobbyStatusCode.Success
        );
    }

    // Poll thread.
    public void OnControl(ulong sender, byte[] data)
    {
        var reader = new NetDataReader(data);
        var type = (Message)reader.GetByte();

        if (IsHost)
        {
            // A client sends nothing but chat.
            if (type == Message.Chat)
                DirectTransport.Peer.Post(() => RelayChat(data));
            return;
        }

        if (type == Message.Snapshot)
        {
            // Applied right here on the poll thread, before the transport queues any later packet:
            // the link has to be keyed by the host's id by then, or the engine would see those
            // packets come from nobody. Nothing else touches this lobby until the join completes.
            ReadSnapshot(reader);
            DirectTransport.Peer.SetServerId(OwnerId);
            m_snapshotReceived.Set();
            return;
        }

        DirectTransport.Peer.Post(() => Apply(type, reader));
    }

    private void AddMember(ulong id, string name)
    {
        m_members[id] = name;
        m_names[id] = name;
    }

    // --- Host side -------------------------------------------------------------

    private void RelayChat(byte[] message)
    {
        if (!IsValid)
            return;

        Broadcast(message);
        var reader = new NetDataReader(message);
        reader.GetByte();
        RaiseChat(reader);
    }

    private void Broadcast(byte[] message)
    {
        foreach (ulong member in m_members.Keys)
        {
            if (member != OwnerId)
                DirectTransport.Peer.SendControl(member, message);
        }
    }

    private void WriteSnapshot(NetDataWriter w)
    {
        w.Put(LobbyId);
        w.Put(OwnerId);
        w.Put((int)LobbyType);
        w.Put(MemberLimit);
        w.Put(m_members.Count);
        foreach (var member in m_members)
        {
            w.Put(member.Key);
            w.Put(member.Value);
        }

        w.Put(m_data.Count);
        foreach (var item in m_data)
        {
            w.Put(item.Key);
            w.Put(item.Value);
        }
    }

    // --- Client side -----------------------------------------------------------

    private void ReadSnapshot(NetDataReader r)
    {
        LobbyId = r.GetULong();
        OwnerId = r.GetULong();
        LobbyType = (MyLobbyType)r.GetInt();
        MemberLimit = r.GetInt();
        for (int count = r.GetInt(); count > 0; count--)
            AddMember(r.GetULong(), r.GetString());
        for (int count = r.GetInt(); count > 0; count--)
            m_data[r.GetString()] = r.GetString();
        IsValid = true;
    }

    private void Apply(Message type, NetDataReader r)
    {
        if (!IsValid)
            return;

        switch (type)
        {
            case Message.Data:
                m_data[r.GetString()] = r.GetString();
                OnDataReceived?.Invoke(true, this, LobbyId);
                break;

            case Message.MemberEntered:
            {
                ulong id = r.GetULong();
                AddMember(id, r.GetString());
                OnChatUpdated?.Invoke(
                    this,
                    id,
                    id,
                    MyChatMemberStateChangeEnum.Entered,
                    MyLobbyStatusCode.Success
                );
                break;
            }

            case Message.MemberLeft:
            {
                ulong id = r.GetULong();
                if (m_members.Remove(id))
                    OnChatUpdated?.Invoke(
                        this,
                        id,
                        id,
                        MyChatMemberStateChangeEnum.Disconnected,
                        MyLobbyStatusCode.Success
                    );
                break;
            }

            case Message.Chat:
                RaiseChat(r);
                break;
        }
    }

    // --- Chat ------------------------------------------------------------------

    private static byte[] WriteChat(
        ulong sender,
        string text,
        byte channel,
        long targetId,
        ChatMessageCustomData? customData
    ) =>
        Write(
            Message.Chat,
            w =>
            {
                w.Put(sender);
                w.Put(text ?? "");
                w.Put(channel);
                w.Put(targetId);
                w.Put(customData.HasValue);
                if (!customData.HasValue)
                    return;

                ChatMessageCustomData data = customData.Value;
                w.Put(data.AuthorName != null);
                if (data.AuthorName != null)
                    w.Put(data.AuthorName);
                w.Put(data.SenderId.HasValue);
                if (data.SenderId.HasValue)
                    w.Put(data.SenderId.Value);
                w.Put(data.TextColor.HasValue);
                if (data.TextColor.HasValue)
                    w.Put(data.TextColor.Value.PackedValue);
            }
        );

    // Every member gets every message, the sender included, as from a Steam lobby chat: the game
    // shows its own messages only when they come back.
    private void RaiseChat(NetDataReader r)
    {
        ulong sender = r.GetULong();
        string text = r.GetString();
        byte channel = r.GetByte();
        long targetId = r.GetLong();
        ChatMessageCustomData? customData = null;
        if (r.GetBool())
        {
            var data = new ChatMessageCustomData();
            if (r.GetBool())
                data.AuthorName = r.GetString();
            if (r.GetBool())
                data.SenderId = r.GetULong();
            if (r.GetBool())
                data.TextColor = new Color(r.GetUInt());
            customData = data;
        }

        OnChatReceived?.Invoke(sender, text, channel, targetId, customData, null);
    }

    private static byte[] Write(Message type, Action<NetDataWriter> body)
    {
        var writer = new NetDataWriter();
        writer.Put((byte)type);
        body(writer);
        return writer.CopyData();
    }
}
