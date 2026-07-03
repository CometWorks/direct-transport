using System;
using System.Reflection;
using HarmonyLib;

namespace ClientPlugin.DirectPatches;

// In no-Steam mode MySteamService.GetAuthSessionTicket calls into an
// uninitialised Steam client and returns false, which makes the client abort
// the join before sending anything. Return a dummy non-empty ticket so the
// handshake proceeds; the server side accepts it unconditionally
// (see the server plugin's game-server auth bypass).
[HarmonyPatch]
public static class AuthTicketPatch
{
    public static bool Prepare() => DirectClient.Enabled;

    public static MethodBase TargetMethod() =>
        AccessTools.Method("VRage.Steam.MySteamService:GetAuthSessionTicket");

    public static bool Prefix(ref bool __result, ref uint ticketHandle, byte[] buffer, ref uint length)
    {
        ticketHandle = 1u;

        // A non-empty token; its contents are irrelevant because the server
        // does not validate it. Encode nothing meaningful.
        length = (uint)Math.Min(8, buffer?.Length ?? 0);
        for (int i = 0; i < length; i++)
            buffer[i] = 0;

        __result = true;
        return false;
    }
}
