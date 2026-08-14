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

**Client** (Pulsar) — enable the *DirectTransport (Client)* plugin and start in
no-Steam mode pointed at the server:

```
Interim --client-id <unique-id> --connect <server-ip>:27016
```

The plugin reads `--connect` from the command line the game was started with,
brings up the UDP transport and auto-joins once the main menu is reached. Give
each concurrent client a distinct `--client-id` (they become distinct in-game
identities). Add `--client-name <name>` to give a client a readable name in chat
and the player list instead of the default `Player<client-id>`. Combine with
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
| `--client-name NAME` | Player name of a no-Steam client, shown in chat and the player list instead of the default `Player<client-id>`. Control characters are stripped and the name is capped at 64 characters. |

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
