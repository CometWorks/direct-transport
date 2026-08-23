using HarmonyLib;
using Sandbox.Engine.Networking;

namespace ClientPlugin.DirectPatches;

// Resolves the player name, in priority order:
//
//   1. --client-name, which wins even when Steam offers a persona: a test
//      client has to be recognisable in chat and the player list regardless of
//      whose machine it happens to run on.
//   2. Whatever the platform layer provides - the Steam persona when the game
//      is talking to Steam.
//   3. ClientIdentity.DefaultName, because a client without Steam has no
//      persona to fall back on: MySteamService only assigns a user name on its
//      online branch, so the game would otherwise show an empty one.
//
// The name reaches the server in the join message
// (MyMultiplayerClient.SendPlayerData sends MyGameService.OnlineName) and
// MyPlayerCollection renames the identity from what arrives, so patching the
// two getters covers chat and the player list as well as the local UI.
[HarmonyPatchCategory(ClientIdentity.PatchCategory)]
[HarmonyPatch(typeof(MyGameService))]
public static class DisplayNamePatch
{
    // Patched for an explicit --client-name, and for a fake identity, which
    // brings no persona with it and so needs the fallback. A plain Steam
    // session with neither option keeps the game's own naming untouched.
    // ReSharper disable once UnusedMember.Global
    public static bool Prepare() =>
        ClientIdentity.Name is not null || ClientIdentity.ClientId.HasValue;

    // ReSharper disable once UnusedMember.Global
    [HarmonyPatch(nameof(MyGameService.OnlineName), MethodType.Getter)]
    [HarmonyPostfix]
    public static void OnlineName(ref string __result) => __result = Resolve(__result);

    // ReSharper disable once UnusedMember.Global
    [HarmonyPatch(nameof(MyGameService.UserName), MethodType.Getter)]
    [HarmonyPostfix]
    public static void UserName(ref string __result) => __result = Resolve(__result);

    // MyGameService hands out null (OnlineName) or string.Empty (UserName)
    // when the platform layer has no name, so both count as "not provided".
    private static string Resolve(string provided) =>
        ClientIdentity.Name
        ?? (string.IsNullOrWhiteSpace(provided) ? ClientIdentity.DefaultName : provided);
}
