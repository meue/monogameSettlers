# monoSettlers server

The relay for multiplayer games: a lobby, plus handing every player's commands to everyone in the game. It runs no game logic and
needs no accounts, database or disk. Design and protocol: [docs/MULTIPLAYER.md](../../docs/MULTIPLAYER.md).

## Running it

On a LAN, for friends:

```sh
docker build -f src/Homestead.Server/Dockerfile -t homestead-server .
docker run -p 8080:8080 homestead-server
```

Players connect from the title screen via "Multiplayer" with `ws://<host>:8080/ws`.

For a server others can reach, drop everything the process doesn't need and cap what it may use. All of this has been tried with the
image, `/tmp` is the only place .NET writes to:

```sh
docker run -d --restart unless-stopped --name homestead \
  -p 127.0.0.1:8080:8080 \
  --read-only --tmpfs /tmp:rw,noexec,nosuid,size=16m \
  --cap-drop ALL --security-opt no-new-privileges \
  --memory 1g --cpus 2 --pids-limit 256 \
  -e Limits__TrustForwardedHeaders=true \
  homestead-server
```

The image runs as a non-root user. Rebuild it now and then: the base image and .NET get security fixes.

## TLS: put a reverse proxy in front

`ws://` is unencrypted: fine on a LAN, not across the internet. The server doesn't do TLS itself; a reverse proxy does, and players then
connect with `wss://your.domain/ws`. With [Caddy](https://caddyserver.com) (certificates included):

```
your.domain {
    reverse_proxy 127.0.0.1:8080
}
```

Set `Limits__TrustForwardedHeaders=true` **only** behind such a proxy, with the server's port reachable by nothing else (`-p 127.0.0.1:…`
as above). Otherwise every player appears to come from the proxy's address and the per-address limit counts them all as one. Without a
proxy, leave it off: anybody could write any address into that header.

## Limits

Nobody logs in, so limits are all that stands between a flood and the other players' games. The defaults leave an honest client far
below every one of them. Set them as environment variables, e.g. `-e Limits__MaxGames=50`:

| Setting | Default | What it does |
|---|---|---|
| `Limits__MaxConnections` | 1000 | WebSocket connections at once |
| `Limits__MaxConnectionsPerAddress` | 16 | The same per IP address (per /64 network for IPv6). Raise it for a LAN party of more than 16 behind one address |
| `Limits__MaxGames` | 200 | Games at once, in the lobby or running |
| `Limits__HandshakeSeconds` | 10 | Time a new connection has to say hello |
| `Limits__IdleSeconds` | 1800 | A connection outside a running game that sends nothing is dropped after this |
| `Limits__StallSeconds` | 180 | Players a running game has waited this long for are dropped, the game goes on without them. A player reading the statistics holds up the game, so keep it generous |
| `Limits__AbandonedSeconds` | 1800 | A running game without a turn for this long is closed |
| `Limits__MessagesPerSecond`, `Limits__MessageBurst` | 100, 1000 | Messages per connection; lobby requests count as five |
| `Limits__BytesPerSecond`, `Limits__ByteBurst` | 128 KiB, 1 MiB | Bytes received per connection (the burst must stay above the largest message, 256 KiB) |
| `Limits__MaxQueuedBytes` | 2 MiB | Bytes waiting to go out to a connection that doesn't read |
| `Limits__SendTimeoutSeconds` | 20 | Time one send may take |

Whoever goes over a limit is dropped; the other players' games carry on. As a rough worst case, a server at the defaults needs about
3 GiB of memory under a deliberate attack on every limit at once (connections × queued bytes, games × held inputs). `--memory` keeps that
from reaching the host, at the price of a restart; lower `MaxConnections` and `MaxGames` to fit a small machine.

Turn timing: `Lockstep__TurnTicks` (2), `Lockstep__DelayTurns` (2), `Lockstep__CheckTurns` (10).

## What this doesn't cover

- **Plain TCP floods** (SYN floods, many connections that never finish a request) stop at Kestrel's connection limit, which then also keeps
  honest players out. That's a job for a firewall or the reverse proxy.
- **Griefing inside a game**: anyone can join an open lobby, there is no way yet to keep someone out or to set a password. A player in a game
  can slow it down within the limits, and with two players nobody can tell who lies about a desync. Play with people you know, or
  expect to host again.
- **Map hacks**: every computer simulates the whole world, so a modified client can see through the fog of war. Lockstep can't prevent that.
- **The server itself isn't trusted by the clients beyond the game**: a hostile server can't reach beyond the game on a player's computer
  (setup, paths, names and texts are all checked), but it can of course forge commands within a game or end it. Connect to servers you trust.
