using System.Net;
using System.Reflection;
using HarmonyLib;

namespace ServerPlugin.DirectPatches;

// Install the UDP transport just before the dedicated server binds. The engine
// resolves its game server and networking through MyServiceManager inside
// MyDedicatedServerBase.Initialize, so registering ours in a prefix (before
// the body runs) makes the whole server bring-up use UDP instead of Steam:
// GameServer.Start, Peer2Peer.SetServer(true), WaitStart and the ServerId all
// route through our implementations.
[HarmonyPatch]
public static class InitializePatch
{
    public static bool Prepare() => DirectServer.Enabled;

    public static MethodBase TargetMethod() =>
        AccessTools.Method(
            "Sandbox.Engine.Multiplayer.MyDedicatedServerBase:Initialize",
            new[] { typeof(IPEndPoint) });

    public static void Prefix(IPEndPoint serverEndpoint) => DirectServer.Install(serverEndpoint);
}
