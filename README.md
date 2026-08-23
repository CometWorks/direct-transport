# DirectTransport

A non-Steam, raw-UDP direct-connect transport for Space Engineers 1. It lets
game **clients** join a **dedicated server** with no Steam client on either
side, so you can stand up multi-client test setups — functional and load
testing — with no human players and no Steam accounts.

It is delivered as two plugins that share one transport implementation:

- **DirectTransport (Client)** — loaded by [Pulsar](https://github.com/SpaceGT/Pulsar) into the game client.
- **DirectTransport (Server)** — loaded by [Magnetar](https://magnetar.se) into the dedicated server.

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
and replication are the stock engine paths.

## Usage

Run one dedicated server (Magnetar) and any number of clients (Pulsar), all on
the same LAN or host.

**Server** (Magnetar) — enable the *DirectTransport (Server)* plugin and set:

```
SE_DIRECT_TRANSPORT=1
```

The server binds UDP to the `IP`/`ServerPort` from its dedicated config
(default `0.0.0.0:27016`).

**Client** (Pulsar) — enable the *DirectTransport (Client)* plugin and start it
pointed at the server:

```
Interim --client-id <unique-id> --connect <server-ip>:27016
```

The plugin reads `--connect` from the command line the game was started with,
brings up the UDP transport and auto-joins once the main menu is reached. Give
each concurrent client a distinct `--client-id`; they become distinct in-game
identities with their own user data folders, and the option lifts the game's
one-instance-per-machine guard so they can run side by side. Add
`--client-name <name>` for a readable name in chat and the player list. Combine
with the Remote plugin's `--no-steam` (Steam out of the picture entirely) and
`--headless` for players-free load testing.

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

Without `--client-id` the game keeps whichever id it would have used on its
own: the real Steam id, or the shared placeholder
`MySteamService.OFFLINE_STEAM_ID` (`1234567891011`) when running without Steam.

Without `--client-name` the player name is whatever the platform layer
provides — the Steam persona when the game is talking to Steam — falling back
to `Player` when there is none, which is the case for a client running without
Steam.

Neither `--client-id` nor `--client-name` depends on `--connect`. `--client-id`
is applied from the Pulsar preloader, before the game's `Main` runs, because
the game derives the user data folder from the identity long before plugins are
loaded.

## Limitations

- Linux, .NET (Core) runtime, dedicated-server target — matching the headless
  test use case.
- No encryption or authentication: intended for trusted, isolated test networks.
- One server per client process (a client joins a single server at a time).

## Building

```
dotnet build DirectTransport.sln -c Release
```

Reference paths (Space Engineers, Dedicated Server, Pulsar, Magnetar) are
auto-detected in `Directory.Build.props`; override there if needed.
