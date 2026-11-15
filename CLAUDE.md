# Homestead

A private remake of "The Settlers III" in MonoGame/C#. Goals and roadmap: [docs/PROJEKT.md](docs/PROJEKT.md).
Communication in German; code and comments in English.
Never put game texts in code: `data/lang/de.json`, `en.json` and `pl.json` (nested, dot-separated keys, `{0}` placeholders), accessed via `Lang.T("key", args)`
in the client; names come from the data by ID (`buildings.<name>`, `goods.<GOOD>`, `peoples.<People>`, `soldiers.types.*`, `spells.<Kind>`, `events.texts.<id>`,
`ai.characters/traits.*`, `panel.tabs/menus.*`, `buildingStatus.*`). Missing English → German → key (console "Missing text"). `LanguageTests`:
same keys/placeholders, every literal key in the client, every data ID. Developer hints (`_message`, window titles, console) stay English, untranslated.
Messages returned by click/hover handlers are game texts too → `Lang.T`.

## Binding rules
- No original assets (GFX, SND, maps, music, exported PNGs) in the repo or the build. Inspection exports go to temporary directories only.
  Sole exception: README screenshots in `docs/screenshots/` (allowlisted in `.gitignore`).
- Never decompile, disassemble or analyse original EXEs/DLLs (including `GFX/Siedler3_18…dat`, a DLL). Data formats only.
- Never bypass copy protection/DRM.
- JSettlers code (MIT) may be incorporated → keep `THIRD_PARTY_NOTICES.md` up to date.
- Open source (MIT, `LICENSE.md`), clean room; rules for contributors: `CONTRIBUTING.md`, README in English.
- No "Siedler"/"Settlers" in type names (repo/README: "monoSettlers").

## Project layout
- `src/Homestead.Assets` – parsers for the original files (GFX, maps, `Sound/SoundFile`: `SND/Siedler3_00.dat`), without MonoGame. Format docs: `docs/formate/`.
- `src/Homestead.AssetViewer` – MonoGame viewer (DesktopGL), without the Content Pipeline.
- `src/Homestead.Server` – multiplayer server (ASP.NET Core, WebSocket, Docker), details: `docs/MULTIPLAYER.md`.
- `data/` – game data (no original assets), copied next to every program. `production.json`: our own balancing
  (times, recipes, stones per rock size, ore per amount step, regeneration, `depletion`: consumption of stones/mountains/fish in %, 0 = infinite;
  overridable in-game via the settings).
  Building data is generated from JSettlers (`tools/data-import/`) – never edit it by hand, use `data/buildings/overrides.json`
  instead (per people/building, `all` for all; e.g. Roman castle 6 infantry + 4 bowmen as in the original, mines only on mountains).
- `tests/Homestead.Simulation.Tests` – xUnit (`dotnet test`); tests with original maps pass unverified without a local installation.

### Details per area
Before working on a system, read the matching file and keep it up to date there (as done here so far: terse, values with their keys from `data/`):
- [docs/architektur/simulation.md](docs/architektur/simulation.md) – `src/Homestead.Simulation`, fixed tick rate (`World.TicksPerSecond`):
  `Map/` (terrain, fog of war), `Pathfinding/`, `World`/`Settler` (movement, land), `Trails`, `Buildings/` (construction, levelling, demolition),
  `Economy/` (logistics, production, housing, professions, trade, ships), `Military/` (combat, combat strength, war machines), `Magic/`,
  `Persistence/` (save games), `Events/`, `SoundCues`, `GameHistory`, `OriginalScenario`, `Map/Generation` (random maps).
- [docs/architektur/ki.md](docs/architektur/ki.md) – `Ai/`: `AiPlayer`, growth, military, campaigns, tactics, magic, characters/traits (`data/ai.json`).
- [docs/architektur/client.md](docs/architektur/client.md) – `src/Homestead.Client`: title screen, campaign, settings, start screen, side panel and
  menus, building panel, statistics, selection/commands, keys, options, sound, rendering.
- [docs/architektur/capture.md](docs/architektur/capture.md) – `--capture` and all `HOMESTEAD_CAPTURE_…` variables.
- [docs/architektur/werkzeuge.md](docs/architektur/werkzeuge.md) – Arena (AI against AI), tournament, AssetTool.
- [docs/MULTIPLAYER.md](docs/MULTIPLAYER.md) – lockstep multiplayer: decisions, status (determinism, command layer), remaining steps, rules for new code.

### Cross-cutting rules
- Sprites only via `SpriteLibrary` (see graphics conventions); texts only via `Lang.T` (see above).
- Captures and inspection exports go to temporary directories only; in captures, save only with a temporary `HOMESTEAD_SAVE_DIR`.
- Save games: `Snapshot` saves via reflection, new fields need no save code (details: simulation.md, `Persistence/`).
- Tests that are not about tools: `TestConfig.NoTools`.

## Map objects
`GameMap` stores the original object code per field (`MapObjectCodes`); walkability and graphics derive from it.
At runtime: tree stump 41 (disappears after `trees.stumpSeconds`), sapling 84, rock sizes 115→127 (rubble).
Ground cover (`GroundCover`, on grass only): farmland (as earth, with the original's transitions to grass), footpath (as sand, with sand-grass transitions).
Own crop codes: grain 200–207 (207 ripe), wine 208–210, rice 211–214, beehives 215–218 (image not found; for now the beekeeper placing it 41/291, `docs/formate/gfx.md`); dock 219.

## Graphics conventions
- Grid → pixels: `x*16 - y*8`, `y*9 - height*HeightStep` (rows offset by half), 6 neighbours. Original HeightStep 2, ours 1 (`ScreenProjection.HeightStep`).
- Terrain atlas: texture coordinates half a texel inside the cell, otherwise lines bleed in from neighbouring cells.
- Sprites only via `SpriteLibrary` (shared atlas pages, `SpriteAtlas`); no separate `Texture2D` per image (draw calls).
- Walk sequences: 72 frames = 6 directions of 12 each (SW, W, NW, NE, E, SE); frame 0 standing, 1–11 one cycle per grid step.
- Load GUI images from the original files at runtime via `GfxDirectory`/`SpriteLibrary` from the installation (like all original sprites).

## Installing the original
`HOMESTEAD_GAME_DIR` or `gameDirectory` in `~/.config/homestead/settings.json`; the first valid one wins (`GameInstallation.TryLocate`).
Checked on every start (`InstallationCheck`: `GFX` with `RequiredGfx`, `SND` with `siedler3_00/01.dat`, `Map` with maps); otherwise
`InstallationDialog` (separate process `--setup` – a second game window in the same process breaks SDL/GL on exit; without original graphics):
path field (typing, Ctrl+V via SDL), folder browser, search in the usual places (`Detect`), live validation, installation also above/below the
chosen folder (`FindRoot`); "Spiel starten" → `GameInstallation.Remember`. Capture: `HOMESTEAD_CAPTURE_DIALOG=<png>` (uses the valid one).
Local: `~/Games/gog/the-settlers-iii-ultimate-collection/drive_c/GOG Games/Settlers 3 Ultimate`.

## Versions and pipeline
- Version from git tags `vX.Y.Z` (MinVer in `Directory.Build.props`; without a tag `0.1.0-alpha.0.<n>`), bottom right on the title screen.
  Rules: `docs/VERSIONING.md` (below 1.0: PATCH for fixes/balancing/translations, MINOR for features or breaking save/settings changes).
- `.github/workflows/build.yml`: push to main/PR → build + tests, packages for linux-x64 (`tar.gz`), win-x64 (`zip`) and osx-arm64/osx-x64
  (on `macos-latest`: `monoSettlers.app`, program folder as `Contents/MacOS`, `packaging/macos/Info.plist`, ad-hoc signed only,
  `zip` via `ditto`; untested), self-contained, uploaded as artifacts; tag `v*` → additionally a GitHub release (`gh release create`, suffix → pre-release).

## Build and run
- `dotnet build Homestead.sln`
- Run programs with `dotnet <path>.dll` or `dotnet run --project …` – the native apphost cannot find .NET in VS Code's Flatpak environment.
