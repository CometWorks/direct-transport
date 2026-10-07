using HarmonyLib;
using SpaceEngineers.Game.GUI;

namespace ClientPlugin.DirectPatches;

// Once the main menu is built, auto-join the configured server over the UDP
// transport, or the --join-lobby host. Fires once; later drops back to the menu
// are handled by the rejoin countdown in DirectClient.Update, or for a lobby by
// the game's own reconnector.
[HarmonyPatch(typeof(MyGuiScreenMainMenu), "CreateMainMenu")]
public static class MainMenuJoinPatch
{
    private static bool s_joined;

    public static bool Prepare() => DirectClient.Enabled || Lobby.LobbyMode.Joining;

    public static void Postfix()
    {
        if (s_joined)
            return;

        s_joined = true;
        if (Lobby.LobbyMode.Joining)
            Lobby.LobbyMode.JoinHost();
        else
            DirectClient.JoinServer();
    }
}
