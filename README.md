# DirectTransport

A non-Steam, raw-UDP direct-connect transport for Space Engineers 1. It lets
game **clients** join a **dedicated server** with no Steam client on either
side, so you can stand up multi-client test setups — functional and load
testing — with no human players and no Steam accounts. A client can also
**host its world** as a friends (lobby) game that other clients join, the same
way, for testing only, see [Lobby games](#lobby-games).

It is delivered as two plugins that share one transport implementation:

- the **client** plugin — loaded by [Pulsar](https://github.com/SpaceGT/Pulsar) into the game client.
- the **server** plugin — loaded by [Magnetar](https://magnetar.se) into the dedicated server.

Both are published under the same friendly name, *Direct Transport*; the plugin
tooltip says which side it is for.

## How it works

The whole engine addresses peers by a single `ulong` id, and everything above
the `VRage.GameServices.IMyPeer2Peer` seam is transport-agnostic. DirectTransport
replaces that seam:

- `Shared/Transport/UdpPeer2Peer` — `IMyPeer2Peer` over [LiteNetLib](https://github.com/RevenantX/LiteNetLib)
  (reliable/unreliable UDP), mapping each `ulong` peer id to a UDP endpoint.
- `Shared/Transport/UdpNetworking` — `IMyNetworking` exposing the peer transport,
  registered with `MyServiceManager` so `MyGameService` routes all multiplayer
  traffic through UDP.
- The client synthesises the server descriptor locally (fixed well-known server
  id, endpoint from `--connect`), so no Steam ping/lobby is involved.
- Steam authentication is neutralised on both ends: the client sends a dummy
  auth ticket; the server admits every client (`UdpGameServer.BeginAuthSession`).

Only the transport and auth are replaced — the join handshake, world download
and replication are the stock engine paths. Lobby games replace the Steam lobby
service on top of that, see [Lobby games](#lobby-games).

## Usage

Run one dedicated server (Magnetar) and any number of clients (Pulsar), all on
the same LAN or host.

**Server** (Magnetar) — enable the *Direct Transport* plugin (tooltip:
*dedicated server*) and set:

```
SE_DIRECT_TRANSPORT=1
```

The server binds UDP to the `IP`/`ServerPort` from its dedicated config
(default `0.0.0.0:27016`).

**Client** (Pulsar) — enable the *Direct Transport* plugin (tooltip: *client*)
and start the game through Pulsar's launcher, pointed at the server:

```
Interim --client-id <unique-id> --connect <server-ip>:27016
```

`Interim` is Pulsar's launcher for the .NET (Core) build of the game, which is
the one this plugin targets.

The plugin reads `--connect` from the command line the game was started with,
brings up the UDP transport and auto-joins once the main menu is reached. Give
each concurrent client a distinct `--client-id`; they become distinct in-game
identities with their own user data folders, and the option lifts the game's
one-instance-per-machine guard so they can run side by side. Add
`--client-name <name>` for a readable name in chat and the player list. Add
Pulsar's `--lazy-steam` to start the game when Steam is not running.

If the link dies under the client — the server restarts, a gateway drops the
session, the connection times out — the plugin rejoins on its own: it waits for
the session to unload and the main menu to come back, holds 5 seconds, then
joins the same server again. A deliberate exit to the menu does not trigger it,
and a rejoin already pending is cancelled if another session is loaded
meanwhile. The transport also writes a per-peer liveness line (connection state,
time since the last packet, ping) to the console every 10 seconds, which is how
one-way silence is told apart from a dead socket.

## Lobby games

**This feature is available strictly for testing purposes. The main purpose is
to allow for multiplayer testing using only clients, which is subpar to proper
Magnetar (DS) based hosting.**

A client started with `--host-lobby` hosts any world it loads with an online
mode other than offline as a friends game. Other clients started with
`--join-lobby` join it. All of them need distinct `--client-id` values, and
none needs Steam.

```
Interim --lazy-steam --client-id 1001 --host-lobby 27020
Interim --lazy-steam --client-id 1002 --join-lobby 127.0.0.1:27020
```

The host has to load the world online: a world saved with
`<OnlineMode>FRIENDS</OnlineMode>` in its `Sandbox.sbc` and `Sandbox_config.sbc`,
or one a plugin loads with
`MySessionLoader.LoadSingleplayerSession(path, onlineMode: MyOnlineModeEnum.FRIENDS, maxPlayers: 4)`.
Without Steam the game's world settings screen refuses to save an online mode,
so it cannot be set from there. The joiner
tries once, when its main menu is reached. If no lobby is up by then, the join
fails with "lobby does not exist" and is not retried.

These are real lobby sessions, `MyMultiplayerLobby` on the host and
`MyMultiplayerLobbyClient` on the joiners, so plugin code that checks those
types takes its lobby branches. The plugin replaces only the Steam lobby
service: the host keeps the lobby's members, data and chat and sends them to
the joiners over the same UDP link as the game traffic. A dropped link counts
as that player leaving. When the host's session ends, its clients go back to
the main menu, and the game's own reconnector may try to join again.

Hosting skips the game's check for a signed in Steam user
(`MyGameService.IsOnline` in `MySession.StartServerRequest`). A world with
Workshop mods still cannot be hosted, because loading a world online has to
download its mods, which needs Steam.

## Command line options (client)

The client plugin reads these directly from the command line of the game
process; Pulsar passes its arguments through to the game. They are available
only while the plugin is loaded. Each option is accepted case-insensitively in
the Linux (`--client-name`), Windows (`/ClientName`) and Space Engineers
(`-clientName`) forms, with the value in the next argument or inline
(`--connect=host:port`).

| Option | Description |
|--------|-------------|
| `--connect ADDRESS` | Join the server at `ADDRESS` (`host:port`, port defaults to `27016`) over raw UDP as soon as the main menu is reached. Requires the matching server-side plugin. Without this option the transport stays inactive. |
| `--client-id ID` | Run under the fake Steam identity `ID` (any positive 64-bit number), which also gives the client its own game user data folder and lifts the game's single-instance guard. Applied whether or not the game talks to Steam: without Steam every client would otherwise share one placeholder identity, and with Steam the option keeps a test client from joining as the machine's Steam user. |
| `--client-name NAME` | Player name, shown in chat and the player list. Overrides the name Steam would supply. Control characters are stripped and the name is capped at 64 characters. |
| `--host-lobby [PORT]` | Host worlds loaded online as lobby games, accepting joiners on UDP `PORT` (default `27016`). Needs `--client-id`. See [Lobby games](#lobby-games). |
| `--join-lobby ADDRESS` | Join the lobby game hosted at `ADDRESS` (`host:port`) once the main menu is reached. Needs `--client-id`. |

Without `--client-id` the game keeps whichever id it would have used on its
own: the real Steam id, or the shared placeholder
`MySteamService.OFFLINE_STEAM_ID` (`1234567891011`) when running without Steam.

Without `--client-name` the player name is whatever the platform layer
provides — the Steam persona when the game is talking to Steam. When that name
is empty, which is the case for a client running without Steam, it falls back
to `Player` — but only if `--client-id` was given, because with neither option
present the plugin leaves the game's own naming completely untouched.

`--connect`, `--host-lobby` and `--join-lobby` exclude each other. Neither
`--client-id` nor `--client-name` depends on them. `--client-id`
is applied from the Pulsar preloader, before the game's `Main` runs, because
the game derives the user data folder from the identity long before plugins are
loaded.

## Limitations

- .NET (Core) runtime (`CoreCLR`), dedicated server and lobby games built for
  the headless test use case. Developed and tested on Linux; nothing in the plugins
  is platform-specific and neither manifest restricts the platform.
- No encryption or authentication: intended for trusted, isolated test networks.
- One server per client process (a client joins a single server at a time).
- Lobby games: no lobby list and no invites, a joiner names its host on the
  command line. Worlds with Workshop mods cannot be hosted without Steam.

## Building

```
dotnet build DirectTransport.sln -c Release
```

Load the working copy through loader development folders: start Pulsar or
Magnetar with `-sources` and add the repository with the Sources button. Builds
deploy only if `Pulsar` or `MagnetarData` is set in `Directory.Build.props.user`
or passed as `-p:Pulsar=...` / `-p:MagnetarData=...`, see *Deployment* below.

### Folder path overrides

`Directory.Build.props` **is** committed and declares the overridable folder
paths with empty defaults:

- `Bin64` — the folder containing `SpaceEngineers.exe`
- `Dedicated64` — the folder containing `SpaceEngineersDedicated.exe`
- `Pulsar` — the Pulsar folder the client plugin is deployed into after each
  build, empty by default (no deployment)
- `Magnetar` — the Magnetar installation folder, the one holding the launcher
  executables and their `Libraries`, which is where `PluginSdk.dll` is referenced from
- `MagnetarData` — the Magnetar config folder the server plugin is deployed into,
  the one holding `Local`, `Sources` and `Profiles`, empty by default (no
  deployment)

It optionally imports `Directory.Build.props.user` from the repository root,
which is **not** committed (matched by `*.user` in `.gitignore`), so each
contributor keeps their own local paths there.

To override a path manually, copy the first `PropertyGroup` of
`Directory.Build.props` into `Directory.Build.props.user`, wrapped into a
top-level `<Project>` element, and fill in your paths. Running

```
python3 setup.py
```

writes that file for you with the auto-detected install locations, creating it
if needed and keeping any other overrides already in it.

Leaving `Bin64`, `Dedicated64` or `Magnetar` empty — or having no
`Directory.Build.props.user` at all — falls back to the auto-detection in
`Directory.Build.props`, which reads the Steam registry keys on Windows and the
usual Steam locations on Linux, then resolves the game and the Dedicated Server
through Steam's `libraryfolders.vdf`, so installs on a secondary Steam library
are found as well. `Magnetar` defaults to the `Magnetar` folder next to the
server install on Windows and to `$XDG_CONFIG_HOME/Magnetar`
(`~/.config/Magnetar`) on Linux; `PluginSdk.dll` is referenced from its
`Libraries/<launcher>` subfolder.

`Pulsar` and `MagnetarData` are never auto-detected. Leaving them empty turns
off deployment.

The build fails with a clear message if `Bin64`, `Dedicated64` or Magnetar's
`PluginSdk.dll` cannot be resolved, and warns instead of failing if a loader
folder is set but missing, in which case that plugin is only built, not
deployed.

### Deployment

Builds don't deploy anything by default. Load the working copy through
development folders instead (start Pulsar or Magnetar with `-sources`, then use
the Sources button), which compile the plugins from source when the loader
starts. A deployed DLL shows up as a separate local plugin, and once the
development folder is disabled it can shadow the published version.

To deploy anyway, set `Pulsar` and/or `MagnetarData` in
`Directory.Build.props.user`, or pass them to a single build:

```
dotnet build DirectTransport.sln -p:Pulsar=$HOME/.config/Pulsar -p:MagnetarData=$HOME/.config/Magnetar/Magnetar
```

Each successful build then copies itself into the loader's `Local` plugin
folder:

| Project        | Build     | Deployed to                                |
|----------------|-----------|--------------------------------------------|
| `ClientPlugin` | `net48`   | `<Pulsar>/Legacy/Local/DirectTransport/`   |
| `ClientPlugin` | `net10.0` | `<Pulsar>/Interim/Local/DirectTransport/`  |
| `ServerPlugin` | `net10.0` | `<MagnetarData>/Local/`                    |

Both Magnetar launchers share `<MagnetarData>/Local`, so only the `net10.0`
server build is deployed there; the `net48` server build is not deployed.

Pulsar identifies a plugin by its folder, so the client DLL is copied as
`plugin.dll`, its symbols as `plugin.pdb` and `DirectTransportClient.xml` from
the repository root as `plugin.xml`. Magnetar identifies a plugin by its DLL
file name, so the server plugin is copied flat as `DirectTransport.dll`, with
`DirectTransportServer.xml` next to it as `DirectTransport.dll.xml`. Either way
the loader shows the plugin under its friendly name and honours the runtime and
platform restrictions declared in the XML.

`Interim` is the Pulsar executable running Space Engineers 1 on .NET 10. It
falls back to the `Legacy` data folder when `<Pulsar>/Interim` does not exist,
and so does the deployment. (`<Pulsar>/Modern` belongs to Space Engineers 2 and
is never a deployment target here.) `MagnetarInterim` is its dedicated server
counterpart. On Linux only the Interim launchers exist, so only the `net10.0`
build is made.
