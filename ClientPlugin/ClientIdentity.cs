using System;
using System.Linq;
using HarmonyLib;

namespace ClientPlugin;

// The client's identity on the command line: --client-id (who this client is)
// and --client-name (what it is called).
//
// --client-id gives the client a fake Steam identity, applied whether or not
// the game talks to Steam. Without Steam - because it is absent, not running,
// or disabled by the remote plugin's --no-steam - MySteamService falls back to
// the shared placeholder OFFLINE_STEAM_ID (1234567891011), so every concurrent
// client would claim the same identity and the same user data folder. With
// Steam running, the option deliberately displaces the real Steam id, so a
// test client never joins as the machine's Steam user. Either way the id given
// here is the one the client runs under.
//
// Omitting the option changes nothing: the patch stays off and the game keeps
// whichever id it would have used on its own - the real Steam id, or
// OFFLINE_STEAM_ID without Steam.
//
// --client-name overrides the player name even when Steam offers a persona.
// Where neither this option nor the platform layer supplies one, the name
// falls back to DefaultName (see DisplayNamePatch).
//
// The timing is what forces both into the Pulsar preloader hooks. MyProgram
// builds MySteamService near the top of Main (through
// MySteamInitializer.InitServices) and starts reading the identity straight
// away - MyFileSystem.InitUserSpecific(MyGameService.UserId.ToString()) picks
// the per-user data folder, MyCommonProgramStartup.CheckSteamRunning logs the
// user name - all long before Pulsar loads the plugins. Patching from
// IPlugin.Init would run well after the identity had been baked in.
public static class ClientIdentity
{
    public const string PatchCategory = "ClientIdentity";

    // The player name for a client that has neither --client-name nor a
    // persona from the platform layer.
    public const string DefaultName = "Player";

    private static readonly string[] IdOption = ["client", "id"];
    private static readonly string[] NameOption = ["client", "name"];

    public static ulong? ClientId { get; private set; }
    public static string Name { get; private set; }

    // Canonical spellings, for log messages.
    public static string IdOptionName => CommandLine.Format(IdOption);
    public static string NameOptionName => CommandLine.Format(NameOption);

    // Runs from Preloader.Initialize, before the game assembly is loaded: it
    // reads the command line and nothing else.
    public static void Initialize()
    {
        ParseClientId(CommandLine.GetOptionValue(IdOption));
        ParseName(CommandLine.GetOptionValue(NameOption));
    }

    // Runs from Preloader.Finish. The plugin logger does not exist yet (it is
    // set up in IPlugin.Init), hence the console.
    public static void ApplyPatches()
    {
        if (ClientId is null && Name is null)
            return;

        // Each patch in the category gates itself further: the identity and
        // single-instance patches need --client-id, while the name patch also
        // applies for a bare --client-name.
        new Harmony("DirectTransport.ClientIdentity").PatchCategory(PatchCategory);

        if (ClientId is not null)
            Console.WriteLine($"[DirectTransport] {IdOptionName}: running as client {ClientId.Value}");

        if (Name is not null)
            Console.WriteLine($"[DirectTransport] {NameOptionName}: playing as '{Name}'");
    }

    private static void ParseClientId(string value)
    {
        if (value is null)
            return;

        if (ulong.TryParse(value.Trim(), out ulong clientId) && clientId != 0)
            ClientId = clientId;
        else
            Console.WriteLine(
                $"[DirectTransport] Invalid {IdOptionName} '{value}', expected a positive 64 bit number"
            );
    }

    private static void ParseName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        // The name travels in the join message and ends up in chat lines and
        // the player list, so keep it to something a name can be: no control
        // characters, and bounded.
        string name = new string(value.Trim().Where(c => !char.IsControl(c)).ToArray());
        if (name.Length > 64)
            name = name.Substring(0, 64);

        if (name.Length != 0)
            Name = name;
    }
}
