# Homestead – remake of "The Settlers III" (working title)

## The project
A private remake of "The Settlers III" (Blue Byte, 1998) that uses the original graphics and sounds.
Motivation: the original is buggy and has not been developed further for a long time. I own the game
several times over (original, Collector's Edition, GOG, among others).

## Framework conditions
- **Engine:** MonoGame (C#, .NET 9)
- **Single player only**, no multiplayer
- **Open source** (MIT, [LICENSE.md](../LICENSE.md)), a non-commercial fan project; rules for contributors: [CONTRIBUTING.md](../CONTRIBUTING.md)
- **Main goals compared to the original:**
  1. Strong AI
  2. No stuck carriers (especially with the AI opponent)
  3. Mountains that do not run out so quickly

## Legal guardrails (binding)
- **No original assets in the repo, in the build or in downloads.** The engine reads GFX/SND at runtime from
  the local installation (path configurable, see [README](../README.md)).
- **No decompiling or disassembling the original EXEs/DLLs**, no taking over original code.
  The game logic is rewritten from observation and knowledge of the game (clean-room approach).
- **Analysing file formats is okay** (GFX, SND, maps): reverse engineering of data, not of the program.
- **Do not bypass copy protection/DRM.**
- Code from JSettlers (MIT license) may serve as a reference or be incorporated; then keep the
  MIT license notice in `THIRD_PARTY_NOTICES.md`.
- The name "monoSettlers" (like JSettlers, Settlers United and other fan projects); no official logos, do not
  appear as an official product.
  Type and namespace names without "Siedler"/"Settlers" (code: *Homestead*).

## Reference: JSettlers
- Active fork: https://github.com/paulwedeck/settlers-remake (Java, MIT, alpha status)
- Reads GFX/SND from an original installation and contains parsers for the file formats –
  use it as a reference **for that**.
- Unconvincing as a game. Positive: the AI behaves noticeably differently from the original.

## Design approaches for the main goals
- **Carrier logistics:** carriers replan their route when it is blocked. A watchdog detects carriers
  that have not moved for too long and resolves the case (replan, put down the good or similar).
  Applies to all players – stable logistics is also a prerequisite for a strong AI.
- **Raw materials in the mountains:** the deposit per field is a setting (higher than in the original),
  optionally with slow regeneration.
- **AI:** plans goods chains and reacts to bottlenecks instead of reeling off fixed build sequences.
  Builds on a stable economy simulation and therefore comes last.

## Architecture principles
- Game simulation strictly separated from rendering, fixed tick rate → AI and logistics can be
  tested and debugged without graphics.
- Balancing values (raw material amounts, production times etc.) data-driven in configuration files,
  not in code.
- Asset access encapsulated behind its own layer (`Homestead.Assets`: parsers → own types,
  no MonoGame dependency).

## Roadmap
1. **Asset viewer:** parse GFX .dat files and display them in MonoGame. ✓ (loose ends open)
   - [x] Installation found: GOG Ultimate Collection (Lutris)
   - [x] GFX format analysed, cross-checked with JSettlers ([formate/gfx.md](formate/gfx.md))
   - [x] C# parser: all 151,088 images of all GFX files decode without errors
   - [x] MonoGame viewer for browsing
   - [ ] Understand the animation tables (type `0x21702`)
   - [ ] Mapping sequence number → meaning (which settler, which building …)
   - [x] Torso/shadow correctly assigned (soldiers complete)
   - [ ] SND format
   - [x] Mini PoC movement: a carrier walks one hexagon on the grid, all 6 directions,
     fixed tick rate (20/s) with interpolation (`Homestead.Simulation` + `Homestead.Client`)
2. **Map & terrain:** render the landscape, camera, map format. ← *in progress*
   - [x] Map format read, all 198 maps load ([formate/map.md](formate/map.md))
   - [x] Terrain with heights, textures and transitions, camera (scrolling, zoom), map switching
   - [x] Map objects: trees (animated), stones, plants, wrecks …, depth-sorted with figures
   - [x] Objects visually cross-checked against the original
   - [ ] Settlers/buildings/goods from the map
   - [x] Unknown terrain values 66–71 clarified (only on the map "Vulkanauge"); 112 open
   - [x] Walkability in the simulation (rules, cross-checked against map data), debug overlay (key B)
   - [x] A* pathfinding with regions for unreachable targets, test project (reference comparison, original maps)
   - [x] Pathfinding in the client: click a figure/click a target, the figure walks and follows the terrain heights
   - [x] Several figures at once: one field per figure, swapping in oncoming traffic, stepping aside,
     detour after a short wait, watchdog lets them pass through after 2 s (never stuck; stress tests)
   - [ ] Lower the pass-through rate in very dense traffic (tuning, once real carriers are walking)
3. **Buildings & carriers:** ← *in progress*
   - [x] Building data of all peoples as JSON (`data/buildings`, converted from JSettlers)
   - [x] Load and display buildings, goods piles and settlers from maps
   - [x] Logistics: requests/offers, carrier jobs with reservation, watchdog (putting down goods),
     the storehouse takes everything, production demand before the storehouse; tests incl. conservation of goods
   - [x] Building: construction sites with placement check, diggers level the ground (terrain changes live),
     builders install the delivered materials, construction progress visible; placement mode in the client (P)
   - [x] The storehouse as a source (goods from the storehouse to construction sites and buildings); order construction site → production → storehouse
   - [x] Further stuck cases found and fixed (pushing each other aside at piles, circling around crowds)
   - [x] Idle carriers spread out (leave the storehouse yard, building surroundings and crowds; remain instantly available)
   - [x] Diggers/builders emerge from carriers + tool (shovel/hammer)
   - [x] Click a building: state (waiting for a good/tool, nothing in range, …) and work area
     (original marker, four circles); little flags on occupied buildings
   - [ ] Move the work area
   - [x] Residences release carriers (small 10, medium 30, large 100; one per second, configurable)
   - [x] Starting equipment on multiplayer maps: guard tower, goods, settlers per the map's start level, the same for all
     players (`data/start.json`); single-player maps bring their own buildings
   - [ ] Animate picking up and putting down; paths (speed up walking)
   - [x] Construction sites: the door and pile spots must be free (otherwise never suppliable); the storehouse keeps one good together
   - [x] The storehouse takes any number of good types (opens more pile spots around it when needed)
   - [ ] Footprints: the JSettlers data are somewhat more generous than the walkability of the original maps
4. **Economy:** goods chains, production, raw materials. ← *started*
   - [x] Production balancing as a file (`data/production.json`, our own values)
   - [x] Workshops (sawmill, mill, bakery, butcher, smelters, charcoal burner, pig farm)
   - [x] Woodcutter (tree stumps remain), stonecutter (rocks shrink down to rubble, yield configurable
     per size), forester (saplings grow back)
   - [x] Workers are recruited from carriers; watchdog for workers
   - [x] Cycle tested: the economy builds a building only from its own production
   - [x] Mineral resources from the original maps (fish, coal, iron, gold, gems, sulfur)
   - [x] Mines: one unit of food per ore (which one is determined by the input piles per people); yield per
     amount step (`orePerAmount`) and regeneration (`regenerateSeconds`) configurable → goal 3
   - [x] Grain farm (fields grow in stages), fisherman (fish stock decreases), waterworks
   - [x] Cycle tested: grain farm + waterworks → mill → bakery → coal mine
   - [ ] Winegrower, beekeeper, rice farmer, donkey farm
   - [ ] Mine display (deposit/exhausted) in-game
   - [x] Tools: carriers become specialists only with a tool (table in `production.json`);
     if their building is demolished, they keep profession and tool and take over the next building of their profession
   - [x] Toolsmith and weaponsmith: the toolsmith only makes what is currently needed and missing;
     the weaponsmith keeps a stock
   - [x] Mine food: favourite food lasts for 8 runs, other food for 2 (`mineFood`, `prefersFood`)
   - [x] Distribution: first the target type (construction site, building type by weight such as 90:10, storehouse), then the nearest target
   - [ ] Smith selection by the player (wish list/shares)
5. **Combat** ← *started*
   - [x] Soldiers: the barracks turns carrier + weapon into swordsman/pikeman/bowman; map soldiers with level
   - [x] Military buildings (tower, large tower, castle) are garrisoned, soldiers stand visibly on the spots
   - [x] Territory: occupied buildings claim land (radii from the original maps, 98.8% identical),
     border posts in the player's colour, building only on own land
   - [x] Player separation in logistics, recruitment, construction sites
   - [x] Attack (soldiers from buildings in range, one stays), melee, bowmen (from the tower too),
     defenders at the door, capture with territory change; buildings on lost land go under
   - [ ] Align combat values with the original behaviour (currently our own values in `data/military.json`)
   - [x] Soldiers switch to closer enemies (neighbouring field immediately); box selection, move order with priority for enemies
   - [x] Combat pathfinding in the vicinity (`combatPathRadius`) around standing/fighting figures; walkers attack within sight
   - [ ] Attack selection as in the original (choose number/types), death animation
   - [ ] Soldier levels through gold/temples, healer
6. **AI** ← *started*
   - [x] Framework: `Ai/AiPlayer` per computer player, the same commands as a human, knows the whole map
     (no exploring, as in the original); values and build list in `data/ai.json`
   - [x] Economy: build list plus demand (residence when short of carriers, toolsmith when a tool is missing,
     food for hungry mines, a new stonecutter/woodcutter when the old ones find nothing any more); location
     at the raw material or near home; the deposit for one stonecutter always remains; demolition of exhausted buildings
   - [x] Strategic expansion (not in the original): towers aimed at missing stones, ores, trees,
     water or space – so that no game stalls for lack of mountains/stones in the starting area
   - [x] Expansion to distant targets (e.g. mountains on "Frontier"): if no tower spot is free within 60 fields
     of the target, the tower goes to our own spot closest to the target – tower by tower towards it, instead of
     diverting to water/space
   - [ ] Ore demand satisfied only at a real amount (minimum amount as with stones, only fields with mountains)
   - [ ] Do not pursue distant targets alternately: if coal and iron lie in opposite directions,
     the AI switches direction tower by tower
   - [ ] Alternative tactics when a raw material (above all mountains) is missing on our own island or
     on the whole map
   - [x] Storehouse only once a smith/the gold smelter produces in quantity (only tools, weapons, gold are
     stored – configurable in `production.json`; storehouse piles hold 8)
   - [x] `tools/Homestead.Arena`: AI against AI without a window on all multiplayer maps, metrics and
     stalled construction sites
   - [ ] Military: defend, attack when superior
   - [ ] Finer economy control (shares instead of a fixed list, weapons/gold, multi-purpose storehouse)
