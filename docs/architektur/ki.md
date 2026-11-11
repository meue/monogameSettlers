# AI (`src/Homestead.Simulation/Ai`)

Concept of the characters and tactics: [../KI-TACTICS.md](../KI-TACTICS.md).

- `Ai/` – `AiPlayer`: the same commands as a human, knows the whole map. `World.AiPlayers` triggered every tick. `MapSurvey`: map overview in cells.
  - Building according to `data/ai.json` and demand: residence as long as carriers ≤ 3 × production buildings (size by
    finished sawmills/stonecutters per people); no holding back material for towers; no further building of a kind
    while one is waiting for workers; raw-material buildings without a spot try further cells; waterworks without a
    spot → expansion towards the river. Buildings without a spot at the anchor → the most spacious own cell.
  - Growth per the build order (`AiPlayer.Growth`, `growth` in `ai.json`): per window (120 s) it measures what finished,
    occupied buildings are waiting for; a good that one kind of consumer waits for ≥ `starved` (25%) of the time
    (per kind, not averaged), without stock → one more producer (workshop/mine only if the existing ones work
    ≥ `busy` (70%) – otherwise the bottleneck lies further up the chain); busy weaponsmiths/barracks (with weapons
    in stock)/small temples → one more. One of each kind at a time; not if one was finished in the window.
  - Mines only from success chance `mines.minChance` (0.3), demolition below `mines.abandonChance` (0.1);
    no spot → ore missing → expansion. Resource buildings without a spot (`_noSpot`) are searched again only after
    `NoSpotRetryTicks` (15 s, constant – config fields are missing in old save games). Smelters only with their own mine for their ore.
  - Before demolishing exhausted stonecutters/fishermen: move the work area (up to `MaxWorkCenterDistance`) to
    where more is in range (`BetterWorkCenter`).
  - The toolsmith is not in the build order, only when a worker is waiting for a tool (without a plank/stone
    reserve: if the saw is missing, no more planks come). Without a toolsmith, further weaponsmiths only as long
    as a smith remains beside them (unemployed smiths + hammers − smithies without a smith ≥ 2, `AiPlayer.Smiths`);
    if the toolsmith still finds none, a weaponsmith is demolished (unoccupied first), its smith switches.
  - Storehouse only with gold/gems (gold smelter/gem mine finished or bars/gems on own land, captured too), then
    at `stockWhenWaiting` (8) storable goods lying around and fewer than 2 free storage spots.
  - Residence demolition if `spareRoom` is still free afterwards. Foresters checked every `forester.checkSeconds` (10),
    stopped from `forester.maxTrees` (10) trees/saplings in their area. Foresters in the build order only as an upper limit: built at the
    finished woodcutter with the fewest, under `forester.buildBelowTrees` (15) trees/saplings in the work area,
    whose centre lies in no forester work area (full forest: none), at most forester radius − 2 away (otherwise the next
    needy woodcutter, otherwise none – they used to land far away, the woodcutter stayed needy, one forester after the other),
    one after the other (the last one must work); without a woodcutter (no tree on own land) at the starting point.
    The beekeeper like a raw-material building: the cell with the most trees, woodcutters around it (16) count against it; without trees none.
  - No building on farmland or in the field area of own farms (radius 4 around the work centre); a farm needs
    35 free grass fields in its area.
  - Expansion: towers aimed at missing raw materials (distant targets tower by tower), avoids spots with more than
    `maxWaterShare` water.
  - After the food chain: large temple, mana chain, small temple; late gold mine/gold smelter (Egyptians: gem mine);
    expansion towards gold (`lateOres`) only once the build order is fulfilled up to the mine.
  - Enemies: all players except allies (`IsEnemy` → `World.AreAllies`, teams from the start screen) – their
    soldiers, buildings, piles (thief) and spell targets it leaves alone.
  - `AiPlayer.Military`: target garrison full (`Staff`), knows all enemy soldiers, re-weighs every second.
    Soldiers on a simple move order and on a (forced) retreat also count as idle – those retreating are
    immediately free for defence/guerrilla/campaign, otherwise they keep walking (guarding does not
    redirect them). Defence first; guerrilla against smaller squads (geologists too, `geologistHealth`) with clear
    superiority; standing-around enemies no deeper than `military.hinterlandDepth` into their land (Skirmisher 8);
    enemy soldiers within `military.reinforceRadius` (30) of the target count too (scattered ones come to
    help); no guerrilla where tower bowmen make up more than `military.towerFireRatio` (0.5) × target strength –
    checked from the centre of the attackers (a running one is cancelled if the enemy lures it under the towers);
    attack on the land = fighting or move target outside our own land; after a cancellation the target is left
    alone for 30 s; guerrilla before holding back (defenders may attack as long as enough remain against other
    squads). Rest: the trait `guardByThreat` guards by threat (without it: stay put); without a threat, tower
    distribution: `frontTowers` (Skirmisher), `everyOtherTower` (General, Legion),
    `rotatingTowers` (Unyielding: 30%, redrawn every minute), Hothead: one heap.
  - `AiPlayer.Campaign`: gathers soldiers, captures enemy military buildings at 1.5:1 (garrison weighted by
    `attack.garrisonWeight`, castle double). While gathering, it gives up only below 85% of the cancellation
    threshold; an abandoned/vacated target is left alone for 60 s.
    Values: `attack.armyRatio` (0 = irrelevant), `attack.commit` (share of free soldiers, 1 = all), `attack.abortRatio` (0 = never give up),
    `attack.maxDistance` (0 = irrelevant), `military.retreatRatio` (0 = never retreat).
  - `AiPlayer.Assault` (cheat `/attackme`, `AssaultOn`, saved): instead of strikes/campaign/defence/hospital,
    per military tick garrisons down to 1, all other soldiers (new ones too) against the player's military
    building nearest their centre (`Military.Attack`; bowmen, and everyone if no military building, via `Move`
    to the door), never retreat (no `TendWounded`); those fighting and those already sent against the player stay on it; priests follow.
    Ends when the player has no building left; allies never.
  - `AiPlayer.Challenge` (AI chat, `ChallengedBy`, saved): at the next military tick a campaign only against this
    player, without `armyRatio`, `maxDistance`, timing traits and a free path (even if our own land is under
    pressure); 1:1 against the defence suffices, at least half of the remaining free fighters come along; a
    normal campaign afterwards. Not during campaign/assault, allies never.
    `CanTakeUpChallenge`: the same target search as a dry run (for `cannotAttackSpeaker` in the situation).
  - `AiPlayer.Superiority` (`superiority` in `ai.json`, all characters; against opponents out of range on large
    maps): from `startMinutes` (60) every `intervalSeconds` (300) a crude comparison of the whole armies (all
    soldiers × combat strength); against the weakest enemy with ≥ `armyRatio` (2):1 a campaign as with the
    challenge (without distance, free path, timing traits), but with `ratio` (2) × the defence of the target;
    if no target is found, the next weakest. Not with enemies in our own land, a running campaign/assault, or a truce.
  - Allies in all strength comparisons (`AiPlayer.Allies`): the enemy side counts all enemies anyway; on our own
    side, allied soldiers outside count (`AlliedStrength`: campaign/hospital retreat within `attack.defenseRadius`
    of the target, strike within `reinforceRadius`, holding back/`forwardBase` within `threatRange`) as do bowmen
    of allied military buildings in range – that is how the AI joins its allies' battles and campaigns.
    `armyRatio` and `superiority` compare team against team (`TeamArmy`).
  - `AiPlayer.Diplomacy` (AI chat, saved, lazy for old save games): `Truce(player, ticks)` ends campaign/assault/challenge
    against them (soldiers home), cancels guerrilla strikes against them, no new ones until expiry; defence in our
    own land remains, their soldiers in our own land break it, `Challenge`/`Assault` end it. `Help(ally, ticks)`:
    half of the free soldiers (at least 1, no more than under `IdleSoldiers`) to the strongest enemy group in the
    ally's land, otherwise to the ally's military building nearest to an enemy building; updated per military tick, home
    after expiry / when the ally has no buildings. `CanHelp` → `cannotHelpSpeaker`.
    Situation for the chat: `AiSituation` (compact JSON, zeros/false omitted, per player: people, character, army,
    `powerVsSpeaker`, `distanceToSpeaker`, enemies in the land, land, buildings, battles, trend of the last 5 min
    from `GameHistory`, `doing`; plus the last 16 chat lines).
  - `AiPlayer.Tactics`: in small engagements (strike/campaign ≤ `military.wounded.maxSoldiers` 10) fighters below
    `withdrawHealth` (35%) retreat (forced, holding position `restDistance` from the enemy towards `RetreatDoor`;
    pursued → to the building; own hospital within call range, not closer to the enemy: released to its door
    instead), as long as the rest has `holdRatio` × opponents; back at `rejoinHealth` (healed), below `needRatio` ×
    opponents or enemy almost defeated (`mopUpRatio`); no renewed retreat for 20 s. Hothead: never.
    Hospital (`hospital` in `ai.json`): idle wounded (< `woundedHealth` 80%) to the nearest own hospital up to
    `sendDistance` (60): the weakest onto the healing spot, the others wait 3 fields in front of the door
    (otherwise they push each other aside and the healer never comes out); more than `wounded` (5) of those (not
    counting those still fighting) farther than `sendDistance` from every hospital (construction sites too) → a
    hospital before the build order (like residences) at the military building nearest them; at most `max` (3),
    none within `spacing` (120) of another (construction sites too).
    Late game = large army (`army.lateSoldiers` 20 soldiers outside the buildings; too large to withdraw
    individuals): weaponsmith shares `lateShares` (spear 100%, bow 80%, sword 15%); below ¾ of that, the shares from before.
    Strikes pursue all parts of disintegrated target groups (nearest first).
  - `AiPlayer.Magic`: priests follow attacks at a distance, cast from `magic.minTargets` at those not yet hit
    (enemies near our own fighters/strike/campaign soldiers, bowmen and reinforcements too; frozen ones count
    again shortly before thawing), several at the same time onto different spots, mana for running spells
    reserved. Frozen ones count for nothing in the strength comparison, more again with the thawing (`Strength`).
    Turning the tables (`TurnWithSpell`): instead of a strike cancellation, campaign abandonment or retreat, first
    the people's spell (not haste), as long as `magic.turnRatio` (0.6) of what is needed is still there. Upgrades
    immediately with enough mana (`magic.upgradeOrder`: swordsmen, bowmen, pikemen). The AI casts only the people's spell.
  - Characters/traits (concept: `docs/KI-TACTICS.md`; ids English, German names in `ui.json` `aiCharacters`/`aiTraits`):
    a character (`AiCharacter`, `characters` in `ai.json`) names traits (`AiTrait`, `Ai/Traits/<Id>Trait`; defaults
    under `traits`, overridable per character) and overrides values; `characterWeights` (per people) draw one per match.
    Decision points: `LeadCampaign` (leading the troops), `LeadStrike` (leading the engagement, `StrikeLeader`),
    `Guard` (guarding), `AllowCampaign` (timing), `GarrisonLimit` (garrison),
    `ArrangeBuildOrder` (build order, once at the start), `RunErrands` (errands, after guarding).
    Traits: `waves` (calm/waves alternating), `frontGarrison` (only buildings near the front full, otherwise
    `backline`), `earlyArmy` (smelter, weaponsmith, barracks right after wood/stone; smelter even without a mine
    if there is ore on own land), `forwardBase` (gathering at the military building nearest the front, retreating
    without a fight from stronger ones), `pincer` (General: feint from the gathering point, main group in an arc
    to the flank), `thief` (Skirmisher, Hothead: `scouts` scouts steal piles at the edge of enemy land, `ranks`
    gold/weapons/ores > tools > food > rest; not with enemy soldiers within `soldierRadius`, otherwise called home,
    piles shunned for `retrySeconds`).
    `summoner` (Legion: mana above `reserveSpells` × the people's spell → "Heavenly gifts" cast by a free priest
    on own land near him, `edgeDistance` from the edge, without enemy soldiers, with `minFreeFields` free fields).
    `formation` (Legion, General, Skirmisher: strike with bows + melee against enemies on foot, still out of
    range – bowmen forward to range − 2, melee fighters hold `parkDistance` behind them; enemy ≤
    `fallBackDistance` from the bowmen → they hold behind the melee fighters, who go in; after that every
    `fallBackSeconds` again, without a target the bowmen move up; at the latest after `screenSeconds` in).
    `buildOrderMoves` moves buildings in the build order.
    People characters (`standard`/`general` only via `--characters`): `hothead` (Egyptians: `earlyArmy`,
    everything attacks, hinterland thin, steals), `unyielding` (Asians: gives up only below half the defence
    strength), `legion` (Romans: early gold, waves, pincer, goods spells), `skirmisher` (Amazons: campaigns only
    from 2:1, small squads, near targets, steals).
    Made up (Arena, tournament): traits with `+` (`skirmisher+forwardBase`, appended ones before their own),
    values with `@path=value` (`general@attack.ratio=1.3@buildOrderMoves.barrack=-5`).
    A trait's notes begin with its id, appear live (`AiPlayer.Noted`, `TraitOfNote`).
    Values in `ai.json`, overridable per people (`civilizations`, the build order there too).
