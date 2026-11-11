# AI tactics and characters

A collection of ideas for making the computer opponents more challenging, more tactical and more surprising.
The basis is the existing AI (`src/Homestead.Simulation/Ai/`, values in `data/ai.json`).

## Terms

A uniform vocabulary – in conversations, docs, code and data:

| Term | English (code, `ai.json`) | Meaning |
|---|---|---|
| **Character** | character (`AiCharacter`) | *how* an AI plays: a list of traits with values |
| **Trait** | trait (`AiTrait`, `Ai/Traits/`) | a self-contained building block of behaviour, attaches to decision points |
| **Decision point** | decision point (method in `AiTrait`) | a fixed place in the AI's train of thought where traits may decide |
| **Built-in behaviour** | built-in behavior | what the AI does without a trait (economy, defence, guerrilla, campaign, garrison) |
| **Difficulty** | difficulty | *how well* an AI plays (Easy, Medium, Hard) – independent of the character |
| **Id** | id | English identifier in `ai.json` and code, e.g. `pincer`, `general` |
| **Name** | – | German display name in `data/ui.json` (`aiCharacters`, `aiTraits`), e.g. "Zange und Finte", "Feldherr" |

## Core idea: traits and characters

The AI remains a player with the same commands as a human. But new behaviour does not come
hard-wired into `AiPlayer`; it comes as a **trait**: a self-contained building block with its own
id, its own values and a clearly outlined place of use. A **character** is a list of
traits with values. Every people gets one or more characters; one of them is chosen per match.

```
People ──(one of them per match)──> Character ──> Traits (+ values)
```

### What stays the foundation

Not everything should be switchable. Without the following basic behaviour an AI would be broken, not different:

- Economy by build order, housing, expansion towards raw materials
- Defence first (enemies in our own land are always fought)
- No demolition of a military building whose land carries own buildings (since demolition leaves land
  unprotected, the AI would otherwise make itself vulnerable)
- No demolition of the last finished garrison – without it the game is lost (`ConquestGoals`);
  the AI never asks for it.

The built-in behaviour (guerrilla, campaign at 1.5:1, full garrison) is the character **Standard**
(`standard`, without traits). Guarding by threat is already a trait
(`guardByThreat`) and switched off for Standard: its idle soldiers stay where they are.

### Decision points

Traits attach to fixed places in the AI's train of thought. Each place determines how several
traits work together:

| Decision point | When | How several traits work together |
|---|---|---|
| **Garrison** (`GarrisonLimit`, implemented) | every military round, per own military building | traits limit the garrison; the lowest limit applies (at least 1) |
| **Attack targets** | planning of a campaign | traits evaluate targets (points add up), the best target wins |
| **Troop command** (`LeadCampaign`, implemented) | during a campaign | one leads the squad (e.g. pincer *or* ambush) |
| **Engagement command** (`LeadStrike`, implemented) | start of a strike (guerrilla, defence), then every military round | one leads the strike (e.g. battle formation); without one they all go in at the same time |
| **Guarding** (`Guard`, implemented) | every military round, for the remaining idle soldiers | the first one to station them decides; without one they stay put |
| **Tower spots** | expansion | traits evaluate build spots, points add up |
| **Timing** (`AllowCampaign`, implemented) | every military round, before planning a campaign | every trait can hold back a new campaign |
| **Spells** | priest at the battle | one decides spell and moment |
| **Build order** (`ArrangeBuildOrder`, implemented) | once at the start | every trait rearranges the build order the previous one left |
| **Errands** (`RunErrands`, implemented) | every military round, after guarding | every trait sends its own people out (e.g. scouts to steal) |
| **Thresholds** | wherever a ratio decides | modifier on values such as `attack.ratio` |

### Distributing soldiers

Several traits want the same idle soldiers. Fixed order:

1. Defence (basic behaviour)
2. running squads keep their soldiers
3. traits by their priority in the character (named first = first)
4. rest: guarding (if the character has it)

A trait takes only what it needs and returns soldiers when its plan ends.

### Data (`ai.json`)

```json
"traits": {
  "pincer": { "minSoldiers": 4, "feintShare": 0.35, ... }
},
"characters": {
  "standard": { "traits": [] },
  "general":  { "traits": ["pincer", "guardByThreat"], "attack": { "ratio": 1.2 }, "military": { "strikeRange": 40 } }
},
"characterWeights": [["standard", 1]],
"civilizations": {
  "Asian": { "characterWeights": [["trickster", 2], ["rusher", 1]] }
}
```

`traits` contains the defaults per trait. A character can override them under the trait's
id, and every general value too (after the people's). The weights
(`characterWeights`, overridable per people) determine how often a character is chosen.

### Code

- Base class `AiTrait` with overridable methods per decision point (`LeadCampaign`, `Guard`;
  more come with the traits that need them).
- Every trait in its own file under `Ai/Traits/` (`<Id>Trait`, e.g. `PincerTrait`).
- `AiPlayer` asks the traits of its character at every decision point.
- Client and Arena: `--characters ,general` fixes the character per player.
- A trait's notes begin with its id; client and Arena show them immediately,
  the client with the German name of character and trait.

## Traits

Status: all **open**. Effort roughly: S (small), M (medium), L (large).

### Guarding

**`guardByThreat` – Guard by threat** · **implemented** (with `general`, not with `standard`)
Idle soldiers guard our own military buildings, distributed by strength and proximity of enemy
squads (`military.threatRange`); without a threat they gather at the home tower. Whoever stands within
`military.guardDistance` of their post stays. Decision point "Guarding" (`Guard`).
Without this trait, idle soldiers stay where they are.

**`forwardBase` – Forward base** · M · **implemented**
Idle soldiers gather at our own military building nearest an enemy one, as a jumping-off point
for guerrilla. If the enemy squads within `military.threatRange` are stronger (`fallBackRatio`), they fall
back into our own land without a fight (building at least `fallBackDistance` fields farther from the
enemy, otherwise home tower). If the enemy advances further into our own land, defence and guerrilla
fall on its rear. Together with `frontGarrison` (hinterland thin, more soldiers free) an intermediate
depot for raids. Stands in the character before `guardByThreat` (both at the "Guarding" decision point).

**Distributing towers** · **implemented**
Without a threat (no enemy soldiers within `military.threatRange` of own military buildings) these
traits distribute the idle soldiers evenly over selected towers; with a threat the next trait
takes over (`guardByThreat`), so they stand before it in the character:
- `frontTowers` – Towers to the front (`skirmisher`): towers from which the nearest enemy
  military building is reachable without a detour (path at most `detour` × straight-line distance + 5) and no own
  military building stands in between.
- `everyOtherTower` – Every other tower (`general`, `legion`): every other one, counting from the front – a tower
  drops out if a neighbour (at most `neighborFactor` × distance to the nearest tower) is already chosen.
  Larger groups that quickly reach the skipped neighbours.
- `rotatingTowers` – Rotating towers (`unyielding`): a random `share` (30%) of the towers, redrawn
  every `rotateSeconds` (60) (fixed seed of the world).
The `hothead` keeps everything on one heap (no such trait).

The AI re-weighs military commands every second (`military.thinkSeconds` 1): soldiers on a simple
AI move order (to the post, home) count as idle, campaigns are abandoned on the way to the
gathering point if the defence has become too strong. Guerrilla also hits enemy
geologists (one soldier suffices). Enemy soldiers within `military.reinforceRadius` (30) count towards a guerrilla's target – lightly
scattered ones come to help; without that, early guerrillas almost always failed. Tower bowmen are
not opponents to be defeated but a cost: guerrilla (not defence) is skipped where they make up more than
`military.towerFireRatio` (0.5) × the strength of the enemies there (otherwise a single soldier next to a
full castle cost three men for one); if the enemy slips under the towers during a guerrilla,
the AI cancels. The check is from the centre of the chosen attackers, not from the nearest one
(he already stood behind the castle, the others ran past it). After a cancellation the same
enemies stay unharassed for 30 s (otherwise: attack, cancellation, attack … every few seconds). A soldier
only walking in his own land (recruit to the gathering point) does not attack the land – `hinterlandDepth` applies to him. `military.hinterlandDepth`
(`skirmisher`: 8) distinguishes attacks on land (enemies walking or fighting – attacked everywhere) from standing-around enemies, who are attacked
only if they stand at most that deep in their owner's own land. The bowmen of enemy military buildings that
reach the path or the target count towards a target's strength too (a
soldier next to a fully garrisoned castle is not alone); likewise in campaigns the bowmen
of neighbouring buildings. Whoever retreats runs to the nearest own military building that is not closer
to the enemy – not the whole way home.

### Garrison

**`frontGarrison` – Garrison by front** · M · **implemented** (character `hothead`)
Military buildings in the hinterland get 1 soldier, buildings on the border to the enemy full, there with
bowmen. Result: clearly more soldiers free for attacks.
Values: `frontDistance` (distance to an enemy military building from which a building is "front"; it stays
front until `frontDistance + 10` without an enemy military building, so the garrison does not flicker), `backline` (garrison in the hinterland).

**`reinforceThreatened` – Reinforce towers early** · S
If an enemy squad stands within range of a border tower, its garrison is set to full immediately,
before the attack begins.

**`rotateWounded` – Rotate the wounded** · M
Battered soldiers return into a tower, fresh ones come out.
*Prerequisite:* soldiers do not heal at the moment. Only sensible once they regain health
in the building (our own game rule, the same for human and AI).
*Built in instead of a trait* (`military.wounded`, `AiPlayer.Tactics`): in small engagements (≤ `maxSoldiers`
own soldiers) the severely wounded withdraw from the fight as long as the rest holds, and hold
out of sight of the enemy with "Hold position"; if an own hospital is within call range, the AI releases
them instead and sends them to its door (the hospital does not call those holding).
The AI also sends idle wounded to the nearest hospital (`hospital.sendDistance`): the weakest onto the
healing spot (whoever stands there is healed without a call), the others wait a few fields in front of the door – in the
crowd at the door they would push each other aside and the healer would never come out. It builds hospitals when needed like residences: more than `hospital.wounded` (5) wounded outside
the range of all hospitals → one at the military building nearest them. They come back when they are healed,
needed, or the enemy is almost defeated. The `hothead` never withdraws (`withdrawHealth` 0).

### Troop command

**`formation` – Battle formation** · M · **implemented** (`legion`, `general`, `skirmisher`)
Decision point "Engagement command" (`LeadStrike`, strikes = guerrilla and defence). If a strike
has bowmen and melee fighters, the enemies are predominantly on foot (their bowmen at most
`maxEnemyBowShare` of their strength) and still out of bow range, the bowmen go up front and shoot
from range; the melee fighters hold `parkDistance` fields behind them, out of sight of the enemies. If an
enemy comes within `fallBackDistance` of a bowman, the bowmen fall back behind the melee
fighters without a fight and shoot from there, the melee fighters go in; likewise in the further fight (at most
every `fallBackSeconds`, bowmen without a target move up in between). After `screenSeconds` the
melee fighters go in in any case. Bowmen and parked melee fighters use "Hold position" like the
player (`Military.Hold`, `Move(…, hold: true)`): only enemies in range, no pursuit, not idle –
so not called as garrison by buildings.

**`pincer` – Pincer and feint** · L · **implemented** (character `general`)
Two squads: a small one lures the defenders out at one spot, the main attack comes shortly
after from the other side. The campaign gets two gathering points for it.
Implementation: `Ai/Traits/PincerTrait.cs`. The feint (`feintShare`) gathers at our own building nearest
the target, the main group walks via a waypoint in an arc to a staging point
(`flankAngle`, `flankDistance`, out of range of the target's bowmen); both ready → the feint
attacks, the main group `strikeSeconds` later. It takes `extraShare` extra soldiers along, gives
up only below `retreatRatio`. The General already attacks at 1.2:1 and lets guerrilla be far from our
own land (`strikeRange` 40), so soldiers remain for campaigns.
Observed (Salomons, 3 players): several pincers from about minute 37; the opponent often pulls
soldiers together, then the General cancels – intended behaviour.

**`flanking` – Ambush by outflanking** · L
A small squad walks in a wide arc around the enemy camp, outside the sight of enemy
soldiers and the range of bowmen in towers, and attacks from behind. The targets are weakly
garrisoned buildings in the hinterland. If one is captured, it takes land, and the enemy
buildings on it fall (the "unprotected land" rule). A human player does not reckon with that.
Values: squad size, minimum distance from the enemy on the detour, highest garrison of a target.
If the squad is discovered (enemies come into sight), it attacks immediately or withdraws.

**`spellTiming` – Well-timed spells** · M
Freeze immediately before the clash, Banish on clusters in front of a tower, Convert
onto the strongest enemies in range. Today the AI casts as soon as enough targets stand in the radius.

**`summoner` – Goods summoner** · S · **implemented** (`legion`)
Mana beyond `reserveSpells` (2) people's spells goes into "Call goods". A free priest casts
onto the best spot within `priestRadius` (12) around him: own land, at least `edgeDistance` (10)
fields from its end (Light fingers, enemies; the map edge does not count), no enemy soldier outside within
`soldierRadius` (20), at least `minFreeFields` (30) free fields in the spell radius; the most free
fields win, then the nearest to the priest. Only as long as none of the player's priests is casting; every
`checkSeconds` (5) checked. Upgrades cost no mana and stay untouched. Decision point "Errands".

### Exploiting weaknesses

**`exposedTowers` – Counter exposed towers** · M
If the opponent pulls soldiers out of his towers for an attack (down to one), exactly
these towers are attacked immediately. Evaluates targets by garrison now, not by the normal garrison.

**`landGrab` – Land grab without a fight** · M
Prefer tower spots whose territory covers unprotected enemy land (e.g. after a
demolition). As soon as the tower is garrisoned, the enemy buildings on it fall.

**`economicTargets` – Hit the economy** · S
Rate attack targets higher whose land carries mines, smelters, weaponsmiths or temples.
A hit there costs the opponent more than any tower.

### Scouts

**`thief` – Light fingers** · M · **implemented** (`skirmisher`, `hothead`)
Keeps `scouts` (2) scouts – free carriers become ones, losses replaced at the earliest after `replaceSeconds` (90)
– and sends them to goods piles at the edge of enemy land: on foreign land at most
`edgeDepth` (12; the piles of a storehouse right at the border lie a good 8 fields deep) fields from its end, at most
`maxDistance` (30) from our own land. Goods by `ranks`: gold, gems, weapons, ores first, then tools, then food (bread, fish,
meat), then the rest; at the pile the scout takes the best good in the theft radius. If an
enemy soldier stands outside within `soldierRadius` (12, somewhat more than `scouts.revealRadius`) of the pile,
no attempt; if one comes that close to the scout or his pile, the AI calls him home and leaves the
pile alone for `retrySeconds` (60). Loaded ones run to the nearest field of our own land and drop the
goods there. Decision point "Errands". Risky against an AI: it becomes suspicious as soon as
a scout stands on its land, and sends a soldier who recognizes him up close.

### Gold

See [Gold as a lever](#gold-as-a-lever). Three traits, each at its own decision point.

**`goldRace` – Secure gold early** · M
Gold mountains (Egyptians: gems) count as an expansion target from the start, not only after the
build order (`lateOres`). Gold mine and gold smelter move up in the build order as soon as food
and tools are running. Values: from which step of the build order, weight of contested gold mountains.
Decision points: tower spots, building.

**`goldGuard` – Protect gold** · M
Land on which our own gold lies (storehouse, output pile of the smelter) counts as front – even in
the hinterland: military buildings there fully garrisoned, with bowmen, guards by the amount of gold
instead of only by threat. A new storehouse only on land covered by several own towers. Optionally distribute
gold over several storehouses so that one hit does not cost everything (trade-off: more places to defend).
Decision points: garrison, guarding, storehouse build spot.
Recommended on Hard in every character – without it a single capture can finish the AI.

**`goldRaid` – Capture gold** · M
Attack targets whose land carries enemy gold get the highest rating: the capture takes combat
strength from the opponent and gives it to the attacker. Worthwhile even with a narrower superiority
(own value for `attack.ratio`), because the gain shifts the forces immediately.
Decision point: attack targets.

### Timing and strategy

**`earlyArmy` – Early army** · S · **implemented**
The first entries of iron smelter, weaponsmith and barracks move up together directly behind the
first `after` entries (wood and stone). The starting goods (iron ore, coal on medium and high
start levels) become soldiers immediately, instead of waiting for mines and food. Worth it only with a character that
uses the early soldiers too: for the cautious Skirmisher it cost economy (0.43 instead of 0.48
against Standard), for the Hothead it brought 0.56 instead of 0.52. `forwardBase` weakened the
Skirmisher too (0.39): her soldiers stood at the front within range of the enemy guerrilla. In return a smelter
may come without its own mine if its ore lies on own land (applies to all).

**`earlyRush` – Early rush** · M
With the starting soldiers, attack a nearby enemy tower early, before the opponent has barracks
and weapons. Normal economy afterwards.

**`powerSpike` – Attack after an upgrade** · S
Hold back attacks until an upgrade is finished or enough gold for high combat strength is stored,
then strike in a concentrated blow. Collect gold specifically for this.

**`waves` – Phases of tension** · S · **implemented** (character `legion`)
An alternation of calm build-up phases and attack waves instead of steady pressure. Values: `calmSeconds`,
`waveSeconds`, `jitter` (spread). Defence and guerrilla keep running in the calm phase.

**`multiFront` – Pressure at several points** · M
Two or three squads threaten different border sections at the same time. The defender must
split up; the attack goes where he stands thinnest.

**`jitter` – Spread thresholds** · S
Fixed ratios (`attack.ratio`, `strikeRatio` …) are rolled within a span at every decision
(e.g. 1.3–1.8 instead of 1.5). The behaviour becomes harder to predict.

### Rejected

**Focus fire** (several soldiers on the same target, wounded and bowmen first): the player
cannot order individual units to attack a specific target. The AI should not get a control that
the human lacks.

## Knobs without a trait

Many differences need no trait of their own, just different values in the character:

| Value | Effect |
|---|---|
| `attack.ratio` | superiority at the target from which a campaign starts |
| `attack.armyRatio` | superiority of the whole army (default: `ratio`; 0 = irrelevant → small campaigns against weak towers) |
| `attack.commit` | share of the remaining free soldiers that comes along in addition (1 = full strength) |
| `attack.abortRatio` | after gathering: cancellation below this ratio (default: `ratio`; 0 = never) |
| `attack.maxDistance` | target at most this far from our own land (0 = irrelevant; a border tower stands a good 30 fields behind its land border) |
| `attack.minSoldiers`, `attack.defenseRadius` | smallest squad; how far enemies around the target count for the defence |
| `military.strikeRange`, `military.strikeRatio` | guerrilla: how far out, from which superiority |
| `military.retreatRatio` | retreat below it (0 = never) |
| `buildOrderMoves` | move buildings in the people's build order, e.g. `{ "barrack": -6 }` |
| `maxSites` | simultaneous construction sites |

For trying things out without an entry in `ai.json`: `pincer+waves` (only these traits) and
`@path=value` (`general@attack.ratio=1.3@buildOrderMoves.barrack=-5`). The tournament
(`tools/trait-tournament.py`) varies values with `--vary attack.ratio=1.2/1.5/2`.

## People characters

Goal: one character per people that feels distinctive as an opponent. Defined in `ai.json`, per
people via `characterWeights` (under `civilizations`).

| People | Character | Idea | Implementation |
|---|---|---|---|
| Egyptians | Hothead (`hothead`) | temperamental, always attacks with full strength | earlyArmy, frontGarrison, thief; `ratio` 1.2, `commit` 1, guerrilla at 1.2:1 up to 120 fields (with earlyArmy 0.56 instead of 0.52 against Standard, decides more matches) |
| Asians | Unyielding (`unyielding`) | never gives up, never retreats | guardByThreat; `abortRatio` 0.5, `retreatRatio` 0.5 (at 0 it ran itself to death: 0.35 points against Standard, with 0.5: 0.50), `ratio` 1.4 |
| Romans | Legion (`legion`) | economically strong, attacks in concentrated waves | waves, pincer, guardByThreat, summoner; `minSoldiers` 8, `commit` 0.6; gold 10 places earlier; 5 construction sites |
| Amazons | Skirmisher (`skirmisher`) | plays it safe, weakens the opponent with small guerrillas and theft at the border | guardByThreat, thief; campaign only from 2:1, but without army comparison from 2 soldiers and only up to 50 fields from our own land (`maxDistance`; measured from the nearest tower it never attacked, because two towers on a shared border already stand a good 60 fields apart; without it, it attacked a tower right across the map in minute 1 and then lost everything); guerrilla up to 150 fields at 2:1 (at 1.3:1 with retreat below 1.2 the small squads turned around at the first sign of danger), retreat below 1:1 |

Guideline: play style, variety and fun before exact balance. Every people should play recognisably
differently as an opponent, with strengths and exploitable weaknesses. Observed (Arena, Amazons against
Egyptians, 10 matches): the Hothead overruns small maps in 20–30 minutes; if the rush fizzles out,
it bleeds to death (in the end 3–33 against 100–220 soldiers). Against Egyptians, then, the task is to
survive early; against Amazons, attack early or prepare for a hard late game.

## Characters (draft)

| Character (id) | Idea | Traits |
|---|---|---|
| Standard (`standard`) | built-in behaviour | – (guerrilla, campaign, full garrison are built in) |
| Rusher (`rusher`) | attacks early and often, a lower superiority suffices | earlyRush, frontGarrison, waves |
| General (`general`) | orderly battles | pincer, formation, guardByThreat (implemented); planned: spellTiming, reinforceThreatened |
| Trickster (`trickster`) | strikes where nobody is looking | flanking, exposedTowers, goldRaid, frontGarrison, jitter |
| Land taker (`landTaker`) | grows beyond borders and takes land without a fight | landGrab, frontGarrison, economicTargets |
| Merchant (`merchant`) | builds up calmly, then strikes with strong soldiers | goldRace, goldGuard, powerSpike, spellTiming |

## Difficulty and character

Two independent axes:

- **Character** = *how* the AI plays (style, traits).
- **Difficulty** = *how well* it does so (perception, reaction, decision quality).

A Rusher can be easy or hard. What is built is the best AI (Hard); easier levels
get targeted deductions.

### Knobs per level

| Knob | Easy | Medium | Hard |
|---|---|---|---|
| Perception | own land, scouts, geologists | what it sees, plus the location of mountains and stones | everything |
| Scouts/geologists | yes, to find raw materials | yes, for finding opponents | no |
| Reaction | only to attacks in our own land | to discovered enemies nearby | to all enemies nearby, anticipating |
| Stationing soldiers | somewhere in the land | at border towers towards the enemy land | at border towers towards enemy troops |
| Guerrilla | no | yes | yes |
| Traits | few, simple ones | the ones for combat | all of the character's |
| Thresholds | rather hesitant or reckless | Standard | precisely calculated |
| Thinking pause | long | medium | short |

Traits can themselves weaken by level (e.g. `flanking` on Easy without regard to
sight range) instead of only being switched on or off.

### Hard must win, not just hold

An impenetrable defence makes the AI hard to *beat*, but not hard to *play*:
the human only has to bunker down too, and the match ends in stalemate. Hard therefore also
needs a path to victory:

- **Outplay the economy:** distribute better, gold earlier, secure resources faster. Against an
  equally strong defence, whoever delivers more and stronger soldiers wins.
- **Overstretch the defender:** pressure at two or three places at once. Even a human can
  not be everywhere; the AI attacks where he is not defending right now.
- **Patience and then a strike:** wait until combat strength (gold) and upgrades are clearly ahead
  (`powerSpike`), then attack in a bundle where a capture drags the economy along
  (`economicTargets`).
- **Magic as a breakthrough:** Freeze, Banish, Convert deliberately in the assault on a tower.

## Gold as a lever

Gold exists in the game precisely to break the defender's advantage and force into the offensive.

- **Rule:** in enemy land a soldier fights at 100% / player count. Gold bars on our own land
  raise that without an upper limit, always more expensive per step. With two players: 115 bars for 100%,
  305 for 130%, after that 10 per point, 1005 for 200% (Egyptians: twice as many bars or half as many
  gems; `combatStrength` in `military.json`). Above 100% a soldier strikes harder abroad
  than at home – whoever is rich is pushed into the attack.
- **Defending needs no gold, attacking does.** Whoever has more gold can attack; whoever has none,
  can only hold.
- **Gold belongs to the land:** what counts are the piles on a player's land, no matter who put
  them down. If a player captures the land around an enemy storehouse, the gold there belongs to him
  (intended). The opponent loses combat strength, the winner gains it – the match can be
  decided by it immediately.
- **Consequences for the AI:**
  - Gold is a strategic target from the start (`goldRace`), not an afterthought at the end of the build order.
  - Our own gold is the most valuable possession and is protected like the front (`goldGuard`).
  - Enemy gold is the best attack target (`goldRaid`).
- **Risk:** the snowball effect is strongest here. The intent is that a successful strike on the
  gold turns the match; it would be bad if that happened without a real chance to counter. Therefore
  gold must lie visibly and defensibly – the same for human and AI.

## Dynamics and stalemate

### Why matches end in stalemate today

- **Defender's advantage:** in our own land 100% combat strength, in enemy land 100% / player count. With
  two players an attacker needs roughly double the army; the AI attacks only from 1.5:1.
- **Omniscient, instantly reacting AI:** guards stand by threat. Whoever gathers
  is seen before attacking.
- **The same calculation on both sides:** two identical AIs hold each other back.
- **Hardly any weaknesses:** the AI rarely stalls (smelter only with its own mine, material for
  the stonecutter held back …). In the original exactly such hang-ups decided the matches.

### What can turn a match (intended)

- **Gold race:** gold raises combat strength in enemy land – the built-in way out of the stalemate.
  Whoever comes close to 100% first can attack; above it they must. Gold mountains become a contested target.
- **Raw materials run out** (`depletion`): whoever does not get to new raw materials loses slowly. If they
  lie between the players, fighting becomes necessary.
- **Capture with consequences:** if a tower falls, the buildings on its land fall. A hit on
  a mine, smelter or weaponsmith cripples a whole chain.
- **Magic:** turns the balance of power without a double army.
- **Different characters:** a Rusher against a Merchant takes a risk; one of them
  wins early or burns out.
- **Intended weaknesses per character:** the Land taker is thinly garrisoned everywhere, the Rusher
  neglects the economy. Something to exploit – recognisable rather than random.

### What can spoil a match

- **Too fast a snowball effect:** an early capture tips over half the economy; the match is
  decided in minute 15 but lasts another hour.
- **Bunkering:** if defending becomes even cheaper, both build only towers and nothing happens.
- **Omniscience feels unfair:** a human's ambush never succeeds. Remedy via the
  perception of the difficulty levels.
- **Random hang-ups:** if the AI stalls because of a bug, luck decides instead of play.
- **The map decides:** if gold is the only lever, whoever happens to have the gold mountain wins.

### First steps

1. **Measure:** Arena, 5–10 matches of AI against AI at 60 minutes each; log combat strength, gold, captures and
   losses. How often does it end in a stalemate?
2. **Gold earlier and deliberately:** aim for gold mountains (`goldRace`), protect gold (`goldGuard`),
   hoard gold for combat strength and then strike (`powerSpike`, `goldRaid`).
3. **Limit perception** (levels Easy/Medium): reaction only to seen squads.
4. **Touch the defender's advantage** only with Arena numbers, e.g. the deduction fully only deep
   in enemy land, weaker at the border.

## Approach

1. ~~Framework for traits~~ **implemented**: characters in `ai.json`, base class `AiTrait` (decision points
   as overridable methods, so far `LeadCampaign`), today's behaviour = character `standard`
   without traits. Further decision points come with the traits that need them.
2. Extend the Arena with measurements on stalemates (combat strength, gold, captures, losses over time).
3. Implement traits one by one, each with a test and Arena comparison against Standard; first the ones that
   break the stalemate (`goldRace`, `goldGuard`, `goldRaid`, `powerSpike`, `economicTargets`, `multiFront`).
4. Difficulty as the second axis: perception, reaction, thresholds, allowed traits.
5. Assign the characters to the peoples.
