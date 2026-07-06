using System;
using System.Collections.Concurrent;
using System.Net;
using Sandbox.Engine.Networking;
using Shared.Logging;
using Shared.Transport;
using VRage;
using VRage.GameServices;

namespace ServerPlugin;

// Server-side activation of the non-Steam direct UDP transport.
//
// Enabled by the SE_DIRECT_TRANSPORT environment variable (1/true/yes/on).
// When enabled, an Initialize prefix installs the UDP networking service and a
// non-Steam game server just before the dedicated server binds, so clients can
// join over UDP with no Steam client present on either side.
public static class DirectServer
{
    public const string EnableEnvVar = "SE_DIRECT_TRANSPORT";

    public static bool Enabled { get; private set; }

    private static IPluginLogger m_log;
    private static readonly ConcurrentQueue<Action> MainThreadQueue = new();
    private static bool s_installed;

    public static void Init(IPluginLogger log)
    {
        m_log = log;
        DirectTransport.Log = msg => log.Info(msg);
        DirectTransport.LogError = msg => log.Error(msg);

        Enabled = IsTruthy(Environment.GetEnvironmentVariable(EnableEnvVar));
        log.Info(Enabled
            ? "Direct transport server active (SE_DIRECT_TRANSPORT set)"
            : $"Direct transport server inactive (set {EnableEnvVar}=1 to enable)");
    }

    // Preloader entry point, injected as a prologue into
    // MyDedicatedServerBase.Initialize by ServerPlugin/Preloader.cs.
    //
    // The dedicated server brings its networking up (MyNetworkMonitor starts
    // polling the game server) BEFORE the engine calls IPlugin.Init, so the
    // Harmony InitializePatch — applied from Plugin.Init — is too late: stock
    // Steam networking runs first and throws "Steamworks GameServer is not
    // initialized". This Cecil-injected call runs at the start of Initialize
    // itself, the same point the Harmony prefix targeted, but without depending
    // on plugin-init timing. It self-initialises so it works even though
    // Plugin.Init has not run yet, and no-ops unless SE_DIRECT_TRANSPORT is set.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static void PreInstall(IPEndPoint bind)
    {
        try
        {
            if (!IsTruthy(Environment.GetEnvironmentVariable(EnableEnvVar)))
                return;
            if (m_log == null)
                Init(new Shared.Logging.PluginLogger(Plugin.Name));
            Install(bind);
        }
        catch (Exception e)
        {
            Console.WriteLine("[DirectTransport] PreInstall failed: " + e);
        }
    }

    // Called from the Initialize prefix with the endpoint the dedicated server
    // is about to bind. Registers our networking + game server so the ensuing
    // engine bring-up transports over UDP instead of Steam.
    public static void Install(IPEndPoint bind)
    {
        if (s_installed)
            return;

        s_installed = true;

        IMyGameService service = MyServiceManager.Instance.GetService<IMyGameService>();
        if (service != null)
            service.UserId = DirectTransport.ServerId;

        MyServiceManager.Instance.AddService<IMyGameServer>(new UdpGameServer());
        DirectTransport.InitServer(bind);

        m_log?.Info($"Installed direct UDP transport for dedicated server on {bind}");
    }

    public static void RunOnMainThread(Action action)
    {
        if (action != null)
            MainThreadQueue.Enqueue(action);
    }

    // Drained once per server frame from the plugin Update.
    public static void PumpMainThread()
    {
        while (MainThreadQueue.TryDequeue(out Action action))
        {
            try { action(); }
            catch (Exception e) { m_log?.Error("Main-thread action failed: " + e); }
        }
    }

    private static bool IsTruthy(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        switch (value.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "on":
                return true;
            default:
                return false;
        }
    }
}
