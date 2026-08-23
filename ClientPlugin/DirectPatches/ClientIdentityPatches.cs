using System.Reflection;
using HarmonyLib;

namespace ClientPlugin.DirectPatches;

// VRage.Steam.MySteamService and VRage.Platform.Windows.Sys.MyWindowsSystem are
// both internal to the game, and Pulsar compiles this plugin without a
// publicizer, so these patches address their targets by name - the same way
// AuthTicketPatch does.

// Substitute the fake identity once MySteamService has finished building
// itself. A Postfix is the only workable point: the constructor assigns UserId
// last, falling back to OFFLINE_STEAM_ID when the service did not come up
// online, so anything applied earlier would simply be overwritten.
[HarmonyPatchCategory(ClientIdentity.PatchCategory)]
[HarmonyPatch]
public static class SteamUserIdPatch
{
    // ReSharper disable once UnusedMember.Global
    public static bool Prepare() => ClientIdentity.ClientId.HasValue;

    // ReSharper disable once UnusedMember.Global
    public static MethodBase TargetMethod() =>
        AccessTools.Constructor(
            AccessTools.TypeByName("VRage.Steam.MySteamService"),
            [typeof(bool), typeof(uint)]
        );

    // ReSharper disable once UnusedMember.Global
    public static void Postfix(object __instance, bool isDedicated)
    {
        // A dedicated server has its own identity and never runs alongside
        // sibling instances on one machine; only the client is renumbered.
        if (isDedicated)
            return;

        Traverse.Create(__instance).Property("UserId").SetValue(ClientIdentity.ClientId.Value);
    }
}

// The game permits a single instance per machine, guarded by a named mutex
// (MyProgram.Main reports "app already running" and returns when the guard
// says no). Distinct client ids exist precisely so several clients can share
// one machine, so the guard is lifted along with them - and only with them:
// without --client-id the instances would share a user data folder and an
// identity, which is what the guard is there to prevent.
[HarmonyPatchCategory(ClientIdentity.PatchCategory)]
[HarmonyPatch(
    "VRage.Platform.Windows.Sys.MyWindowsSystem, VRage.Platform.Windows",
    "get_IsSingleInstance"
)]
public static class SingleInstancePatch
{
    // ReSharper disable once UnusedMember.Global
    public static bool Prepare() => ClientIdentity.ClientId.HasValue;

    // ReSharper disable once UnusedMember.Global
    public static bool Prefix(ref bool __result)
    {
        __result = true;
        return false;
    }
}
