# monoSettlers

**A remake of "The Settlers III" in C# and MonoGame – with a strong AI, carriers that never get stuck, and mountains that don't run dry in no time.**

![Roman economy after one hour of play](docs/screenshots/economy.png)

The graphics, sounds and music come from your own copy of the original game and are read at runtime. Game logic, rendering, AI and tools are
written from scratch – the [clean-room way](#clean-room-how-this-game-is-made). The original is full of bugs and hasn't been maintained for years; monoSettlers plays like the game you
remember, minus the old annoyances.

## Combat

| Skirmish at the tower | Pitched battle |
|---|---|
| ![Small fight: attackers at the guard tower](docs/screenshots/skirmish.png) | ![Pitched battle between Romans and Asians](docs/screenshots/battle.png) |
| Enemies storm the starting tower; the garrison fights back, bowmen shoot from the tower. | Two armies of 30 clash in the middle of the village (staged with the capture option `BATTLE=big`). |

- **Swordsmen, pikemen and bowmen** in three ranks; combat strength depends on territory and gold
- **Conquer towers, large towers and castles**: break down the door, defeat the defenders in front of it, take the building
- **RTS-style controls**: drag to select, groups on Ctrl+1–9, forced moves with Ctrl, waypoints with Shift, patrols with Alt, Tab keeps
  only the wounded in the selection, orders by right-clicking the minimap
- **Hospitals, priests and all the spells** of the original per people: convert, samurai sword, freeze, scatter, summon goods and more
- **War machines**: catapult, ballista, gong and cannon, built in their own workshops
- **Divine upgrades** for soldiers, paid with mana from wine, mead, beer or rice liquor
- **Geologists, scouts and pioneers**: prospect for resources, steal goods, claim land without a tower
- **Battle sounds** from the original: swords, spears and bows each sound their own, Amazons have their own death cry

## Economy

- **Four peoples** (Romans, Egyptians, Asians, Amazons) with their own buildings, production chains, settlers and music
- **Sounds of work** in time with the animations: chopping, sawing, smithing, levelling, building, fishing
- **Logistics without traffic jams**: carriers step aside, swap places and re-plan; a watchdog resolves every deadlock
- **Trade by land and water**: donkey markets, ships, water trade and ferries
- **Trails**: well-used routes turn into footpaths you walk faster on
- **Levelling** with real terrain changes, construction sites with diggers and builders
- **Controls like the original**: distribution, transport order, storehouses specialised on one good, smith shares and orders, production statistics
- **Configurable balancing** in JSON: amounts in mountains, fish and stone up to "infinite", production times, recipes

## AI

Computer opponents use the same commands as a human player and each has its own character:

| Character | People | Style |
|---|---|---|
| Hothead | Egyptians | Early army, attacks with everything |
| Unyielding | Asians | Holds on long, gives up only below half strength |
| Legion | Romans | Early gold, attacks in waves, pincer moves |
| Skirmisher | Amazons | Small squads, nearby targets, campaigns only at 2:1 |

On top of that: guerilla strikes, defence by threat, gathered campaigns, retreats when outnumbered, targeted expansion towards missing resources.
The **Arena** (`tools/Homestead.Arena`) runs AI against AI without a window; a tournament script compares characters across many maps.

## Statistics

| Economy | Military |
|---|---|
| ![Statistics: economy](docs/screenshots/statistics-economy.png) | ![Statistics: military](docs/screenshots/statistics-military.png) |
| Land, settlers, buildings and goods of every player over the whole game. | Soldiers by type, combat strength, kills and losses, captured buildings. |

Press Esc or end the game, and the statistics screen opens on the blue marble of the original menus – with far more
than the original ever showed. Five pages (overview, economy, military, goods, society) follow every player through
the whole game: land and its share over time, settlers, buildings, goods produced per minute, stock, soldiers by type,
combat strength, kills and losses, captured buildings, mana and more. Hover over a chart to read every player's value
at that minute, pick any good to compare its production, and jump back into the game with one click.

## Maps

![Random map generator on the start screen](docs/screenshots/random-map.png)

- All single-player and multiplayer maps of the original
- **Random maps** for 2–6 players with mirrored or repeated symmetry, rivers, swamps, ore veins and treasures – fair for every start position
- **King of the Hill**: a random-map option that turns the middle into a hill worth fighting for – a hoard of gold and weapons, mountains
  full of gold, gems and coal, ringed by a moor with one pass per player
- **Hardcore mode** per player, **save games** in 8 slots, **fog of war**

## Clean room: how this game is made

Imagine you want to build a car that drives exactly like a famous old model, but you are **not** allowed to copy its
blueprints. What you *may* do is drive the old car, watch closely what it does, measure it from the outside – and then
design your own car from scratch so that it behaves the same. That is the "clean room" method, and it is how
monoSettlers is built.

**What we do:**
- **Play the original and watch.** How fast does a carrier walk? When does a soldier attack? How does a farm grow?
  We observe the behaviour and write our own code that behaves the same way.
- **Read the data files.** Graphics, maps and sounds are stored in files with a certain format. Figuring out such a
  file format is like learning to read a foreign alphabet – it tells us how to *open* the files, nothing more.
  The files themselves stay on your computer and come from your own copy of the game.
- **Use open-source work.** Some code and data tables come from the open-source project
  [JSettlers](https://github.com/paulwedeck/settlers-remake), which allows this under its MIT License.

**What we never do:**
- **We never look inside the original program.** No decompiling, no disassembling, no debugging of the original
  EXE or DLLs. We don't know – and don't want to know – how Blue Byte wrote their code.
- **We never copy original code**, not even from leaked or decompiled sources found on the internet.
- **We never ship original content.** No graphics, sounds, maps or music are in this repository, in a build or in a
  download. Without your own copy of the game, monoSettlers shows nothing.
- **We never bypass copy protection.**

In short: the *program* is 100 % our own, the *content* is 100 % yours – from the game you bought.

## Getting started

You need your own copy of "The Settlers III" – and .NET 9 only to build it yourself.

### Getting the original game

monoSettlers exists because of a great game. If you don't own it yet, please buy it – it's cheap and still runs today:

- **GOG:** [The Settlers III: Ultimate Collection](https://www.gog.com/en/game/the_settlers_3_ultimate_collection) – DRM-free,
  with the base game, Quest of the Amazons and the Mission CD; this is the version monoSettlers is tested with.
  After buying, download it from your [GOG library](https://www.gog.com/en/account) or with [GOG Galaxy](https://www.gog.com/galaxy).
- **Ubisoft:** [The Settlers III – History Edition](https://www.ubisoft.com/en-us/games/the-settlers-iii-history-edition),
  also part of [The Settlers History Collection](https://www.ubisoft.com/en-gb/game/the-settlers/history-collection)

On Linux the Windows installer works with Wine or Lutris; monoSettlers only needs the installed files.

### Download

Linux, Windows and macOS builds are on the [releases page](../../releases) (no installation: unpack and start
`Homestead.Client`, `Homestead.Client.exe` or `monoSettlers.app`). Versions follow [docs/VERSIONING.md](docs/VERSIONING.md).

The macOS app (`osx-arm64` for Apple Silicon, `osx-x64` for Intel Macs) is not notarized, so Gatekeeper refuses to open
it ("damaged" or "unidentified developer"). Remove the quarantine flag once after unpacking:
`xattr -dr com.apple.quarantine monoSettlers.app`. Nobody has tried the macOS build on a Mac yet – reports welcome.

### First start: point it to your installation

![Installation dialog on the first start](docs/screenshots/install-dialog.png)

monoSettlers ships without a single original graphic, sound or map – that is the [clean-room](#clean-room-how-this-game-is-made)
rule. So on the first start a dialog asks for the folder of your own installation (the one with `GFX`, `SND` and `Map` in it)
and offers the installations it finds in the usual places (GOG, Steam, Wine prefixes, Lutris). The files are only read
from there, never copied. The folder is stored as `gameDirectory` in `~/.config/homestead/settings.json`
(Windows: `%APPDATA%\homestead`) and checked on every start; if it fails, the dialog comes back. `HOMESTEAD_GAME_DIR` overrides it.

### Build and run

```sh
dotnet build Homestead.sln -c Release
dotnet src/Homestead.Client/bin/Release/net9.0/Homestead.Client.dll
```

The campaign videos (Indeo 5) are decoded by [ffmpeg](https://ffmpeg.org), if it is installed (on the `PATH`, next to
the program or set with `HOMESTEAD_FFMPEG`); without it they are skipped.

Tests: `dotnet test`.

## Project layout

| Project | Contents |
|---|---|
| `src/Homestead.Assets` | Parsers for the original files (GFX, maps), no MonoGame dependency |
| `src/Homestead.Simulation` | Game logic at a fixed tick rate: economy, combat, AI, save games – testable without graphics |
| `src/Homestead.Client` | The game (MonoGame DesktopGL) |
| `src/Homestead.AssetViewer` | Viewer for the original graphics |
| `tools/` | Arena, asset tool, AI tournament, data import |
| `data/` | Game data and balancing as JSON |

Internal project name and namespaces: *Homestead*.

## Known issues

monoSettlers is fully playable, but not perfect. What is known so far:

- **Performance on large maps with many units**: late games with hundreds of settlers and soldiers can still slow down
- **Craftsmen at work**: they don't yet walk the exact routes of the original inside and around their buildings, and most
  work animations (e.g. the smith forging) are missing – these differ for every building and every people
- **Graphics**: now and then a shadow is missing or a wrong image is used
- **Spells**: most of the spell sounds and animations are not triggered yet and some might need some finetuning
- **Guessed values**: some numbers are not yet measured against the original (e.g. war machine range and speed, donkey loads)
- **AI**: computer opponents don't trade, sail or use war machines yet
- **AI chat**: talking to the computer opponents through Claude is experimental and only configured in `settings.json`
  (section `aiChat`: `apiKey`, `model`, `effort`), not in the settings screen
- **macOS**: the builds have never been tried on a real Mac

Measurements from the original, savegames showing odd behaviour and pull requests are very welcome.

## Maybe-Goal
- Multiplayer

## Thanks

- **[JSettlers / settlers-remake](https://github.com/paulwedeck/settlers-remake)** – the open-source Settlers III remake in Java.
  Its readers for the original file formats, its building and animation tables and its many details about the game were
  an enormous help; parts of its code and data are used here under the MIT License (see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)).
- **[Blue Byte](https://bluebyte.ubisoft.com/en/)** for creating The Settlers III in 1998.
- **[Siedler 3 Wiki](https://wiki.siedler3.net)** and **[StrategyWiki](https://strategywiki.org/wiki/The_Settlers_III)** – their collected
  knowledge about buildings, goods, spells and game mechanics helped a lot to get the details right.
- **[MonoGame](https://monogame.net/)** and **[FontStashSharp](https://github.com/FontStashSharp/FontStashSharp)**.

## Contributing

Contributions are welcome – please read [CONTRIBUTING.md](CONTRIBUTING.md) first. In short: stay legal, no original
assets, never touch the original program.

## License and legal

- monoSettlers is open source under the [MIT License](LICENSE.md). This includes code and data taken from JSettlers (MIT);
  which parts, and the libraries included in the release packages, are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
- The license covers the code and data **in this repository** only, not "The Settlers III" or any of its content – graphics,
  sounds, music, maps, texts, the game program itself. Those belong to Ubisoft / Blue Byte.
- This repository contains **no original assets** (graphics, sound, maps). The game needs your own, legally acquired copy.
  The screenshots above show original graphics from such a copy; they only illustrate the project and are not licensed under the MIT License.
- "The Settlers" is a trademark of Ubisoft; the original game was made by Blue Byte. monoSettlers is a non-commercial fan project and not affiliated with or endorsed by them.
