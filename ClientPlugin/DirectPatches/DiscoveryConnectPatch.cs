using System;
using HarmonyLib;
using VRage.GameServices;
using VRage.Network;

namespace ClientPlugin.DirectPatches;

// MyMultiplayerClient's construction calls MyGameService.ConnectToServer,
// which delegates to the registered IMyServerDiscovery.Connect. We register a
// MyNullServerDiscovery whose Connect just reports success; here we intercept
// it to first establish the UDP link (blocking) so the reliable join
// handshake is not dropped, then report JoinResult.OK.
[HarmonyPatch(typeof(MyNullServerDiscovery), nameof(MyNullServerDiscovery.Connect))]
public static class DiscoveryConnectPatch
{
    public static bool Prepare() => DirectClient.Enabled;

    public static bool Prefix(Action<JoinResult> onDone, ref bool __result)
    {
        bool connected = DirectClient.Connect();
        onDone?.Invoke(connected ? JoinResult.OK : JoinResult.UserNotConnected);
        __result = connected;
        return false;
    }
}
