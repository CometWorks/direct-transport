using HarmonyLib;
using SpaceEngineers.Game.GUI;

namespace ClientPlugin.DirectPatches;

// Once the main menu is built, auto-join the configured server over the UDP
// transport. Fires once; later drops back to the menu are handled by the
// rejoin countdown in DirectClient.Update.
[HarmonyPatch(typeof(MyGuiScreenMainMenu), "CreateMainMenu")]
public static class MainMenuJoinPatch
{
    private static bool s_joined;

    public static bool Prepare() => DirectClient.Enabled;

    public static void Postfix()
    {
        if (s_joined)
            return;

        s_joined = true;
        DirectClient.JoinServer();
    }
}
