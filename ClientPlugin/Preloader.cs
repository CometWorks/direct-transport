// DO NOT USE A NAMESPACE HERE!
// CRITICAL: Using a namespace here would prevent Pulsar from finding the Preloader class.
// INTENTIONALLY COMMENTED OUT: namespace ClientPlugin;

// Pulsar preloader hooks, needed here for one reason: the client identity has
// to exist before the game's Main runs (see ClientIdentity). Initialize runs
// after the plugin assemblies are loaded but before the game assembly starts,
// so it must not touch game types; Finish runs once Pulsar's game assembly
// resolver is in place, still ahead of the game's Main, which is where the
// patches go. Everything else this plugin does happens later, from
// IPlugin.Init. No TargetDLLs/Patch members: this plugin does not rewrite any
// game assembly.
public class Preloader
{
    public static void Initialize()
    {
        ClientPlugin.ClientIdentity.Initialize();
    }

    public static void Finish()
    {
        ClientPlugin.ClientIdentity.ApplyPatches();
    }
}
