using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Sandbox.Engine.Networking;
using Shared.Transport;
using VRage.GameServices;

namespace ClientPlugin.Lobby;

// The lobby service MyGameService hands to the game in lobby mode (see LobbyMode). There is no
// lobby list and no invites: a host creates its lobby when it loads a world online, and a joiner
// always joins the host given by --join-lobby.
//
// Both answers come later, on the update thread. MyMultiplayer.HostLobby only sets
// MyMultiplayer.Static after the lobby constructor that calls CreateLobby returns, and its
// callback fails the lobby when Static is not set yet.
public sealed class DirectLobbyDiscovery : IMyLobbyDiscovery
{
    // How long a joiner waits for the host's snapshot once the link is up. The host sends it right
    // after accepting, so this only covers a host that accepted and then stalled.
    private const int SnapshotTimeoutMs = 10000;

    public bool Supported => true;
    public bool FriendSupport => false;
    public bool ContinueToLobbySupported => false;

    public event MyLobbyJoinRequested OnJoinLobbyRequested
    {
        add { }
        remove { }
    }

    public void CreateLobby(MyLobbyType type, uint maxPlayers, MyLobbyCreated createdResponse)
    {
        if (!LobbyMode.Hosting)
        {
            LobbyMode.Log.Error("Not hosting lobbies: start the game with --host-lobby");
            DirectTransport.Peer.Post(() => createdResponse(null, false, MyLobbyStatusCode.NoUser));
            return;
        }

        DirectLobby.Current?.Leave();
        DirectLobby lobby = DirectLobby.Host(type, maxPlayers);
        LobbyMode.Log.Info($"Created {type} lobby {lobby.LobbyId} for up to {maxPlayers} players");
        DirectTransport.Peer.Post(() => createdResponse(lobby, true, MyLobbyStatusCode.Success));
    }

    public void JoinLobby(ulong lobbyId, MyJoinResponseDelegate responseDelegate)
    {
        if (!LobbyMode.Joining)
        {
            LobbyMode.Log.Error("Not joining lobbies: start the game with --join-lobby");
            DirectTransport.Peer.Post(() =>
                responseDelegate(false, null, MyLobbyStatusCode.DoesntExist)
            );
            return;
        }

        DirectLobby.Current?.Leave();
        DirectLobby lobby = DirectLobby.Join();
        ulong localId = MyGameService.UserId;
        string localName = MyGameService.OnlineName;

        // ConnectClient blocks until the link is up, which can take the whole connect timeout.
        Task.Run(() =>
        {
            bool ok = false;
            try
            {
                ok =
                    DirectTransport.ConnectClient(LobbyMode.HostEndpoint, localId, localName)
                    && lobby.WaitForSnapshot(SnapshotTimeoutMs);
            }
            catch (Exception e)
            {
                LobbyMode.Log.Error(e, "Joining the lobby failed");
            }

            DirectTransport.Peer.Post(() =>
            {
                if (ok && lobby.IsValid)
                {
                    LobbyMode.Log.Info(
                        $"Joined lobby {lobby.LobbyId} of {lobby.OwnerId} with {lobby.MemberCount} members"
                    );
                    responseDelegate(true, lobby, MyLobbyStatusCode.Success);
                    return;
                }

                LobbyMode.Log.Error($"No lobby at {LobbyMode.HostEndpoint}");
                lobby.Leave();
                responseDelegate(false, null, MyLobbyStatusCode.DoesntExist);
            });
        });
    }

    // Only used to look at lobbies from a list, which this service has none of.
    public IMyLobby CreateLobby(ulong lobbyId) => null;

    public bool OnInvite(string protocolData) => false;

    public void RequestLobbyList(Action<bool> completed) => completed?.Invoke(true);

    public void AddPublicLobbies(List<IMyLobby> lobbyList) { }

    public void AddFriendLobbies(List<IMyLobby> lobbyList) { }

    public void AddLobbyFilter(string key, string value) { }
}
