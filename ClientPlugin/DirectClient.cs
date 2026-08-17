using System;
using System.Net;
using LiteNetLib;
using Sandbox.Engine.Networking;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using Shared.Logging;
using Shared.Transport;
using SpaceEngineers.Game.GUI;
using VRage;
using VRage.Game;
using VRage.GameServices;

namespace ClientPlugin;

// Client-side activation of the non-Steam direct UDP transport.
//
// Activated by the SE_DIRECT_CONNECT environment variable (host:port), which
// Pulsar exports from its --connect option when running in no-Steam
// (--steamid) mode. When set, the transport is installed and the client
// auto-joins the given server once the main menu is reached.
public static class DirectClient
{
    public const string ConnectEnvVar = "SE_DIRECT_CONNECT";

    public static bool Enabled { get; private set; }
    public static IPEndPoint ServerEndpoint { get; private set; }

    private static IPluginLogger m_log;

    // Automatic rejoin after an involuntary disconnect (server or gateway
    // dropped the link, timeout, node crash). Armed by OnConnectionFailed;
    // once the session has unloaded and the main menu is open again, a
    // countdown starts and the client rejoins the configured server.
    // Deliberate local disconnects (the engine closing the session when the
    // user exits to the menu) surface as DisconnectPeerCalled and do not arm.
    private const int RejoinDelayMs = 5000;
    private static bool m_rejoinArmed;
    private static long m_rejoinAtMs;

    public static void Init(IPluginLogger log)
    {
        m_log = log;
        DirectTransport.Log = msg => log.Info(msg);
        DirectTransport.LogError = msg => log.Error(msg);

        string address = Environment.GetEnvironmentVariable(ConnectEnvVar);
        if (string.IsNullOrWhiteSpace(address))
        {
            log.Info("Direct transport inactive (no SE_DIRECT_CONNECT set)");
            return;
        }

        if (!TryParseEndpoint(address.Trim(), out IPEndPoint endpoint))
        {
            log.Error($"Invalid {ConnectEnvVar} '{address}', expected host:port");
            return;
        }

        ServerEndpoint = endpoint;
        Enabled = true;

        // Bring up the local UDP socket and register the networking service so
        // MyGameService routes multiplayer traffic through us. A trivial
        // server-discovery instance is registered too, so the join path has a
        // provider to Connect through (patched in DiscoveryConnectPatch).
        DirectTransport.InitClient();
        MyServiceManager.Instance.AddService<IMyServerDiscovery>(new MyNullServerDiscovery());

        // Arm the automatic rejoin when the server-side link drops. Delivered
        // on the update thread (see UdpPeer2Peer.DispatchEngineEvents), and
        // subscribed before the engine's own handler, so this only sets flags
        // and leaves the session teardown to the engine.
        DirectTransport.Peer.ConnectionFailed += OnConnectionFailed;

        log.Info($"Direct transport client active, will connect to {endpoint}");
    }

    private static void OnConnectionFailed(ulong id, string reason)
    {
        if (id != DirectTransport.ServerId)
            return;

        // A locally initiated Disconnect() (engine closing the session on a
        // deliberate exit to the menu) reports DisconnectPeerCalled; anything
        // else means the link died under us and is worth rejoining.
        if (reason == DisconnectReason.DisconnectPeerCalled.ToString())
            return;

        m_log?.Info($"Link to {ServerEndpoint} lost ({reason});"
            + $" rejoining {RejoinDelayMs / 1000}s after returning to the main menu");
        m_rejoinArmed = true;
        m_rejoinAtMs = 0;
    }

    // Called every simulation frame from Plugin.Update on the update thread.
    // Runs the rejoin countdown: wait until the dropped session has fully
    // unloaded and the main menu is open, then hold RejoinDelayMs and rejoin.
    public static void Update()
    {
        if (!Enabled || !m_rejoinArmed)
            return;

        if (MySession.Static != null)
        {
            // Before the countdown starts this is the old session still
            // unloading; keep waiting. Once the countdown is running a live
            // session means the user loaded or joined something else, so the
            // pending rejoin is stale.
            if (m_rejoinAtMs != 0)
            {
                m_log?.Info("Rejoin cancelled: another session is active");
                m_rejoinArmed = false;
                m_rejoinAtMs = 0;
            }
            return;
        }

        // The host-left message box sits on top of the menu without hiding
        // it, so the menu stays OPENED underneath and this passes unattended.
        if (!MyScreenManager.IsScreenOfTypeOpen(typeof(MyGuiScreenMainMenu)))
            return;

        long now = Environment.TickCount64;
        if (m_rejoinAtMs == 0)
        {
            m_rejoinAtMs = now + RejoinDelayMs;
            return;
        }

        if (now < m_rejoinAtMs)
            return;

        m_rejoinArmed = false;
        m_rejoinAtMs = 0;
        m_log?.Info($"Rejoining {ServerEndpoint}");
        JoinServer();
    }

    // Kick off the standard join flow towards the configured server. The
    // descriptor is synthesised locally: no ping is needed because the
    // endpoint is known (SE_DIRECT_CONNECT) and the server uses the
    // well-known direct-transport id. Used by the initial auto-join
    // (MainMenuJoinPatch) and by the rejoin countdown above.
    public static void JoinServer()
    {
        var server = new MyGameServerItem
        {
            SteamID = DirectTransport.ServerId,
            Name = "DirectTransport server",
            ConnectionString = $"udp://{ServerEndpoint}",
            ServerVersion = (int)MyFinalBuildConstants.APP_VERSION,
            AppID = 244850u,
            Secure = false,
            MaxPlayers = 16,
        };

        // Pass an EMPTY (but non-null) rules dictionary, not null. In this game
        // version MyJoinGameHelper.JoinGame(server, rules) only reaches StartJoin
        // — which creates the MyMultiplayerClient that drives the connect through
        // our patched MyNullServerDiscovery.Connect — when rules != null. With
        // rules == null it takes the outer else and merely calls failedToJoin,
        // so nothing connects. An empty dict still skips settings/consent
        // deserialization (MyCachedServerItem.DeserializeSettings returns early
        // when the "sc" key is absent), so the direct-connect path stays clean.
        MyJoinGameHelper.JoinGame(server, new System.Collections.Generic.Dictionary<string, string>());
    }

    // Establish the UDP link to the server, blocking until it is up. Called
    // from the discovery Connect patch just before the join proceeds.
    public static bool Connect()
    {
        ulong localId = MyGameService.UserId;
        // OnlineName is SE_DIRECT_NAME when set (DisplayNamePatch), otherwise the platform layer's
        // "Player<client-id>". Either way it is what this client wants to be called, and the handshake
        // is the only place a no-Steam client can say so before the server names its identity.
        string localName = MyGameService.OnlineName;
        m_log?.Info($"Establishing direct link to {ServerEndpoint} as user {localId} ('{localName}')");
        bool ok = DirectTransport.ConnectClient(ServerEndpoint, localId, localName);
        if (!ok)
            m_log?.Error($"Direct link to {ServerEndpoint} timed out");
        return ok;
    }

    private static bool TryParseEndpoint(string address, out IPEndPoint endpoint)
    {
        endpoint = null;

        int colon = address.LastIndexOf(':');
        string host = colon >= 0 ? address.Substring(0, colon) : address;
        int port = DirectTransport.DefaultPort;
        if (colon >= 0 && !int.TryParse(address.Substring(colon + 1), out port))
            return false;

        if (!IPAddress.TryParse(host, out IPAddress ip))
        {
            try
            {
                IPAddress[] resolved = Dns.GetHostAddresses(host);
                if (resolved.Length == 0)
                    return false;
                ip = resolved[0];
            }
            catch
            {
                return false;
            }
        }

        endpoint = new IPEndPoint(ip, port);
        return true;
    }
}
