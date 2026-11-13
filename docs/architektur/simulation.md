# Simulation (`src/Homestead.Simulation`)

Game logic without MonoGame, fixed tick rate (`World.TicksPerSecond`). AI: [ki.md](ki.md).

**Determinism** (lockstep multiplayer: every machine simulates the same): randomness only via `new Random(… + World.Seed)`, no clock, no threads,
no `GetHashCode` in the logic; trigonometric functions only via `DeterministicMath` (Sin/Cos/Atan2 from basic arithmetic – `Math.Sin` & co. come from
the platform's C library); basic arithmetic and `Math.Sqrt` are identical everywhere per IEEE. `WorldChecksum.Of` (FNV-1a over 64-bit words, `Parts` per part, over tick, settlers,
buildings, piles, map, mana) for desync detection; `DeterminismTests` (AI game on `random:4:mirror:11`, 25 min, checksum per minute) runs in the build
on Linux, Windows (x64) and macOS (ARM), `determinism-compare` compares.

- `Map/GameMap`: terrain, objects, walkability, mineral resources (`ResourceAt`).
  `Map/Vision`: fog of war per player – sight of own figures outside (soldiers `Sight`, scouts `scouts.sight` 26, donkeys `trade.sight` 3
  – no scouts –, otherwise `SettlerSight`) and buildings (`Military.SightOfBuilding`: finished ones with `buildingSight` in `military.json`,
  lookout tower 45; occupied military buildings territory radius + `militarySightMargin` (3), empty ones nothing; all other finished buildings
  `otherBuildingSight` (5), residences/storehouses too; construction sites nothing); own land itself sees nothing (demolished/taken by pioneers → shadow);
  explored stays explored; the AI does not use it.
- `Pathfinding/PathFinder`: A*; reachability via `Regions` (they track walkability changes). Step costs by walking time
  (`SetStepCosts`: path 8, flat 10, mountains 13); land areas per owner.
  Distant targets in legs (`FindRoute`, from `WalkTo`/`Replan`; those not restricted to own land via `ExactDistance` (96)): rough route over
  `Clusters` (16×16 cells, each cell its connected parts, neighbours cached, changed cells recomputed), exact only up to `LegNodes` (4) cells ahead
  into the part; `Settler.PathIsLeg` → at the end of a leg `World` plans the next. Exact near the target.
  Unreachable command target (sea, other island): nearest reachable point (`PathFinder.NearestReachable`, ring by ring;
  `World.CommandTarget` for soldiers, geologists/scouts/pioneers, priests).
- `World`/`Settler`: one field per figure, swapping, stepping aside, detours, watchdog pass-through (comment in `World`).
  `Settler.StepProgress` in `World.StepUnits`; speed per target field: mountains ×0.8, footpath ×1.2 (`walking` in `production.json`);
  plus soldiers per type `speed` in `military.json` (swordsmen ×1.1, pikemen ×1.05, bowmen ×1); `armor` (pikemen 0.5):
  that much less damage per hit, rounded down, at least 1 (`SoldierStats.Taken`; the AI calculates with `Toughness`).
  Settlers and buildings have a player; logistics/recruitment only for their own. `World.TerritoryRules` enforces own land
  (build spot including the door, piles, builder spots, paths – except soldiers, priests, geologists, scouts, pioneers); the stranded flee
  (`Idling`); whoever loses the land under their feet while on a job (`World.IsStranded`) cancels, drops what they are carrying and accepts
  something new only on own land.
- `Trails`: carriers on a job tread footpaths into grass (`GroundCover.Trail`, after `trailSteps`), they fade after `trailFadeSeconds` (300).
  Farmland and paths keep one field of distance from everything except grass/levelled ground and from each other (`GameMap.CoverFits`, otherwise the
  original transitions break). Grain is sown onto paths too and shortens paths all around (`Trails.CutAround`); sowing turns grass within
  `farmlandRadius` (1) into farmland, as far as it fits and lies in the work area.
- `Buildings/`: definitions from `data/buildings/*.json`.
  - Build-spot check (`CanPlace`, `DoorLeadsOut`, `PlanLevelling`): "seen" via a marker field over the map (`NewSearch`/`See`), no HashSet.
  - `Construction`: levelling (`World.PlanLevelling`, `levelling` in `production.json`): footprint to the average height, ring by ring around it,
    until no neighbour deviates by more than `maxSlope` (8) (wider on slopes). Effort = moved height; invalid above `maxWork` or if a field to be
    changed is built on, planned otherwise, unwalkable or foreign. Diggers: `heightPerDig` per pass, never a slope above `maxSlope`
    (on a slope the top first). Buildings that are finished immediately level immediately. Mines: mountains only, no levelling
    (`levelling: false`, `NeedsLevelling`).
  - Builders without material become free (idle, pushable, one step away, switch construction site); spots only as long as the material suffices for one more.
  - Buildings JSettlers does not have (war machine works, gunpowder maker): `data/buildings/handmade.json` per people
    (footprint = grid points covered by the original image, door/piles per the image), loaded before `overrides.json`.
  - `World.RemoveBuilding`: demolition/loss with a dust cloud (`DustClouds`), a share `demolition.payback` of the building material stays on the ground. The land
    of demolished military buildings stays with the player, but unprotected (an enemy military building takes it together with the buildings).
  - Demolition: the worker keeps profession and tool, takes over the next building of their profession.
- `Economy/`: goods, piles.
  - A new building pile (construction site, finished) on a loose pile: its goods are put down next to it (`AddPile` → `DropGoods`), not overwritten.
  - `Logistics` with watchdog: for each good among the current consumers – construction sites as one (`Logistics.Construction`), otherwise per
    building type – one drawn by lot according to player shares (`ShareOf`/`SetShare`; defaults `distribution` in `production.json`, 0–1, e.g. food
    mines 1 : 0.1, planks construction site 1, charcoal burner/shipyard 0.5; listed goods give unlisted consumers 0, unlisted goods give everyone 1; a share
    of 0 gets nothing, unless all are 0), then the nearest target with space; the storehouse only if nobody else wants it.
  - Storing only what the player stores (`IsStored`/`SetStored`); the storehouse can specialise in one good
    (`SpecializationOf`/`SetSpecialization`, `StoresHere`, overrides the general rule); default `storage.materials`: tools, weapons, gold, gems.
  - Offers in transport order (`TransportOrder`/`MoveInTransportOrder`, default `storage.transportOrder`); loose piles on nobody's land: the player
    with free carriers. Otherwise goods stay on the output pile (8; full → the producer waits, gatherers too).
  - The construction site with the highest priority (`Building.HasPriority`): its piles before all consumers (shares too), its goods at the front of
    the transport order; if more is missing than is ready, carriers deliver only that many to other construction sites (`Logistics.FreeForPriority`,
    nearest first; on the way to the source: cancelled, loaded: drop it – in the original all); failed deliveries are replaced at the next reconciliation.
  - `Problems` counts cancellation reasons.
  - `Production`: workshops; smiths make one product per cycle: what was ordered, then the expected tool, otherwise weighted round-robin by shares
    (`shares`: toolsmith 0 = demand only; weaponsmith sword 100%, bow 70%, spear 45%), as long as below `keepInStock` (0 = without limit);
    the choice only with iron and coal.
    Mines: food piles max. `mineFood.pileCapacity` (4); the first `mineFood.freeRuns` (8) runs per building free; success chance =
    remaining ore in the vicinity against `resources.fullAmount` (8) per field (empty/foreign fields = blanks, every find lowers them;
    `MineChance`); a blank costs time and food.
    Gatherers: waterworks only on rivers; farmer, winegrower, rice farmer, beekeeper per `crops` – grain makes farmland
    (`GroundCover.Farmland`, walkable, until demolition/loss of the farm), grain/wine/rice grow in 600 s as in the original; wine faster on slopes;
    rice only on swamp; vines/beehives grow back.
    Beekeeper: at most `maxFields` (12) hives per beekeeper building (`Production._fields`; ownerless ones in the work area – old save games – it
    takes over), new ones only with at least one tree in the vicinity and `fieldSpacing` (5) fields of distance from other hives (placing takes
    `sowSeconds` 1.2 s = one animation round), where most of them stand; hives without trees around them it collects after the harvest (then new
    ones by trees, otherwise none); demolition/loss of the beekeeper building takes its hives along immediately. A hive is full in 720 s, with
    `idealTrees` (10) mature trees in the vicinity `treeRadius` (5) `1 + treeBonus` (3) × as fast = 240 s
    (12 hives at ~20 s harvest run → 3 honey/min as in the original; fewer trees proportionally).
  - `Healing`: the hospital healer calls the weakest (fewest HP, tie: nearest) wounded idle own soldier within `hospital.callRadius` (40) onto the
    healing spot in front of the door (`HealSpotOf`; not those holding); whoever stands still next to the door, healer spot or healing spot is healed
    without a call (the weakest first),
    heals them (from then on interruptible only by demolition; full HP after `healSeconds`, wherever they are), goes
    inside, `callPauseSeconds` pause.
  - `Housing`: residences release carriers and provide that much housing (plus the starting population, `SettleStartPopulation`). Carriers and workers
    need space (diggers/builders too – construction sites recruit at most a share of `Share.Diggers`/`Builders`, at least one each), not soldiers,
    geologists, scouts, pioneers. Without space they go on strike (`Settler.IsStriking`): first idle carriers, then workers (they stay inside).
  - `Recruitment`: carriers fetch a tool, become specialists; the three shares together ≤ 100% (`SetShare` limits them). Tests that are not about tools:
    `TestConfig.NoTools`.
  - `Idling`: stranded carriers/professions outside our own land flee to the nearest reachable own field (`RefugeRadius` 64, otherwise an own building
    door); without one they wander around and die after 30–60 s (fixed per settler by hash, ghost, death sound), rescued back on own land.
    Idle ones (carriers, soldiers, diggers, builders, geologists, scouts, pioneers, the unemployed) step out of the way
    (after `IdleTicks`; soldiers immediately, every tick, with one field of distance).
  - `Geology`: geologists walk like soldiers (spread over free fields); on mountains they search the vicinity `geologists.searchRadius`
    (nearest free field, tie random), put up signs with raw material and amount.
  - Scouts (`Profession.Scout`, graphic `THIEF`): converted/sent like geologists (`Geology.Convert(…, Profession.Scout)`), never search.
    For the other players a carrier of their own (`Settler.IsRevealedTo`), not attacked until an enemy soldier outside comes within
    `scouts.revealRadius` (10) (`Military.RevealScouts`, every tick); once recognised he stays recognised as long as the player sees him
    (`Military.Sees`: sight of own figures/buildings like fog of war), then camouflaged again. Recognized: fought like a geologist
    (by tower bowmen too), the AI counts him among enemy groups. AI suspicion (`AiPlayer.InspectScouts`): a scout on its land → the nearest idle
    soldier follows him (otherwise it calls one from the nearest military building with more than one soldier).
  - `Theft`: scouts steal (`Steal`; Alt+right-click: `StealAt`) the nearest good within `scouts.stealRadius` (5) – an unreserved unit, not from own
    buildings or loose goods on own land; carries it (`Carrying`, crouched), speed `scouts.loadedSpeed` (0.5);
    drops it quietly on own land or where he dies. Sent with `StealAt`, he runs straight back with the good to where he stood when ordered.
  - `Pioneering`: pioneers (converted/sent like scouts; `Claim`, Alt+right-click `ClaimAt`) take field by field: the nearest free or foreign,
    unprotected field (`Military.IsProtected`) within `pioneers.searchRadius` (10), tie nearest to the target point;
    `claimSeconds` (2) post, then the field belongs to him together with the unwalkable neighbours (`Military.TakeLand`, unprotected foreign buildings fall);
    at the shore also the free water in front up to `pioneers.waterReach` (6) fields out (across own water) – space for dock and fisherman;
    the client never draws border posts on water (shore = border). In protected enemy land towards free land, otherwise the end.
    Completely enclosed free land (not map edge/foreign land) is taken at once (up to 4096 fields). Land unprotected.
  - `Trade`: land trade. The donkey farm (`trade` in `production.json`: grain + water → one donkey, `Profession.Donkey`, not a good) breeds as long as
    the player has fewer than `keepIdle` (4) donkeys without a job. Donkeys: not controllable, immortal, walk everywhere, do not count for
    housing/profession shares. Marketplace (`market_place`): 6 piles `PileRole.Trade` (never an offer); per marketplace the destination = a point
    (`Reachable`: walkable and reachable from the marketplace, on a building its door; a new destination deletes the stops), optionally up to 3 stops;
    per good an amount (`Remaining`, ∞ = `Trade.Unlimited`, decreases with each arrival at the marketplace). Carriers deliver only with a destination
    (logistics: marketplace always share 1, even from the destination itself – with ∞ an endless loop as in the original). Every unit at the marketplace
    calls the nearest free donkey; it loads up to `load` (2), walks the stops (watchdog skips), puts down at the destination (at the door of an own storehouse
    into its piles), back via the same stops in reverse. An enemy soldier (tower bowman too) strikes a loaded donkey once: the good falls, the donkey turns
    around (`Struck`, event `donkeyRobbed`, trade); unloading on foreign land → `World.GoodsHandedOver(from, to, good)` (C# event, not saved); nobody
    attacks empty donkeys (only loaded ones are `_combatants`). Target unreachable → home with the load, next to the marketplace.
  - `Shipping`: the shipyard (`shipping` in `production.json`) first needs a dock (`Dock`: shore field + direction, `dockLength` (4) fields):
    `PlaceDock` sets it immediately where its end is closest to the click – free, walkable own shore up to `dockDistance` (20) from the shipyard,
    reachable from its door, dock over own open water (from the second field on, water all around), a new one replaces the old one, goes with the shipyard.
    Drawn as map object `MapObjectCodes.Dock` (219, file 1 sequence 112 per field), walkable (`GameMap.ComputeWalkable`).
    Shipbuilding (`Order`, one at a time per shipyard, `ships` with guessed costs): logistics delivers to the shipyard only what the ship still needs
    (`Wants`); the shipbuilder (`DOCKWORKER`, hammer, builder graphic; `Production` part `Shipbuilding.cs`) fetches one unit at a time from the pile, carries
    it to the end of the dock, hammers for `buildSeconds` (4); every unit grows the hull (`Progress`), the last one launches the ship
    (`Berth`: two fields behind the end of the dock, across the dock). Demolition of the shipyard discards the order. `Launch`: finished immediately
    (captures). `Ship` (own list, not settlers): steered like a soldier (`Move`, breadth-first search over the water to the nearest reachable
    point), only on `IsNavigable` (water with `ShoreClearance` (3) water all around, no dock), keeps `Spacing` (6) to other ships (waits, then detour),
    `stepSeconds` (0.5) per field, sight `sight` (16). Ferry (`Ferries.cs`): takes `passengers` (7) units (soldiers, priests, geologists, scouts,
    pioneers) aboard when it lies still and has walkable land (shore, dock) up to `landingDistance` (6) (`IsNearLand`); `Board`: each walks to the
    nearest reachable shore field (soldiers forced), boards there (`IsInside`, position = ship, rides along); a new command cancels.
    `Unload`: everyone onto the free land fields nearest the ship (breadth-first search from the shore). Trade ship 0 slots. No combat yet.
    Sea trade (`SeaTrade.cs`): the harbor (`trade.harbor` = `harbor`, `Trade.IsHarbor`/`IsTradingPost`) like marketplace + donkey – route, destination,
    amounts in `Trade` (piles `PileRole.Trade`, logistics share 1), needs a dock like the shipyard (`HasDocks`); route points on open water the dock
    reaches (`SeaPoint`: nearest in the same body of water `SeaRegion`, not saved; destination with a shore up to `landingDistance`). Every good at the harbor
    calls the nearest free trade ship (one per harbor loading at a time): sails to the dock, loads up to `cargoPiles` (3) piles of `pileSize` (8) of one good
    each (`loadSeconds` per unit, waits up to `loadWaitSeconds` for more), sails the stops, puts down one loose pile per pile on the shore at the destination
    (no dock there), back via the same stops. Watchdog: point not reached after 3 tries → stop skipped, destination → home, load next to the dock. Trade ships
    under way take no commands (`HarborOf`, `CargoOf`).
- Teams (`World.Teams`, `AreAllies`; from the start screen, saved): allies do not fight each other (`MilitaryCombat.IsEnemy`, `Attack`,
  tower bowmen, war machines `IsTarget`), enemy spells spare them (`Magic.Affects`), towers/pioneers take no allied land
  (`Claim`, `TakeLand`, `IsClaimable`), thieves do not steal from them (`Theft.IsOwn`); `ConquestGoals`: won when all enemies are defeated
  (outcome per player, `IGameGoals.OutcomeOf`/`Decide(player, …)`).
  Shared sight: `Vision`, `Military.Sees`, `Magic.SeesBySpell` count the whole team; a revealed scout is revealed to the whole team of the soldier.
- `Military/`: the barracks trains at the door (the carrier becomes a soldier at that very spot, does not go inside), recruits walk to the gathering
  point. Military buildings call only idle soldiers up to `garrisonCallRadius` (100) fields from the door; further ones stay put. One carrier per
  weapon at a time (as far as the carrier reserve goes, those walking from all barracks count), each is trained at the door or directly beside it.
  Target garrison per type (`ChangeWanted`: new 1 swordsman; never 0, never above the infantry/bow slots; "+" with full infantry slots replaces the
  other infantry kind; surplus ones turn around or come out, never the last one – the replacement swaps at the door). Occupied ones claim land; always
  their own ground (otherwise a captured tower would fall again immediately).
  - `Move`: target fields around the point first free (nobody stands there, no other soldier sent there), otherwise occupied, then round-robin.
    Soldiers do not walk through someone standing on their target on the last step (watchdog), but stop in front (counts as arrived).
  - `MilitaryCombat`: enemy search via cells (`CellSize` 8) of the fighters at the start of the tick, per player (only cells of enemies searched)
    (`EnemiesNear`, same order as `EnemiesOf`).
    Idle soldiers and those with a non-forced move order attack enemy soldiers and occupied military buildings
    (door in sight), nearer ones first; those walking continue afterwards; the garrison stays inside. Melee fighters break down the door first
    (`door`, HP like a pikeman; 5 s without an enemy within 6 → the guard goes in, the door regenerates 0 → full in 30 s).
    Then one defender each, immovable in front of the door (`FightPoint`; spots per people/building as an offset in `door.points`, otherwise automatic,
    front first; infantry first, then bowmen), attackable only by melee fighters on the door spots (`door.fighters`: tower/large tower 2,
    castle 1; pikemen like range 1, the rest waits). Whoever kills the last defender is in. Waiting attackers: free field 2–5 from the guard field, never
    assigned twice in the same tick (`QueueSpot`);
    everything full → where they stand or the next free field, never onto the guard field (otherwise hundreds on one field). Bowmen attack no buildings
    (neither on their own nor on command – `Attack` skips them; AI campaigns without them) and on the ground do not shoot below `minRange` (5):
    too close → step back (`SoldierStats.InReach`/`OffReach`), door guards too; from the tower without a minimum range.
    Bowmen in military buildings shoot `towerRangeFactor` (1.69) farther (`TowerRange`, AI tower fire too). Bowmen hit with the shot, the arrow
    (`Military.Arrows`, `ArrowTicks`) is presentation only. Cooldown between strikes/shots per soldier (`_nextShot`), not per command: a new command in
    the middle of a fight does not reset it. Near the enemy local pathfinding (`combatPathRadius`).
    "Hold position" (`Military.Hold`, selection panel for soldiers, in place of "Claim land"; `Move(…, hold: true)` walks there first):
    not idle (no building calls him), fights only enemies in range, pursues nobody, returns to the spot until the next command
    (`HoldTarget`/`Holds`; the hospital does not call those holding). AI: the wounded, parked melee fighters, the bowmen of the battle formation.
    Waypoints (`AddWaypoint`, Shift+right-click): with a command (walk, attack, own fight) that one first, then in turn up to `MaxWaypoints` (3)
    points, one more replaces the last (`Fighter.Waypoints`, `Dismiss` makes the next one the move order); without a command (or holding/patrolling)
    like `Move`. Patrol (`Patrol`, Alt+right-click): to the point and back to the starting place, back and forth until the next command
    (`Fighter.PatrolBack`); fights on the way like with a move order, independent attacks continue with the patrol when it ends
    (`EndAttack` keeps `MoveTo`).
  - Combat strength (`CombatStrength`): own land 100%, otherwise a starting value by player count (`startPercent`, measured in the original: 1 → 54%
    (as there, although pointless), 2 → 50%, 4 → 37%, 8 → 25%, 16 → 14%); plus gold on own land (by land ownership; Egyptians: gold half,
    gems double), every further percentage point costs according to the combat strength already reached (bands `stepPercent` from 0%,
    `unitsPerPercent`, measured in the original: up to 50% 1.2 bars, from 90% 20; a lower start rises faster), without an upper limit
    (200% = double damage outside; forces the rich to attack). The AI knows all values and evaluates the place of battle.
  - Values: `data/military.json`; territory radii measured from original maps.
  - `WarMachines` (`warMachines` in `military.json`): one machine per people (catapult, ballista, cannon, gong) from its workshop:
    `Order` queues a machine or a load of ammunition (gong without; `Cancel` takes back the last one not started), logistics delivers only what the
    current order needs (`Wants`), the shipyard worker hammers unit after unit in front of the door (`IAssembly`, shared with the shipyard,
    `Shipbuilding.cs`); loads lie at the workshop (`Loads`). Machine = `Profession.WarMachine` (people in `People`, `Ammo`), does not count as
    population, drives like soldiers (`Move`, speed `speed`), occupies its field and the 6 neighbours (`Settler.IsWide`/`Body`: pathfinding only over
    points with 6 walkable neighbours (`PathFinder.IsClear`, exact, at most `WideSearchLimit` nodes; if it cannot get through, it drives as close as
    possible – in the original too), pushes idle ones aside, nobody swaps with it, waiting ones plan around all 7 fields, melee reaches to the edge),
    fires only at finished enemy military buildings (`Attack`; standing ones at everything within `range`) every `cooldownSeconds` (5) with
    `hitChance` (catapult 50%, ballista 75%, cannon 90%, gong 50% – from the user), projectile `MachineShot` (gong immediately), impacts briefly in
    `Impacts`; every hit counts against `buildingHits` (`DamageOf`), the last one tears it down
    (the garrison comes out alive, event `buildingDestroyed`); a hit heals per `repairSeconds` (60) after the last one. `shots` (6) per load;
    `Reload`: to the next own workshop with a load. Melee fighters destroy machines
    (`health`, dust instead of a ghost), bowmen do not (`EnemiesOf(…, bowman)`). Range, speed, HP, hits per building guessed.
    Not yet: avoiding mountains, AI. Gunpowder maker (Asians): coal + sulfur → gunpowder (`POWDER_MAKER`, without a tool).
- `Magic/`: mana per player. Small temple: wine/mead/beer/liquor → mana (one unit per `templeSeconds` 35.7 as in the original, liquor 2 mana);
  the large temple sends a priest immediately and every `priestSeconds`. Spells as in the original (values from wiki.siedler3.net "Mana und Magie"):
  8 per people in the menu (`menu`, order by the icons; `own` = the AI's people's spell), per spell `cost`, `radius` (0 = whole map), `count`,
  `seconds`, `factor`; `placeholder` = not implemented yet (visible, not castable).
  `Cast`: the priest walks up to `range` (24)
  (`anywhere`: casts from the spot, however far – reinforcement, call/send goods; without a radius immediately on the menu click),
  casts only where something affected is (`Magic.HasTargets`/`Affected`, nearest first up to `count`), no fight needed; costs per player and spell kind
  get `costIncrease`% (25 as in the original, rounded up: 20, 25, 32 …) more expensive with every cast (`Magic.CostOf`, the menu shows the current
  price); takes effect immediately when casting starts (`castSeconds` only the gesture).
  Spells that move/transform units (Convert, Banish,
  Reinforcements, Bows to pioneers) take priests, soldiers, scouts, geologists, pioneers outside, no soldiers of a military building
  (`Military.IsTowerBound`: garrison, door, on the way in); spells on soldiers for a time (`Settler.Charm`,
  `SpellCharm`: Samurai sword only swordsmen ×2 speed ×1.33 damage, Festering fear ×0.5 damage on the caster's land, Strengthen soldiers ×2
  outside own land, Ch'ih-Yu's shield ×0.67 damage taken, Cursed arrows no arrow damage; Freeze enemies `FrozenUntil`) those too
  (frozen tower bowmen do not shoot);
  cast again extends, does not add (`Military.DamageOf`/`Taken`). Goods spells (Midas touch, Fish to meat, Stone to iron, Gold to stone: up to 40 units
  in the vicinity, whosever they are, loose/output/storehouse, not reserved, `World.TakeUnits` → `DropGoods`; call goods: from own
  land to the priest; send goods: those around the priest to the target; both unlimited range). Sight (`SpellSight`, `Magic.Sights`: Magic eye
  radius 32, Reveal the map completely, 20 s; fog of war and `Military.Sees`).
  Land (`LandSpells.cs`): terrain change (Grassland galore, Horus' heat,
  Drain the swamp – moor and mud –, Melt snow → mountains) over ground classes,
  edges one field each like the random map generator
  (`Paint`, not under buildings/piles, farmland/paths that no longer fit removed; the client draws
  `TerrainKinds.Of`, the minimap still the original); halve resources, fish flood (+1–4 per water field, max. 15), forest fire (trees in the vicinity → stumps,
  after `FireSpreadTicks` once the ones directly beside, flames), Forest's favour (mature trees onto free grass), Stone curse (10–20 rocks of various sizes, 2 fields apart),
  Gorgeous growth (`Production.Hasten`: grain/wine/rice ripe at the latest in 180 s). Heavenly gifts (`SummonGoods`: 3–4 piles of 4–8 each from food, weapons,
  tools, gold, iron, coal, ore in the vicinity `SummonSpread`; Egyptians 50% extra stones; flames staggered in time). Priests attackable.
  Old save games adopt the current rules (`Magic.FollowRules`). Two accounts: spells consume `Mana`, upgrades check `ManaEarned`
  (`upgrades.manaEarned` for every n-th upgrade, original: 10/30/60/110/170/200). `Upgrade`: needs a finished small temple; first all three types
  level 2, then 3; applies only to those trained afterwards. Values: `data/magic.json`.
- `Commands/` (lockstep preparation): player commands as data (`GameCommand` records: player, IDs of settlers/buildings/ships, points,
  enums, numbers; building type as a `BuildingDefinition`, unique via people + name), act only on the player's own things. `World.Commands`
  (`CommandQueue`): `Issue` assigns a `Sequence`, `World.Step` first applies all waiting ones in this order, results in `Applied` until
  the next step; while paused only `ApplyWhilePaused`. Groups: `UnitCommands` (right-click `MoveUnits`: attack an enemy building, otherwise
  walk; waypoint, patrol, hold, specialists, priests, spells, ships), `BuildingCommands` (construction site, demolition, pause, priority,
  work area, garrison, storehouse, marketplace, shipyard, war works), `EconomyCommands` (distribution, transport, smiths, professions,
  recruitment, upgrade), `ChatCommands` (`Say`, `AiReply` for the AI chat, `Cheat`). Waiting commands are in the save game.
- `Persistence/`: `Snapshot` writes the whole object graph via reflection (all fields, private ones too, object identity; type table with
  field names – missing ones skipped, new ones: collections/classes with a parameterless constructor empty, non-nullable references without one →
  incompatible, otherwise default value (not the initialiser!); Dictionary/HashSet with their inner structure, hashes recomputed on loading → the game
  continues tick-exact, `SaveTests`). Delegates only with targets in own assemblies (client subscriptions are re-set after loading);
  `[NonSerialized]` omits; foreign types abort with a path, `JsonElement` as text. New fields need no save code.
  `SaveSlots.Load` recomputes walkability (`GameMap.RefreshWalkable`; docks from earlier builds). Type names resolved without assembly version;
  missing type/method → `SaveIncompatibleException`. `SaveLoadFailure.Of`: kind (incompatible/corrupt/unreadable)
  and error code `HS-<kind>-<FNV-hash>`; the title screen shows it in the message box (`ShowLoadFailure`), the console the exception.
  `SaveGame`: world, catalogues, map, sight, client values. `SaveSlots`: 8 slots `slot N.hsav` in `~/.config/homestead/saves`
  (`HOMESTEAD_SAVE_DIR`), header `SaveInfo` (map, people, time, tick) uncompressed, the rest gzip, via a temporary file.
- `Events/` (`GameEvents`, `World.Events`): messages per player from `data/events.json` – category info/combat/resources/trade,
  `cooldownSeconds` per player/event (0 = always), text (`{building}`), on/off. `Raise(id, player, place, settler/building)`.
  Combat: `attacked`, `buildingAttacked` (door/guard), `buildingLost`/`buildingCaptured`. `Tick` checks the states of finished buildings every second,
  reports once after `afterSeconds` until the end: `noStaff`, `noTool`, `noResources`, `mineExhausted` (chance ≤ `maxChance`%). `Announce`: without a place.
- `ChatLog` (`World.Chat`, `World.Say(player, text)`): global chat, lines with tick and player in order, trimmed, empty ones nothing,
  at most `MaxLength` (200) characters; saved with the game.
- `SoundCues` (`World.SoundCues`, not saved): collected only when the client sets a list (empties it per frame); `Strike` on every
  hit/shot (at the door too, with soldier type), `Death` in `MilitaryCombat.Kill` (with the people of the dead), `TreeFalls` when felling, `BuildingFalls` in
  `RemoveBuilding`, `Spell` when casting starts, `Healing` (hospital), `Claiming` (pioneer post), `GeologistFinds` (with amount),
  `Sowing` (with `Trade`); `Strike` carries the people (the Egyptian bow sounds different).
- `GameHistory` (`World.History`): every 30 s (`SampleTicks`) one `HistorySample` per player (land, settlers by kind, soldiers by type,
  buildings, construction sites, military buildings, housing, strikers, stock, produced goods, combat strength, mana) plus running totals
  (killed/fallen, buildings captured/lost, completed/destroyed); for the statistics screen.
- `Lockstep/`: `LockstepSession` (commands for the current turn + delay, waits for the bundle, compares checksums → `Desync`), `TurnRelay`
  (server logic), `CommandCodec`
  (JSON network format), `LocalRelay`, `GameFingerprint` (protocol, version, data hashes; details: `docs/MULTIPLAYER.md`).
- `GameSetup` (`MapChoice`, `PlayerSetup`): game setup as data (JSON) – map by seed or hash, players, starting positions, game values, one seed
  for all draws; `Start` → world with AIs (details: `docs/MULTIPLAYER.md`).
- `OriginalScenario`: map → world (buildings, piles, settlers; settler type 23 = scout/thief, 22 geologist, 36 priest, 40 pioneer; the garrison of
  the military buildings from the map inside immediately, `Military.Station`, target = garrison; without a specification 1 swordsman level 1 as in the
  original). Maps without buildings (multiplayer, also single player such as Beginner I/III): `StartSetup` gives every player the equipment from
  `data/start.json` (the start tower wants infantry only, bowmen wait outside).
- `Map/Generation/RandomMapGenerator`: random maps as `OriginalMap` (`OriginalMap.Create`), the same `RandomMapSettings` + seed = the same map,
  values `data/randommap.json`, size by player count. One landmass with a rugged coast (distorted noise, half circle, half rhombus;
  cut-off parts sink, cut-off starts get a land bridge to the centre); mirrored a circle in grid geometry. Every start the same: flat starting area,
  a mountain with coal/iron/gold, a pond with a brook (waterworks need rivers), forest, stones. Plus mountains, lakes, rivers to the sea, swamps
  (at least one), forests, stone fields, ore veins, fish, decoration, treasures (goods piles ≥ 32 fields from the starts; "much": plus a hoard full of
  piles of gold/weapons in the centre of the map, `hoard`). King of the Hill (`RandomMapSettings.KingOfTheHill`, `kingOfTheHill` in `randommap.json`):
  the centre as a hill with a hoard,
  mountains between the passages (full veins of gold/gems/coal) and a ring of unwalkable moor (80/81) with one passage each towards the start;
  home features keep their distance (`OnHill`). Rivers as a steadily turning course in half-field steps (downhill, meandering via noise), wider
  downstream; grassland slightly uneven. Symmetry: `Mirror` (wedge area; exact for 2/3/4/6,
  5 rounded), `Repeat` (start tile `homeRadius` copied). Terrain bytes last from ground classes (water depth, beach, edges one field each,
  so that every triangle has an original transition tile – features keep their distance). Starts without a connection → rerolled.
