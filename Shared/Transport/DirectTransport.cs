using System;
using System.Net;
using VRage;
using VRage.GameServices;

namespace Shared.Transport;

// Central orchestration and configuration for the non-Steam UDP transport,
// shared by the client and server plugins. Each plugin sets the two Log
// hooks, then calls InitServer / InitClient and registers the networking
// service so the engine routes all multiplayer traffic through UDP.
public static class DirectTransport
{
    // Well-known peer id representing "the dedicated server" on both ends. A
    // game client only ever talks to the single server it joined, so a fixed
    // id is sufficient and sidesteps the bind-address vs connect-address
    // mismatch that a derived id would suffer (server binds 0.0.0.0, client
    // dials a concrete IP). Chosen outside the individual-account SteamID
    // range and away from the reserved 0 / ulong.MaxValue sentinels.
    public const ulong ServerId = 0xFF00000000000001UL;

    // LiteNetLib connection key: any peer not presenting it is rejected, so a
    // stray packet or a real Steam client cannot half-open a session.
    public const string ProtocolKey = "SE-DirectTransport-v1";

    public const int DefaultPort = 27016;

    // Wait this long for the client link before giving up on a join.
    public const int ConnectTimeoutMs = 15000;

    // Logging hooks, set by the hosting plugin (Shared has no logger of its
    // own). Default to no-ops so the transport is safe to touch pre-init.
    public static Action<string> Log = _ => { };
    public static Action<string> LogError = _ => { };

    public static bool Active { get; private set; }
    public static bool IsServer { get; private set; }
    public static UdpPeer2Peer Peer { get; private set; }

    // Server: bind the UDP socket and register the networking service so the
    // dedicated server object (created later) transports over UDP. Call
    // before MyMultiplayer.Static is constructed.
    public static void InitServer(IPEndPoint bind)
    {
        if (Active)
            return;

        IsServer = true;
        Peer = new UdpPeer2Peer(isServer: true);
        Peer.StartServer(bind);
        RegisterNetworking();
        Active = true;
        Log($"Direct transport initialised as server on {bind}");
    }

    // Client: open the local socket and register the networking service.
    // Connection to the server happens later, when the join starts.
    public static void InitClient()
    {
        if (Active)
            return;

        IsServer = false;
        Peer = new UdpPeer2Peer(isServer: false);
        Peer.StartClient();
        RegisterNetworking();
        Active = true;
        Log("Direct transport initialised as client");
    }

    // Client: connect to the given server endpoint and block until the link
    // is established, so the reliable join handshake is not dropped.
    public static bool ConnectClient(IPEndPoint serverEndpoint, ulong localId, string localName = null)
    {
        if (Peer == null || IsServer)
            return false;

        return Peer.ConnectToServer(serverEndpoint, ServerId, localId, localName, ConnectTimeoutMs);
    }

    // Point MyGameService at our networking. AddService fires MyServiceManager
    // .OnChanged, which makes MyGameService drop its cached networking and
    // re-resolve to ours on the next access.
    private static void RegisterNetworking()
    {
        IMyGameService service = MyServiceManager.Instance.GetService<IMyGameService>();
        if (service == null)
        {
            LogError("No IMyGameService registered; cannot install UDP networking");
            return;
        }

        var networking = new UdpNetworking(service, Peer);
        MyServiceManager.Instance.AddService<IMyNetworking>(networking);

        // Deliver the transport's SessionRequest / ConnectionFailed events on
        // the engine's update thread. OnUpdate fires from IMyGameService
        // .Update(), the exact point where Steam and EOS deliver their P2P
        // callbacks; the engine's handlers (session teardown on HostLeft in
        // particular) are only safe on that thread.
        service.OnUpdate += Peer.DispatchEngineEvents;

        Log("Registered UDP networking service");
    }
}
