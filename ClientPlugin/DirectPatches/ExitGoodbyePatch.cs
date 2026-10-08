using System.Reflection;
using HarmonyLib;
using Sandbox;

namespace ClientPlugin.DirectPatches;

// T-0405: send the Disconnect before Pulsar kills the process on exit.
//
// Pulsar's Legacy Patch_ExitThreadSafe prefixes MySandboxGame.ExitThreadSafe with Process.GetCurrentProcess().Kill()
// (Modern: Patch_ExitGame on VRageCore.Exit), so every exit - the menu's Exit, Alt-F4, the Remote plugin's exit_game -
// ends the process without unloading the session. A Steam client still leaves at once (Steam closes its networking
// session for the dead process); a direct-transport client sent nothing, and the cluster gateway kept the session and
// the player's body until its 2-min Timeout (r121h: 79 of 80 harness DT disconnects were Timeout).
//
// Priority.First runs this prefix before Pulsar's (default priority): it says goodbye and returns true, so the next
// prefix - Pulsar's kill, or the original when Pulsar is absent - proceeds as before.
[HarmonyPatch(typeof(MySandboxGame), nameof(MySandboxGame.ExitThreadSafe))]
public static class ExitGoodbyePatch
{
    // ReSharper disable once UnusedMember.Global
    public static bool Prepare() => DirectClient.Enabled;

    // ReSharper disable once UnusedMember.Global
    [HarmonyPriority(Priority.First)]
    public static bool Prefix()
    {
        DirectClient.Goodbye("game exit");
        return true;
    }
}

// The same for Pulsar's Modern launcher, whose kill sits on Keen.VRage.Core.VRageCore.Exit. Addressed by name: the
// type does not exist in Space Engineers 1, where Prepare returns false and nothing is patched.
[HarmonyPatch]
public static class ExitGoodbyeModernPatch
{
    private static MethodBase Target => AccessTools.Method(AccessTools.TypeByName("Keen.VRage.Core.VRageCore"), "Exit");

    // ReSharper disable once UnusedMember.Global
    public static bool Prepare() => DirectClient.Enabled && Target != null;

    // ReSharper disable once UnusedMember.Global
    public static MethodBase TargetMethod() => Target;

    // ReSharper disable once UnusedMember.Global
    [HarmonyPriority(Priority.First)]
    public static bool Prefix()
    {
        DirectClient.Goodbye("game exit");
        return true;
    }
}
