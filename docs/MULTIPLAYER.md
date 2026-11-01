# Multiplayer (lockstep)

As of 2026-11-01, branch `feature/lockstep` (up to date with `main`, not merged into it yet).

## Decisions

- **Lockstep**: every computer simulates the whole world itself; only the players' commands go over the network.
- **Server** as a simple relay (collects commands per turn, hands them out to everyone), as a Docker image for self-hosting,
  maybe a public server as well. Keep an eye on costs (lockstep needs hardly any bandwidth).
- **Map** via the seed/hash of the random map (a name like `random:4:mirror:11`, generation is deterministic);
  original maps: hash of the map file (everyone has their own copy).
- **Settings** (resource consumption `depletion` and the other game values from `GameSettings`) come from the server, not from the
  individual players' `settings.json`.
- **Computer opponents stay**: they run deterministically on every computer, no network cost (mixed games too, e.g. 2 humans vs. 2 AIs).
- **Off in multiplayer for now**: own AI (key I), AI chat via Claude (API key, network replies), fast-forward (F12), cheats.
- **Pause stays** (players want it): comes later as a shared pause for everyone, not locked.

## Done

### Determinism (commits 21ec216, e10f25d)
- Simulation audit: randomness only via seeds (`World.Seed`), no clock, no threads, no `GetHashCode` in the logic,
  no culture-dependent sorting, no SIMD/FMA. Basic arithmetic and `Math.Sqrt` are the same everywhere per IEEE.
- The only risk was `Math.Sin/Cos/Atan2` (the platform's C library) in the random map generator and in `PincerTrait`:
  replaced by `DeterministicMath` (basic arithmetic only, ~1e-12 accurate). New trigonometric functions in the simulation only through it.
- `WorldChecksum.Of(world)`: checksum over tick, settlers, buildings, piles, map, mana – for desync detection.
- `DeterminismTests`: AI game on `random:4:mirror:11`, 25 min, a checksum per minute. The build job `determinism` runs it on
  Linux x64, Windows x64 and macOS ARM, `determinism-compare` compares. **Result: all three bit-identical.**
  On branches the build only runs manually (Actions → Build → Run workflow).

### Command layer (commit 1604922)
- The client changes the world only through commands: `src/Homestead.Simulation/Commands/` – `GameCommand` records (pure data:
  player, IDs of settlers/buildings/ships, points, enums, numbers; building type as `BuildingDefinition`, unique by people + name;
  spells as a `Spell` record). Each command only affects its own player's things.
- `World.Commands` (`CommandQueue`): `Issue` assigns `Sequence`; `World.Step` first applies all pending ones in that order,
  results in `Applied` until the next step. `ApplyWhilePaused` only for the paused single-player game. Pending commands are in the savegame.
- Client: `MapGame.Issue(command, then)` (result → message, sound, selection; `TakeCommandResults` after every `Step`), menus, building panel
  and AI chat via the delegate `IssueCommand` (`IssueReported`).
- Exceptions (directly on the world, off in multiplayer or without influence): game setup, capture/demo code, own AI (key I);
  the cheat menu already goes through `Cheat` commands; the statistics sample when opening (`GameHistory.Sample`) and calendar events
  (`Events.Announce`) – the game logic reads neither.
- Tests: `CommandTests` (order and results, ownership, savegame).

### Local player
- `MapGame._local` instead of always player 0: commands, selection (settlers, ships, box, double-click), building panel, menus, fog of war (`Vision`),
  minimap, control groups (`ControlGroups(player)`), event buttons, spells, AI chat (`AiChat.Player`), start screen, capture scenarios
  (`DemoEconomy … player:`). All other players of a free game are AI; missions always `MissionGoals.Player` (0).
- Victory/defeat per player: `IGameGoals.OutcomeOf(player)`, `DecidedAt(player)`, `Decide(player, …)` (cheat only for one's own).
  Older savegames: the outcome is determined anew within the next second (the defeated are saved).
- Savegame: client value `player` (older ones: 0). To try it out: `--player <n>` (1–8) for free games.
- In multiplayer `_local` comes from the server (`started`: own player).

### Shared game setup
- `GameSetup` (simulation, pure data, `ToJson`/`FromJson`, enums as text): map (`MapChoice`), `Seed`, players (`PlayerSetup`: people,
  color as palette index, team, hardcore, `Human`, AI character; `null` = drawn), start positions (the player for each map player, `null` = drawn),
  game values (`GameSettings`; `null` = `data/`). Same setup → same world on every computer.
- `MapChoice`: random map (settings + seed) or original map as a path relative to the map folder (`/`) + SHA-256; `Locate` looks there
  first, then at every `.map` with the hash (everyone has their own installation), else `FileNotFoundException`.
- `GameSetup.Start(map)`: draws peoples, start positions and AI characters from the seed – each draw with its own random numbers (`RandomFor(Draw)`:
  peoples, start positions, colors, characters), so one doesn't shift another; world seed = `Seed`; teams, hardcore, `ConquestGoals`,
  an AI for every non-human player. The client draws colors with `RandomFor(Draw.Colors)` (`PlayerColors.Assign`).
- Client: every game through a setup (`MapGame.SoloSetup` → `StartGame`): start screen, `--map`, PgUp/PgDn, missions (then `MissionGoals`).
  Alone: `Seed` random (captures 0), human = local player, game values from the settings. In multiplayer the server provides the setup.
- Tests: `GameSetupTests` (same world after JSON too, setup, separate draws, map by hash).

### Commands for a target turn (`Lockstep/`)
- Time in turns (`LockstepOptions.TurnTicks`, default 2 ticks = 100 ms). What a player orders during turn T (`LockstepSession.Issue`)
  goes to the server at the start of turn T+1 as a `TurnInput` for turn T+1+`DelayTurns` (default 2) – every turn, empty ones too. A command
  thus takes effect after 2–3 turns (≈ 100–150 ms + frame), on every computer at the same tick (the turn's first tick).
- Server (`TurnRelay`, no game logic, reads the commands only as JSON): collects the inputs of all human players per turn, hands
  complete turns out in order as a `TurnBundle` to everyone (players ascending, per player in send order). Drops commands
  with another `player`, second inputs, inputs for turns already handed out, anything unreadable.
- `LockstepSession.TryStep`: at the start of a turn send the own input, then the turn's bundle into `World.Commands.Enqueue` – if it's missing,
  the game waits (`Waiting`). The own commands, too, take effect only through the bundle (decoded like everyone's). Turns before `FirstTurn` are empty.
- Wire format `CommandCodec` (JSON): command type in `$type` (all `GameCommand` types via reflection), camelCase, enums as text,
  `sequence` (per player, so the client finds its command's result again), building type as people + name → the game's definition,
  spells only by kind.
- `LocalRelay`: a relay in the same process (tests, later a game without network). The client uses the session in multiplayer games
  (over `ServerConnection.Link`, see below) and matches results by player + number.
- Tests `LockstepTests`: every command type the same there and back (new commands without a matching sample stand out), relay rules, two computers
  over a network with 0–3 rounds of delay (overtaking messages, different speeds) → same checksum every second,
  same chat ticks; waiting without a server. Counter-check: a construction site bypassing the session → checksums differ.

### Version and data check
- `GameFingerprint` (`Lockstep/`): protocol (`CurrentProtocol`, bump with every change to the wire format), program version
  (MinVer with commit, e.g. `0.5.1-alpha.0.8+98f7a9e…` – so development builds only match on the same commit) and SHA-256 per file
  in `data/`, except what only the client reads (`ClientOnly`: `lang/`, `ui.json`, `sound.json`, `settlers.json`, `ai-chat.json`,
  `campaign.json`) – so new data files count automatically. CRLF/LF and BOM don't count (Windows checkout ≠ Linux otherwise).
- `Differences(other)` names what differs (protocol, version, `data/… differs/missing`); `Data` = one hash over everything (for the lobby).
- `GameSetup.Fingerprint`: the server fills in the game's; `GameSetup.Start` throws `IncompatibleGameException` with the differences
  instead of computing a different game. Alone (`null`) no check.
- The server checks it: the client sends its fingerprint with `hello`, `join` compares it with the game's and refuses with the
  differences (see Server).
- Tests `GameFingerprintTests`.

### Desync detection
- `WorldChecksum.Parts(world)` → `WorldChecksums` (tick, settlers, buildings, piles, map, mana, each its own checksum; `Total`, `Of` = Total;
  `Differences` names the differing parts). Hash: FNV-1a on whole 64-bit words instead of bytes, mixed afterwards, map one step per field –
  largest map (512², 6 players) in Release 0.57 ms instead of 7.5 ms (one simulation step there ≈ 1.5 ms).
- Every `LockstepOptions.CheckTurns` turns (default 10 = 1 s) the input sent at the start of turn T carries `StateCheck(T, checksums)` –
  the state at the start of the turn before its commands, the same moment on every computer. The server puts the checks into the bundle (`checks`,
  per player) and compares them itself too (`TurnRelay.DesyncedTurns`, for its log).
- `LockstepSession` compares the others' with its own on receipt (`ChecksCompared`); the first difference → `Desync`
  (`DesyncReport`: turn, tick, for each differing player the parts, e.g. `piles`), after that the game stops for good (`TryStep` false).
  A desync is noticed at the latest `CheckTurns` + `DelayTurns` turns after it arises (default ≈ 1.2 s).
- With three or more players the report shows who differs from everyone else (majority). The client shows a banner (see below). Later:
  saving a savegame at the desync for comparison, then taking over the host's state.
- Tests: `LockstepTests` (laggy network: compared every second, no difference; deliberately altered world → both computers report turn 50,
  part `piles`, and stop).

### Locked in multiplayer
- `World.Multiplayer` (more than one human, set by `GameSetup.Start`, saved): `Cheat` does nothing (result null), neither does `AiReply`
  (false) – the same on every computer, so also against forged commands.
- Client with `Multiplayer`: no F12, no AI chat (neither questions nor replies), no chat commands (info `chat.noCheatsMultiplayer`),
  cheat menu closed, no keys I (own AI – would only run on this computer: desync), L (the opponent AIs' decisions), PgUp/PgDn
  (another map locally).
- Pause stays on purpose (see decisions), but is not yet available in multiplayer games (info message): a pause of one computer alone would
  make the others wait for its bundle (no desync, but a standstill) – a shared pause is still open. Note: `ApplyWhilePaused` must not
  exist in multiplayer (applies commands immediately → desync).
- Test: `GameSetupTests.With_more_than_one_human_cheats_and_the_ai_chat_do_nothing`.

## Open – order

### 1. Client (before the server)
1. ~~Local player instead of always player 0~~ – done (see above).
2. ~~Shared game setup~~ – done (see above); the server hands it out (`started`).
3. ~~Commands for a target tick~~ – done as a target turn (see above).
4. ~~Version and data check~~ – done (see above); the server compares on `hello` and `join`.
5. ~~Desync detection~~ – done (see above); banner in the client; taking over the host's state later.
6. ~~Locks in multiplayer~~ – done (see above). Open for later: shared pause, saving for everyone (via the server).

### 2. Server
Done (first stage, C# in the same repo – shares `TurnRelay`, `GameSetup`, `GameFingerprint` with the client):
- `src/Homestead.Server` (ASP.NET Core, WebSocket `/ws`, status line `/`): `Hub` (one lock for everything, a send queue per connection),
  `Connection`, `Game`. Messages: `ServerMessages` (simulation, used by both sides), JSON with `type`, at most 256 KB.
  - `hello` (name, fingerprint; another protocol → `refused`, connection closed) → `welcome`.
  - `create` (name, `GameSetup`: its human players are the seats; the host takes the first, its fingerprint becomes the game's)
    → `lobby` (seats with names, host, own seat) to everyone in the game; `list` → `games` (open games, seats, data hash);
    `join` (the fingerprint must match, else `refused` with the differences; "full", "doesn't exist"); `leave` (seat free, choice back
    to free; host → `closed`); `choose` (own people and color into the setup, `null` = drawn; a taken color → `error`). `lobby` carries per
    seat name, people, color, the AI players (`computers`: player, people, color, character) and all taken colors (the AIs' fixed ones too).
  - `start` (host only, all seats taken) → `started` to each: setup with fingerprint, own player, `LockstepOptions`
    (server configuration `Lockstep__TurnTicks`/`DelayTurns`/`CheckTurns`). Then `input` → `TurnRelay` → `bundle` to everyone.
  - Leaving a running game → `left` to the others (who then wait). Unreadable fields → `error`, the connection stays.
- `TurnRelay` with flooding limits: turns at most `maxAhead` (100) beyond the next one, at most `maxCommands` (200) per input
  (`Refused`, in the log); desyncs into the log.
- Client side: `ServerConnection` (Simulation/Lockstep, `ClientWebSocket`, read/write task, `Receive` for polling from the
  game loop, `WaitForAsync` for tools/tests, `Link` as `ILockstepLink` for the `LockstepSession`).
- Docker: `src/Homestead.Server/Dockerfile` (context = repo root because of MinVer/.git, else `--build-arg VERSION=…`; aspnet image,
  non-root, port 8080, ≈ 230 MB): `docker build -f src/Homestead.Server/Dockerfile -t homestead-server .`, `docker run -p 8080:8080 homestead-server`.
- Tests `ServerTests` (real server on a free port): lobby → start → two worlds for 40 s over WebSockets, same checksum/chat, `left`;
  protocol refused; joining with other data/another version refused, start only when full, host gone → `closed`; broken message; status.

### Multiplayer in the client
- Title → "Mehrspieler" (`MultiplayerScreen`, the mission list's background): name + server address (`ws://…/ws`, remembered in
  `settings.json`, section `multiplayer`, `MultiplayerSettings`; Tab/click switches the field, Ctrl+V) → connect (refusal with
  reasons) → open games (a click joins, "Refresh", "Open game") → lobby (seats, host; "Start" only for the host and when full,
  "Leave"). Esc goes back one step.
- "Open game": start screen in multiplayer mode (`StartScreen(multiplayer: true)`): all players humans at first, a click on a
  player's name toggles human (a seat for a fellow player) / AI, one's own is player 1. Other humans' seats: people "Free choice", color "?",
  not changeable (the map's presets still apply); the "Open" button → `GameSetup` (random seed) to the server.
- Lobby: per seat name, people, color, the AI players in between ("AI · character"), by player number; on one's own seat a click cycles the people (random → the peoples) or the color ("?" → free
  colors) (`ServerConnection.Choose`).
- `started` → open the map (if missing: message, connection closed), colors from `RandomFor(Colors)`, `StartGame`, then `LockstepSession`
  via `ServerConnection.Link`. `MapGame`: steps only through `TryStep`; while waiting no time is accumulated (else a sprint, then waiting
  again); whoever is behind the fastest one (`LockstepSession.Behind` from `TurnBundle.Newest`, the furthest turn for which an input
  exists; > 1 turn) runs at `CatchUp` (1.5×) until caught up. Build spots/`CanPlace` for the local player (mind the simulation's default `player = 0`!);
  commands via `session.Issue`; banners "Waiting for players" (> 1 s), "Out of sync" (desync, console with the parts); info: player gone,
  connection lost. Pause (P/Esc) still off in multiplayer (info), statistics (Esc) halts the own computer → the others wait.
  Quitting the game/title/new game close the connection (`EndNetworkGame`).
- Capture: `HOMESTEAD_CAPTURE_START=multiplayer` (picture of connecting), `HOMESTEAD_CAPTURE_SERVER=ws://…/ws` (connects, opens a
  game with 1 human + 1 AI on a random map, starts and plays it through the server; without the demo economy).

Open:
- Client: shared pause, saving for everyone, desync savegame, chat in the lobby, statistics without halting.
- A player gone in a running game: the seat to an AI (on all computers at the same turn) instead of waiting; reconnecting.
- A CI job that builds the image (and pushes it to ghcr.io on tags); maybe Native AOT for a smaller image.
- Security hardening, see below.

### Security hardening
The server is open to anyone who can reach it (no accounts), and every client trusts what comes from the others through the server. Goal:
no client can crash, stall or take over the server or other players' games, and a malicious server can't harm a client's computer.
Lockstep can't hide the game state: every computer simulates the whole world, so a map hack (ignoring the fog of war) can't be prevented –
only cheats that change the world, and those are prevented by the simulation itself (commands only for their own player).

- **Flooding**: `TurnRelay` keeps every input whose turn hasn't been handed out yet – a malicious client could fill the memory with inputs
  for turns far in the future (or very many or very large commands). Done: turns at most `maxAhead` ahead, at most `maxCommands` per input,
  messages at most 256 KB. Open: a rate limit per connection (messages and bytes per second); whoever violates the limits gets kicked.
- **Connections and games**: upper limits for connections in total and per IP address, games in total and open games per connection;
  a timeout for the handshake (connected without `hello`) and for idle lobby connections. Kestrel's own limits (request headers,
  slow clients) set explicitly.
- **Validating the setup on the server**: `create` takes a whole `GameSetup` from the host – limits for players (≤ 8), random map size and
  options, `GameSettings` within sensible ranges (no 0 or huge values), known peoples/characters, colors 0–63; refused otherwise. Otherwise one
  host could make every joining computer run out of memory or crash while building the world.
- **Validating the setup on the client** (against a malicious server or host): the same checks before `GameSetup.Start`; `MapChoice.MapFile`
  only inside the map directory (no `..`, no absolute path – today `Locate` combines it unchecked and hashes whatever file it points to).
- **Commands must never crash**: every `GameCommand` checks ownership already; also check ranges (coordinates on the map, existing IDs,
  amounts ≥ 0, text lengths for chat) and turn invalid ones into a deterministic "refused" result. An exception while applying a command
  would stop the game on every computer at once – a cheap way to end any game. A fuzz test (`LockstepTests`): random values for every command
  type, the world must survive and stay the same on two computers.
- **Names and texts**: length limits are done (name 32, game name 40); also strip control characters and line breaks, which could garble
  the lobby or the log; check how the client draws glyphs its font doesn't have.
- **Fake desyncs**: a client can send wrong checksums and so end a game for everyone (griefing). With three or more players the majority shows
  who is out of line; then only that player should be dropped, not the game.
- **Fingerprint**: self-reported, so only a compatibility check, not protection – a modified client can claim anything. Nothing on the server
  may rely on it beyond refusing honest mismatches.
- **Transport**: plain `ws://` is fine on a LAN; a public server only behind TLS (`wss://`, e.g. a reverse proxy like Caddy or Traefik), described
  in the server's README. No cookies or credentials, so cross-site WebSocket hijacking has nothing to steal; checking `Origin` isn't needed.
- **Container**: non-root is done; additionally recommend `--read-only`, `--cap-drop ALL`, `--security-opt no-new-privileges`, memory/CPU limits
  (`--memory`, `--cpus`); keep the base image current (Dependabot for the Dockerfile and NuGet).
- **Logging**: structured logging only (no names spliced into the message text), no chat contents or whole messages in the log.

### 3. Later
- Dropouts/reconnecting, spectators, rankings, accounts (mind data protection).

## Extending the determinism test (recommended, in parallel)
So far the test covers only an AI game: economy, construction, combat, AI tactics. Not covered: **player commands** (the command layer
is new) and what rarely happens in 25 min – **ships, trade, war machines, most spells**. Suggestion: a second scenario
that plays a fixed sequence of commands (via `World.Commands`) and triggers these systems, in the same build job across all platforms.

## Rules for new code (simulation)
- Randomness only `new Random(… + World.Seed)`; no clock, no threads, no `GetHashCode`/`HashCode` in the logic, sorting only by
  numbers or ordinal.
- Trigonometric functions and the like only through `DeterministicMath` (add there when needed, basic arithmetic only).
- Everything a player triggers as a `GameCommand` (a new record in `Commands/`), never directly from the client.
  Fields only pure data that survive JSON losslessly (else add a sample value in `LockstepTests.Sample` and check it);
  a command may only change its own `Player`'s things and read nothing from the computer (clock, settings, selection).
