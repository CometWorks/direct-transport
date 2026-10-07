using System.Net;
using Sandbox.Game.Gui;
using Shared.Logging;
using Shared.Transport;
using VRage;
using VRage.GameServices;

namespace ClientPlugin.Lobby;

// Friends (lobby) games without Steam: one client hosts its world as a lobby, others join it.
//
// --host-lobby [PORT] binds the UDP transport on PORT and makes any world loaded with an online
// mode other than offline a lobby. --join-lobby HOST:PORT joins such a host once the main menu is
// reached. The game's own MyMultiplayerLobby and MyMultiplayerLobbyClient run the session; this
// plugin only supplies the lobby service they talk to (DirectLobbyDiscovery, DirectLobby), with
// the lobby state carried over the transport's control channel.
public static class LobbyMode
{
    private static readonly string[] HostOption = ["host", "lobby"];
    private static readonly string[] JoinOption = ["join", "lobby"];

    public static bool Hosting { get; private set; }
    public static bool Joining { get; private set; }
    public static IPEndPoint HostEndpoint { get; private set; }

    public static IPluginLogger Log { get; private set; }

    // Called from Plugin.Init, after DirectClient.Init.
    public static void Init(IPluginLogger log)
    {
        Log = log;

        string hostOption = CommandLine.Format(HostOption);
        string joinOption = CommandLine.Format(JoinOption);
        bool host = CommandLine.HasOption(HostOption);
        string joinAddress = CommandLine.GetOptionValue(JoinOption);
        if (!host && joinAddress == null)
            return;

        if (host && joinAddress != null || DirectClient.Enabled)
        {
            log.Error(
                $"{hostOption}, {joinOption} and {CommandLine.Format("connect")} exclude each other; lobby mode inactive"
            );
            return;
        }

        // Host and joiner must be different users (MyMultiplayer.JoinLobby refuses to join its own
        // lobby), and without --client-id every no-Steam client is the same placeholder user.
        if (ClientIdentity.ClientId is null)
        {
            log.Error(
                $"{(host ? hostOption : joinOption)} needs {ClientIdentity.IdOptionName}; lobby mode inactive"
            );
            return;
        }

        if (host)
        {
            int port = DirectTransport.DefaultPort;
            string value = CommandLine.GetOptionValue(HostOption);
            if (value != null && int.TryParse(value, out int parsed))
                port = parsed;

            DirectTransport.InitServer(new IPEndPoint(IPAddress.Any, port));
            DirectTransport.Peer.AcceptPeers = () =>
                DirectLobby.Current is { IsHost: true, IsFull: false };
            DirectTransport.Peer.PeerAccepted += (id, name) =>
                DirectLobby.Current?.OnPeerAccepted(id, name);
            Hosting = true;
            log.Info($"Lobby host mode: worlds loaded online are joinable on UDP port {port}");
        }
        else
        {
            if (!DirectClient.TryParseEndpoint(joinAddress.Trim(), out IPEndPoint endpoint))
            {
                log.Error($"Invalid {joinOption} value '{joinAddress}', expected host:port");
                return;
            }

            HostEndpoint = endpoint;
            DirectTransport.InitClient();
            Joining = true;
            log.Info($"Lobby join mode: will join the lobby at {endpoint}");
        }

        DirectTransport.Peer.ControlReceived += (sender, data) =>
            DirectLobby.Current?.OnControl(sender, data);
        DirectTransport.Peer.ConnectionFailed += (id, _) => DirectLobby.Current?.OnPeerLost(id);

        // Replaces Steam's lobby discovery, which stays registered without Steam and would put
        // Steam's networking back in place of ours on every create or join.
        MyServiceManager.Instance.AddService<IMyLobbyDiscovery>(new DirectLobbyDiscovery());
    }

    // Kick off the stock lobby join towards the --join-lobby host. The lobby id does not matter:
    // DirectLobbyDiscovery.JoinLobby always dials HostEndpoint. The game's reconnector passes the
    // id of the last lobby, which ends up in the same place.
    public static void JoinHost()
    {
        Log.Info($"Joining the lobby at {HostEndpoint}");
        MyJoinGameHelper.JoinGame(1uL);
    }
}
