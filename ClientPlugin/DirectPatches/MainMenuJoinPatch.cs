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

        // Pass an EMPTY (but non-null) rules dictionary, not null. In this game
        // version MyJoinGameHelper.JoinGame(server, rules) only reaches StartJoin
        // — which creates the MyMultiplayerClient that drives the connect through
        // our patched MyNullServerDiscovery.Connect — when rules != null. With
        // rules == null it takes the outer else and merely calls failedToJoin,
        // so nothing connects. An empty dict still skips settings/consent
        // deserialization (MyCachedServerItem.DeserializeSettings returns early
        // when the "sc" key is absent), so the direct-connect path stays clean.
        MyJoinGameHelper.JoinGame(server, new System.Collections.Generic.Dictionary<string, string>());
    }
}
