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
