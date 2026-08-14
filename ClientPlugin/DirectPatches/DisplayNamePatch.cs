using System.Linq;
using HarmonyLib;
using Sandbox.Engine.Networking;

namespace ClientPlugin.DirectPatches;

// A no-Steam client has no persona, so the platform layer hands out "Player<client-id>" and the
// server names the player and the identity from it (MyMultiplayerClient.SendPlayerData sends
// MyGameService.OnlineName; MyPlayerCollection renames the identity from what arrives). Pass
// --client-name to give a headless client a readable name instead - useful when several of them
// share a world with human players and have to be told apart in chat and the player list.
[HarmonyPatch(typeof(MyGameService))]
public static class DisplayNamePatch
{
    private static readonly string[] NameOption = ["client", "name"];

    public static string Name { get; private set; }

    public static void Init(Shared.Logging.IPluginLogger log)
    {
        string name = CommandLine.GetOptionValue(NameOption);
        if (string.IsNullOrWhiteSpace(name))
            return;

        // The name travels in the join message and ends up in chat lines and the player list, so
        // keep it to something a name can be: no control characters, and bounded.
        Name = new string(name.Trim().Where(character => !char.IsControl(character)).ToArray());
        if (Name.Length > 64)
            Name = Name.Substring(0, 64);
        if (Name.Length == 0)
            Name = null;
        else
            log.Info($"Direct transport client name set to '{Name}'");
    }

    public static bool Prepare() => DirectClient.Enabled && Name != null;

    [HarmonyPatch(nameof(MyGameService.OnlineName), MethodType.Getter)]
    [HarmonyPostfix]
    public static void OnlineName(ref string __result) => __result = Name;

    [HarmonyPatch(nameof(MyGameService.UserName), MethodType.Getter)]
    [HarmonyPostfix]
    public static void UserName(ref string __result) => __result = Name;
}
