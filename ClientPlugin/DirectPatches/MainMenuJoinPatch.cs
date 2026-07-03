using HarmonyLib;
using Sandbox.Game.Gui;
using SpaceEngineers.Game.GUI;
using VRage.Game;
using VRage.GameServices;
using Shared.Transport;

namespace ClientPlugin.DirectPatches;

// Once the main menu is built, auto-join the configured server over the UDP
// transport. The server descriptor is synthesised locally: no ping is needed
// because we already know the endpoint (SE_DIRECT_CONNECT) and use the
// well-known direct-transport server id. Fires once.
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

        var server = new MyGameServerItem
        {
            SteamID = DirectTransport.ServerId,
            Name = "DirectTransport server",
            ConnectionString = $"udp://{DirectClient.ServerEndpoint}",
            ServerVersion = (int)MyFinalBuildConstants.APP_VERSION,
            AppID = 244850u,
            Secure = false,
            MaxPlayers = 16,
        };

        // rules == null skips the settings/consent deserialization; the direct
        // connect path does not need server rules.
        MyJoinGameHelper.JoinGame(server, null);
    }
}
