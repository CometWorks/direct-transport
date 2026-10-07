using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Sandbox.Engine.Networking;
using Sandbox.Game.World;

namespace ClientPlugin.DirectPatches;

// A world loaded with an online mode other than offline creates its lobby in
// MySession.StartServerRequest, but only when MyGameService.IsOnline, which is false without Steam,
// and the load then fails with "no user". Lobby hosting does not need Steam, so this one check is
// swapped for CanHost. Patching IsOnline itself would send the workshop code and about 20 other
// callers to Steam.
[HarmonyPatch(typeof(MySession), "StartServerRequest")]
public static class StartServerRequestPatch
{
    // ReSharper disable once UnusedMember.Global
    public static bool Prepare() => Lobby.LobbyMode.Hosting;

    public static bool CanHost() => true;

    // ReSharper disable once UnusedMember.Global
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var isOnline = AccessTools.PropertyGetter(
            typeof(MyGameService),
            nameof(MyGameService.IsOnline)
        );
        var il = instructions.ToList();
        int index = il.FindIndex(ci => ci.Calls(isOnline));
        if (index < 0)
            throw new System.InvalidOperationException(
                "MySession.StartServerRequest no longer reads MyGameService.IsOnline"
            );

        il[index] = new CodeInstruction(il[index])
        {
            operand = AccessTools.Method(typeof(StartServerRequestPatch), nameof(CanHost)),
        };
        return il;
    }
}
