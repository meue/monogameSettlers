# Client (`src/Homestead.Client`)

MonoGame game. Capture options: [capture.md](capture.md).

- **Title screen** (`TitleScreen`, first without `--map`/`--capture`): main menu image of the Gold Edition (`titleScreen` in `ui.json`, file 61 GUI 1,
  smooth gold beside it), engraved buttons on the left as in the original (dimensions/colours from the original menu): at the very top Continue
  (loads `SaveSlots.Latest`, pale without a save game), set off Campaign, Mission CD, Amazon campaign (each → campaign),
  set off Single player (→ start screen), Multiplayer (→ `MultiplayerScreen`, see `docs/MULTIPLAYER.md`), Settings (→ settings), Load game
  (list of the 8 slots as a `ListScreen`: name, date, people; empty ones pale; clicking loads, load error above the title), set off Readme/Contribution/Licenses (popup on the
  original message box file 02 GUI 16, `popup` in `titleScreen`: "Open in browser" → `README.md`/`CONTRIBUTING.md`/`THIRD_PARTY_NOTICES.md` on GitHub, Enter/Esc),
  set off at the bottom Quit; Esc/"Main menu" of the statistics/Esc in the start screen lead here.
- **Loading screen** (`LoadingScreen`, `MapGame.ShowLoading`): before every load of a game (start screen, mission, save game) the three helmets,
  at least 1 s (real time, from the first draw); the game is already drawn hidden underneath while loading (`WarmFrames`, 3 – the first frames fill the atlases,
  otherwise the fade stutters), then 0.3 s fading out over the running game (from the first fade frame) (`loadingScreen` in `ui.json`, 1024 × 768, edge columns stretched to the side): campaign orange (file 02 GUI 28), Mission CD green
  (60/0), Amazons purple with torso (48/0), otherwise gold (61/0); theme other than Normal: always its image; captures load immediately.
- **Campaign** (`CampaignScreen`, above the title screen): choosing the side per campaign (`campaignScreens` in `ui.json`: image and name field
  per side, heading; clicking selects, "Back"/Esc → title – the original has none). Settlers (`settlers`): per people, file 02 GUI 7, golden frame under the
  mouse. Mission CD (`missionCd`): file 60 GUI 4 (castaways in the storm), name fields with difficulty (`label`, wrapped),
  brighter figures file 60 sequence 0. Amazons (`amazons`): file 48 GUI 1, the Amazons on the left (`amazons`), the army of the three peoples on the right (`peoples`); brighter figures under the
  mouse (file 48, sequence 2, placed by correlation, `lit`). → Intro video of the side → (end or click) description of the first mission
  (`BriefingScreen` on the start-screen background GUI 8: mission text at the top with scrolling, map in the slanted window with the players' initials
  at their starting points – own green, others red –, hints on the right, voice-over with it; "Back"/Esc → people selection,
  "Next"/Enter → mission with the map's peoples/buildings). `data/campaign.json`: per campaign `name` (text key, in the save game name) and `sides` with `name` (text key), `intro`,
  `outro` (victory video) and `missions` (`map`, `text`/`hint` = entries of the text table in GFX file 07, German/English/Polish by language (`Lang.TextIndex`: column 0/1/4 of the text table, Polish in code page 1250), `voice`,
  `labels` per map player – so far only mission 1, from the mission text – or `preview` [file, sequence, image] of a finished original preview image
  instead of the rendered map: Amazons file 48 sequence 3, Z01–Z12 = 0–11, G01–G12 = 12–23, assigned via the water mask); paths in the
  installation, `GameInstallation.FindPath` case-insensitively. Mission CD: `S3MCD/MAP/MIS_M/R|E|A` (texts 24–31 Romans, 32–39 Egyptians, 40–47 Asians, preview images file 60 sequence 1 = 0–23), without videos and voice-overs
  (`intro`/`outro`/`voice` omitted: skipped, not in the mission selection). Amazon campaign: `S3QOTA/MIS_M/Z` (Amazons, texts 48–59) and `G`
  (the three peoples, player 1 changes, texts 60–71), hints +72, videos `S3QOTA/ADD/1/DATAP0_N` (intro Amazons 1, three peoples 2; victory 3 and 5 respectively).
  Progress (global save game): `CampaignProgress` (`campaign.json` next to the save games, number of missions accomplished per campaign/side);
  people selection with progress → mission selection (`MissionSelectScreen` over `ListScreen` – shared list with "Load game" –, file 02 GUI 18, `missionScreen` in `ui.json`: the intro first,
  all accomplished ones and the next, names from the mission texts, after all of them the finale; a longer list scrolls (wheel, arrows, scroll bar), opens at the newest; click → description, whose "Back" returns
  here, or video and back afterwards; Esc/"Back" → people selection). Videos via `MapGame.PlayVideo(path, after)`. The running mission `MissionRef` (`settlers/Roman/0`, `amazons/peoples/3`) is in the
  save game (client value `mission`, header `SaveInfo.Mission` – lists show "Campaign Romans, mission 1").
  Victory/defeat: `World.Goals` (`MissionGoals`, in missions only, saved) checks every second for player 1 (`MissionGoals.Player`): no own building
  (construction sites too) left, after having had one, or none and no settlers (G06 starts without buildings) → lost; no foreign one that is not allied left (destroyed/captured) → won; once decided, it stays that way. Mission-specific conditions
  from the texts (holding an outpost, gold amounts …) not yet.
  Free matches (start screen, old save games too): `ConquestGoals` (`World.Goals` is `IGameGoals`) – whoever no longer has a finished military building
  (tower, large tower, castle) after having had one is definitively defeated (`IsDefeated`); their AI no longer thinks (`World` skips it),
  settlers/soldiers act only on their own. Per player (`OutcomeOf`, `DecidedAt`): defeated → lost, all enemies defeated → won; the client shows that of
  the local player (banner with `system.wonHint`/`lostHint`, statistics without "Resume"). Won: "Game won" in white at the top, progress saved immediately, playing on
  possible; quit game → statistics ("Resume") → next mission description via mission selection and people selection (after the last one the victory video, then the selection).
  Winning lifts the fog of war (`FogOn`; the cheat choice `_fogChosen` stays for the next game).
  Lost: "Game lost", no more commands (selection, building, menus, keys except Esc/F11/F3/T/Space); camera, minimap, events and system menu
  keep going; Esc → statistics without resuming → the same mission description.
  Cheat menu: "Mission accomplished"/"Mission lost".
  `VideoPlayer`: `AviFile` (Homestead.Assets, `docs/formate/avi.md`) provides header and PCM audio; an installed ffmpeg decodes the video (Indeo 5) in its
  own process (`HOMESTEAD_FFMPEG`, next to the program, PATH; otherwise skipped); frames follow the clock from the first frame/audio start, volume = effects;
  Esc/Space/Enter/click cancels.
- **Settings** (`SettingsScreen`): background file 02 GUI 14 (`settingsScreen` in `ui.json`, marble beside it); at the top in the four
  boxes tabs with icons of the original statistics menus (file 03, dark = selected, pressed in, otherwise light; Tab/Shift+Tab): Resources,
  Economy (still empty), Military, Display; one row of values per setting (Resources: `depletion` stones/mountains/fish
  100/75/50/25/10%/∞; Military: spell cost increase constant/+10/+25/+50% → `MagicConfig.CostIncrease`; Display: hardware cursor, full screen, language Deutsch/English/Polski (default English, independent of the system language; applies to the installation dialog too),
  theme Normal/Gold/Classic/Amazons (title and loading screen; `titleScreen.themes` in `ui.json` with engraving colours, Normal = Gold; `HOMESTEAD_THEME` overrides), music/effects 100/75/50/25%/off, music per people, preferred color
  (`DisplaySettings.PreferredColor`, colour fields, saved as an identifier), autosave
  (yes/no, default no: every 15 min real time in-game and on "Quit game" into slot 8, not after a defeat, not in captures), intro video (yes/no, default yes: `MapGame.IntroVideo` = `S3/ADD/001/01_00_00.DAT` at every start before the title screen, end/click → title) – music without a choice by system language; `Lang.Use` sets the culture too;
  Gold Edition title image per language, `background` `{de, en}`; Polish without its own title image (the English one), `HOMESTEAD_LANGUAGE=de|en|pl` overrides, e.g. for captures).
  OK/Enter saves `GameSettings` (section `game`) and `DisplaySettings` (client, section `display`, applied immediately) to
  `~/.config/homestead/settings.json` (`SettingsFile`: per section, the rest of the file stays); cancel/Esc discards, defaults resets the open
  tab (game values: `production.json`). Hardware cursor off: own golden pointer (`SoftwareCursor`, in `EndDraw`, not in captures).
  New matches: `GameSettings.Load(…)` → `GameSetup.Settings` → `Apply(…)` in `OriginalScenario.Load(production:, magic:)`; save games keep their values (Arena/tests: `production.json` only).
- **Start screen** (`StartScreen`): on the original's map selection background (`startScreen` in `ui.json`, file 02 GUI 8,
  scaled from the 800×600 grid to the window size, marble beside it; oval stone buttons, stone bar under the selection, preview in the slanted window): map list with preview, tabs Single player/Multiplayer/Own maps (Tab key),
  sorted by player count (`OriginalMap.ReadPlayerCount` only reads MapInfo). Tab "Random generator": settings (players, symmetry, rivers, resources, stones, forests,
  treasures, King of the Hill; decoration not selectable, always medium) – a change regenerates with the same seed, "reroll"/R a new seed. Per player (table with fixed row height, more players scroll): people (or random; map presets apply per position),
  colour (`PlayerColors.Palette`, sorted by hue, identifiers `Choices`; clicking opens a picker below with "?" and all colours, taken ones
  struck through; player 1 preset with the settings' "Preferred color", default red; "Random" → `PlayerColors.Assign`), team (clicking opens a picker 1–4/"-", default "-" = none; `World.Teams`,
  set after loading; `TeamOf`, `AreAllies`), Hardcore.
  Multiplayer starting positions drawn by lot (preview black "?", clicking assigns/reverts; `GameSetup.Positions` → `GameSetup.ResolvePositions`).
  Every game (start screen, `--map`, PageUp/PageDown, missions) starts via a `GameSetup` (`MapGame.SoloSetup` → `StartGame`, see `docs/MULTIPLAYER.md`).
  Hardcore (`World.HardcorePlayers`, `Hardcore`): +10% combat strength flat (above 100% too), +30% settlers per residence, +20% soldier HP
  (`World.MaxHealth`), −25% building material per pile (rounded, at least 1, `World.ConstructionCost`), work times × 0.9,
  start +2 swordsmen, +5 planks, +5 logs (`OriginalScenario.Load(hardcore:)`).
- **Side panel** (`SidePanel`, large variant, scaled to the window height; menus draw/check clicks in the panel space, `SidePanel.ToPanel`):
  - Minimap at the top in the original frame (black behind it; on the right a narrow gold border with a vine, file 4 sequence 3): explored terrain (`Minimap`; no longer in sight: half as bright, without foreign things),
    own always white, allies turquoise (`Minimap.AllyColor`, like own under fog too; border posts on the map stay in the player's colour), opponents always red – territory borders and dots (`MinimapDot`):
    soldiers, priests, geologists, pioneers, scouts outside (foreign ones under fog only in sight, scouts only once revealed), buildings/construction sites as larger black squares (occupied military buildings white/red inside), visible section as a white frame. Click: camera flight 0.35 s; dragging moves the frame; scrolling cancels the flight;
    right-click/Alt+right-click with a selection: command there as on the map.
  - Event buttons to the right of the computer button (`EventButtons`: info, combat, resources, trade; space/images in `events.json` `categories`):
    a new event → the button blinks for `blinkSeconds` at 1 Hz, then glows until seen. Click (newest unseen of the kind, otherwise newest) or
    Space (newest unseen; held down after 1 s at 10 Hz) → event page (`PanelTab.Event`: category, text white), camera flight,
    four arrows `arrowSeconds` on the building or living figure. `dates`: greetings on calendar days (MM-dd), once a year
    (`~/.config/homestead/events-shown.json`; `HOMESTEAD_EVENT_DATE=12-25` fakes it).
  - Info feed (`InfoFeed`): white notes at the bottom left of the play area, new ones slide in from below and push older ones up (at most 6), 60 s each;
    with a player reference a round dot in the player's colour in front (`InfoFeed.Add(…, dot)`);
    texts `info.*`: AI player defeated (`ConquestGoals.IsDefeated`, not those already defeated before loading; their colour), demolition confirmation on our own
    last finished military building "This is the last tower" (own colour), saved/autosave/failed,
    "Not saved for n minutes" every 30 min real time since saving/start/loading (`MapGame.NoteNews`).
    Chat (`ChatInput`, after a defeat too): Enter opens an input line at the bottom ("> ", caret), then a terminal view: the last 12 lines (faded
    ones too, history 100) on a dark background; the keys go only into the line (no hotkeys/camera/F12, the mouse keeps working); Enter says it as
    player 1 in `World.Chat` (`ChatLog`, saved), Esc discards, ↑/↓ earlier lines, Ctrl+V pastes (`SdlClipboard`). New chat lines from all
    players arrive as `chat.line` ("Player n: …", dot in the player's colour) in the info feed; nothing when loading.
    Commands (`/…`, not in the chat, `MapGame.RunCommand`) only with `"cheats": true` in the `display` section of `settings.json` (`DisplaySettings.Cheats`,
    not in the settings screen); otherwise a random computer opponent (not allied, not defeated) says `chat.noCheats` in the chat.
    `/cheats` opens/closes the cheat menu (`CheatMenu`: soldiers per type/level, priests, geologists, scouts, pioneers, carriers,
    war machines, +50 mana, mission accomplished/lost, fog of war on/off); `/attackme`: all undefeated computer opponents `AiPlayer.Assault(0)` (info `chat.attackMe`); unknown: `chat.unknown` with the list.
    AI chat (`AiChat`, only with a key: `aiChat` section of `settings.json` with `apiKey`, `model` (default `claude-haiku-5-5`, without thinking;
    other models always think), `effort` (`low`), otherwise `ANTHROPIC_API_KEY`; tokens per request on the console): every own line → a request to the Claude API in the background (Anthropic SDK), one at a time
    (things said in between go with the next one). Prompt and character voices in `data/ai-chat.json` (system cached), situation `AiSituation`.
    Claude speaks for all computer opponents via the tools `say` (→ `World.Say`), `attack` (→ `AiPlayer.Challenge`, `all_in` → `Assault`;
    per opponent at most every `attackCooldownSeconds`), `truce` (→ `Truce`, up to `truceMaxMinutes`) and `defend` (→ `Help`, up to `helpMaxMinutes`); applied in the game thread, answers for another game discarded; errors `chat.aiFailed`;
    unsolicited (`AiChat.Reactions`, `reactions` in ai-chat.json): goods that our own donkeys unload on a computer opponent's land
    (`World.GoodsHandedOver`), summed by `giftValues` (gold 10 … food 2, rest 0.2, log −1), at `giftPleased` (60) or
    `giftInsulted` (−8) a hidden request "this happened"; captured military buildings (`buildingCaptured`/`buildingLost`, `captureChance` 15%) and big
    fights (≥ `bigFightSoldiers` dead per statistics sample, `chance` 30%), per opponent at most every `cooldownSeconds` (90);
    our own army advancing on an opponent (`army`, not allies: ≥ 20 own soldiers outside in his land or ≤ 20 fields in front of his military buildings, counted
    every second, on reaching it) at 30%, per opponent at most every 10 min; prompt: alliances last the whole game, allied soldiers in the land protect; attack commands with the result on the console; umlauts written as `\u00e4` converted back.
    Multiplayer later: only the server asks and distributes the results as commands.
  - Computer button at the bottom left (`SystemMenu`, `PanelTab.System`): "Save game" (8 slots with map, date, people; empty ones dashed;
    clicking saves, overwriting too), "Quit game" with a confirmation (Yes at the bottom left → statistics, No at the bottom right).
  - Main tabs Build/Goods/Settlers (`PanelTabs`: original buttons file 03, active one pressed; clicking clears the selection; submenus `panelTabs` in `ui.json`).
    - Build: build menu (categories from `data/ui.json`, original buttons; clicking an icon → placing; our own people; number finished (white) and under construction (orange); tooltip with the name after dwelling 1 s on an icon, gone on leaving – `BuildMenu.DrawTooltip`, in screen space, may protrude over the map). The same box next to the cursor (`UiTooltip`, likewise after 1 s) also names the icon-only controls of the submenus (stock, manufacture, goods transport, distribution, settler statistics, professions, warriors, production) and the icons at the top of the building panel (demolish, stop/resume work, highest priority, work area).
    - Goods › Stock (`StockMenu`, `World.InStock`: storehouse, output and loose piles; not input/construction sites).
    - Goods › Manufacture (`ManufactureMenu`): per product an order with up/down (`Production.Ordered`/`ChangeOrdered`, made first) and
      a share bar (`ShareBar`: clicking sets, dragging, 5% steps, near the ends 0/100%).
    - Goods › Distribution (`DistributionMenu`): goods with ≥ 2 consumers (order `distributionMenu`), on the right per consumer share + `ShareBar`.
    - Goods › Transport (`TransportMenu`): transport order in rows of six (offset); dot green = store, red = not; the selected
      good: store/not, to the very top, up, down, to the very bottom (images `transportMenu`); icons glide.
    - Settlers › Settler statistics (`SettlersOverview`): housing, demand, all settlers, soldiers, per kind portrait + count (donkeys 0; without a portrait the figure in a frame).
    - Settlers › Professions (`ProfessionsMenu`): shares ±5% (carriers at least, diggers/builders at most; `Recruitment.ShareOf`/`SetShare`,
      defaults `keptCarriers`/`maxDiggers`/`maxBuilders` 30/25/25%, sum ≤ 100%, the locked half of the raise buttons grey); special professions geologists, pioneers, scouts, pioneers→carriers (carrier portrait)
      with search buttons (all on the map, all in view, nearest in turn with a camera flight) and convert 5/1
      (`Geology.ConvertCarriers` with `among`: only carriers in view, nearest the view centre first – none in view: a hint; `Geology.PioneersToCarriers`); converted ones are selected immediately (the menu stays open, `_menuOverSelection`), those still delivering join after the conversion.
    - Settlers › Warriors (`SoldiersMenu`): combat strength, divine upgrade (mana progress, weapon button per type, adorned from level 2),
      automatic/manual recruitment with free carriers; manual: per type weapons in stock and planned recruits (⇈ +5, ↑ +1, ↓ −1, ⇊ −5;
      `Military.SetManualRecruitment`/`ChangePlanned`).
    - Settlers › Production (`StatisticsMenu`): chart of the last 60 min of a good (units/min, 2-min average, scale adapts);
      good selection `GoodsGrid` (shared with Stock: icons `goodsGrid` in `ui.json`, indices from JSettlers' EMaterialType, not producible dark);
      counted in `World.Statistics` (`ProductionStatistics`, 30-s sections).
  - Clicking a building replaces the build menu (without its own background): name, image (centred at the bottom, scaled down to the box – never cropped –
    `EconomyRenderer.DrawBuildingImage`; when space is tight, first the goods go, then the image gets lower), for own buildings the status, top right the demolish icon as in the original (file 03 GUI 199, `buildingInfo` in `ui.json`) with a confirmation as in the original (only the building image stays in its place, question at the bottom, Yes/No as light texts; any other action – clicking beside it when releasing, another selection, Esc – cancels);
    top left as in the original "Stop work" (house, GUI 395)/"Resume work" (hand, 194) for production and construction sites (`World.SetPaused`:
    work finished, then inside; construction site: no deliveries/workers, those present free after their cycle), next to it for construction sites "Highest priority"
    (gears 197, pressed with "!" 380; `World.SetPriority`), for finished own buildings with a work area the button for it in the horizontal middle
    (rings on a stone tile as in the original, GUI 203; clicking frames it, the next left-click moves the work area like a right-click, that one/Esc cancels; `BuildingInfo.WorkArea`),
    the name then under the icons (their meaning given by the shared tooltip box next to the cursor); military: garrison per type (present/wanted, +/−);
    storehouse: all goods (`GoodsGrid`; dot = general rule, clicking specialises – red frame –, again lifts it);
    marketplace (`BuildingInfo.Market`, "Trade – land", without image; harbor "Trade – sea": bar GUI 377 `seaRoute`, ship in front of ×1 sets the dock like
    the shipyard's hammer, route points onto the water, route from the berth): bar ×1 ×2 ×3 package (file 03 GUI 374, `market` in `ui.json`; not set
    dark): choose a button (white framed; stops only with a destination; clicking the chosen stop again = delete), then a left-click on the map sets
    it (flag as a preview at the mouse pointer, only where donkeys can get; right-click/Esc cancels), route like the barracks' gathering point; goods with amount underneath, ∞ (386)/0 (389)/arrows ±5/±1 for the selected good; goods/building material,
    mine success chance (goods block centred below the label in the free area, one good horizontally); work area (right-click moves it, `World.SetWorkCenter`, max. `MaxWorkCenterDistance` = 20, otherwise the next allowed
    field; gatherers take the target nearest the centre; barracks: gathering point anywhere, `Building.HasGatheringPoint`, path with flag).
    Shipyard (`BuildingInfo.Dockyard`, `dockyard` in `ui.json`): ferry and trade ship on the left (last 12 GUI images of the people's file; with a dock
    clickable → order, the button stays pressed (dark image), both locked until the launch; building material delivered/needed at the bottom),
    hammer on the right (file 03 GUI 392) → the next left-click into the water sets the dock (pale preview at the mouse pointer; right-click/Esc cancels;
    locked during construction).
    German names/states in `data/ui.json`; text via FontStashSharp with the system font.
- **Statistics** (`StatisticsScreen`; "Quit game", after a lost game Esc; the game stands still): blue marble (`statisticsBackground`, file 02 GUI 29),
  pages Overview (table, land share), Economy, Military, Goods (every good per minute), Society – charts `Charts` (lines with values
  under the mouse, shares, bars) from `GameHistory`/`ProductionStatistics`; ←/→/Tab page through; "Resume" (Esc; not after "Quit game"),
  "Main menu".
- **Player names** in-game (statistics, chat, defeat, trait hints): `PlayerNames.Of` → `start.playerPeople` ("Player 2 (Romans)"),
  never the AI character (only in the console at startup any more). In the `InfoFeed` (chat, defeat) the name stands in the player's colour
  (`Add(…, names)`; very dark colours lightened), the rest white. Notes wider than the box (at most 40 × font size) wrap at
  spaces (overly long words are split), continuation lines indented further (`Hang`, 1.25 × line height); the wrap cached per note.
- **Text buttons** (`TextButton`): stone buttons from the menus before the game (`textButton` in `ui.json`, file 02 GUI 0, 9-slice), e.g. demolish,
  pause, Yes/No, recruitment, statistics. Garrison ±: up/down spinners as in manufacture.
- **Commands to the world** (lockstep preparation): the client changes the simulation only via `GameCommand`s – `MapGame.Issue(command, then)`
  (result to `then` after applying: message, sound, selection; `TakeCommandResults` after every `Step`), menus/panel/AI chat via the delegate
  `IssueCommand` (`IssueReported`: result → message). Exceptions: game setup, capture/demo code, own AI (key I, single player only),
  statistics measuring point on opening and calendar events (the game logic reads neither).
- **Selection/commands**: a box or a click selects own figures, priority soldiers > war machines (never together) > priests > geologists > scouts > pioneers,
  carriers and other professions never (a box without such figures: the building nearest to its centre; buildings: white corner frame 3 px). Shift adds,
  Ctrl+click all own of this profession in view (soldiers: all), Alt+click this type in view (soldiers: type, rank irrelevant). Selected ones as in the original: two white corner brackets above the head, in between
  a round dot with a black border green→yellow→red by HP (`DrawSelectionMarker`).
  `SelectionPanel` per kind (type/level and people – converted ones show theirs) portrait (`Portraits`, `ui.json` `portraits`; other peoples `MapGame.PortraitsOf`) + count (too many: compressed, more columns or smaller), combat strength at the bottom – like the spell menu
  in the content area (`BuildMenu.Content`). Ctrl+1–9 stores a group (ships too; every figure/ship in at most one – it leaves its old one; portrait of the first member as in the `SelectionPanel` or ship button image, the same height, wider images (war machine, ship) wider, following ones move further up, top right, chosen ones framed yellow; clicking or 1–9 selects; if the selection is exactly one group, what Shift adds goes into the group too; twice within 0.5 s: camera flight to the centre; the dead, those entering buildings, converted ones drop out; in the save game as client value `groups`).
  Soldiers: right-click send there/attack, Ctrl+right-click forced without fighting, Shift+right-click waypoint (`Military.AddWaypoint`, forced with Ctrl),
  Alt+right-click patrol (`Military.Patrol`); Tab: only wounded soldiers remain of the selection (HP < `World.MaxHealth`; control groups unchanged). Ships (`_ships`, instead of settlers; click, Ctrl: all in view,
  a box without settlers in it): right-click sails there; panel: button image per kind, name, count, ferries "On board: n/7", trade ships "Cargo: n", "Unload ship" at the bottom (`BuildingInfo.DrawShips`/`UnloadAt`).
  Chosen units: right-click on an own ferry near land → board; drawn standing on deck (`ShipRenderer`: whole ship, passengers behind
  the mast, front part = sequence in front of it, sail, passengers in front of the mast). Geologists: right-click there, search on mountains.
  Priests: spell menu as in the original instead of a selection (`SpellMenu`: 8 spells per people, `menu` in `magic.json` – implemented ones and placeholders with
  a guessed name/cost, not castable; symbols `spellMenu` in `ui.json`; charge bar mana/cost: below 1 a gradient, then one stripe per charge,
  alternating light/dark, each 1.35× as wide as the previous one, whole bar = all mana (measured in the original); at the top the name/cost under the mouse or a target hint, at the bottom mana), choose a spell, then a target
  (target ring red: affects nobody); right-click there.
  Converting carriers only via Settlers › Professions. A left-click into empty space deselects (figures, buildings, menus) and clears the panel as in the original
  (`PanelTab.None`, no tab pressed); dragging with the right button moves the map.
- **Mouse** (`SdlMouse`, everywhere instead of `Mouse.GetState()`; `NextFrame()` first in `Update`): buttons from SDL events (event watch) instead of
  from MonoGame's per-frame query – a button counts as pressed in the frame if it is down or was pressed since the frame before (taps on the
  trackpad are not lost; on macOS the two-finger click arrives as the right button this way, issue #6). Position from `SDL_GetMouseState` (relative to the window; MonoGame computes globally minus
  window position, which is off by the window offset under XWayland/COSMIC). Without SDL: MonoGame's state.
  SDL library for own DllImports: `SdlLibrary` (in the platform folder too, like `osx-arm64/`).
- **Keys**: Enter chat (see info feed), Esc cancels placing/aiming, otherwise pause like P; no cheat keys (only `/cheats`, see info feed);
  F attack on the enemy military building under the mouse, fog of war (sight 1×/s, `FogLayer` fades softly,
  tapering off over `SoftFields` (3); unexplored black, explored dark, foreign figures only in sight; on/off only in the cheat menu), F11 full screen (for the session only; default in the settings),
  F2 scaling sharp/linear/Catmull-Rom (default)/Lanczos2 (world 1:1 into a small render target, enlarged by the zoom; markers/panel sharp;
  `Shaders/Upscale.fx` → embedded `.mgfx`, after changes `tools/compile-shaders.sh`, needs Wine),
  F3 performance display (FPS, ms simulation/update/draw, longest frame, draw calls; draws only what lies in the visible rows and
  horizontally in view – margin 96 px, building strips by pixel width up to 320 px, ships 160 px), F12 held 10× speed, T game time, I AI plays for you
  (other players always AI), L AI decisions to the console,
  P/Esc pause (banner like "Game won"; world stands, camera/panel/commands work), placing via the build menu (dots at suitable build spots, green→red by levelling effort – `World.LevellingEffort` / `levelling.maxWork`; max. 6 ms per frame,
  `World.BuildSpotEffort`; preview at the nearest build spot; Q/E buildings; ends after placing, right-click cancels).
- **Options**: `--scene map` (default) / `--scene walk` (walking PoC); `--load <slot>` (1–8; terrain/objects from the map, then field by field up to date);
  `--map <name>` (default: first multiplayer map; `random[:players[:none|mirror|repeat[:seed]]]`); `--peoples amazon,roman,…` (open peoples,
  player i: entry i mod n; otherwise random; map presets 0–3 apply); `--characters ,general` (AI character per player, entry i → player i+1, empty = drawn);
  `--player <n>` (played player of free matches, 1–8, default 1; all others AI).
- **Local player** (`MapGame._local`, from 0): this machine's player – commands, selection, fog of war (`Vision`), minimap (white), menus,
  building panel, control groups, events, victory/defeat (`Goals.OutcomeOf`), AI chat (`AiChat.Player`), start screen ("you", preferred color), capture scenarios.
  Missions always `MissionGoals.Player` (0); save games store it (client value `player`, older ones: 0). For multiplayer later from the server.
- **Sound** (`GameAudio`, not in captures): effects from `SND/Siedler3_00.dat` per `SoundCue` (numbers in `data/sound.json`, recognized in the original:
  sword 30, bow 33 – Egyptians 32 –, spear 34, death 35, Amazon 113, tree falls 36, building falls 93, casting 75, healing 21, pioneer 25/1, geologist from
  `minAmount` 24/1–3, farmer sowing 8; table `docs/formate/snd.md`; `SoundRef`: number or `{sound, variants}`), variant random, only in sight (fog), full in view, outside quieter by up to `hearing` half view widths,
  panned left/right, at most `maxVoices` simultaneously and `perFrame` per sound and frame. From the centre of the view: command to controllable units (walk/attack, search, steal, claim land, cast; `command` 57),
  event message (`events`: combat 109, otherwise 110), panel via `UiSounds` (`ui`: order/share/garrison/transport order up 47/down 48,
  switches like recruitment, pausing, storing, storehouse specialisation 54, work area moved 22, click on the demolish icon 51). `WorkSounds`: work outside per `Activity` (`work`: felling 0, levelling 2, building 1, stonecutter 3,
  fishing 16) in the rhythm of the work animation (moment `at` per round, `SettlerRenderer.WorkRoundFrames`); workshops (`workshops`: sawmill 5,
  toolsmith/weaponsmith 6/7, iron/gold smelter 37/38) about every `everySeconds`, as long as `Production.IsProducing`. `Ambience` (`ambience` in `sound.json`): terrain in view sampled every 0.1 s (every `sampleStep`-th field, `margin` fading out beyond the edge,
  unexplored silent), per kind loud by share (full from `fullShare`, gliding in `fadeSeconds`): sea 68, river 71, desert 67, wind 73/74
  (mountains, snow) as variants strung together without a pause (`overlap`), panned to the side of the terrain; bees 117/0 by beehives in view (`objects`, every field counted,
  full from `fullCount` 6), quiet and only one at a time (`overlap` 0; in the original one per hive, overdriven); birds 69/70 on grass individually every `everySeconds`
  from one field in view; keeps running during a pause, silent outside the game. `SwampBubbles` (presentation only, not saved): bubbles on green swamp (`Moor`, not brown
  mud `Mud`) in view (one per field every `bubbles.perFieldSeconds`, only in sight): file 1 sequence 106 (grows), then 107 (sinks) or 108 (bursts, bubbling 72 – a quiet recording, `gain` amplifies it on loading). `MusicPlayer`: `MUSIC/TrackNN.ogg` (NVorbis streamed)
  from `Tracks` (`SoundConfig.TracksFor`: list of our own people, `music.tracks`, assigned by ear; "Music per people" off: all, own first),
  a new list begins with its first piece, then shuffled, pause in between, a new list without the running piece fades out; silent in the menus before the game. Music/music per people/effects in the settings
  (Display, `DisplaySettings`).
- **War machines** (`WarMachineRenderer`, `warMachines` in `ui.json`, locations `docs/formate/gfx.md`): body per direction, rolling wheels
  above it, firing per direction, projectiles in an arc; the gong moves like a settler walks. Impact: small grey explosion (file 13 seq. 49);
  damaged buildings smoke (42, from half 43 dark) and burn (41 from ¼, 40 from ½, 39 from ¾ damage) until they are healed. Selection together with soldiers (`SelectionKind`, portrait
  `WAR_MACHINE`), right-click: go there or bombard a military building; ammunition count above the dot; "Load ammunition" in the selection panel.
  Works panel (`BuildingInfo.WarWorks`): machine/ammunition (click +1, Ctrl+click back, ×n ordered), loads, material of the
  current order. Sound `machineShot` (guessed), hit `machineHit` 88/0. Cheat menu: 2 war machines.
- **Rendering**: ships (`ShipRenderer`, `ships` in `ui.json`): file 36, 4 sequences per ship (deck, shadow, front part, entirely without sails)
  of 6 directions each, sails 28–34 with the player's colour; the small ship = ferry, the large one = trade ship (checked with Romans in-game), cargo as piles on deck; under construction the hull growing from below. Waves (`Terrain/WaveLayer`): a long straight swell runs diagonally (towards the bottom left), water corners rise by up to 4 height steps (water triangles in view redrawn, light like slopes), crests blended with a light water tile (image 16), the front side gleams, above it the original glitter (file 01 seq. 26) as overlapping tiles (distance ¾ of an image, every second row offset by half a step, not raised with the waves), image by the phase of the swell at the tile centre – plays while the front passes through, several one after another simultaneously (swell 18 fields long, 1.8 fields/s); strength by distance from the shore (1 field calm, then full over 14, soft); ships rock along (presentation only); stand still during a pause. Figures by people; the dead: ghost in the player's colour (file 12 seq. 27, `World.Ghosts`); spell effects as the original's status symbols above the head (`StatusIcons`, `statusIcons` in `ui.json`: file 1 sequences 128–133 – Samurai sword, Strengthen, Festering fear, Ch'ih-Yu's shield, Cursed arrows, Frozen –, several next to each other; 134/135 levelling uneven/very uneven, 136–138 info signs, still unused); those standing without a task change their facing every 2–30 s
  (presentation only); tower bowmen with a firing animation in rhythm, without a shadow (as in the original), infantry in the tower as a window image; buildings: layer 0, garrison, then further layers (window frames/balustrades in front); drawn in 8-px strips, each in the foremost blocker row of its column (`EconomyRenderer.Strip`, otherwise a tall building hides what stands in front of it sideways); arrows in a flat arc
  (`docs/formate/gfx.md`). `SettlerAnimations` resolves type/action/good/direction via `data/settlers.json` (generated with
  `tools/data-import/convert_jsettlers_movables.py`, do not edit by hand). Construction sites show a sign and posts.
