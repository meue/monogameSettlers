# Contributing to monoSettlers

Thanks for wanting to help! monoSettlers is a **fan project**: built by fans of "The Settlers III", out of love for
the game, without any commercial interest. To keep it alive and on the right side of the law, every contribution has
to follow a few rules. They are not negotiable – a pull request that breaks one of them will be closed, however good
the code is.

## The ground rules

### 1. Stay legal – always
- Only do what is clearly allowed. If you are unsure whether something is legal, **ask first** (open an issue) instead of doing it.
- Only use your **own, legally acquired copy** of the original game (e.g. from GOG or Ubisoft).
- **No cracks, no copy-protection or DRM workarounds**, no "abandonware" downloads, no pirated files – not even for testing.

### 2. No original assets in the repository
Nothing from the original game goes into this repository or into any build, release or download:
- no graphics, sounds, music, maps, videos or texts of the original,
- **nothing exported from them either** – no PNGs of sprites, no converted sound files, no copied map files.

The game reads these files at runtime from the player's own installation; that is the whole point. If you need to
export something to look at it, do it in a temporary folder outside the repository and delete it afterwards.
(The only exception are the few README screenshots in `docs/screenshots/`, maintained by the project owner.)

### 3. Clean room: never look inside the original program
We rebuild the game the clean-room way (see the [README](README.md#clean-room-how-this-game-is-made)). That means:
- **Never decompile, disassemble, debug or otherwise analyse the original program** – the EXE, the DLLs, and also
  `GFX/Siedler3_18…dat`, which is really a DLL in disguise.
- **Never copy or paraphrase code** from the original or from leaked/decompiled sources.
- Allowed: analysing the **data files** (graphics, sound and map formats), playing the original and **observing**
  how it behaves, your own knowledge of the game, and open-source projects with a compatible license.

If you have ever worked with decompiled code of the original, please tell us before contributing to the same area.

### 4. Respect other people's work
- Code from other open-source projects may only be used if its license allows it (e.g. MIT, like
  [JSettlers](https://github.com/paulwedeck/settlers-remake)).
- Keep the license notice and add an entry to [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md): what was taken, from where, into which file.
- By contributing, you agree that your contribution is published under the project's [MIT License](LICENSE.md).

### 5. It stays a fan project
- No selling, no ads, no paid features.
- Don't present monoSettlers as an official product, and don't use Ubisoft or Blue Byte logos.
- No new type or namespace names with "Settlers" in them; the code base is called *Homestead*.

## How to contribute

1. Open an issue first for anything bigger than a small fix, so we can agree on the approach.
2. Fork, create a branch, make your change.
3. Run `dotnet build Homestead.sln` and `dotnet test` – everything must stay green.
4. Open a pull request and describe what you changed and how you tested it.

### Conventions
- Code and code comments in **English**.
- Game logic lives in `Homestead.Simulation` and must not depend on MonoGame; it runs at a fixed tick rate and must stay
  **deterministic** (see below).
- Balancing values belong in the JSON files under `data/`, not in code.
- Generated data (`data/buildings/*.json`, `data/settlers.json`) is never edited by hand – use `data/buildings/overrides.json`
  or the import scripts in `tools/data-import/`.
- Match the style of the surrounding code; add tests for new behaviour.

### The simulation must be 100 % deterministic
Multiplayer is lockstep: only the players' commands go over the network, and every computer simulates the whole world
by itself. That only works if the same commands give **bit for bit the same world** on every computer – on Linux,
Windows and macOS alike. One differing number, and the games drift apart (a desync); the game stops.

So in `Homestead.Simulation`:
- **Randomness is fine, but only seeded from the world**: `new Random(<your own constant> + world.Seed)`, kept in the
  system that uses it. A random critical hit is no problem done that way – the same "random" hit on every computer.
  Never `Random.Shared`, `new Random()` without a seed, `Guid.NewGuid()` or anything else that differs per computer.
- **No clock** (`DateTime.Now`, `Stopwatch`, frame times), **no threads or parallel loops**, nothing from the computer
  it runs on (settings, screen, selection, culture) – the world advances only by ticks and commands.
- **No `GetHashCode`/`HashCode`** in game logic and no iteration order that depends on it; sort only by numbers or
  ordinal strings.
- **No `Math.Sin`, `Cos`, `Atan2` & co.**: they come from each platform's C library and may differ in the last bit.
  Use `DeterministicMath` (add a function there if one is missing). Plain arithmetic and `Math.Sqrt` are fine.
- **Everything a player does is a `GameCommand`** (`Commands/`), never a direct change of the world from the client.

`DeterminismTests` plays a long AI game and compares checksums; the build runs it on all three platforms. It can't
cover every system, so think about it when you write new logic. Details: [docs/MULTIPLAYER.md](docs/MULTIPLAYER.md).

## Be nice

Treat everybody with respect. We are all here because we like the same old game.
