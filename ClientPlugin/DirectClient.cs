using System;
using System.Net;
using Sandbox.Engine.Networking;
using Shared.Logging;
using Shared.Transport;
using VRage;
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

        log.Info($"Direct transport client active, will connect to {endpoint}");
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
