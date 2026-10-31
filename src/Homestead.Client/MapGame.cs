using Homestead.Assets;
using Homestead.Assets.Gfx;
using Homestead.Assets.Maps;
using Homestead.Client.MapObjects;
using Homestead.Client.Terrain;
using Homestead.Simulation;
using Homestead.Simulation.Ai;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Events;
using Homestead.Simulation.Magic;
using Homestead.Simulation.Map;
using Homestead.Simulation.Map.Generation;
using Homestead.Simulation.Pathfinding;
using Homestead.Simulation.Persistence;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Homestead.Client;

/// <summary>
/// An original map with terrain, objects and settlers that walk where they are sent.
/// </summary>
/// <remarks>
/// Mouse: left click on a settler selects it, left click elsewhere sends the selected settler there ·
/// right drag pan · wheel zoom.
/// Keys: arrows/WASD scroll · N new carrier at cursor · M 50 wandering settlers ·
/// G demo economy at cursor · P pause · placing (build menu): Q/E building type, click places a construction site ·
/// PgUp/PgDn map ·
/// O objects · B blocked fields · T game time · Esc quit
/// </remarks>
public sealed class MapGame : Game
{


    private readonly GraphicsDeviceManager _graphics;
    private Point _windowedSize = new(1280, 800);
    private readonly GfxDirectory _gfx;
    private readonly string[] _mapFiles;
    private readonly string? _captureDirectory;
    private readonly Random _random = new(1);

    /// <summary>Draws the peoples of players the map leaves open ("random"): a new draw every game.</summary>
    private readonly Random _peopleDraw = new();
    private IReadOnlyList<Civilization>? _peoples;
    /// <summary>Players with the hardcore head start (start screen).</summary>
    private IReadOnlySet<int> _hardcore = new HashSet<int>();
    /// <summary>Team per player of the free game (0: none).</summary>
    private int[] _teams = [];
    /// <summary>Start position (map player) per player, from the start screen; null: as the map says.</summary>
    private int[]? _positions;
    private readonly string _mapDirectory;

    /// <summary>Shown before the first map is loaded, until the game is started.</summary>
    private TitleScreen? _title;
    /// <summary>The settings screen, over the title screen while open.</summary>
    private SettingsScreen? _settingsScreen;
    /// <summary>The campaign's choice of people, over the title screen while open.</summary>
    private CampaignScreen? _campaignScreen;
    /// <summary>A video playing over the title screen (a campaign's intro or outro).</summary>
    private VideoPlayer? _video;
    /// <summary>What follows the video playing, after its end or a click.</summary>
    private Action? _afterVideo;
    /// <summary>A campaign mission's description, over the campaign screen (and the choice of missions) while open.</summary>
    private BriefingScreen? _briefing;
    /// <summary>The choice of missions of a people with progress, over the campaign screen while open.</summary>
    private MissionSelectScreen? _missionSelect;
    /// <summary>The list of saved games (title screen › "Spiel laden"), over the title screen.</summary>
    private ListScreen? _loadList;
    /// <summary>The campaign being played or chosen (<c>settlers</c>, <c>amazons</c>).</summary>
    private string _campaign = "settlers";
    /// <summary>The side chosen in the campaign (a people's id in the settlers' campaign, <c>amazons</c> or <c>peoples</c> in the Amazons').</summary>
    private string _campaignSide = "";
    /// <summary>The mission whose description is open (from 0).</summary>
    private int _briefingIndex;
    /// <summary>The campaign mission being played (saved with the game); null in free games.</summary>
    private MissionRef? _mission;
    /// <summary>The decided outcome already noted (progress saved, own selection dropped).</summary>
    private MissionOutcome _outcomeNoted;

    /// <summary>Notes at the bottom left of the game view.</summary>
    private readonly InfoFeed _info = new();
    private readonly ChatInput _chat = new();
    // The computer players answering in the chat; null without an API key.
    private readonly AiChat? _aiChat = AiChat.Create();
    // Lines of World.Chat already in the info feed.
    private int _chatShown;
    /// <summary>The computer players whose defeat is told already (or was, before the game was loaded).</summary>
    private readonly HashSet<int> _defeatNoted = new();
    /// <summary>Real seconds played since the game was saved, started or loaded (autosave, reminders).</summary>
    private double _sinceSave;
    /// <summary>The next reminder of an unsaved game, in minutes of <see cref="_sinceSave"/>.</summary>
    private int _nextSaveReminder = SaveReminderMinutes;
    private const int SaveReminderMinutes = 30;
    /// <summary>The original's intro video, played before the title screen (<see cref="DisplaySettings.IntroVideo"/>).</summary>
    private const string IntroVideo = "S3/ADD/001/01_00_00.DAT";
    private const int AutoSaveMinutes = 15;
    /// <summary>The slot autosave writes into: the last.</summary>
    private const int AutoSaveSlot = SaveSlots.Count - 1;

    /// <summary>The chosen side of the campaign.</summary>
    private CampaignSide Side => CampaignConfig.Load().Campaigns[_campaign][_campaignSide];
    private readonly GameInstallation _installation;
    // Sound effects and music; none in capture runs.
    private GameAudio? _audio;
    private readonly WorkSounds _workSounds = new();
    private readonly Ambience _ambience = new();
    private readonly SwampBubbles _bubbles = new();
    private readonly SoundConfig _soundConfig = SoundConfig.Load();
    /// <summary>Hardware cursor, full screen, language (<c>display</c> in settings.json).</summary>
    /// <summary>HOMESTEAD_THEME=normal|gold|classic|amazons overrides the settings' theme (capture runs).</summary>
    private DisplaySettings _display = Enum.TryParse<Theme>(Environment.GetEnvironmentVariable("HOMESTEAD_THEME"), ignoreCase: true, out var theme)
        ? DisplaySettings.Load() with { Theme = theme }
        : DisplaySettings.Load();
    /// <summary>The game's own pointer while the hardware cursor is off.</summary>
    private SoftwareCursor? _cursor;
    private StartScreen? _startScreen;
    /// <summary>The loading screen while a game is about to load (<see cref="ShowLoading"/>), and the loading.</summary>
    private LoadingScreen? _loading;
    private Action? _afterLoading;

    /// <summary>The statistics screen while it is shown (the game stands still meanwhile).</summary>
    private StatisticsScreen? _statistics;
    private readonly bool _showStartScreen;

    /// <summary>AI character per player from the command line (empty: drawn by the people's weights).</summary>
    private readonly IReadOnlyList<string> _characters;

    private readonly AiNames _aiNames = new();

    private AiPlayer NewAi(int player)
    {
        var ai = new AiPlayer(_world, _playerCatalogs[player], player,
            character: player < _characters.Count && _characters[player].Length > 0 ? _characters[player] : null, random: _peopleDraw);
        // What the character's traits do is shown as it happens, to watch them at work.
        ai.Noted += OnAiNote;
        return ai;
    }

    private void OnAiNote(AiPlayer a, string line)
    {
        if (a.TraitOfNote(line) is not { } trait)
            return;
        string who = $"{PlayerNames.Of(_world, a.Player)} · {_aiNames.Trait(trait.Id)}";
        Console.WriteLine($"{who}: {line}");
        _message = $"{who}: {line}";
    }
    private BuildingCatalog? _romans;
    private BuildingDefinition[] _placeable = [];
    private BuildMenu? _buildMenu;
    private Portraits? _portraits;

    /// <summary>Portraits of other peoples, made when needed (won-over soldiers in the selection).</summary>
    private readonly Dictionary<Civilization, Portraits> _peoplePortraits = new();

    /// <summary>The portraits of a people: the own ones, or those of another people (made once).</summary>
    private Portraits PortraitsOf(Civilization people)
    {
        if (people == Own.Civilization)
            return _portraits!;
        if (!_peoplePortraits.TryGetValue(people, out var portraits))
            _peoplePortraits[people] = portraits = new Portraits(GraphicsDevice, _gfx, people);
        return portraits;
    }

    private void DisposePeoplePortraits()
    {
        foreach (var portraits in _peoplePortraits.Values)
            portraits.Dispose();
        _peoplePortraits.Clear();
    }
    private PanelTabs _tabs = null!;
    private EventButtons _eventButtons = null!;

    /// <summary>The event the arrows point at after a jump there, until <see cref="_pointUntil"/> (real time).</summary>
    private GameEvent? _pointed;
    private double _pointUntil;

    /// <summary>HOMESTEAD_CAPTURE_EVENT=1: a capture run jumps to every new event, to show the arrows.</summary>
    private readonly bool _captureEvent = Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_EVENT") == "1";
    private SoldiersMenu? _soldiersMenu;
    private SettlersOverview? _settlersOverview;
    private ProfessionsMenu? _professionsMenu;
    private GoodsGrid? _goodsGrid;
    private StatisticsMenu? _statisticsMenu;
    private StockMenu? _stockMenu;
    private ManufactureMenu? _manufactureMenu;
    private DistributionMenu? _distributionMenu;
    private TransportMenu? _transportMenu;
    private bool _captureManualRecruitment;
    private readonly int? _loadSlot;
    private readonly SystemMenu _systemMenu = new();
    private Homestead.Simulation.Economy.Material? _captureGood;
    private SidePanel _sidePanel = null!;
    private Minimap? _minimap;
    private readonly SelectionPanel _selectionPanel = new();
    private readonly SpellMenu _spellMenu = new();

    /// <summary>A spell button was clicked: the next click on the map casts <see cref="_aimSpell"/>.</summary>
    private bool _aiming;
    private Spell? _aimSpell;
    private ControlGroups _controlGroups = new();
    private readonly CheatMenu _cheats = new();
    private readonly BuildingInfo _buildingInfo = new();
    private UiFont _font = null!;
    private int _placeIndex;
    private bool _placing;
    /// <summary>Placement mode: where the chosen building fits in view, with its levelling effort (0 flat, 1 steep).</summary>
    private readonly List<(GridPoint Spot, float Effort)> _buildSpots = new();
    /// <summary>Checked points for <see cref="_buildSpotsFor"/>: when, and the levelling effort if the building fits.</summary>
    private readonly Dictionary<GridPoint, (long Tick, float? Effort)> _buildSpotChecks = new();
    private BuildingDefinition? _buildSpotsFor;

    private int _mapIndex;
    /// <summary>Random map asked for on the command line (<c>--map random:…</c>).</summary>
    private readonly (RandomMapSettings Settings, int Seed)? _randomMap;
    private OriginalMap _map = null!;
    private TerrainMesh _terrain = null!;
    private WaveLayer _waves = null!;
    private MapObjectLayer _objects = null!;
    private GameMap _gameMap = null!;
    private Texture2D _atlas = null!;
    private BasicEffect _effect = null!;
    private SpriteBatch _spriteBatch = null!;
    private Texture2D _pixel = null!;
    private SpriteLibrary _sprites = null!;
    private StatusIcons _statusIcons = null!;
    private ShipRenderer _shipRenderer = null!;
    private SettlerRenderer _settlerRenderer = null!;
    private WarMachineRenderer _machineRenderer = null!;
    private EconomyRenderer _economyRenderer = null!;

    private World _world = null!;
    private Settler? _selected;
    private Building? _selectedBuilding;

    /// <summary>Start point of player 1 on the current map.</summary>
    private GridPoint _start;

    /// <summary>Soldiers selected with a selection box; a click then sends them (or attacks).</summary>
    private readonly List<Settler> _group = new();

    /// <summary>Selected own ships (instead of settlers; sent like soldiers, over the water only).</summary>
    private readonly List<Homestead.Simulation.Economy.Ship> _ships = new();

    /// <summary>Carriers converted in Siedler › Berufe that join the selection once they have delivered and converted.</summary>
    private readonly List<Settler> _joining = new();

    /// <summary>The group key pressed last and when: pressed again within <see cref="GroupDoublePressSeconds"/>, the camera flies to the group.</summary>
    private int _lastGroupKey = -1;
    private double _lastGroupAt;
    private const double GroupDoublePressSeconds = 0.5;

    /// <summary>
    /// The open menu stays although settlers are selected (those just converted in Siedler › Berufe, so that more
    /// can be converted); any other selection, deselecting or a tab ends it.
    /// </summary>
    private bool _menuOverSelection;

    /// <summary>Selects nobody (the menu shows again, see <see cref="_menuOverSelection"/>).</summary>
    private void ClearSelection()
    {
        _group.Clear();
        _ships.Clear();
        _menuOverSelection = false;
    }

    /// <summary>The side panel shows the selected settlers or ships (not the open menu).</summary>
    private bool ShowsSelection => (_group.Count > 0 || _ships.Count > 0) && !_menuOverSelection;

    /// <summary>Whether the building is its player's only finished military building (as counted by <see cref="ConquestGoals"/>).</summary>
    private bool IsLastMilitaryBuilding(Building building) =>
        IsMilitary(building) && !_world.Buildings.Any(b => b != building && b.Player == building.Player && IsMilitary(b));

    private static bool IsMilitary(Building b) => b.Exists && b.State == BuildingState.Finished && b.Definition.Occupiers.Count > 0;
    private Vector2? _dragStart;
    private Vector2? _rightStart;
    private const float DragThreshold = 6;
    private string _message = "";
    private IReadOnlyList<BuildingCatalog> _playerCatalogs = [];

    /// <summary>The AI playing for player 1 (key I), if on.</summary>
    private AiPlayer? _ownAi;
    private TimeSpan _accumulator;

    /// <summary>The game is paused (P): no ticks run, a banner says so.</summary>
    private bool _paused;
    private const int FastForward = 10;
    private static readonly TimeSpan MaxBacklog = TimeSpan.FromSeconds(0.5);
    private TimeSpan _time;

    /// <summary>Fog of war for player 1 (key V switches it off and on); capture runs only with HOMESTEAD_CAPTURE_FOG=1.</summary>
    /// <summary>The fog of war as chosen (V); see <see cref="FogOn"/>.</summary>
    private bool _fogChosen;

    /// <summary>The fog of war shown: as chosen, gone once the game is won (the whole map uncovered).</summary>
    private bool FogOn => _fogChosen && Outcome != MissionOutcome.Won;
    private Vision _vision = null!;
    private FogLayer? _fog;
    private BasicEffect _fogEffect = null!;
    private TimeSpan _nextVision;
    private static readonly TimeSpan VisionInterval = TimeSpan.FromSeconds(1);

    private bool _showBlocked;
    private bool _showObjects = true;
    private Vector2 _camera; // world position shown at the screen center

    /// <summary>Camera flight after a click on the minimap: from, to, start (real seconds); none while <see cref="_flightStart"/> is negative.</summary>
    private Vector2 _flightFrom, _flightTo;
    private double _flightStart = -1;

    /// <summary>Real seconds at the current update (for flights started outside the minimap code).</summary>
    private double _now;
    private const double FlightSeconds = 0.35;

    /// <summary>The left button went down on the minimap and is still held: moving the mouse drags the view frame.</summary>
    private bool _minimapDrag;
    private Vector2 _minimapPress;

    /// <summary>Where the right button went down on the minimap (a click there sends the selection).</summary>
    private Vector2? _minimapRightPress;
    private float _zoom = 1.25f;
    /// <summary>The wheel step the zoom is gliding to; null when it rests.</summary>
    private float? _zoomTarget;
    /// <summary>Time constant of the glide: about 95 % of the way after three of these.</summary>
    private const float ZoomGlideSeconds = 0.04f;

    /// <summary>
    /// Wheel steps, roughly evenly spaced by ratio (×1.2–1.33). Quarter values repeat the pixel pattern of the
    /// upscaled world every few pixels, which keeps the interpolation steady. Farthest out is 1: drawing cost
    /// grows with the square of the area shown.
    /// </summary>
    private static readonly float[] ZoomLevels = [1f, 1.25f, 1.5f, 2f, 2.5f, 3f, 4f];
    private KeyboardState _previousKeys;
    private MouseState _previousMouse;
    private int _capturedFrames;
    private readonly PerfStats _perf = new();
    private bool _showPerf;

    /// <summary>
    /// F2 cycles: except <see cref="Scaling.Sharp"/> (every sprite scaled with point sampling) the world is drawn 1:1
    /// into <see cref="_worldTarget"/> and that is scaled up by the zoom, soft like the original upscaled to the screen.
    /// </summary>
    private enum Scaling { Sharp, Linear, CatmullRom, Lanczos2 }

    private Scaling _scaling = Scaling.CatmullRom;
    private RenderTarget2D? _worldTarget;
    private Effect _upscale = null!;

    /// <summary>Real time until which the scaling mode chosen with F2 is shown.</summary>
    private TimeSpan _scalingShownUntil;
    private bool _showClock;

    /// <param name="peoples">Peoples for the players whose people the map leaves open (see <see cref="OriginalScenario.Load"/>).</param>
    /// <param name="characters">AI character per player (entry i for player i); empty or missing: drawn.</param>
    /// <param name="startScreen">Show the start screen (map, peoples, colors) first.</param>
    /// <param name="loadSlot">Load the saved game of this slot (0-based) instead of starting one.</param>
    public MapGame(GameInstallation installation, GfxDirectory gfx, string? mapName, string? captureDirectory,
        IReadOnlyList<Civilization>? peoples = null, IReadOnlyList<string>? characters = null, bool startScreen = false, int? loadSlot = null)
    {
        _showStartScreen = startScreen;
        _loadSlot = loadSlot;
        AddCheats();
        _installation = installation;
        _mapDirectory = installation.MapDirectory;
        _gfx = gfx;
        _peoples = peoples;
        _characters = characters ?? [];
        _captureDirectory = captureDirectory;
        _fogChosen = captureDirectory is null || Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_FOG") == "1";
        _mapFiles = installation.MapFiles.ToArray();
        _randomMap = mapName is null ? null : RandomMapSettings.ParseName(mapName, defaultSeed: Random.Shared.Next());
        // Default: the first multiplayer map (equal start equipment for every player).
        _mapIndex = mapName is null
            ? Math.Max(0, Array.FindIndex(_mapFiles, f => OriginalMap.Open(f) is { IsSinglePlayer: false, Buildings.Count: 0 }))
            : Math.Max(0, Array.FindIndex(_mapFiles, f => Path.GetFileNameWithoutExtension(f).Equals(mapName, StringComparison.OrdinalIgnoreCase)));

        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1280,
            PreferredBackBufferHeight = 800,
        };
        IsMouseVisible = _display.HardwareCursor;
        Window.AllowUserResizing = true;
        if (captureDirectory is not null)
        {
            // Offscreen runs must not wait for the screen: a covered window gets throttled to
            // about one frame per second by the window system.
            _graphics.SynchronizeWithVerticalRetrace = false;
            IsFixedTimeStep = false;
        }
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        Window.TextInput += (_, e) =>
        {
            if (!_chat.IsOpen)
                return;
            if (e.Key == Keys.Back)
                _chat.Back();
            else
                _chat.Type(e.Character);
        };
        using (var shader = typeof(MapGame).Assembly.GetManifestResourceStream("Upscale.mgfx")!)
        using (var bytes = new MemoryStream())
        {
            shader.CopyTo(bytes);
            _upscale = new Effect(GraphicsDevice, bytes.ToArray());
        }
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData([Color.White]);
        _cursor = new SoftwareCursor(GraphicsDevice);
        if (_display.FullScreen && _captureDirectory is null)
            SetFullScreen(true);
        if (_captureDirectory is null)
        {
            _audio = new GameAudio(_installation, _soundConfig);
            UiSounds.Audio = _audio;
            ApplyVolumes(_display);
        }
        _atlas = TerrainAtlas.Build(GraphicsDevice, _gfx.Open(0));
        _effect = new BasicEffect(GraphicsDevice)
        {
            TextureEnabled = true,
            Texture = _atlas,
            VertexColorEnabled = true,
            LightingEnabled = false,
        };
        _fogEffect = new BasicEffect(GraphicsDevice) { VertexColorEnabled = true, LightingEnabled = false };
        _sidePanel = new SidePanel(GraphicsDevice, _gfx);
        TextButton.Load(GraphicsDevice, _gfx);
        _tabs = new PanelTabs(GraphicsDevice, _gfx);
        _eventButtons = new EventButtons(GraphicsDevice, _gfx, remember: _captureDirectory is null);
        // HOMESTEAD_CAPTURE_TAB=goods|settlers[:<sub-menu id>[:manual|<good>]]: a capture run shows that main tab
        // (and sub-menu; manual recruitment, or the good chosen in Waren › Verteilung or Warentransport).
        if (_captureDirectory is not null && Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_TAB")?.Split(':') is { } captureTab
            && Enum.TryParse<PanelTab>(captureTab[0], ignoreCase: true, out var tab))
        {
            _tabs.Current = tab;
            if (captureTab.Length > 1 && tab == PanelTab.System)
                _systemMenu.Show(captureTab[1]);
            else if (captureTab.Length > 1)
                _tabs.ShowMenu(captureTab[1]);
            if (captureTab.Length > 2 && captureTab[2] == "manual")
                _captureManualRecruitment = true;
            else if (captureTab.Length > 2)
                _captureGood = Homestead.Simulation.Economy.Materials.Parse(captureTab[2].ToUpperInvariant());
        }
        // HOMESTEAD_CAPTURE_COLORS=2,4: palette entries of the players (here green and purple).
        if (_captureDirectory is not null && Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_COLORS") is { } captureColors)
            PlayerColors.Assign(captureColors.Split(',').Select(c => int.TryParse(c, out int color) ? color : (int?)null).ToArray(), _random);
        _sprites = new SpriteLibrary(GraphicsDevice, _gfx);
        _shipRenderer = new ShipRenderer(_sprites);
        _statusIcons = new StatusIcons(_sprites);
        _settlerRenderer = new SettlerRenderer(_sprites, SettlerAnimations.Load(),
            player => player < _playerCatalogs.Count ? _playerCatalogs[player].Civilization : Civilization.Roman);
        _economyRenderer = new EconomyRenderer(_sprites);
        _machineRenderer = new WarMachineRenderer(_sprites);
        SelectionPanel.UsesAmmo = machine => _world.WarMachines.KindOf(machine) is { Shots: > 0 };
        ControlGroups.PortraitSize = s => SettlerRenderer.TypeOf(s) is { } type ? PortraitsOf(_settlerRenderer.PeopleOf(s)).SizeOf(type) : Portraits.Size;
        ControlGroups.DrawShip = _buildingInfo.DrawShipIcon;
        _font = new UiFont();
        if (_showStartScreen)
        {
            // HOMESTEAD_CAPTURE_START=<category>: the start screen's picture shows this tab (3: random maps);
            // =intro: a picture of the game's intro video, video.png;
            // =title: the title screen's (=title:readme, =title:contribution, =title:licenses: with that popup open; =title:load: the list of saved games, load.png, =title:loadError: with a message about one that did not load);
            // =settings[:<tab>]: the settings screen's (tab: resources, economy, military, display);
            // =loading[:<campaign>]: the loading screen's, loading.png (settlers, missionCd, amazons; without: a free game's);
            // =campaign: the campaign's choice of people (=campaign:<people>: a picture of its intro video, video.png;
            // =campaign:<people>:briefing: its first mission's description, briefing.png; =campaign:<people>:choose: the people
            // chosen (with progress the choice of missions, missions.png; otherwise the intro); =campaign:<people>:won|lost: its first
            // mission started and decided at once, the game goes on (the banner); with :next appended the statistics are left –
            // the second one's or again the first one's description, briefing.png; a number at the end takes that mission instead
            // (won:next:8 – the outro, video.png); with HOMESTEAD_SAVE_DIR temporary, it notes the progress).
            string? captureStart = _captureDirectory is null ? null : Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_START");
            bool campaignCapture = captureStart?.StartsWith("campaign") == true || captureStart?.StartsWith("amazons") == true
                || captureStart?.StartsWith("missionCd") == true;
            if (captureStart?.StartsWith("loading") == true)
                _loading = new LoadingScreen(GraphicsDevice, _gfx, captureStart.Split(':') is [_, var campaign] ? campaign : null, _display.Theme);
            else if (captureStart is null or "intro" || captureStart.StartsWith("title") || captureStart.StartsWith("settings") || campaignCapture)
            {
                _title = new TitleScreen(GraphicsDevice, _gfx, _display.Theme);
                // The game's intro first (unless switched off); a click or its end leads to the title screen.
                if (captureStart is null && _display.IntroVideo || captureStart == "intro")
                    PlayVideo(IntroVideo, () => _title?.Resume());
                if (campaignCapture)
                {
                    var parts = captureStart!.Split(':');
                    _campaign = parts[0] is "amazons" or "missionCd" ? parts[0] : "settlers";
                    // HOMESTEAD_CAPTURE_HOVER=<side>: that side as if under the mouse.
                    _campaignScreen = new CampaignScreen(GraphicsDevice, _gfx, _campaign) { Hovered = Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_HOVER") };
                    if (parts.Length > 1)
                    {
                        _campaignSide = CampaignConfig.Load().Campaigns[_campaign].Keys.First(k => k.Equals(parts[1], StringComparison.OrdinalIgnoreCase));
                        if (parts is [_, _, "briefing"])
                            OpenBriefing(0);
                        else if (parts is [_, _, "choose"])
                            ChooseSide(_campaignSide);
                        else if (parts is [_, _, "won" or "lost", ..])
                        {
                            OpenBriefing(int.TryParse(parts[^1], out int number) ? number - 1 : 0);
                            StartMission();
                            _world.Goals!.Decide(parts[2] == "lost" ? MissionOutcome.Lost : MissionOutcome.Won, _world.Tick);
                            NoteOutcome();
                            if (parts.Contains("next"))
                                LeaveStatistics();
                        }
                        else
                            PlayIntro(_campaignSide);
                    }
                }
                if (captureStart?.StartsWith("settings") == true)
                {
                    _settingsScreen = new SettingsScreen(GraphicsDevice, _gfx);
                    if (captureStart.StartsWith("settings:"))
                        _settingsScreen.Show(captureStart["settings:".Length..]);
                }
                if (captureStart == "title:load")
                    OpenLoadList();
                else if (captureStart?.StartsWith("title:") == true)
                    _title.Show(captureStart["title:".Length..]);
            }
            else
            {
                _startScreen = new StartScreen(GraphicsDevice, _gfx, _mapFiles, _mapDirectory, _mapIndex, _peoples, _display.PreferredColor);
                if (int.TryParse(captureStart?.Split(':')[0], out int category))
                    _startScreen.ShowCategory(category);
                if (captureStart?.EndsWith(":color") == true)
                    _startScreen.OpenColorChoice(0);
                else if (captureStart?.EndsWith(":team") == true)
                    _startScreen.OpenTeamChoice(0);
            }
        }
        else if (_loadSlot is { } slot)
            LoadGame(SaveSlots.Load(slot));
        else if (_randomMap is { } random)
            LoadMap(RandomMapGenerator.Generate(random.Settings, random.Seed));
        else
            LoadMap(_mapIndex);
    }

    private void UpdateStartScreen()
    {
        var choice = _startScreen!.Update(GraphicsDevice.Viewport.Bounds, out bool quit);
        if (quit)
        {
            _startScreen.Dispose();
            _startScreen = null;
            _title = new TitleScreen(GraphicsDevice, _gfx, _display.Theme);
            return;
        }
        if (Keyboard.GetState().IsKeyDown(Keys.F11) && !_previousKeys.IsKeyDown(Keys.F11))
            ToggleFullScreen();
        _previousKeys = Keyboard.GetState();
        _previousMouse = Mouse.GetState();
        if (choice is null)
            return;
        _startScreen.Dispose();
        _startScreen = null;
        var players = choice.Players;
        PlayerColors.Assign(players.Select(p => p.Color).ToArray(), _peopleDraw);
        // Random peoples drawn per player.
        _peoples = Enumerable.Range(0, Math.Max(8, players.Count))
            .Select(player => (player < players.Count ? players[player].People : null)
                              ?? Enum.GetValues<Civilization>()[_peopleDraw.Next(Enum.GetValues<Civilization>().Length)])
            .ToArray();
        _hardcore = Enumerable.Range(0, players.Count).Where(p => players[p].Hardcore).ToHashSet();
        _teams = players.Select(p => p.Team).ToArray();
        _positions = choice.Positions is { } chosen ? StartScreen.ResolvePositions(chosen, _peopleDraw) : null;
        ShowLoading(null, () =>
        {
            if (choice.Random is { } random)
                LoadMap(RandomMapGenerator.Generate(random.Settings, random.Seed));
            else
                LoadMap(choice.MapIndex);
        });
    }

    /// <summary>
    /// Shows the loading screen of the campaign (null: a free game) and loads the game once it is on the screen, so the
    /// window does not hang on the menu meanwhile. Capture runs load at once.
    /// </summary>
    private void ShowLoading(string? campaign, Action load)
    {
        if (_captureDirectory is not null)
        {
            load();
            return;
        }
        _loading?.Dispose();
        _loading = new LoadingScreen(GraphicsDevice, _gfx, campaign, _display.Theme);
        _afterLoading = load;
    }

    /// <summary>Building catalog of the own player (player 1): the people the map gives or the chosen one.</summary>
    private BuildingCatalog Own => _playerCatalogs.Count > 0 ? _playerCatalogs[0] : _romans ??= BuildingCatalog.Load(Civilization.Roman);

    /// <summary>Build menu and placeable buildings of the own people.</summary>
    private void UsePeople(BuildingCatalog catalog, bool force = false)
    {
        if (!force && _buildMenu is not null && _placeable.FirstOrDefault()?.Civilization == catalog.Civilization)
            return;
        _buildMenu?.Dispose();
        _buildMenu = new BuildMenu(GraphicsDevice, _gfx, catalog);
        _portraits?.Dispose();
        _portraits = new Portraits(GraphicsDevice, _gfx, catalog.Civilization);
        DisposePeoplePortraits();
        _soldiersMenu?.Dispose();
        _soldiersMenu = new SoldiersMenu(GraphicsDevice, _gfx, catalog.Civilization);
        _settlersOverview?.Dispose();
        _settlersOverview = new SettlersOverview(GraphicsDevice, _gfx, catalog.Civilization);
        _professionsMenu ??= new ProfessionsMenu(GraphicsDevice, _gfx);
        _goodsGrid?.Dispose();
        _goodsGrid = new GoodsGrid(GraphicsDevice, _gfx, catalog, Homestead.Simulation.Economy.ProductionConfig.Load());
        _statisticsMenu = new StatisticsMenu(_goodsGrid);
        _stockMenu = new StockMenu(_goodsGrid);
        _manufactureMenu?.Dispose();
        _manufactureMenu = new ManufactureMenu(GraphicsDevice, _gfx, _goodsGrid);
        _buildingInfo.Spinner = _manufactureMenu.SpinnerImage;
        _buildingInfo.LoadIcons(GraphicsDevice, _gfx);
        _spellMenu.LoadIcons(GraphicsDevice, _gfx);
        _distributionMenu = new DistributionMenu(_goodsGrid, catalog, _buildingInfo.NameOf);
        _transportMenu?.Dispose();
        _transportMenu = new TransportMenu(GraphicsDevice, _gfx, _goodsGrid);
        _buildingInfo.StorehouseGoods = _goodsGrid;
        if (_captureGood is { } chosenGood)
            _transportMenu.Choose(chosenGood);
        if (_captureGood is { } good)
            _distributionMenu.Choose(good);
        _placeable = catalog.All.Where(b => b.ConstructionPiles.Count > 0)
            .OrderBy(b => b.Name).ToArray();
        _placeIndex = Math.Max(0, Array.FindIndex(_placeable, b => b.Name == "lumberjack"));
        _placing = false;
    }

    private void LoadMap(int index)
    {
        _mapIndex = (index % _mapFiles.Length + _mapFiles.Length) % _mapFiles.Length;
        LoadMap(OriginalMap.Open(_mapFiles[_mapIndex]));
    }

    private void LoadMap(OriginalMap map)
    {
        _mission = null;
        _outcomeNoted = MissionOutcome.Open;
        _terrain?.Dispose();
        _map = map;
        _terrain = new TerrainMesh(GraphicsDevice, _map.Area);
        _waves = new WaveLayer(_terrain);
        var production = Homestead.Simulation.Economy.ProductionConfig.Load();
        var magic = Homestead.Simulation.Magic.MagicConfig.Load();
        var settings = GameSettings.Load(production, magic);
        (_world, var report) = OriginalScenario.Load(_map, freeChoice: _peoples, random: _peopleDraw,
            hardcore: _hardcore.Where(p => p < _map.Players.Count),
            positions: _positions?.Length == _map.Players.Count ? _positions : null,
            production: settings.Apply(production), magic: settings.Apply(magic));
        _world.Teams.AddRange(_teams.Take(_map.Players.Count));
        // A free game is won by defeating everybody else (a mission brings its own goals).
        _world.Goals = new ConquestGoals();
        BeginSession();
        _gameMap = _world.Map!;
        _gameMap.PointChanged += OnPointChanged;
        _world.SoundCues = _audio is null ? null : new();
        _objects = new MapObjectLayer(_sprites, _map.Area, _terrain);
        _vision = new Vision(_gameMap.Size, player: 0);
        _vision.Update(_world);
        _fog?.Dispose();
        _fog = new FogLayer(GraphicsDevice, _terrain);
        _fog.SetTargets(_vision);
        _minimap?.Dispose();
        _minimap = new Minimap(GraphicsDevice, _map.Area);
        _minimap.Update(_vision, all: !FogOn, _gameMap, IsAlly);
        _nextVision = _time + VisionInterval;
        _message = $"{report.Buildings} buildings, {report.Piles} piles, {report.Settlers} settlers from the map";
        // Every other player is a computer player.
        _playerCatalogs = report.Catalogs;
        UsePeople(Own);
        _ownAi = null;
        for (int player = 1; player < report.Catalogs.Count; player++)
        {
            var ai = NewAi(player);
            _world.AiPlayers.Add(ai);
            Console.WriteLine($"Spieler {player + 1} ({report.Catalogs[player].Civilization}): KI {_aiNames.Character(ai.Character.Id)} " +
                              $"({string.Join(", ", ai.Character.Traits.Select(t => _aiNames.Trait(t.Id)))})");
        }
        // The camera starts at the own start position (drawn ones included).
        var home = _map.Players.Count > 0 ? _map.Players[_positions?.Length == _map.Players.Count ? _positions[0] : 0] : null;
        _start = home is not null
            ? new GridPoint(home.StartX, home.StartY)
            : new GridPoint(_map.Area.Size / 2, _map.Area.Size / 2);
        _selected = null;
        ClearSelection();
        _paused = false;
        _controlGroups = new ControlGroups();
        _selectedBuilding = null;
        _camera = ToWorld(_start);
    }

    /// <summary>
    /// A saved game: its world goes on where it was saved. The ground is drawn from its map and brought to
    /// the world's state point by point (levelled ground, fields, trails, objects).
    /// </summary>
    private void LoadGame(SaveGame game)
    {
        _terrain?.Dispose();
        _map = game.Map;
        _terrain = new TerrainMesh(GraphicsDevice, _map.Area);
        _waves = new WaveLayer(_terrain);
        _world = game.World;
        _gameMap = _world.Map!;
        _gameMap.PointChanged += OnPointChanged;
        _world.SoundCues = _audio is null ? null : new();
        _objects = new MapObjectLayer(_sprites, _map.Area, _terrain);
        for (int y = 0; y < _gameMap.Size; y++)
        for (int x = 0; x < _gameMap.Size; x++)
            OnPointChanged(new GridPoint(x, y));
        _vision = game.Vision ?? new Vision(_gameMap.Size, player: 0);
        _vision.Update(_world);
        _fog?.Dispose();
        _fog = new FogLayer(GraphicsDevice, _terrain);
        _fog.SetTargets(_vision);
        _minimap?.Dispose();
        _minimap = new Minimap(GraphicsDevice, _map.Area);
        _minimap.Update(_vision, all: !FogOn, _gameMap, IsAlly);
        _nextVision = _time + VisionInterval;
        PlayerColors.Restore(game.Client.GetValueOrDefault("colors"));
        _mission = MissionRef.Parse(game.Client.GetValueOrDefault("mission"));
        // Free games saved before they had goals get them now.
        if (_mission is null)
            _world.Goals ??= new ConquestGoals();
        _outcomeNoted = MissionOutcome.Open;
        BeginSession();
        _playerCatalogs = game.Catalogs;
        UsePeople(Own, force: true);
        // The computer players' notes go to the screen again; the own player's AI (I) goes on if it was on.
        foreach (var ai in _world.AiPlayers)
            ai.Noted += OnAiNote;
        _ownAi = _world.AiPlayers.FirstOrDefault(a => a.Player == 0);
        _selected = null;
        ClearSelection();
        _paused = false;
        _controlGroups = ControlGroups.Restore(game.Client.GetValueOrDefault("groups"), _world.Settlers, _world.Shipping.Ships);
        _selectedBuilding = null;
        _placing = _aiming = false;
        _tabs.Current = PanelTab.Build;
        _start = new GridPoint(_gameMap.Size / 2, _gameMap.Size / 2);
        _camera = game.Client.GetValueOrDefault("camera")?.Split(',') is [var cx, var cy]
                  && float.TryParse(cx, System.Globalization.CultureInfo.InvariantCulture, out float camX)
                  && float.TryParse(cy, System.Globalization.CultureInfo.InvariantCulture, out float camY)
            ? new Vector2(camX, camY)
            : ToWorld(_start);
        _message = $"game loaded ({_map.Name})";
    }

    /// <summary>A game started or loaded: no notes yet, defeats before it untold, the time since saving from 0.</summary>
    private void BeginSession()
    {
        _info.Clear();
        _chat.Close();
        _chatShown = _world.Chat.Messages.Count;
        _defeatNoted.Clear();
        foreach (var ai in _world.AiPlayers)
            if (_world.Goals?.IsDefeated(ai.Player) == true)
                _defeatNoted.Add(ai.Player);
        _sinceSave = 0;
        _nextSaveReminder = SaveReminderMinutes;
    }

    /// <summary>
    /// Once a frame in the game: computer players newly defeated are told in the info feed; with autosave on the game is
    /// saved every <see cref="AutoSaveMinutes"/>, otherwise reminded of every <see cref="SaveReminderMinutes"/> unsaved
    /// (not once lost, nor in capture runs).
    /// </summary>
    private void NoteNews(TimeSpan real)
    {
        for (; _chatShown < _world.Chat.Messages.Count; _chatShown++)
        {
            var said = _world.Chat.Messages[_chatShown];
            string name = PlayerNames.Of(_world, said.Player);
            _info.Add(Lang.T("chat.line", name, said.Text), _now, PlayerColors.Of(said.Player), (name, PlayerColors.Of(said.Player)));
        }
        if (_world.Goals is { } goals)
            foreach (var ai in _world.AiPlayers.OrderBy(a => a.Player))
                if (ai.Player != 0 && goals.IsDefeated(ai.Player) && _defeatNoted.Add(ai.Player))
                {
                    string name = PlayerNames.Of(_world, ai.Player);
                    _info.Add(Lang.T("info.defeated", name), _now, PlayerColors.Of(ai.Player), (name, PlayerColors.Of(ai.Player)));
                }
        if (_captureDirectory is not null || Outcome == MissionOutcome.Lost)
            return;
        _sinceSave += real.TotalSeconds;
        if (_display.AutoSave && _sinceSave >= AutoSaveMinutes * 60)
            SaveGameTo(AutoSaveSlot, auto: true);
        else if (_sinceSave >= _nextSaveReminder * 60)
        {
            _info.Add(Lang.T("info.notSaved", _nextSaveReminder), _now);
            _nextSaveReminder += SaveReminderMinutes;
        }
    }

    /// <summary>Saves the game into a slot (overwriting it); the list shows it right away, the info feed tells it.</summary>
    private void SaveGameTo(int slot, bool auto = false)
    {
        var info = new SaveInfo(_map.Name, Own.Civilization.ToString(), DateTime.Now, _world.Tick, _mission?.ToString());
        var game = new SaveGame
        {
            World = _world,
            Catalogs = _playerCatalogs,
            Map = _map,
            Vision = _vision,
            Client =
            {
                ["camera"] = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{_camera.X},{_camera.Y}"),
                ["colors"] = PlayerColors.Saved(),
                ["groups"] = _controlGroups.Saved(),
            },
        };
        if (_mission is not null)
            game.Client["mission"] = _mission.ToString();
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            SaveSlots.Save(slot, info, game);
            _message = $"saved into slot {slot + 1} ({watch.ElapsedMilliseconds} ms)";
            _info.Add(Lang.T(auto ? "info.autoSaved" : "info.saved", slot + 1), _now);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            _message = $"saving failed: {e.Message}";
            _info.Add(Lang.T("info.saveFailed", slot + 1), _now);
        }
        // Also after a failure: autosave tries again next time, not every frame.
        _sinceSave = 0;
        _nextSaveReminder = SaveReminderMinutes;
        _systemMenu.Refresh();
    }

    /// <summary>
    /// Shows the statistics screen: after „Spiel beenden“ (then only back to the start screen) or on Esc
    /// (then also back into the game).
    /// </summary>
    private void OpenStatistics(bool canResume)
    {
        _placing = _aiming = false;
        _statistics?.Dispose();
        // A decided mission leads on ("Weiter"): won to the next one, lost to the same one again; lost there is no way back.
        var outcome = Outcome;
        string name = outcome switch
        {
            MissionOutcome.Won => $"{Lang.T("campaign.won")} · {_map.Name}",
            MissionOutcome.Lost => $"{Lang.T("campaign.lost")} · {_map.Name}",
            _ => _map.Name,
        };
        _statistics = new StatisticsScreen(GraphicsDevice, _gfx, _world, name, _goodsGrid, canResume && outcome != MissionOutcome.Lost,
            outcome == MissionOutcome.Open || _mission is null ? null : Lang.T("campaign.next"));
    }

    /// <summary>The outcome of the campaign mission or the free game.</summary>
    private MissionOutcome Outcome => _world.Goals?.Outcome ?? MissionOutcome.Open;

    /// <summary>The statistics screen; a capture run saves every page as <c>stats_N.png</c> and quits.</summary>
    private void DrawStatistics()
    {
        var statistics = _statistics!;
        if (_captureDirectory is null)
        {
            GraphicsDevice.Clear(Color.Black);
            _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
            statistics.Draw(_spriteBatch, _pixel, _font, GraphicsDevice.Viewport.Bounds);
            _spriteBatch.End();
            return;
        }
        for (int page = 0; page < StatisticsScreen.Pages.Length; page++)
        {
            statistics.Page = page;
            using var target = NewCaptureTarget();
            GraphicsDevice.SetRenderTarget(target);
            GraphicsDevice.Clear(Color.Black);
            _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
            statistics.Draw(_spriteBatch, _pixel, _font, target.Bounds);
            _spriteBatch.End();
            GraphicsDevice.SetRenderTarget(null);
            using var stream = File.Create(Path.Combine(_captureDirectory, $"stats_{page}.png"));
            target.SaveAsPng(stream, target.Width, target.Height);
        }
        Exit();
    }

    /// <summary>Quits the game: back to the title screen.</summary>
    private void QuitToTitle()
    {
        _tabs.Current = PanelTab.Build;
        _ownAi = null;
        _title = new TitleScreen(GraphicsDevice, _gfx, _display.Theme);
    }

    /// <summary>
    /// Plays the intro video of the campaign's <paramref name="side"/>; its first mission's description follows
    /// (at once if the video cannot be played).
    /// </summary>
    private void PlayIntro(string side)
    {
        _campaignSide = side;
        PlayVideo(Side.Intro, () => OpenBriefing(0));
    }

    /// <summary>
    /// Plays a video of the installation (<paramref name="relative"/>); <paramref name="after"/> follows its end or a click
    /// (at once if it cannot be played or there is none).
    /// </summary>
    private void PlayVideo(string? relative, Action after)
    {
        _video?.Dispose();
        _video = null;
        if (relative is not null && _installation.FindPath(relative) is { } path)
            _video = VideoPlayer.TryOpen(GraphicsDevice, path, _display.EffectsVolume / 100f);
        else if (relative is not null)
            Console.WriteLine($"Video skipped: {relative} is not in the installation");
        if (_video is null)
            after();
        else
            _afterVideo = after;
    }

    /// <summary>
    /// The chosen side of the campaign: the intro and the first mission at the start, otherwise the choice of every
    /// accomplished mission and the next.
    /// </summary>
    private void ChooseSide(string side)
    {
        _campaignSide = side;
        if (CampaignProgress.Load().Accomplished(_campaign, side) == 0)
            PlayIntro(side);
        else
            OpenMissionSelect();
    }

    /// <summary>The list of saved games over the title screen: per slot the game's name, when it was saved and the people; empty ones faint.</summary>
    private void OpenLoadList()
    {
        _loadList?.Dispose();
        var rows = Enumerable.Range(0, SaveSlots.Count).Select(slot => SaveSlots.Info(slot) is { } info
            ? new ListRow($"{slot + 1}.  {Lang.SaveName(info)}",
                $"{info.SavedAt.ToString(Lang.T("formats.savedAt"), Lang.Culture)} · {Lang.T("peoples." + info.People)}")
            : new ListRow($"{slot + 1}.  ------------", "", Enabled: false)).ToArray();
        _loadList = new ListScreen(GraphicsDevice, _gfx, Lang.T("title.buttons.load"), rows);
    }

    /// <summary>Loads a saved game from the title screen (behind the loading screen); one that does not load is told there.</summary>
    private void LoadSlot(int slot)
    {
        SaveGame game;
        try
        {
            game = SaveSlots.Load(slot);
        }
        catch (Exception exception)
        {
            var failure = SaveLoadFailure.Of(exception);
            Console.Error.WriteLine($"Loading slot {slot + 1} failed ({failure.Code}): {exception}");
            _title!.ShowLoadFailure(failure);
            return;
        }
        _title!.Dispose();
        _title = null;
        ShowLoading(MissionRef.Parse(game.Client.GetValueOrDefault("mission"))?.Campaign, () => LoadGame(game));
    }

    /// <summary>Back from the list of saved games to the title screen.</summary>
    private void CloseLoadList()
    {
        _loadList?.Dispose();
        _loadList = null;
        _title?.Resume();
    }

    /// <summary>The choice of missions of the chosen side (as far as its progress goes), over the campaign screen.</summary>
    private void OpenMissionSelect()
    {
        _missionSelect?.Dispose();
        var texts = _gfx.Open(7).ReadTexts(Lang.TextIndex(Lang.Current), Lang.CodePage(Lang.Current));
        var names = Side.Missions.Select(m => m.Text < texts.Count ? MissionSelectScreen.NameOf(texts[m.Text]) : $"{m.Text}").ToArray();
        _missionSelect = new MissionSelectScreen(GraphicsDevice, _gfx, Lang.T(Side.Name), names,
            CampaignProgress.Load().Accomplished(_campaign, _campaignSide), Side.Intro is not null, Side.Outro is not null);
    }

    /// <summary>
    /// Once a frame: a newly decided mission. Won, it is noted in the campaign's progress (the global save game; again
    /// after loading a won game, which changes nothing); lost, the own player loses all control.
    /// </summary>
    private void NoteOutcome()
    {
        var outcome = Outcome;
        if (outcome == _outcomeNoted)
            return;
        _outcomeNoted = outcome;
        if (outcome == MissionOutcome.Won && _mission is { } mission)
        {
            var progress = CampaignProgress.Load();
            progress.Accomplish(mission);
            try
            {
                progress.Save();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"Campaign progress not saved: {e.Message}");
            }
        }
        if (outcome == MissionOutcome.Won)
            _minimap!.Update(_vision, all: !FogOn, _gameMap, IsAlly); // the fog lifts from the whole map
        if (outcome == MissionOutcome.Lost)
        {
            _placing = _aiming = false;
            _selected = null;
            ClearSelection();
            _selectedBuilding = null;
        }
    }

    /// <summary>
    /// The statistics' last button: to the title screen; after a decided mission to the campaign's choice of people with the
    /// next mission's description (won; none after the last) or the same one's again (lost) over it.
    /// </summary>
    private void LeaveStatistics()
    {
        var outcome = Outcome;
        var mission = _mission;
        _statistics?.Dispose();
        _statistics = null;
        QuitToTitle();
        _mission = null;
        _outcomeNoted = MissionOutcome.Open;
        if (outcome == MissionOutcome.Open || mission is null)
            return;
        _campaign = mission.Campaign;
        _campaignSide = mission.Side;
        _campaignScreen = new CampaignScreen(GraphicsDevice, _gfx, _campaign);
        OpenMissionSelect();
        int next = outcome == MissionOutcome.Won ? mission.Index + 1 : mission.Index;
        if (next < Side.Missions.Count)
            OpenBriefing(next);
        else
            // The last mission won: the side's outro, then the list with every mission.
            PlayVideo(Side.Outro, () => _missionSelect?.Resume());
    }

    /// <summary>Opens the description of mission <paramref name="mission"/> (from 0) of the chosen side of the campaign.</summary>
    private void OpenBriefing(int mission)
    {
        _briefingIndex = mission;
        try
        {
            _briefing = new BriefingScreen(GraphicsDevice, _gfx, _installation, Side.Missions[mission],
                Lang.Current, _display.EffectsVolume / 100f);
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            Console.WriteLine($"Mission {mission + 1} of {_campaign}/{_campaignSide} unavailable: {e.Message}");
            _campaignScreen?.Resume();
        }
    }

    /// <summary>Starts the mission whose description is open: its map with the peoples, buildings and settlers it gives.</summary>
    private void StartMission()
    {
        var map = OriginalMap.Open(_briefing!.MapPath);
        _briefing.Dispose();
        _briefing = null;
        _campaignScreen?.Dispose();
        _campaignScreen = null;
        _missionSelect?.Dispose();
        _missionSelect = null;
        _title?.Dispose();
        _title = null;
        PlayerColors.Assign(new int?[map.Players.Count], _peopleDraw);
        _peoples = null;
        _hardcore = new HashSet<int>();
        _teams = [];
        _positions = null;
        var mission = new MissionRef(_campaign, _campaignSide, _briefingIndex);
        ShowLoading(_campaign, () =>
        {
            LoadMap(map);
            _mission = mission;
            _world.Goals = new MissionGoals();
        });
    }

    private void UpdateTitle()
    {
        if (_briefing is not null)
        {
            switch (_briefing.Update(GraphicsDevice.Viewport.Bounds))
            {
                case BriefingAction.Back:
                    _briefing.Dispose();
                    _briefing = null;
                    if (_missionSelect is not null)
                        _missionSelect.Resume();
                    else
                        _campaignScreen?.Resume();
                    break;
                case BriefingAction.Start:
                    StartMission();
                    break;
            }
            _previousKeys = Keyboard.GetState();
            _previousMouse = Mouse.GetState();
            return;
        }
        if (_video is not null)
        {
            _video.Update();
            // A click (or its end) leads on: after the intro to the first mission's description, from the list back to it.
            if (_video.Finished && _captureDirectory is null)
            {
                _video.Dispose();
                _video = null;
                var after = _afterVideo;
                _afterVideo = null;
                after?.Invoke();
            }
            _previousKeys = Keyboard.GetState();
            _previousMouse = Mouse.GetState();
            return;
        }
        if (_missionSelect is not null)
        {
            if (_missionSelect.Update(GraphicsDevice.Viewport.Bounds))
            {
                _missionSelect.Dispose();
                _missionSelect = null;
                _campaignScreen?.Resume();
            }
            else if (_missionSelect.Chosen is { } mission)
                OpenBriefing(mission);
            else if (_missionSelect.ChosenVideo is { } video)
            {
                PlayVideo(video == MissionSelectScreen.Video.Intro ? Side.Intro : Side.Outro, () => _missionSelect?.Resume());
            }
            _previousKeys = Keyboard.GetState();
            _previousMouse = Mouse.GetState();
            return;
        }
        if (_loadList is not null)
        {
            if (_loadList.Update(GraphicsDevice.Viewport.Bounds))
                CloseLoadList();
            else if (_loadList.Chosen is { } slot)
            {
                CloseLoadList();
                LoadSlot(slot);
                return;
            }
            _previousKeys = Keyboard.GetState();
            _previousMouse = Mouse.GetState();
            return;
        }
        if (_campaignScreen is not null)
        {
            if (_campaignScreen.Update(GraphicsDevice.Viewport.Bounds))
            {
                _campaignScreen.Dispose();
                _campaignScreen = null;
                _title!.Resume();
            }
            else if (_campaignScreen.Chosen is { } side)
                ChooseSide(side);
            _previousKeys = Keyboard.GetState();
            _previousMouse = Mouse.GetState();
            return;
        }
        if (_settingsScreen is not null)
        {
            if (_settingsScreen.Update(GraphicsDevice.Viewport.Bounds))
            {
                if (_settingsScreen.Saved is { } display)
                {
                    bool newTheme = display.Theme != _display.Theme;
                    ApplyDisplay(display);
                    if (newTheme)
                    {
                        _title!.Dispose();
                        _title = new TitleScreen(GraphicsDevice, _gfx, _display.Theme);
                    }
                }
                _settingsScreen.Dispose();
                _settingsScreen = null;
                _title!.Resume();
            }
            _previousKeys = Keyboard.GetState();
            _previousMouse = Mouse.GetState();
            return;
        }
        var action = _title!.Update(GraphicsDevice.Viewport.Bounds);
        if (Keyboard.GetState().IsKeyDown(Keys.F11) && !_previousKeys.IsKeyDown(Keys.F11))
            ToggleFullScreen();
        _previousKeys = Keyboard.GetState();
        _previousMouse = Mouse.GetState();
        if (action == TitleAction.Quit)
            Exit();
        else if (action == TitleAction.Continue && SaveSlots.Latest() is { } latest)
            LoadSlot(latest);
        else if (action == TitleAction.Load)
            OpenLoadList();
        else if (action == TitleAction.Settings)
            _settingsScreen = new SettingsScreen(GraphicsDevice, _gfx);
        else if (action is TitleAction.Campaign or TitleAction.MissionCd or TitleAction.AmazonCampaign)
        {
            _campaign = action switch { TitleAction.Campaign => "settlers", TitleAction.MissionCd => "missionCd", _ => "amazons" };
            _campaignScreen = new CampaignScreen(GraphicsDevice, _gfx, _campaign);
        }
        else if (action == TitleAction.SinglePlayer)
        {
            _title.Dispose();
            _title = null;
            _startScreen = new StartScreen(GraphicsDevice, _gfx, _mapFiles, _mapDirectory, _mapIndex, _peoples, _display.PreferredColor);
        }
    }

    /// <summary>
    /// Diggers, woodcutters, stonecutters, foresters, farms and carriers change the map: keep terrain and objects
    /// in sync. Farmland shows as earth, trails as sand paths; the original's transition tiles to grass join neighbors.
    /// </summary>
    private void OnPointChanged(GridPoint p)
    {
        var kind = _gameMap.CoverAt(p) switch
        {
            GroundCover.Farmland => TerrainKind.Earth,
            GroundCover.Trail => TerrainKind.Sand,
            // As loaded, unless a spell changed the ground (desert, swamp, snow); water keeps its depth.
            _ => TerrainKinds.Of(_gameMap.TerrainAt(p)) ?? _terrain.OriginalKindAt(p.X, p.Y),
        };
        _terrain.Update(p.X, p.Y, _gameMap.HeightAt(p), kind);
        _objects?.SetCode(p.X, p.Y, _gameMap.ObjectCodeAt(p));
        _fog?.Touch(p.X, p.Y);
    }

    /// <summary>Work area marks by grid row, computed once per selected building (too slow for every frame).</summary>
    private ILookup<int, (GridPoint Point, int Ring, Color? Tint)> WorkAreaMarksByRow()
    {
        var key = (_selectedBuilding, _selectedBuilding?.Exists ?? false, _selectedBuilding?.WorkCenter, RouteKey());
        if (_marks is null || _marksKey != key)
        {
            _marks = WorkAreaMarks().ToLookup(m => m.Point.Y);
            _marksKey = key;
        }
        return _marks;
    }

    private ILookup<int, (GridPoint Point, int Ring, Color? Tint)>? _marks;
    private (Building?, bool, GridPoint?, string) _marksKey;

    /// <summary>The selected market's route, to notice when it changes.</summary>
    private string RouteKey() =>
        _selectedBuilding is { Exists: true } market && _world.Trade.IsTradingPost(market)
            ? string.Join(";", _world.Trade.Route(market)) + _world.Shipping.DockOf(market) : "";

    /// <summary>
    /// Work area of the selected building as in the original: posts on the borders of four circles at
    /// a quarter, half, three quarters and the full work radius around the work center.
    /// </summary>
    private IEnumerable<(GridPoint Point, int Ring, Color? Tint)> WorkAreaMarks()
    {
        // An own market: its route from the door over the stops to the destination, a flag at each; the legs colored
        // green, after the first stop yellow, after the second orange, after the third red. A harbor's from its dock.
        if (_selectedBuilding is { Exists: true, Player: 0 } market && _world.Trade.IsTradingPost(market))
        {
            var from = _world.Shipping.DockOf(market)?.Berth ?? market.Door;
            var route = _world.Trade.Route(market);
            for (int leg = 0; leg < route.Count; leg++)
            {
                foreach (var (point, ring, _) in GatheringPathMarks(from, route[leg]))
                    yield return (point, ring, RouteLegColors[Math.Min(leg, RouteLegColors.Length - 1)]);
                from = route[leg];
            }
            yield break;
        }
        if (_selectedBuilding is not { Exists: true } building || building.Definition.WorkRadius is not { } radius || radius <= 0)
            yield break;
        var center = building.WorkCenter;
        if (building.HasGatheringPoint)
        {
            foreach (var mark in GatheringPathMarks(building.Door, center))
                yield return mark;
            yield break;
        }
        for (int ring = 0; ring < 4; ring++)
        {
            int r = Math.Max(1, (int)Math.Round(radius * (ring + 1) / 4.0));
            // Border: inside the circle with a neighbor outside.
            foreach (var p in PathFinder.Circle(center, r))
                if (_gameMap.Contains(p) && Enumerable.Range(0, DirectionExtensions.Count)
                        .Any(d => !PathFinder.InCircle(center, p.Neighbor((Direction)d), r)))
                    yield return (p, ring, null);
        }
    }

    /// <summary>Colors of a market's route legs: to the first stop, after it, after the second, after the third.</summary>
    private static readonly Color[] RouteLegColors = [new(60, 220, 60), new(255, 230, 40), new(255, 140, 20), new(230, 30, 30)];

    /// <summary>Marks the <see cref="WorkAreaMarks"/> use for the flag at the end of a gathering path.</summary>
    private const int GatheringFlag = -1;

    /// <summary>
    /// A gathering point (barracks): an inner-ring post on every second field of the way there, the flag
    /// at its end. Where there is no way, the posts go straight.
    /// </summary>
    private IEnumerable<(GridPoint Point, int Ring, Color? Tint)> GatheringPathMarks(GridPoint from, GridPoint to)
    {
        var points = new List<GridPoint>();
        if (_world.PathFinder?.FindPath(from, to) is { } path)
        {
            var p = from;
            foreach (var step in path)
                points.Add(p = p.Neighbor(step));
        }
        else
        {
            for (var p = from; p != to && points.Count < 4 * _gameMap.Size;)
            {
                p = Enumerable.Range(0, DirectionExtensions.Count).Select(d => p.Neighbor((Direction)d))
                    .MinBy(n => PathFinder.Distance(n, to));
                points.Add(p);
            }
        }
        for (int i = 1; i < points.Count - 1; i += 2)
            yield return (points[i], 0, null);
        yield return (to, GatheringFlag, null);
    }

    /// <summary>Border posts of grid row <paramref name="y"/>: owned points next to land of another owner, none in the water (the shore is the border).</summary>
    private void DrawBorderRow(int y, RectangleF visible)
    {
        const float margin = 32;
        int firstX = Math.Max(0, (int)((visible.X - margin + y * ScreenProjection.TileWidth / 2f) / ScreenProjection.TileWidth));
        int lastX = Math.Min(_gameMap.Size - 1,
            (int)((visible.X + visible.Width + margin + y * ScreenProjection.TileWidth / 2f) / ScreenProjection.TileWidth));
        for (int x = firstX; x <= lastX; x++)
        {
            var p = new GridPoint(x, y);
            if (_gameMap.OwnerAt(p) is not { } owner || _gameMap.TerrainAt(p) == TerrainType.Water)
                continue;
            for (int d = 0; d < DirectionExtensions.Count; d++)
            {
                var n = p.Neighbor((Direction)d);
                if (_gameMap.Contains(n) && _gameMap.OwnerAt(n) != owner)
                {
                    _economyRenderer.DrawBorderPost(_spriteBatch, p, owner, _terrain);
                    break;
                }
            }
        }
    }

    private readonly List<MinimapDot> _minimapDots = [];

    /// <summary>
    /// Dots on the minimap (player 1's and its allies' where explored, everyone else's only where seen now): buildings and construction
    /// sites black, occupied military buildings on top in white for player 1, turquoise for its allies and red for everyone else; then
    /// units outside – soldiers, priests, geologists, pioneers and scouts (allies' like the own, enemies' only where player 1 sees now,
    /// scouts once revealed), colored alike, red drawn last so enemies stay visible in a crowd.
    /// </summary>
    private List<MinimapDot> MinimapDots()
    {
        _minimapDots.Clear();
        foreach (var occupied in new[] { false, true })
            foreach (var building in _world.Buildings)
                if (building.Exists && _world.Military.IsOccupied(building) == occupied
                    && (!FogOn || (_world.AreAllies(building.Player, 0) ? _vision.IsExplored(building.Position.Y * _gameMap.Size + building.Position.X)
                        : _vision.IsVisible(building.Position))))
                    _minimapDots.Add(new MinimapDot(building.Position, !occupied ? Color.Black : MinimapColor(building.Player), Building: true));
        // Own first, then allies, enemies last.
        foreach (int side in new[] { 0, 1, 2 })
            foreach (var settler in _world.Settlers)
                if ((settler.Player == 0 ? 0 : IsAlly(settler.Player) ? 1 : 2) == side && OnMinimap(settler))
                    _minimapDots.Add(new MinimapDot(settler.Position, MinimapColor(settler.Player)));
        return _minimapDots;
    }

    /// <summary>Whether the player is another one on player 1's team.</summary>
    private bool IsAlly(int player) => player != 0 && _world.AreAllies(0, player);

    private Color MinimapColor(int player) => player == 0 ? Color.White : IsAlly(player) ? Minimap.AllyColor : Color.Red;

    private bool OnMinimap(Settler settler) =>
        settler.Profession is Profession.Soldier or Profession.Priest or Profession.Geologist or Profession.Pioneer or Profession.Scout
            or Profession.WarMachine
        && !settler.IsDead && !settler.IsInside
        && (_world.AreAllies(settler.Player, 0) || (settler.IsRevealedTo(0) && (!FogOn || _vision.IsVisible(settler.Position))));

    private Vector2 ToWorld(GridPoint p) => ScreenProjection.ToWorld(p.X, p.Y, _terrain.HeightAt(p.X, p.Y));

    protected override void Update(GameTime gameTime)
    {
        _now = gameTime.TotalGameTime.TotalSeconds;
        if (_audio is not null)
        {
            // The own people's music in the game (statistics included), silence in the menus before it.
            _audio.Music.Tracks = _audio.Config.TracksFor(_title is null && _startScreen is null && _loading is not { Loaded: false } ? Own.Civilization : null, _display.PeopleMusic);
            _audio.Music.Update((float)gameTime.ElapsedGameTime.TotalSeconds);
        }
        if (_loading is { Gone: true })
        {
            _loading.Dispose();
            _loading = null;
        }
        if (_loading is { Fading: false })
        {
            // Loads once the loading screen is on the screen, then holds it; the input meanwhile must not act in the game.
            if (_loading.Shown && _afterLoading is { } load)
            {
                _afterLoading = null;
                load();
                _loading.MarkLoaded();
            }
            _previousKeys = Keyboard.GetState();
            _previousMouse = Mouse.GetState();
            base.Update(gameTime);
            return;
        }
        // The land falls silent outside the game.
        if (_title is not null || _startScreen is not null || _statistics is not null)
            _ambience.Silence();
        if (_title is not null)
        {
            UpdateTitle();
            base.Update(gameTime);
            return;
        }
        if (_startScreen is not null)
        {
            UpdateStartScreen();
            base.Update(gameTime);
            return;
        }
        if (_statistics is not null)
        {
            switch (_captureDirectory is null ? _statistics.Update(GraphicsDevice.Viewport.Bounds) : StatisticsAction.None)
            {
                case StatisticsAction.Resume:
                    _statistics.Dispose();
                    _statistics = null;
                    // The key or click that closed it must not act in the game too.
                    _previousKeys = Keyboard.GetState();
                    _previousMouse = Mouse.GetState();
                    break;
                case StatisticsAction.Quit:
                    LeaveStatistics();
                    break;
            }
            base.Update(gameTime);
            return;
        }
        var updateClock = System.Diagnostics.Stopwatch.StartNew();
        // Capture runs ignore input: keyboard and mouse state are global and would leak in from other windows.
        if (_captureDirectory is null)
            HandleInput(gameTime);

        // Capture runs use fixed frame times at 4x speed, so long processes fit into a short run.
        var elapsed = _captureDirectory is null ? gameTime.ElapsedGameTime : TimeSpan.FromSeconds(4 / 60.0);
        // Paused (P or Esc): the world stands still; the camera, the panel and orders still work.
        if (_paused)
            elapsed = TimeSpan.Zero;
        // F12 held: the game runs at FastForward times its speed.
        if (_captureDirectory is null && !_chat.IsOpen && Keyboard.GetState().IsKeyDown(Keys.F12))
            elapsed *= FastForward;
        // Never more than MaxBacklog behind: a slow machine would otherwise fall further back every frame.
        _accumulator = TimeSpan.FromTicks(Math.Min((_accumulator + elapsed).Ticks, MaxBacklog.Ticks));
        _time += elapsed;
        _perf.BeginSection();
        while (_accumulator >= World.TickDuration)
        {
            _world.Step();
            _accumulator -= World.TickDuration;
        }
        _perf.EndSimulation();
        NoteOutcome();
        NoteNews(gameTime.ElapsedGameTime);
        if (_aiChat is not null)
            foreach (string note in _aiChat.Apply(_world))
                _info.Add(note, _now);
        if (_audio is not null && _world.SoundCues is { } cues)
        {
            _audio.BeginFrame(Hearing);
            _audio.Play(cues);
            cues.Clear();
            _workSounds.Update(_world, _settlerRenderer, _audio, _time.TotalSeconds);
            _ambience.Update(_gameMap, ViewRect(), p => !FogOn || _vision.IsExplored(p.Y * _gameMap.Size + p.X), _audio,
                (float)gameTime.ElapsedGameTime.TotalSeconds);
        }
        _bubbles.Update(_gameMap, ViewRect(), p => !FogOn || _vision.IsVisible(p), _soundConfig.Ambience.Bubbles, _audio,
            _time.TotalSeconds, (float)elapsed.TotalSeconds);
        if (_eventButtons.Update(_world, 0, _now) is { } raised)
        {
            if (_audio is not null)
                _audio.Play(raised.Category == EventCategory.Combat ? _audio.Config.CombatEvent : _audio.Config.OtherEvent, _audio.Config.VolumeOf("events"));
            if (_captureDirectory is not null && _captureEvent)
                LookAt(raised);
        }

        // Fog of war: what player 1 sees is checked once a second, the fog then fades to it.
        if (_time >= _nextVision)
        {
            _vision.Update(_world);
            _fog!.SetTargets(_vision);
            _minimap!.Update(_vision, all: !FogOn, _gameMap, IsAlly);
            _nextVision = _time + VisionInterval;
        }
        _fog!.Advance((float)elapsed.TotalSeconds);

        var cursor = ScreenToGrid(Mouse.GetState().Position.ToVector2());
        var logistics = _world.Logistics;
        if (_placing)
        {
            var definition = _placeable[_placeIndex];
            string reason = "";
            if (BuildSpotNear(Mouse.GetState().Position.ToVector2()) is null)
                _world.CanPlace(definition, cursor, out reason);
            _message = $"placing {definition.Name} (Q/E change, Esc stop){(reason.Length > 0 ? ": " + reason : "")}";
        }
        if (_selectedBuilding is { } selectedBuilding && !_placing)
            _message = $"{selectedBuilding.Definition.Name}: {_world.StatusOf(selectedBuilding)}";
        var production = _world.Production;
        Window.Title = $"{_map.Name} · {_world.Settlers.Count} settlers · {logistics.Jobs.Count} transports, " +
                       $"produced {production.Produced}, trees {production.TreesCut}/{production.TreesPlanted}, stones {production.StonesCut}, " +
                       $"crop {production.CropHarvested}/{production.FieldsSown}, fish {production.FishCaught}, water {production.WaterDrawn}, ore {production.OreMined}, tools {production.ToolsMade} · " +
                       $"{logistics.Delivered} delivered, {logistics.DroppedOnTheWay} dropped · " +
                       $"residents {_world.Housing.Released} · mana {_world.Magic.Mana(0)} · combat strength {_world.Military.CombatStrength(0):P0} · pass-throughs {_world.PassThroughs} · cursor {cursor.X},{cursor.Y} " +
                       $"{_gameMap.TerrainAt(cursor)}{(_gameMap.IsWalkable(cursor) ? "" : " (blocked)")}" +
                       (_ownAi is { Log.Count: > 0 } ? $" · AI: {_ownAi.Log[^1]}" : "") +
                       (_message.Length > 0 ? $" · {_message}" : "");
        base.Update(gameTime);
        _perf.AddUpdate(updateClock.Elapsed.TotalMilliseconds);
    }

    /// <summary>Switches between the window and borderless fullscreen at desktop resolution; the window keeps its size.</summary>
    private void ToggleFullScreen() => SetFullScreen(!_graphics.IsFullScreen);

    /// <summary>The settings of the settings screen: pointer and full screen at once (the language is only stored so far).</summary>
    private void ApplyDisplay(DisplaySettings display)
    {
        _display = display;
        Lang.Use(display.Language);
        IsMouseVisible = display.HardwareCursor;
        ApplyVolumes(display);
        if (_captureDirectory is null && display.FullScreen != _graphics.IsFullScreen)
            SetFullScreen(display.FullScreen);
    }

    /// <summary>The soldiers' answer to an order.</summary>
    private void PlayCommand() => _audio?.Play(_audio.Config.Command, _audio.Config.VolumeOf("command"));

    private void ApplyVolumes(DisplaySettings display)
    {
        if (_audio is null)
            return;
        _audio.EffectsVolume = display.EffectsVolume / 100f;
        _audio.Music.Volume = display.MusicVolume / 100f;
    }

    /// <summary>The view in world pixels.</summary>
    private RectangleF ViewRect()
    {
        var viewport = GraphicsDevice.Viewport;
        return new RectangleF(_camera.X - viewport.Width / 2f / _zoom, _camera.Y - viewport.Height / 2f / _zoom,
            viewport.Width / _zoom, viewport.Height / _zoom);
    }

    /// <summary>
    /// Where a sound at the point is heard: its distance from the view's center in half view sizes (−1…1 on screen);
    /// null under the fog of war.
    /// </summary>
    private (float X, float Y)? Hearing(GridPoint p)
    {
        if (!_gameMap.Contains(p) || (FogOn && !_vision.IsVisible(p)))
            return null;
        var viewport = GraphicsDevice.Viewport;
        var at = ToWorld(p) - _camera;
        return (at.X / (viewport.Width / 2f / _zoom), at.Y / (viewport.Height / 2f / _zoom));
    }

    private void SetFullScreen(bool on)
    {
        if (on == _graphics.IsFullScreen)
            return;
        if (!on)
        {
            _graphics.IsFullScreen = false;
            _graphics.PreferredBackBufferWidth = _windowedSize.X;
            _graphics.PreferredBackBufferHeight = _windowedSize.Y;
        }
        else
        {
            _windowedSize = new Point(Window.ClientBounds.Width, Window.ClientBounds.Height);
            var display = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode;
            _graphics.HardwareModeSwitch = false;
            _graphics.IsFullScreen = true;
            _graphics.PreferredBackBufferWidth = display.Width;
            _graphics.PreferredBackBufferHeight = display.Height;
        }
        _graphics.ApplyChanges();
    }

    private void HandleInput(GameTime gameTime)
    {
        var typed = Keyboard.GetState();
        // While the chat line is open the keys type into it, the game sees none (the mouse still works).
        var keys = HandleChat(typed) ? new KeyboardState() : typed;
        var mouse = Mouse.GetState();
        bool Pressed(Keys k) => keys.IsKeyDown(k) && !_previousKeys.IsKeyDown(k);
        var cursorScreen = mouse.Position.ToVector2();

        // A lost game leaves no command: the camera, the minimap, the events and the computer menu (save, quit) still work;
        // Esc leaves (→ statistics).
        bool lost = Outcome == MissionOutcome.Lost;
        if (lost)
        {
            if (Pressed(Keys.Escape))
            {
                OpenStatistics(canResume: false);
                _previousKeys = typed;
                _previousMouse = mouse;
                return;
            }
            if (Pressed(Keys.F11))
                ToggleFullScreen();
            if (Pressed(Keys.F3))
                _showPerf = !_showPerf;
            if (Pressed(Keys.T))
                _showClock = !_showClock;
        }
        else
            HandleKeys(keys, cursorScreen, gameTime);
        // Space: to the newest event not looked at yet; once all are, nothing. Held, it goes on faster and
        // faster: from SpaceFirstRepeat between events down to SpaceFastRepeat after SpaceRampSeconds.
        if (Pressed(Keys.Space))
        {
            _spaceDownAt = _now;
            _nextSpace = _now + SpaceFirstRepeat;
            if (_eventButtons.Look(_world, 0, null) is { } unseen)
                LookAt(unseen);
        }
        else if (keys.IsKeyDown(Keys.Space) && _now >= _nextSpace)
        {
            float ramp = (float)Math.Min(1, (_now - _spaceDownAt) / SpaceRampSeconds);
            _nextSpace = _now + MathHelper.Lerp(SpaceFirstRepeat, SpaceFastRepeat, ramp);
            if (_eventButtons.Look(_world, 0, null) is { } unseen)
                LookAt(unseen);
        }

        // The menu at the left edge: the build menu (a click chooses a category or the building to place).
        bool onMenu = SidePanel.Contains(cursorScreen, GraphicsDevice.Viewport.Height);
        // The menus in the side panel work in panel space.
        var panelCursor = SidePanel.ToPanel(cursorScreen, GraphicsDevice.Viewport.Height);
        // A click on the minimap (also on its part over the game view) flies the camera there; dragging
        // with the button held moves the view frame along (also beyond the minimap's edge).
        if (SidePanel.MinimapAt(cursorScreen, GraphicsDevice.Viewport.Height, _gameMap.Size) is { } onMinimap)
        {
            onMenu = true;
            if (mouse.LeftButton == ButtonState.Pressed && _previousMouse.LeftButton == ButtonState.Released && IsActive)
            {
                _flightFrom = _camera;
                _flightTo = ToWorld(onMinimap);
                _flightStart = gameTime.TotalGameTime.TotalSeconds;
                _minimapDrag = true;
                _minimapPress = cursorScreen;
            }
            // A right click on the minimap gives the selected settlers their order there, as on the map.
            if (mouse.RightButton == ButtonState.Pressed && _previousMouse.RightButton == ButtonState.Released && IsActive)
                _minimapRightPress = cursorScreen;
            if (mouse.RightButton == ButtonState.Released && _previousMouse.RightButton == ButtonState.Pressed
                && _minimapRightPress is { } press && Vector2.Distance(press, cursorScreen) < DragThreshold && _group.Count > 0 && !lost)
                Command(onMinimap);
        }
        if (mouse.RightButton == ButtonState.Released)
            _minimapRightPress = null;
        if (_minimapDrag && mouse.LeftButton == ButtonState.Released)
            _minimapDrag = false;
        if (_minimapDrag)
        {
            onMenu = true;
            if (Vector2.Distance(_minimapPress, cursorScreen) >= DragThreshold
                && SidePanel.MinimapAt(cursorScreen, GraphicsDevice.Viewport.Height, _gameMap.Size, clamp: true) is { } dragged)
            {
                _flightStart = -1;
                _camera = ToWorld(dragged);
                _minimapPress = new Vector2(float.NegativeInfinity); // from now on every move counts
            }
        }
        bool released = mouse.LeftButton == ButtonState.Released && _previousMouse.LeftButton == ButtonState.Pressed && _dragStart is null;
        // The tear-down question gives way to anything else: a click outside the panel (on release, so the building's
        // overview does not flash up in between), another selection.
        if (_buildingInfo.Asking is { } asking
            && (asking != _selectedBuilding || !ShowsBuildingInfo || ShowsSelection
                || !onMenu && mouse.RightButton == ButtonState.Released && _previousMouse.RightButton == ButtonState.Pressed))
            _buildingInfo.Cancel();
        // The group icons at the top right: a click selects the group.
        _controlGroups.Update();
        float panelScale = SidePanel.Scale(GraphicsDevice.Viewport.Height);
        int? groupIcon = _controlGroups.GroupAt(cursorScreen, GraphicsDevice.Viewport.Width, panelScale);
        _cheats.Top = ControlGroups.Bottom(panelScale) + 8;
        if (groupIcon is { } clickedGroup && released && !lost)
            SelectGroup(clickedGroup);
        onMenu |= groupIcon is not null;
        // The cheat menu below them (/cheats).
        if (released && !lost)
            _cheats.Click(cursorScreen, GraphicsDevice.Viewport.Width);
        onMenu |= _cheats.Contains(cursorScreen, GraphicsDevice.Viewport.Width);
        if (!PriestsSelected)
            _aiming = false;
        // The main tabs (build, goods, settlers): a click shows that menu instead of the selection.
        if (_placing)
            _tabs.Current = PanelTab.Build;
        // The event buttons right of it: a click jumps to that kind's latest event.
        if (onMenu && _eventButtons.At(panelCursor) is { } eventCategory)
        {
            _message = _eventButtons.NameAt(panelCursor)!;
            if (released)
                LookAt(_eventButtons.Look(_world, 0, eventCategory));
        }
        // The computer button at the bottom left: save, quit.
        if (onMenu && released && SystemMenu.Button.Contains(panelCursor))
        {
            _tabs.Current = PanelTab.System;
            _systemMenu.Open();
            ClearSelection();
            _selected = null;
            _selectedBuilding = null;
            _placing = _aiming = false;
        }
        bool onTab = onMenu && _tabs.TabAt(panelCursor) is not null && !lost;
        if (onMenu && _tabs.NameAt(panelCursor) is { } tabName)
            _message = tabName;
        if (onTab && released)
        {
            _tabs.Current = _tabs.TabAt(panelCursor)!.Value;
            ClearSelection();
            _selected = null;
            _selectedBuilding = null;
            _placing = _aiming = false;
        }
        else if (onMenu && ShowsSelection && _ships.Count > 0 && BuildingInfo.UnloadAt(panelCursor, _ships))
        {
            _message = "unload: everybody on board goes ashore (the ship must lie near land)";
            if (released)
            {
                int ashore = _ships.Sum(ship => _world.Shipping.Unload(ship));
                if (ashore > 0)
                    PlayCommand();
                _message = ashore > 0 ? $"{ashore} units went ashore" : "no free land near the ship";
            }
        }
        else if (PriestsSelected)
        {
            var menu = _world.Magic.MenuOf(0);
            if (onMenu && released && _spellMenu.SpellAt(panelCursor, menu.Count) is { } index && menu[index].Spell is null)
                _message = "spell not implemented yet";
            else if (onMenu && released && _spellMenu.SpellAt(panelCursor, menu.Count) is { } chosen)
            {
                var spell = menu[chosen].Spell!;
                bool again = _aiming && _aimSpell == spell;
                _aiming = !again && _world.Magic.Mana(0) >= menu[chosen].Cost;
                _aimSpell = spell;
                _message = again ? "" : !_aiming ? "not enough mana"
                    : "click where the spell should work";
                // A spell on the whole map needs no target: cast at once.
                if (_aiming && !spell.Targeted)
                {
                    _aiming = false;
                    CastSpell(_group[0].Position);
                }
            }
        }
        else if (onMenu && ShowsSelection && SelectionPanel.ActionAt(_group, panelCursor) is { } action)
        {
            if (action == SelectionAction.Steal)
            {
                _message = "scouts: steal the nearest goods around them";
                if (released)
                {
                    int stealing = _world.Theft.Steal(_group);
                    if (stealing > 0)
                        PlayCommand();
                    _message = stealing > 0 ? $"{stealing} scouts go stealing" : "no goods to steal nearby";
                }
            }
            else if (action == SelectionAction.Reload)
            {
                _message = "war machines: drive to the nearest own works with ammunition and load";
                if (released)
                {
                    int loading = _world.WarMachines.Reload(_group);
                    if (loading > 0)
                        PlayCommand();
                    _message = loading > 0 ? $"{loading} war machines go to load" : "no works with ammunition";
                }
            }
            else if (action == SelectionAction.Hold)
            {
                _message = "soldiers: hold the position, fight only enemies within reach";
                if (released)
                {
                    int holding = _world.Military.Hold(_group);
                    if (holding > 0)
                        PlayCommand();
                    _message = $"{holding} soldiers hold their position";
                }
            }
            else
            {
                _message = "pioneers: take the free land around them, field by field";
                if (released)
                {
                    int claiming = _world.Pioneering.Claim(_group);
                    if (claiming > 0)
                        PlayCommand();
                    _message = claiming > 0 ? $"{claiming} pioneers take land" : "no free land nearby";
                }
            }
        }
        else if (ShowsBuildingInfo)
        {
            // A military building's garrison: one soldier of a type more or fewer (never empty, never overfull).
            if (onMenu && _buildingInfo.GarrisonChangeAt(panelCursor, SidePanel.ContentHeight, _selectedBuilding!, 0) is var (type, delta))
            {
                string name = _buildingInfo.SoldierName(type);
                bool possible = _world.Military.CanChangeWanted(_selectedBuilding!, type, delta);
                _message = possible ? $"{name}: {(delta > 0 ? "one more" : "one fewer")}"
                    : delta > 0 ? $"{name}: no free place"
                    : _world.Military.Wanted(_selectedBuilding!, type) == 0 ? $"{name}: none wanted" : $"{name}: the building must not be left empty";
                if (released && possible)
                {
                    _world.Military.ChangeWanted(_selectedBuilding!, type, delta);
                    UiSounds.Step(delta > 0);
                }
            }
            // A production building's or construction site's stop/restart icon, a site's highest priority.
            if (onMenu && released && _buildingInfo.PauseAt(panelCursor, SidePanel.ContentHeight, _world, _selectedBuilding!, 0))
            {
                _world.SetPaused(_selectedBuilding!, !_selectedBuilding!.IsPaused);
                UiSounds.Toggle();
                _message = $"{_buildingInfo.NameOf(_selectedBuilding.Definition)} {(_selectedBuilding.IsPaused ? "stopped" : "restarted")}";
            }
            if (onMenu && released && _buildingInfo.PriorityAt(panelCursor, SidePanel.ContentHeight, _selectedBuilding!, 0))
            {
                _world.SetPriority(_selectedBuilding!, !_selectedBuilding!.HasPriority);
                UiSounds.Toggle();
                _message = $"{_buildingInfo.NameOf(_selectedBuilding.Definition)}: {(_selectedBuilding.HasPriority ? "highest priority" : "normal priority")}";
            }
            // A market's route and goods.
            if (onMenu && _buildingInfo.MarketHover(panelCursor, _world, _selectedBuilding!, 0) is { } marketHover)
                _message = marketHover;
            if (onMenu && released && _buildingInfo.MarketClick(panelCursor, _world, _selectedBuilding!, 0) is { } traded)
                _message = traded;
            // A shipyard's dock and ships.
            if (onMenu && _buildingInfo.DockyardHover(panelCursor, _world, _selectedBuilding!, 0) is { } dockHover)
                _message = dockHover;
            if (onMenu && released && _buildingInfo.DockyardClick(panelCursor, _world, _selectedBuilding!, 0) is { } docked)
                _message = docked;
            // A war machine works: machines and ammunition ordered (Ctrl: one taken back).
            if (onMenu && _buildingInfo.WarWorksHover(panelCursor, _world, _selectedBuilding!, 0) is { } worksHover)
                _message = worksHover;
            if (onMenu && released && _buildingInfo.WarWorksClick(panelCursor, _world, _selectedBuilding!, 0, ControlDown(Keyboard.GetState())) is { } ordered)
                _message = ordered;
            // A storehouse's goods: specialize it on one, or back to the general rule.
            if (onMenu && _buildingInfo.StorehouseHover(panelCursor, _world, _selectedBuilding!, 0) is { } storeHover)
                _message = storeHover;
            if (onMenu && released && _buildingInfo.StorehouseClick(panelCursor, _world, _selectedBuilding!, 0) is { } stored)
                _message = stored;
            // The info panel's tear-down icon (the crack, then it asks).
            if (onMenu && _buildingInfo.DemolishAt(panelCursor, SidePanel.ContentHeight, _selectedBuilding!, 0))
                _message = "tear down (asks first)";
            bool asked = _buildingInfo.Asking == _selectedBuilding;
            bool demolish = onMenu && released && _buildingInfo.Click(panelCursor, SidePanel.ContentHeight, _selectedBuilding!, 0);
            // Asking about the own last military building: losing it loses the game (ConquestGoals).
            if (!asked && _buildingInfo.Asking == _selectedBuilding && IsLastMilitaryBuilding(_selectedBuilding!))
                _info.Add(Lang.T("info.lastTower"), _now, PlayerColors.Of(_selectedBuilding!.Player));
            if (demolish)
            {
                _message = $"{_buildingInfo.NameOf(_selectedBuilding!.Definition)} torn down";
                _world.RemoveBuilding(_selectedBuilding);
                _selectedBuilding = null;
            }
        }
        else if (!ShowsSelection && _tabs.Current == PanelTab.System)
        {
            if (onMenu && _systemMenu.HoverText(panelCursor) is { } hover)
                _message = hover;
            switch (onMenu && released ? _systemMenu.Click(panelCursor) : SystemAction.None)
            {
                case SystemAction.Save:
                    SaveGameTo(_systemMenu.ChosenSlot);
                    break;
                case SystemAction.Quit:
                    if (_display.AutoSave && Outcome != MissionOutcome.Lost && _captureDirectory is null)
                        SaveGameTo(AutoSaveSlot, auto: true);
                    OpenStatistics(canResume: false);
                    return;
            }
        }
        else if (lost)
        {
            // Lost: the other menus only show.
        }
        else if (!ShowsSelection && _tabs.Current != PanelTab.Build)
        {
            if (onMenu && released && _tabs.MenuAt(panelCursor) is { } menu)
                _tabs.ShowMenu(menu);
            else if (_tabs.CurrentMenu == "soldiers" && onMenu)
            {
                if (_soldiersMenu!.Hover(panelCursor, _world, 0, _buildingInfo) is { } hover)
                    _message = hover;
                if (released && _soldiersMenu.Click(panelCursor, _world, 0, _buildingInfo) is { } done)
                    _message = done;
            }
            else if (_tabs.Current == PanelTab.Goods && _tabs.CurrentMenu == "tools")
            {
                if (onMenu && _manufactureMenu!.Hover(panelCursor, _world, 0) is { } hover)
                    _message = hover;
                bool pressed = mouse.LeftButton == ButtonState.Pressed && IsActive;
                bool justPressed = pressed && _previousMouse.LeftButton == ButtonState.Released;
                if (_manufactureMenu!.Update(panelCursor, pressed, justPressed, onMenu, _world, 0) is { } done)
                    _message = done;
            }
            else if (_tabs.Current == PanelTab.Goods && _tabs.CurrentMenu == "distribution")
            {
                if (onMenu && _distributionMenu!.Hover(panelCursor) is { } hover)
                    _message = hover;
                bool pressed = mouse.LeftButton == ButtonState.Pressed && IsActive;
                bool justPressed = pressed && _previousMouse.LeftButton == ButtonState.Released;
                if (_distributionMenu!.Update(panelCursor, pressed, justPressed, onMenu, _world, 0) is { } done)
                    _message = done;
            }
            else if (_tabs.Current == PanelTab.Goods && _tabs.CurrentMenu == "transport" && onMenu)
            {
                if (_transportMenu!.Hover(panelCursor, _world, 0) is { } hover)
                    _message = hover;
                if (released && _transportMenu.Click(panelCursor, _world, 0) is { } done)
                    _message = done;
            }
            else if (_tabs.Current == PanelTab.Goods && _tabs.CurrentMenu == "stock" && onMenu)
            {
                if (_stockMenu!.Hover(panelCursor, _world, 0) is { } hover)
                    _message = hover;
            }
            else if (_tabs.CurrentMenu == "statistics" && onMenu)
            {
                if (_statisticsMenu!.Hover(panelCursor, _world, 0) is { } hover)
                    _message = hover;
                if (released && _statisticsMenu.Click(panelCursor, _world, 0) is { } done)
                    _message = done;
            }
            else if (_tabs.CurrentMenu == "professions" && onMenu)
            {
                if (_professionsMenu!.Hover(panelCursor) is { } hover)
                    _message = hover;
                if (released)
                {
                    var converted = new List<Settler>();
                    string? done = _professionsMenu.Click(panelCursor, _world, 0, out var find, converted, CarriersByCenter());
                    if (find is { } f)
                        Find(f.Profession, f.Mode);
                    else if (done is not null)
                        _message = done;
                    // Converted specialists are selected at once; carriers still delivering join when they convert.
                    if (converted.Count > 0)
                    {
                        ClearSelection();
                        _group.AddRange(converted.Where(s => !s.IsCarrier));
                        _joining.Clear();
                        _joining.AddRange(converted.Where(s => s.IsCarrier));
                        _selected = null;
                        _selectedBuilding = null;
                        _menuOverSelection = true;
                    }
                }
            }
        }
        else if (!ShowsSelection)
        {
            if (onMenu && released && _buildMenu!.Click(panelCursor) is { } chosen)
            {
                _placeIndex = Math.Max(0, Array.IndexOf(_placeable, chosen));
                _placing = true;
            }
            if (onMenu && _buildMenu!.BuildingAt(panelCursor) is { } hovered)
                _message = hovered.Name;
        }

        // Left button: a click on release, or a selection box when dragged.
        if (mouse.LeftButton == ButtonState.Pressed && _previousMouse.LeftButton == ButtonState.Released
            && IsActive && GraphicsDevice.Viewport.Bounds.Contains(mouse.Position) && !onMenu && !lost)
            _dragStart = cursorScreen;
        if (mouse.LeftButton == ButtonState.Released && _previousMouse.LeftButton == ButtonState.Pressed && _dragStart is { } dragStart)
        {
            // The selection is exactly a group: what Shift adds to it joins the group too.
            var before = (Settlers: _group.ToList(), Ships: _ships.ToList());
            int? joinedGroup = ShiftDown(Keyboard.GetState()) ? _controlGroups.Matching(_group, _ships) : null;
            if (!_placing && !_buildingInfo.AimsRoute(_selectedBuilding) && !_buildingInfo.AimsDock(_selectedBuilding) && Vector2.Distance(dragStart, cursorScreen) >= DragThreshold)
                SelectInBox(dragStart, cursorScreen);
            else
                Click(cursorScreen);
            if (joinedGroup is { } index && _group.Count + _ships.Count > before.Settlers.Count + before.Ships.Count
                && before.Settlers.All(_group.Contains) && before.Ships.All(_ships.Contains))
                _controlGroups.Store(index, _group, _ships);
            _dragStart = null;
        }
        // Converted carriers join the selection once they are specialists (if it still is of their kind or empty);
        // who died or will not convert any more drops out.
        _joining.RemoveAll(s => s.IsDead || s.IsAvailableCarrier);
        foreach (var joined in _joining.Where(s => !s.IsCarrier).ToList())
        {
            _joining.Remove(joined);
            if (_group.Count == 0 && _selectedBuilding is null || _group.Count > 0 && _group[0].Profession == joined.Profession)
                _group.Add(joined);
        }
        // The selection holds one kind (war machines apart from soldiers); who died, went inside or changed profession leaves it.
        var kind = _group.FirstOrDefault(s => !s.IsDead && !s.IsInside)?.Profession;
        _group.RemoveAll(s => s.IsDead || s.IsInside || s.Profession != kind);

        // The flight: eased out (fast start, soft arrival); scrolling by hand ends it.
        if (_flightStart >= 0)
        {
            float t = (float)Math.Min(1, (gameTime.TotalGameTime.TotalSeconds - _flightStart) / FlightSeconds);
            _camera = Vector2.Lerp(_flightFrom, _flightTo, 1 - MathF.Pow(1 - t, 3));
            if (t >= 1)
                _flightStart = -1;
        }

        float speed = 800 / _zoom * (float)gameTime.ElapsedGameTime.TotalSeconds;
        if (keys.IsKeyDown(Keys.Left) || keys.IsKeyDown(Keys.A) || keys.IsKeyDown(Keys.Right) || keys.IsKeyDown(Keys.D)
            || keys.IsKeyDown(Keys.Up) || keys.IsKeyDown(Keys.W) || keys.IsKeyDown(Keys.Down) || keys.IsKeyDown(Keys.S)
            || mouse.RightButton == ButtonState.Pressed)
            _flightStart = -1;
        if (keys.IsKeyDown(Keys.Left) || keys.IsKeyDown(Keys.A))
            _camera.X -= speed;
        if (keys.IsKeyDown(Keys.Right) || keys.IsKeyDown(Keys.D))
            _camera.X += speed;
        if (keys.IsKeyDown(Keys.Up) || keys.IsKeyDown(Keys.W))
            _camera.Y -= speed;
        if (keys.IsKeyDown(Keys.Down) || keys.IsKeyDown(Keys.S))
            _camera.Y += speed;

        // Right button: dragging moves the map, a click gives the selected soldiers their order.
        if (mouse.RightButton == ButtonState.Pressed && _previousMouse.RightButton == ButtonState.Released
            && IsActive && GraphicsDevice.Viewport.Bounds.Contains(mouse.Position) && !onMenu)
            _rightStart = cursorScreen;
        if (mouse.RightButton == ButtonState.Pressed && _previousMouse.RightButton == ButtonState.Pressed)
            _camera -= (mouse.Position - _previousMouse.Position).ToVector2() / _zoom;
        if (mouse.RightButton == ButtonState.Released && _previousMouse.RightButton == ButtonState.Pressed && _rightStart is { } rightStart)
        {
            if (Vector2.Distance(rightStart, cursorScreen) < DragThreshold && !lost)
                Command(cursorScreen);
            _rightStart = null;
        }

        int wheel = mouse.ScrollWheelValue - _previousMouse.ScrollWheelValue;
        if (wheel != 0)
            _zoomTarget = NextZoomLevel(_zoomTarget ?? _zoom, wheel > 0);
        if (_zoomTarget is float zoomTarget)
        {
            // Exponential glide in log space, so zooming in and out feel alike at every level.
            float keep = MathF.Exp(-(float)gameTime.ElapsedGameTime.TotalSeconds / ZoomGlideSeconds);
            _zoom = zoomTarget * MathF.Pow(_zoom / zoomTarget, keep);
            if (MathF.Abs(_zoom / zoomTarget - 1) < 0.002f)
            {
                _zoom = zoomTarget;
                _zoomTarget = null;
            }
        }

        _previousKeys = typed;
        _previousMouse = mouse;
    }

    /// <summary>
    /// The chat line: Enter opens it (also once the game is lost); open, Enter says the line in the global chat as the own
    /// player, Esc drops it, ↑/↓ recall lines sent before, Ctrl+V pastes. Returns whether the keys went to the chat this frame.
    /// </summary>
    private bool HandleChat(KeyboardState keys)
    {
        bool Pressed(Keys k) => keys.IsKeyDown(k) && !_previousKeys.IsKeyDown(k);
        if (!_chat.IsOpen)
        {
            if (!Pressed(Keys.Enter))
                return false;
            _chat.Open();
            return true;
        }
        bool control = keys.IsKeyDown(Keys.LeftControl) || keys.IsKeyDown(Keys.RightControl);
        if (Pressed(Keys.Escape))
            _chat.Close();
        else if (Pressed(Keys.Enter))
        {
            string line = _chat.Submit().Trim();
            if (line.StartsWith('/'))
                RunCommand(line[1..]);
            else if (_world.Say(0, line) is not null)
                _aiChat?.Heard(_world);
        }
        else if (Pressed(Keys.Up))
            _chat.Recall(-1);
        else if (Pressed(Keys.Down))
            _chat.Recall(1);
        else if (control && Pressed(Keys.V) && SdlClipboard.Text() is { } pasted)
            _chat.Paste(pasted);
        return true;
    }

    /// <summary>Chat commands (after the „/“), all cheats: only with <c>cheats</c> in the settings, otherwise a computer opponent objects.</summary>
    private static readonly string[] Commands = ["cheats", "attackme"];

    private void RunCommand(string line)
    {
        string name = line.Split(' ', 2)[0].ToLowerInvariant();
        if (!_display.Cheats)
        {
            var opponents = _world.AiPlayers.Where(ai => ai.Player != 0 && !_world.AreAllies(0, ai.Player) && _world.Goals?.IsDefeated(ai.Player) != true)
                .ToList();
            if (opponents.Count > 0)
                _world.Say(opponents[_random.Next(opponents.Count)].Player, Lang.T("chat.noCheats"));
            return;
        }
        switch (name)
        {
            case "cheats":
                _cheats.IsOpen = !_cheats.IsOpen;
                break;
            case "attackme":
                // Every computer opponent still in the game assaults player 1 with everything and never falls back.
                int attackers = 0;
                foreach (var ai in _world.AiPlayers.Where(ai => ai.Player != 0 && _world.Goals?.IsDefeated(ai.Player) != true))
                {
                    ai.Assault(0);
                    if (ai.AssaultOn == 0)
                        attackers++;
                }
                _info.Add(Lang.T("chat.attackMe", attackers), _now);
                break;
            default:
                _info.Add(Lang.T("chat.unknown", "/" + name, string.Join(", ", Commands.Select(c => "/" + c))), _now);
                break;
        }
    }

    /// <summary>The game's keys (not once the game is lost): pause, view, groups, placing and debugging.</summary>
    private void HandleKeys(KeyboardState keys, Vector2 cursorScreen, GameTime gameTime)
    {
        bool Pressed(Keys k) => keys.IsKeyDown(k) && !_previousKeys.IsKeyDown(k);
        if (Pressed(Keys.Escape))
        {
            if (_aiming)
                _aiming = false;
            else if (_buildingInfo.AimsRoute(_selectedBuilding))
                _buildingInfo.CancelRoute();
            else if (_buildingInfo.AimsDock(_selectedBuilding))
                _buildingInfo.CancelDock();
            else if (_buildingInfo.Asking is not null)
                _buildingInfo.Cancel();
            else if (_placing)
                _placing = false;
            else
                _paused = !_paused;
        }
        if (Pressed(Keys.F2))
        {
            _scaling = (Scaling)(((int)_scaling + 1) % Enum.GetValues<Scaling>().Length);
            _scalingShownUntil = gameTime.TotalGameTime + TimeSpan.FromSeconds(2);
        }
        if (Pressed(Keys.F3))
            _showPerf = !_showPerf;
        if (Pressed(Keys.T))
            _showClock = !_showClock;
        if (Pressed(Keys.F11))
            ToggleFullScreen();
        // Groups: Ctrl+1-9 stores the selection, 1-9 selects the group again; pressed twice the camera flies to it.
        bool control = keys.IsKeyDown(Keys.LeftControl) || keys.IsKeyDown(Keys.RightControl);
        for (int i = 0; i < ControlGroups.Count; i++)
        {
            if (!Pressed(Keys.D1 + i) && !Pressed(Keys.NumPad1 + i))
                continue;
            if (control)
            {
                _controlGroups.Store(i, _group, _ships);
                int stored = _group.Count + _ships.Count;
                _message = stored > 0 ? $"group {i + 1}: {stored} selected" : $"group {i + 1} cleared";
            }
            else
            {
                SelectGroup(i);
                if (_lastGroupKey == i && _now - _lastGroupAt <= GroupDoublePressSeconds && _group.Count + _ships.Count > 0)
                {
                    float alpha = TickAlpha;
                    _flightFrom = _camera;
                    _flightTo = _group.Select(s => SettlerRenderer.Anchor(s, _terrain, alpha)).Concat(_ships.Select(ShipCenter))
                        .Aggregate(Vector2.Zero, (sum, p) => sum + p) / (_group.Count + _ships.Count);
                    _flightStart = _now;
                }
                _lastGroupKey = i;
                _lastGroupAt = _now;
            }
        }
        if (Pressed(Keys.P))
            _paused = !_paused;
        if (_placing && Pressed(Keys.E))
            _placeIndex = (_placeIndex + 1) % _placeable.Length;
        if (_placing && Pressed(Keys.Q))
            _placeIndex = (_placeIndex - 1 + _placeable.Length) % _placeable.Length;
        if (Pressed(Keys.PageDown))
            LoadMap(_mapIndex + 1);
        if (Pressed(Keys.PageUp))
            LoadMap(_mapIndex - 1);
        if (Pressed(Keys.B))
            _showBlocked = !_showBlocked;
        if (Pressed(Keys.O))
            _showObjects = !_showObjects;
        if (Pressed(Keys.I))
        {
            if (_ownAi is null && _playerCatalogs.Count > 0)
            {
                _ownAi = NewAi(0);
                _world.AiPlayers.Add(_ownAi);
                _message = "the AI plays for you (I: stop)";
            }
            else if (_ownAi is not null)
            {
                _world.AiPlayers.Remove(_ownAi);
                _ownAi = null;
                _message = "you play again";
            }
        }
        if (Pressed(Keys.L))
        {
            foreach (var ai in _world.AiPlayers.OrderBy(a => a.Player))
            {
                Console.WriteLine($"-- AI player {ai.Player + 1}, land {ai.Land}");
                foreach (string line in ai.Log)
                    Console.WriteLine("   " + line);
            }
            _message = "AI decisions written to the console";
        }
        if (Pressed(Keys.F) && _world.BuildingAt(ScreenToGrid(cursorScreen)) is { } target)
        {
            // F: attack the enemy military building under the cursor with player 1's soldiers nearby.
            int sent = _world.Military.Attack(0, target);
            _message = sent > 0 ? $"{sent} soldiers attack {target.Definition.Name}" : "no soldiers in range or not an enemy military building";
        }
    }

    /// <summary>The next wheel step in or out from <paramref name="zoom"/>, which may lie between steps (e.g. from an old save).</summary>
    private static float NextZoomLevel(float zoom, bool zoomIn)
    {
        if (zoomIn)
            return ZoomLevels.FirstOrDefault(level => level > zoom + 0.01f, ZoomLevels[^1]);
        return ZoomLevels.LastOrDefault(level => level < zoom - 0.01f, ZoomLevels[0]);
    }

    /// <summary>
    /// Placement mode: place a construction site. Otherwise select the settler under the cursor, or
    /// send the selected one to the clicked point.
    /// </summary>
    private void Click(Vector2 screen)
    {
        if (_placing)
        {
            var definition = _placeable[_placeIndex];
            if (BuildSpotNear(screen) is not { } at)
            {
                _world.CanPlace(definition, ScreenToGrid(screen), out string reason);
                _message = $"{definition.Name}: {reason}";
                return;
            }
            bool placed = _world.PlaceConstructionSite(definition, 0, at) is not null;
            _message = placed ? $"{definition.Name} site placed" : "could not place";
            // Placed: the choice is done (choose again in the build menu for another one).
            if (placed)
            {
                _placing = false;
                _buildSpotChecks.Clear();
            }
            return;
        }

        // A market's route button chosen: the click sets that point.
        if (_buildingInfo.AimsRoute(_selectedBuilding) && _selectedBuilding is { Exists: true } market)
        {
            _message = _buildingInfo.MarketCommand(_world, market, ScreenToGrid(screen));
            return;
        }

        // A shipyard's hammer chosen: the click puts the dock near it.
        if (_buildingInfo.AimsDock(_selectedBuilding) && _selectedBuilding is { Exists: true } dockyard)
        {
            _message = _buildingInfo.DockCommand(_world, dockyard, ScreenToGrid(screen));
            return;
        }

        if (_aiming)
        {
            _aiming = false;
            CastSpell(ScreenToGrid(screen));
            return;
        }

        float alpha = TickAlpha;
        var world = Vector2.Transform(screen, Matrix.Invert(View));
        // Settler sprites stand above their anchor; compare with the body center.
        var hit = _world.Settlers.Where(s => !s.IsInside)
            .Select(s => (Settler: s, Distance: Vector2.Distance(world, SettlerRenderer.Anchor(s, _terrain, alpha) - new Vector2(0, 10))))
            .Where(t => t.Distance < 10)
            .OrderBy(t => t.Distance)
            .Select(t => t.Settler)
            .FirstOrDefault();
        // Own soldiers, priests, geologists, scouts and pioneers can be selected; carriers and other trades not at all.
        // Shift adds to the selection (by priority, if the kinds differ) instead of replacing it.
        bool adding = ShiftDown(Keyboard.GetState());
        if (hit is { Player: 0 } && Selectable(hit))
        {
            // On screen: Ctrl every own settler of that kind (soldiers: all of them), Alt of that type
            // (soldiers: of that type, whatever their rank).
            var keys = Keyboard.GetState();
            bool control = ControlDown(keys), alt = AltDown(keys);
            var clicked = control || alt
                ? OnScreen().Where(s => s.Player == 0 && !s.IsInside && Selectable(s) && s.Profession == hit.Profession
                                        && (control && s.Profession == Profession.Soldier || s.SoldierType == hit.SoldierType))
                    .Append(hit).Distinct().ToList()
                : [hit];
            var selection = ByPriority(adding ? _group.Concat(clicked) : clicked);
            ClearSelection();
            _group.AddRange(selection);
            _selected = null;
            _selectedBuilding = null;
            _message = SelectionHint();
            return;
        }
        // Own ships: Shift adds, Ctrl all own ships on the screen.
        if (hit is null && ShipAt(world) is { Player: 0 } ship)
        {
            var keys = Keyboard.GetState();
            var clicked = ControlDown(keys) ? _world.Shipping.Ships.Where(s => s.Player == 0 && OnScreen(s)).Append(ship).Distinct().ToList() : [ship];
            var ships = adding ? _ships.Concat(clicked).Distinct().ToList() : clicked;
            ClearSelection();
            _ships.AddRange(ships);
            _selected = null;
            _selectedBuilding = null;
            _message = $"{_ships.Count} ships: right click sends them (over the water)";
            return;
        }
        // Settlers without a trade can still be picked and sent around one by one.
        if (hit is { Profession: Profession.None } && _group.Count == 0)
        {
            _selected = hit;
            _selectedBuilding = null;
            _message = $"settler {hit.Id} selected";
            return;
        }

        // The selection gets its orders by right click; a left click elsewhere deselects it (not with Shift).
        if ((_group.Count > 0 || _ships.Count > 0) && adding)
            return;
        // A click into the empty landscape deselects and empties the panel (as in the original).
        if (_group.Count > 0 || _ships.Count > 0)
        {
            ClearSelection();
            _selected = null;
            _tabs.Current = PanelTab.None;
            _message = "";
            return;
        }

        // A building: select it (shows its state and work area). Without a selected settler,
        // clicking elsewhere clears the selection.
        if (_world.BuildingAt(ScreenToGrid(screen)) is { } building)
        {
            _selectedBuilding = building;
            _selected = null;
            return;
        }
        if (_selectedBuilding is not null && _selected is null)
        {
            _selectedBuilding = null;
            _tabs.Current = PanelTab.None;
            _message = "";
            return;
        }

        if (_selected is null)
        {
            _tabs.Current = PanelTab.None;
            return;
        }
        var target = ScreenToGrid(screen);
        _message = _world.WalkTo(_selected, target) ? $"walking to {target.X},{target.Y}" : "unreachable";
    }

    /// <summary>Half the width and the height of a ship's body around which a click or a box picks it.</summary>
    private static readonly Vector2 ShipReach = new(36, 22);

    /// <summary>Where a ship's body is (its anchor lies at its waterline; the hull reaches up from it).</summary>
    private Vector2 ShipCenter(Homestead.Simulation.Economy.Ship ship) =>
        ShipRenderer.Anchor(ship, _terrain, _world.Shipping.ShipStepTicks, TickAlpha) - new Vector2(0, 14);

    /// <summary>The ship whose body is at the world point (the nearest), if any.</summary>
    private Homestead.Simulation.Economy.Ship? ShipAt(Vector2 world) => _world.Shipping.Ships
        .Where(s => Math.Abs(ShipCenter(s).X - world.X) <= ShipReach.X && Math.Abs(ShipCenter(s).Y - world.Y) <= ShipReach.Y)
        .OrderBy(s => Vector2.Distance(ShipCenter(s), world)).FirstOrDefault();

    private bool OnScreen(Homestead.Simulation.Economy.Ship ship)
    {
        var viewport = GraphicsDevice.Viewport;
        var p = ShipCenter(ship);
        return Math.Abs(p.X - _camera.X) <= viewport.Width / 2f / _zoom && Math.Abs(p.Y - _camera.Y) <= viewport.Height / 2f / _zoom;
    }

    /// <summary>Selects the members of a group that can be selected now (not inside a building).</summary>
    private void SelectGroup(int index)
    {
        ClearSelection();
        _group.AddRange(_controlGroups.Members(index).Where(s => !s.IsInside && Selectable(s)));
        _ships.AddRange(_controlGroups.Ships(index));
        _selected = null;
        _selectedBuilding = null;
        _message = _group.Count > 0 ? SelectionHint()
            : _ships.Count > 0 ? $"{_ships.Count} ships: right click sends them (over the water)"
            : $"group {index + 1}: nobody to select";
    }

    /// <summary>Whether the settler stands in the view.</summary>
    private bool OnScreen(Settler s)
    {
        var viewport = GraphicsDevice.Viewport;
        var p = SettlerRenderer.Anchor(s, _terrain, TickAlpha);
        return Math.Abs(p.X - _camera.X) <= viewport.Width / 2f / _zoom && Math.Abs(p.Y - _camera.Y) <= viewport.Height / 2f / _zoom;
    }

    /// <summary>
    /// The find buttons of Siedler › Berufe: selects all own settlers of the profession, those on the
    /// screen, or the next one (one after another, those on the screen first; the camera flies to it).
    /// </summary>
    private void Find(Profession profession, FindMode mode)
    {
        var all = _world.Settlers.Where(s => s.Player == 0 && s.Profession == profession && !s.IsInside && !s.IsDead)
            .OrderBy(s => s.Id).ToList();
        var found = mode switch
        {
            FindMode.World => all,
            FindMode.Screen => all.Where(OnScreen).ToList(),
            _ => NextOf(all) is { } next ? [next] : [],
        };
        if (found.Count == 0)
        {
            _message = mode == FindMode.Screen ? "none on the screen" : "none there";
            return;
        }
        ClearSelection();
        _group.AddRange(found);
        _selected = null;
        _selectedBuilding = null;
        _message = SelectionHint();
        if (mode == FindMode.One && !OnScreen(found[0]))
        {
            _flightFrom = _camera;
            _flightTo = SettlerRenderer.Anchor(found[0], _terrain, TickAlpha);
            _flightStart = _now;
        }
    }

    /// <summary>Space held: seconds between events at first, after <see cref="SpaceRampSeconds"/> (10 Hz), and when it was pressed / goes on next.</summary>
    private const float SpaceFirstRepeat = 0.4f, SpaceFastRepeat = 0.1f, SpaceRampSeconds = 1;
    private double _spaceDownAt, _nextSpace;

    /// <summary>The event looked at last: its page in the panel (<see cref="PanelTab.Event"/>) shows its text.</summary>
    private GameEvent? _shownEvent;

    /// <summary>The event's text, its building's name filled in.</summary>
    /// <summary>The event's text in the chosen language (<c>events.texts.&lt;id&gt;</c>; greetings carry theirs), the building's name filled in.</summary>
    private string TextOf(GameEvent e)
    {
        string text = Lang.Find("events.texts." + e.Definition.Id) ?? e.Definition.Text;
        return e.Building is { } building ? text.Replace("{building}", _buildingInfo.NameOf(building.Definition)) : text;
    }

    /// <summary>
    /// Shows the event's page in the panel (like the computer button, the selection goes), flies the camera
    /// to its place (if it has one) and points arrows at it for a while.
    /// </summary>
    private void LookAt(GameEvent? e)
    {
        if (e is null)
        {
            _message = "no events";
            return;
        }
        _shownEvent = e;
        _tabs.Current = PanelTab.Event;
        ClearSelection();
        _selected = null;
        _selectedBuilding = null;
        _placing = _aiming = false;
        if (e.Where is not { } where)
            return;
        _flightFrom = _camera;
        _flightTo = ToWorld(where);
        _flightStart = _now;
        _pointed = e;
        _pointUntil = _now + FlightSeconds + _eventButtons.ArrowSeconds;
    }

    /// <summary>The event's page in the panel: the name of its kind, below it its text in white.</summary>
    private void DrawEventPage()
    {
        if (_shownEvent is not { } e)
            return;
        var content = BuildMenu.Content;
        int y = content.Y;
        SelectionPanel.Shadowed(_spriteBatch, _font, _eventButtons.NameOf(e.Category), new Vector2(content.X + 4, y), 16, new Color(240, 225, 190));
        y += 28;
        foreach (string line in _font.Wrap(TextOf(e), content.Width - 8, 16))
        {
            SelectionPanel.Shadowed(_spriteBatch, _font, line, new Vector2(content.X + 4, y), 16, Color.White);
            y += 20;
        }
    }

    /// <summary>
    /// Four arrows around the event's place pointing at it (the building, else the settler while alive),
    /// moving in and out; screen size independent of the zoom.
    /// </summary>
    private void DrawEventArrows(float alpha)
    {
        if (_pointed is not { } e || _now >= _pointUntil)
            return;
        var center = e.Building is { Exists: true } building ? ToWorld(building.Position) - new Vector2(0, 20)
            : e.Settler is { IsDead: false } settler ? SettlerRenderer.Anchor(settler, _terrain, alpha) - new Vector2(0, HitHeight)
            : e.Position is { } position ? ToWorld(position)
            : (Vector2?)null;
        if (center is not { } at)
            return;
        float scale = 1 / _zoom;
        float distance = (44 + 10 * MathF.Sin((float)_now * 8)) * scale;
        foreach (float angle in new[] { -MathF.PI * 3 / 4, -MathF.PI / 4, MathF.PI / 4, MathF.PI * 3 / 4 })
        {
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var tip = at + direction * distance;
            DrawArrow(tip, -direction, scale, Color.Black, 7);
            DrawArrow(tip, -direction, scale, new Color(255, 210, 40), 4);
        }
    }

    /// <summary>An arrow whose tip is at <paramref name="tip"/>, pointing along <paramref name="direction"/>: a shaft and two barbs.</summary>
    private void DrawArrow(Vector2 tip, Vector2 direction, float scale, Color color, float thickness)
    {
        void Line(Vector2 from, Vector2 to)
        {
            var d = to - from;
            float extra = (thickness - 4) / 2 * scale; // the outline reaches beyond the ends too
            var unit = Vector2.Normalize(d);
            from -= unit * extra;
            _spriteBatch.Draw(_pixel, from, null, color, MathF.Atan2(d.Y, d.X), new Vector2(0, 0.5f),
                new Vector2(d.Length() + 2 * extra, thickness * scale), SpriteEffects.None, 0);
        }
        float rotation = MathF.Atan2(direction.Y, direction.X);
        Vector2 Back(float turn, float length) =>
            tip - new Vector2(MathF.Cos(rotation + turn), MathF.Sin(rotation + turn)) * length * scale;
        Line(Back(0, 30), tip);
        Line(Back(0.6f, 13), tip);
        Line(Back(-0.6f, 13), tip);
    }

    /// <summary>The settler the "next one" find button chose last (its id).</summary>
    private int _lastFound = -1;

    /// <summary>The next settler after the one found last: among those on the screen if there are any, else anywhere.</summary>
    private Settler? NextOf(List<Settler> all)
    {
        var pool = all.Where(OnScreen).ToList() is { Count: > 0 } visible ? visible : all;
        var next = pool.FirstOrDefault(s => s.Id > _lastFound) ?? pool.FirstOrDefault();
        _lastFound = next?.Id ?? -1;
        return next;
    }

    /// <summary>
    /// Selection by priority: the soldiers among the settlers if there are any, else the war machines, priests, geologists,
    /// scouts, pioneers.
    /// </summary>
    private static List<Settler> ByPriority(IEnumerable<Settler> settlers)
    {
        var list = settlers.Distinct().ToList();
        foreach (var profession in new[] { Profession.Soldier, Profession.WarMachine, Profession.Priest, Profession.Geologist, Profession.Scout, Profession.Pioneer })
            if (list.Where(s => s.Profession == profession).ToList() is { Count: > 0 } chosen)
                return chosen;
        return [];
    }

    private static bool ControlDown(KeyboardState keys) => keys.IsKeyDown(Keys.LeftControl) || keys.IsKeyDown(Keys.RightControl);

    private static bool ShiftDown(KeyboardState keys) => keys.IsKeyDown(Keys.LeftShift) || keys.IsKeyDown(Keys.RightShift);

    private static bool AltDown(KeyboardState keys) => keys.IsKeyDown(Keys.LeftAlt) || keys.IsKeyDown(Keys.RightAlt);

    /// <summary>The settlers whose body lies in the visible part of the map (the panel's side excluded).</summary>
    private IEnumerable<Settler> OnScreen()
    {
        var inverse = Matrix.Invert(View);
        var viewport = GraphicsDevice.Viewport;
        var min = Vector2.Transform(new Vector2(SidePanel.Width * SidePanel.Scale(viewport.Height), 0), inverse);
        var max = Vector2.Transform(new Vector2(viewport.Width, viewport.Height), inverse);
        float alpha = TickAlpha;
        return _world.Settlers.Where(s => SettlerRenderer.Anchor(s, _terrain, alpha) - new Vector2(0, 10) is var p
                                          && p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y);
    }

    /// <summary>The own carriers on the screen, nearest to the middle of the visible map first (they become specialists).</summary>
    private List<Settler> CarriersByCenter()
    {
        var inverse = Matrix.Invert(View);
        var viewport = GraphicsDevice.Viewport;
        var middle = Vector2.Transform(new Vector2((SidePanel.Width * SidePanel.Scale(viewport.Height) + viewport.Width) / 2, viewport.Height / 2f), inverse);
        float alpha = TickAlpha;
        return OnScreen().Where(s => s.Player == 0 && s.IsCarrier && !s.IsInside)
            .OrderBy(s => Vector2.DistanceSquared(SettlerRenderer.Anchor(s, _terrain, alpha), middle)).ThenBy(s => s.Id).ToList();
    }

    /// <summary>Own settlers that can be selected: soldiers, war machines, priests, geologists, scouts and pioneers (not carriers).</summary>
    private static bool Selectable(Settler s) =>
        s.Profession is Profession.Soldier or Profession.WarMachine or Profession.Priest or Profession.Geologist or Profession.Scout or Profession.Pioneer;

    private List<Settler> SelectedSoldiers() => _group.Where(s => s.Profession == Profession.Soldier).ToList();

    private List<Settler> SelectedMachines() => _group.Where(s => s.Profession == Profession.WarMachine).ToList();

    /// <summary>A building is selected: the menu at the left shows what there is to know about it.</summary>
    private bool ShowsBuildingInfo => _selectedBuilding is { Exists: true } && !_placing && !PriestsSelected;

    /// <summary>Only priests are selected: the menu at the left offers their spell.</summary>
    /// <summary>The entries of the cheat menu (/cheats): own units in the middle of the view, mana, fog of war.</summary>
    private void AddCheats()
    {
        GridPoint Center() => ScreenToGrid(new Vector2(GraphicsDevice.Viewport.Width / 2f, GraphicsDevice.Viewport.Height / 2f));
        void Units(string key, int count, Func<GridPoint, Settler?> spawn) => _cheats.Add($"{count} {Lang.T("cheats." + key)}", () =>
        {
            int made = Enumerable.Range(0, count).Count(_ => spawn(Center()) is not null);
            _message = $"cheat: {made} {key}";
        });
        foreach (var type in new[] { SoldierType.Swordsman, SoldierType.Bowman, SoldierType.Pikeman })
            Units(type.ToString().ToLowerInvariant(), 5, at => _world.SpawnSoldier(at, type, _world.Magic.SoldierLevel(0, type)));
        Units("priests", 1, at => _world.Magic.SpawnPriest(at, 0));
        Units("geologists", 2, at => _world.Spawn(at, Profession.Geologist));
        Units("scouts", 2, at => _world.Spawn(at, Profession.Scout));
        Units("pioneers", 2, at => _world.Spawn(at, Profession.Pioneer));
        Units("carriers", 5, at => _world.Spawn(at, Profession.Carrier));
        Units("warMachines", 2, at => _world.WarMachines.Spawn(at, 0, _world.PeopleOf(0)));
        _cheats.Add("+50 Mana", () =>
        {
            _world.Magic.AddMana(0, 50);
            _message = $"cheat: mana {_world.Magic.Mana(0)}";
        });
        void Decide(string key, MissionOutcome outcome) => _cheats.Add(Lang.T("cheats." + key), () =>
        {
            if (_world.Goals is null)
                _message = "cheat: no goals in this game";
            else
                _world.Goals.Decide(outcome, _world.Tick);
        });
        Decide("missionWon", MissionOutcome.Won);
        Decide("missionLost", MissionOutcome.Lost);
        _cheats.Add(Lang.T("cheats.fog"), () =>
        {
            _fogChosen = !_fogChosen;
            _minimap!.Update(_vision, all: !FogOn, _gameMap, IsAlly);
            _message = _fogChosen ? "cheat: fog of war on" : "cheat: fog of war off";
        });
    }

    /// <summary>The own buildings by name: finished, and under construction (for the build menu).</summary>
    private Dictionary<string, (int Finished, int Building)> OwnBuildingCounts()
    {
        var counts = new Dictionary<string, (int Finished, int Building)>();
        foreach (var building in _world.Buildings)
        {
            if (!building.Exists || building.Player != 0)
                continue;
            var (finished, constructing) = counts.GetValueOrDefault(building.Definition.Name);
            counts[building.Definition.Name] = building.State == BuildingState.Finished ? (finished + 1, constructing) : (finished, constructing + 1);
        }
        return counts;
    }

    private bool ScoutsSelected => _group.Any(s => s.Profession == Profession.Scout);

    private bool PioneersSelected => _group.Any(s => s.Profession == Profession.Pioneer);

    private bool PriestsSelected => _group.Count > 0 && _group.All(s => s.Profession == Profession.Priest);

    private string SelectionHint() => _group.Count == 0 ? "" : _group[0].Profession switch
    {
        Profession.Soldier or Profession.WarMachine when _group.Any(s => s.Profession == Profession.WarMachine) =>
            $"{_group.Count} units selected (right click: go there / attack; war machines fire at military buildings only)",
        Profession.Soldier => $"{_group.Count} soldiers selected (right click: go there / attack, left click: deselect)",
        Profession.Geologist => $"{_group.Count} geologists selected (right click: go there, on a mountain: search there)",
        Profession.Scout => $"{_group.Count} scouts selected (right click: go there, Alt+right click: steal there; enemies take them for carriers)",
        Profession.Pioneer => $"{_group.Count} pioneers selected (right click: go there, Alt+right click: take land there)",
        Profession.Priest => $"{_group.Count} priests selected (menu at the left: cast the spell, right click: go there)",
        _ => "",
    };

    /// <summary>
    /// Right click: the selected soldiers attack the enemy military building clicked on, otherwise go
    /// there (fighting enemies they meet on the way).
    /// </summary>
    private void Command(Vector2 screen)
    {
        // Units onto an own ferry clicked on (lying near land).
        if (_group.Count > 0 && !_placing && !_aiming && ShipAt(Vector2.Transform(screen, Matrix.Invert(View))) is { Player: 0 } ship)
        {
            if (_world.Shipping.Capacity(ship) == 0)
                _message = "only a ferry takes units aboard";
            else if (!_world.Shipping.IsNearLand(ship))
                _message = "the ferry is too far from land to board";
            else if (_world.Shipping.Board(_group, ship) is > 0 and var boarding)
            {
                PlayCommand();
                _message = $"{boarding} units go aboard ({ship.Passengers.Count}/{_world.Shipping.Capacity(ship)} on board)";
            }
            else
                _message = "the ferry is full (or nobody can get to it)";
            return;
        }
        Command(ScreenToGrid(screen));
    }

    private void Command(GridPoint clicked)
    {
        // Right click while placing (or aiming): no building (spell) chosen any more.
        if (_placing || _aiming)
        {
            _placing = _aiming = false;
            _message = "";
            return;
        }
        // Setting a market's route point: cancelled.
        if (_buildingInfo.AimsRoute(_selectedBuilding))
        {
            _buildingInfo.CancelRoute();
            _message = "";
            return;
        }
        // Setting a shipyard's dock: cancelled.
        if (_buildingInfo.AimsDock(_selectedBuilding))
        {
            _buildingInfo.CancelDock();
            _message = "";
            return;
        }
        // Ships: over the water to the open water nearest the click.
        if (_ships.Count > 0)
        {
            int sailing = _world.Shipping.Move(_ships, clicked);
            if (sailing > 0)
                PlayCommand();
            _message = sailing > 0 ? $"{sailing} ships to {clicked.X},{clicked.Y}" : "no way there over the water";
            return;
        }
        // A selected own building with a work area: move the work area there (at most 20 steps away, a gathering point anywhere).
        if (_selectedBuilding is { Exists: true, Player: 0, HasWorkArea: true } worksite)
        {
            if (_world.SetWorkCenter(worksite, clicked) is { } center)
            {
                UiSounds.WorkArea();
                _message = worksite.HasGatheringPoint ? $"gathering point moved to {center.X},{center.Y}"
                    : center == clicked ? $"work area moved to {center.X},{center.Y}"
                    : $"work area moved to {center.X},{center.Y} (at most {World.MaxWorkCenterDistance} fields away)";
            }
            return;
        }
        var priests = _group.Where(s => s.Profession == Profession.Priest && !_world.Magic.IsBusy(s)).ToList();
        if (priests.Count > 0)
        {
            int walking = priests.Count(p => _world.CommandTarget(p, clicked) is { } to && _world.WalkTo(p, to));
            if (walking > 0)
                PlayCommand();
            _message = walking > 0 ? $"{walking} priests to {clicked.X},{clicked.Y}" : "no way there";
            return;
        }
        // Alt: scouts go there and steal, pioneers go there and take land.
        var keyboard = Keyboard.GetState();
        if ((keyboard.IsKeyDown(Keys.LeftAlt) || keyboard.IsKeyDown(Keys.RightAlt)) && (ScoutsSelected || PioneersSelected))
        {
            int stealing = _world.Theft.StealAt(_group, clicked);
            int claiming = _world.Pioneering.ClaimAt(_group, clicked);
            if (stealing + claiming > 0)
                PlayCommand();
            _message = stealing + claiming == 0 ? "no way there"
                : claiming == 0 ? $"{stealing} scouts steal at {clicked.X},{clicked.Y}"
                : stealing == 0 ? $"{claiming} pioneers take land at {clicked.X},{clicked.Y}"
                : $"{stealing} scouts steal, {claiming} pioneers take land at {clicked.X},{clicked.Y}";
            return;
        }
        var geologists = _group.Where(s => s.Profession is Profession.Geologist or Profession.Scout or Profession.Pioneer).ToList();
        if (geologists.Count > 0)
        {
            int sent = _world.Geology.Send(geologists, clicked);
            if (sent > 0)
                PlayCommand();
            string name = geologists.All(s => s.Profession == Profession.Scout) ? "scouts"
                : geologists.All(s => s.Profession == Profession.Pioneer) ? "pioneers" : "geologists";
            _message = sent == 0 ? "no way there"
                : name == "geologists" && _world.Geology.IsSearchable(clicked) ? $"{sent} geologists search around {clicked.X},{clicked.Y}"
                : $"{sent} {name} to {clicked.X},{clicked.Y}";
            return;
        }
        var soldiers = SelectedSoldiers();
        var machines = SelectedMachines();
        if (soldiers.Count + machines.Count == 0)
            return;
        bool control = Keyboard.GetState().IsKeyDown(Keys.LeftControl) || Keyboard.GetState().IsKeyDown(Keys.RightControl);
        // Bowmen do not attack buildings: they keep their order (alone: they walk there). War machines fire at military buildings.
        if (!control && _world.BuildingAt(clicked) is { } attacked && attacked.Player != 0
            && (soldiers.Count > 0 ? _world.Military.Attack(soldiers, attacked) : 0) + _world.WarMachines.Attack(machines, attacked) is > 0 and var attacking)
        {
            PlayCommand();
            _message = $"{attacking} units attack {attacked.Definition.Name}";
            return;
        }
        // Ctrl: forced order, they walk there without stopping to fight.
        var keys = Keyboard.GetState();
        bool forced = keys.IsKeyDown(Keys.LeftControl) || keys.IsKeyDown(Keys.RightControl);
        if (soldiers.Count > 0)
            _world.Military.Move(soldiers, clicked, forced);
        if (machines.Count > 0)
            _world.WarMachines.Move(machines, clicked);
        PlayCommand();
        _message = $"{soldiers.Count + machines.Count} units to {clicked.X},{clicked.Y}{(forced ? " (forced)" : "")}";
    }

    /// <summary>
    /// Selects the own settlers (outside buildings) inside the box between two screen points: the
    /// soldiers if there are any, else the priests, else the geologists. Without them the building
    /// nearest to the middle of the box is chosen. With Shift settlers join the selection.
    /// </summary>
    private void SelectInBox(Vector2 from, Vector2 to)
    {
        var inverse = Matrix.Invert(View);
        var a = Vector2.Transform(from, inverse);
        var b = Vector2.Transform(to, inverse);
        var min = Vector2.Min(a, b);
        var max = Vector2.Max(a, b);
        float alpha = TickAlpha;
        var inBox = _world.Settlers.Where(s => s.Player == 0 && !s.IsInside && Selectable(s))
            .Where(s => SettlerRenderer.Anchor(s, _terrain, alpha) - new Vector2(0, 10) is var p
                        && p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y)
            .ToList();
        // Shift adds to the selection.
        var candidates = ShiftDown(Keyboard.GetState()) ? _group.Concat(inBox).ToList() : inBox;
        // No settlers there: own ships.
        var shipsInBox = _world.Shipping.Ships.Where(s => s.Player == 0 && ShipCenter(s) is var c
                                                          && c.X >= min.X && c.X <= max.X && c.Y >= min.Y && c.Y <= max.Y).ToList();
        if (candidates.Count == 0 && (shipsInBox.Count > 0 || ShiftDown(Keyboard.GetState()) && _ships.Count > 0))
        {
            var ships = (ShiftDown(Keyboard.GetState()) ? _ships.Concat(shipsInBox) : shipsInBox).Distinct().ToList();
            ClearSelection();
            _ships.AddRange(ships);
            _selected = null;
            _selectedBuilding = null;
            _message = $"{_ships.Count} ships: right click sends them (over the water)";
            return;
        }
        if (candidates.Count == 0 && BuildingInBox(min, max) is { } building)
        {
            ClearSelection();
            _selected = null;
            _selectedBuilding = building;
            _message = "";
            return;
        }
        var selection = ByPriority(candidates);
        ClearSelection();
        _group.AddRange(selection);
        _selected = null;
        _selectedBuilding = null;
        _message = SelectionHint();
    }

    /// <summary>
    /// The building nearest to the middle of the box (world coordinates): its image's middle lies in
    /// the box, or the box's middle lies on its image.
    /// </summary>
    private Building? BuildingInBox(Vector2 min, Vector2 max)
    {
        var middle = (min + max) / 2;
        return _world.Buildings.Where(b => b.Exists)
            .Select(b => (Building: b, Bounds: _economyRenderer.BuildingBounds(b, _terrain)))
            .Select(t => (t.Building, t.Bounds, Center: new Vector2(t.Bounds.Center.X, t.Bounds.Center.Y)))
            .Where(t => t.Center.X >= min.X && t.Center.X <= max.X && t.Center.Y >= min.Y && t.Center.Y <= max.Y
                        || t.Bounds.Contains(middle))
            .OrderBy(t => Vector2.DistanceSquared(t.Center, middle))
            .Select(t => t.Building)
            .FirstOrDefault();
    }

    private float TickAlpha => (float)(_accumulator / World.TickDuration);

    private Matrix View
    {
        get
        {
            var viewport = GraphicsDevice.Viewport;
            return Matrix.CreateTranslation(-_camera.X, -_camera.Y, 0)
                   * Matrix.CreateScale(_zoom)
                   * Matrix.CreateTranslation(MathF.Round(viewport.Width / 2f), MathF.Round(viewport.Height / 2f), 0);
        }
    }

    /// <summary>Screen position to the nearest grid point, ignoring terrain height.</summary>
    /// <summary>
    /// The grid point drawn under a screen position, height included: higher points are drawn further
    /// up, so the rows below the flat estimate are searched for the point drawn nearest the cursor.
    /// </summary>
    private GridPoint ScreenToGrid(Vector2 screen)
    {
        var world = Vector2.Transform(screen, Matrix.Invert(View));
        int size = _map.Area.Size;
        int flatY = (int)MathF.Round(world.Y / ScreenProjection.RowHeight);
        int maxRows = 255 * ScreenProjection.HeightStep / ScreenProjection.RowHeight + 1;
        var best = new GridPoint(0, 0);
        float bestDistance = float.MaxValue;
        for (int y = Math.Max(0, flatY - 1); y <= Math.Min(size - 1, flatY + maxRows); y++)
        {
            int x0 = (int)MathF.Round((world.X + y * ScreenProjection.TileWidth / 2f) / ScreenProjection.TileWidth);
            for (int x = Math.Max(0, x0 - 1); x <= Math.Min(size - 1, x0 + 1); x++)
            {
                float distance = Vector2.DistanceSquared(world, ScreenProjection.ToWorld(x, y, _terrain.HeightAt(x, y)));
                if (distance < bestDistance)
                {
                    best = new GridPoint(x, y);
                    bestDistance = distance;
                }
            }
        }
        return best;
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_loading is { Loaded: false })
        {
            // Capture runs with HOMESTEAD_CAPTURE_START=loading save its picture and quit.
            // Once loaded, the game is drawn beneath it (LoadingScreen.WarmFrames).
            var loadingTarget = _captureDirectory is null ? null : NewCaptureTarget();
            GraphicsDevice.SetRenderTarget(loadingTarget);
            GraphicsDevice.Clear(Color.Black);
            _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
            _loading.Draw(_spriteBatch, GraphicsDevice.Viewport.Bounds);
            _spriteBatch.End();
            if (loadingTarget is not null)
            {
                GraphicsDevice.SetRenderTarget(null);
                using (var stream = File.Create(Path.Combine(_captureDirectory!, "loading.png")))
                    loadingTarget.SaveAsPng(stream, loadingTarget.Width, loadingTarget.Height);
                loadingTarget.Dispose();
                Exit();
            }
            base.Draw(gameTime);
            return;
        }
        if (_title is not null || _startScreen is not null)
        {
            // Capture runs with HOMESTEAD_CAPTURE_START save one picture of the title or start screen and quit
            // (a video's once it shows its 100th frame).
            var startTarget = _captureDirectory is null || _video is { Shown: < 100 } ? null : NewCaptureTarget();
            GraphicsDevice.SetRenderTarget(startTarget);
            GraphicsDevice.Clear(new Color(12, 12, 16));
            _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
            if (_briefing is not null)
                _briefing.Draw(_spriteBatch, _pixel, _font, GraphicsDevice.Viewport.Bounds);
            else if (_video is not null)
                _video.Draw(_spriteBatch, _pixel, GraphicsDevice.Viewport.Bounds);
            else if (_missionSelect is not null)
                _missionSelect.Draw(_spriteBatch, _pixel, _font, GraphicsDevice.Viewport.Bounds);
            else if (_loadList is not null)
                _loadList.Draw(_spriteBatch, _pixel, _font, GraphicsDevice.Viewport.Bounds);
            else if (_campaignScreen is not null)
                _campaignScreen.Draw(_spriteBatch, _pixel, _font, GraphicsDevice.Viewport.Bounds);
            else if (_settingsScreen is not null)
                _settingsScreen.Draw(_spriteBatch, _pixel, _font, GraphicsDevice.Viewport.Bounds);
            else if (_title is not null)
                _title.Draw(_spriteBatch, _pixel, _font, GraphicsDevice.Viewport.Bounds);
            else
                _startScreen!.Draw(_spriteBatch, _pixel, _font, GraphicsDevice.Viewport.Bounds);
            _spriteBatch.End();
            if (startTarget is not null)
            {
                GraphicsDevice.SetRenderTarget(null);
                string picture = _briefing is not null ? "briefing.png" : _video is not null ? "video.png" : _missionSelect is not null ? "missions.png" : _loadList is not null ? "load.png" : _campaignScreen is not null ? "campaign.png"
                    : _settingsScreen is not null ? "settings.png" : _title is not null ? "title.png" : "start.png";
                using (var stream = File.Create(Path.Combine(_captureDirectory!, picture)))
                    startTarget.SaveAsPng(stream, startTarget.Width, startTarget.Height);
                startTarget.Dispose();
                Exit();
            }
            base.Draw(gameTime);
            return;
        }
        if (_statistics is not null)
        {
            DrawStatistics();
            base.Draw(gameTime);
            return;
        }
        _perf.BeginSection();
        RenderTarget2D? target = null;
        if (_captureDirectory is not null)
        {
            target = NewCaptureTarget();
            GraphicsDevice.SetRenderTarget(target);
        }

        var viewport = GraphicsDevice.Viewport;
        var visible = new RectangleF(
            _camera.X - viewport.Width / 2f / _zoom, _camera.Y - viewport.Height / 2f / _zoom,
            viewport.Width / _zoom, viewport.Height / _zoom);

        // Smooth scaling: the world goes 1:1 into a small target whose pixel 0 is the whole world point origin.
        bool smooth = _scaling != Scaling.Sharp && _zoom > 1;
        var origin = new Vector2(MathF.Floor(visible.X), MathF.Floor(visible.Y));
        var worldView = View;
        var worldProjection = Matrix.CreateOrthographicOffCenter(0, viewport.Width, viewport.Height, 0, -1, 1);
        var worldSize = Point.Zero;
        if (smooth)
        {
            worldSize = new Point((int)MathF.Ceiling(visible.Width) + 2, (int)MathF.Ceiling(visible.Height) + 2);
            // Only grows: while the zoom glides the needed size changes every frame; the top left part is used.
            if (_worldTarget is null || _worldTarget.Width < worldSize.X || _worldTarget.Height < worldSize.Y)
            {
                _worldTarget?.Dispose();
                _worldTarget = new RenderTarget2D(GraphicsDevice, Math.Max(worldSize.X, _worldTarget?.Width ?? 0),
                    Math.Max(worldSize.Y, _worldTarget?.Height ?? 0));
            }
            GraphicsDevice.SetRenderTarget(_worldTarget);
            worldView = Matrix.CreateTranslation(-origin.X, -origin.Y, 0);
            worldProjection = Matrix.CreateOrthographicOffCenter(0, _worldTarget.Width, _worldTarget.Height, 0, -1, 1);
        }
        GraphicsDevice.Clear(Color.Black);

        _effect.World = Matrix.Identity;
        _effect.View = worldView;
        _effect.Projection = worldProjection;
        GraphicsDevice.RasterizerState = RasterizerState.CullNone;
        // Point sampling: linear filtering would bleed neighboring atlas cells into the texture edges.
        GraphicsDevice.SamplerStates[0] = SamplerState.PointClamp;
        GraphicsDevice.BlendState = BlendState.Opaque;
        GraphicsDevice.DepthStencilState = DepthStencilState.None;
        _terrain.Draw(_effect, visible);
        _waves.Draw(GraphicsDevice, _effect, visible, (float)_time.TotalSeconds);

        _spriteBatch.Begin(samplerState: SamplerState.PointClamp, transformMatrix: worldView);
        _waves.DrawGlitter(_spriteBatch, _sprites, visible, (float)_time.TotalSeconds);
        _bubbles.Draw(_spriteBatch, _economyRenderer, _terrain, _time.TotalSeconds);
        if (_selected is not null)
            DrawPath(_selected);

        // Back to front by grid row, objects and settlers interleaved.
        float alpha = TickAlpha;
        int rows = _objects.RowCount(visible, out int firstRow);
        // Only what can be in view: on a big map the rows run far beyond the screen to both sides (16 players: some
        // thousand settlers, most of them off the screen, cost more than all that is seen).
        bool InRows(int y) => y >= firstRow && y < firstRow + rows;
        bool Across(GridPoint p, float left, float right)
        {
            float x = ScreenProjection.ToWorld(p).X;
            return x + right >= visible.X && x + left <= visible.X + visible.Width;
        }
        // The outermost strips of a building reach without end: as far as the widest picture (a castle, some 250 px).
        const float SpriteMargin = 96, ShipMargin = 160, BuildingReach = 320;
        var settlersByRow = _world.Settlers.Where(s => !s.IsInside && InRows(SettlerRenderer.DepthRow(s)) && Across(s.Position, -SpriteMargin, SpriteMargin))
            .ToLookup(SettlerRenderer.DepthRow);
        var buildingsByRow = _world.Buildings.Where(b => b.Exists && InRows(EconomyRenderer.DepthRow(b)) && Across(b.Position, -SpriteMargin, SpriteMargin))
            .ToLookup(EconomyRenderer.DepthRow);
        // Buildings in slices, each in the front row of the footprint below it (see EconomyRenderer.Strip).
        var stripsByRow = _world.Buildings.Where(b => b.Exists)
            .SelectMany(b => _economyRenderer.StripsOf(b.Definition).Select(strip => (Building: b, Strip: strip)))
            .Where(s => InRows(s.Building.Position.Y + s.Strip.Row) && Across(s.Building.Position,
                Math.Max(s.Strip.Left, -BuildingReach) - SpriteMargin, Math.Min(s.Strip.Right, BuildingReach) + SpriteMargin))
            .ToLookup(s => s.Building.Position.Y + s.Strip.Row);
        var pilesByRow = _world.Piles.Where(p => InRows(p.Position.Y) && Across(p.Position, -SpriteMargin, SpriteMargin)).ToLookup(p => p.Position.Y);
        var signsByRow = _world.Geology.Signs.Where(s => InRows(s.Key.Y)).ToLookup(s => s.Key.Y);
        var ghostsByRow = _world.Ghosts.Where(g => InRows(g.Position.Y)).ToLookup(g => g.Position.Y);
        var shipsByRow = _world.Shipping.Ships.Where(ship => InRows(ShipRenderer.DepthRow(ship)) && Across(ship.Position, -ShipMargin, ShipMargin))
            .ToLookup(ShipRenderer.DepthRow);
        var stocksByRow = _world.Buildings.Where(b => b.Exists && _world.Shipping.Building(b) is not null && _world.Shipping.DockOf(b) is not null)
            .ToLookup(b => _world.Shipping.DockOf(b)!.Value.Berth.Y);
        var marksByRow = WorkAreaMarksByRow();
        for (int y = firstRow; y < firstRow + rows; y++)
        {
            DrawBorderRow(y, visible);
            foreach (var (point, ring, tint) in marksByRow[y])
            {
                if (ring == GatheringFlag)
                    _economyRenderer.DrawGatheringFlag(_spriteBatch, point, _terrain, PlayerColors.Of(_selectedBuilding!.Player), _time);
                else
                    _economyRenderer.DrawWorkAreaMark(_spriteBatch, point, ring, _terrain, tint);
            }
            foreach (var pile in pilesByRow[y])
                _economyRenderer.DrawPile(_spriteBatch, pile, _terrain);
            foreach (var (point, sign) in signsByRow[y])
                _economyRenderer.DrawResourceSign(_spriteBatch, point, sign, _terrain);
            if (_showObjects)
                _objects.DrawRow(_spriteBatch, y, visible, _time);
            foreach (var (building, strip) in stripsByRow[y])
                _economyRenderer.DrawStrip(_spriteBatch, building, strip, _world.Military.Occupants(building), _settlerRenderer, _terrain,
                    PlayerColors.Of(building.Player), _world.Tick, alpha);
            foreach (var building in buildingsByRow[y])
            {
                var color = PlayerColors.Of(building.Player);
                _economyRenderer.DrawSiteMarks(_spriteBatch, building, _terrain, color);
                _economyRenderer.DrawFlag(_spriteBatch, building, _world.Production.HasWorker(building) || _world.Military.IsOccupied(building),
                    _terrain, color, _time);
            }
            foreach (var settler in settlersByRow[y])
            {
                // Under the fog, other players' settlers are seen only where player 1 sees now.
                if (FogOn && settler.Player != 0 && !_vision.IsVisible(settler.Position))
                    continue;
                if (settler.Profession == Profession.WarMachine)
                {
                    _machineRenderer.Draw(_spriteBatch, settler, _settlerRenderer.PeopleOf(settler), _terrain, _world.Tick, alpha,
                        PlayerColors.Of(settler.Player));
                    continue;
                }
                // Scouts in disguise look like one of player 1's carriers.
                _settlerRenderer.Draw(_spriteBatch, settler, _terrain, _world.Tick, alpha,
                    PlayerColors.Of(settler.IsRevealedTo(0) ? settler.Player : 0), disguised: !settler.IsRevealedTo(0));
            }
            foreach (var dockyard in stocksByRow[y])
            {
                var dock = _world.Shipping.DockOf(dockyard)!.Value;
                _shipRenderer.DrawOnStocks(_spriteBatch, dockyard.Definition.Civilization, _world.Shipping.Building(dockyard)!.Value, dock.ShipFacing,
                    ScreenProjection.ToWorld(dock.Berth.X, dock.Berth.Y, _terrain.HeightAt(dock.Berth.X, dock.Berth.Y)),
                    _world.Shipping.Progress(dockyard) ?? 0, PlayerColors.Of(dockyard.Player));
            }
            foreach (var ship in shipsByRow[y])
            {
                if (FogOn && ship.Player != 0 && !_vision.IsVisible(ship.Position))
                    continue;
                // The waves only lift the picture; the ship stays where it is.
                float lift = _waves.LiftAt(ShipRenderer.GridAt(ship, _world.Shipping.ShipStepTicks, alpha), (float)_time.TotalSeconds);
                _shipRenderer.Draw(_spriteBatch, ship, ShipRenderer.Anchor(ship, _terrain, _world.Shipping.ShipStepTicks, alpha) - new Vector2(0, lift),
                    PlayerColors.Of(ship.Player), _settlerRenderer, _economyRenderer, _world.Shipping.CargoOf(ship));
            }
            foreach (var ghost in ghostsByRow[y])
                if (!FogOn || _vision.IsVisible(ghost.Position))
                    _economyRenderer.DrawGhost(_spriteBatch, ghost.Position, (_world.Tick - ghost.Tick + alpha) / World.GhostTicks, _terrain,
                        PlayerColors.Of(ghost.Player));
        }

        foreach (var cloud in _world.DustClouds)
            _economyRenderer.DrawDustCloud(_spriteBatch, cloud.Position, (_world.Tick - cloud.Tick + alpha) / World.DustCloudTicks, _terrain);
        DrawArrows(alpha);
        if (FogOn)
        {
            // Over the world, under markers and previews: black, transparent where seen.
            _spriteBatch.End();
            _fogEffect.View = worldView;
            _fogEffect.Projection = worldProjection;
            GraphicsDevice.BlendState = BlendState.AlphaBlend;
            GraphicsDevice.RasterizerState = RasterizerState.CullNone;
            _fog!.Draw(_fogEffect, visible);
            _spriteBatch.Begin(samplerState: SamplerState.PointClamp, transformMatrix: worldView);
        }
        if (smooth)
        {
            // Scaled up linearly to the screen; markers and previews below stay sharp at screen resolution.
            _spriteBatch.End();
            GraphicsDevice.SetRenderTarget(target);
            GraphicsDevice.Clear(Color.Black);
            Effect? filter = null;
            if (_scaling is Scaling.CatmullRom or Scaling.Lanczos2)
            {
                filter = _upscale;
                filter.CurrentTechnique = filter.Techniques[_scaling.ToString()];
                filter.Parameters["TextureSize"].SetValue(new Vector2(_worldTarget!.Width, _worldTarget.Height));
            }
            _spriteBatch.Begin(samplerState: filter is null ? SamplerState.LinearClamp : SamplerState.PointClamp,
                blendState: BlendState.Opaque, effect: filter);
            _spriteBatch.Draw(_worldTarget, Vector2.Transform(origin, View), new Rectangle(Point.Zero, worldSize), Color.White, 0, Vector2.Zero, _zoom, SpriteEffects.None, 0);
            _spriteBatch.End();
            _spriteBatch.Begin(samplerState: SamplerState.PointClamp, transformMatrix: View);
        }

        if (_selectedBuilding is { Exists: true } selectedBuilding)
            DrawBuildingFrame(selectedBuilding);
        if (_selected is not null)
            DrawSelectionMarker(_selected, alpha);
        foreach (var settler in _group.Where(s => !s.IsDead))
            DrawSelectionMarker(settler, alpha);
        foreach (var ship in _ships)
            DrawSelectionMarker(ShipCenter(ship) - new Vector2(0, 34 - SelectionMarkerTop), 1);
        if (_dragStart is { } boxStart && !_placing)
            DrawSelectionBox(boxStart, Mouse.GetState().Position.ToVector2());
        DrawMagic(alpha);
        DrawEventArrows(alpha);
        if (_placing)
        {
            UpdateBuildSpots(visible);
            foreach (var (spot, share) in _buildSpots)
            {
                var position = ToWorld(spot);
                DrawDot((int)position.X - 2, (int)position.Y - 2, HealthColor(1 - share));
            }
            if (!SidePanel.Contains(Mouse.GetState().Position.ToVector2(), viewport.Height)
                && BuildSpotNear(Mouse.GetState().Position.ToVector2()) is { } at)
                _economyRenderer.DrawPreview(_spriteBatch, _placeable[_placeIndex], at, _terrain, PlayerColors.Of(0));
        }
        // Debug overlay on top of everything, so markers are not hidden behind trees.
        if (_showBlocked)
            for (int y = firstRow; y < firstRow + rows; y++)
                DrawBlockedRow(y, visible);
        _spriteBatch.End();

        // Side panel: minimap and blue panel scaled to the window height, the menus inside in panel space.
        _sidePanel.Draw(_spriteBatch, _pixel, _minimap?.Texture, _gameMap.Size, visible, MinimapDots());
        SidePanel.Begin(_spriteBatch, viewport.Height);
        var panelMouse = SidePanel.ToPanel(Mouse.GetState().Position.ToVector2(), viewport.Height);
        if (ShowsBuildingInfo)
            _buildingInfo.Draw(_spriteBatch, _pixel, SidePanel.ContentHeight, _font, _economyRenderer, _settlerRenderer, Own.Civilization, _world, _selectedBuilding!, 0,
                panelMouse);
        else if (ShowsSelection && _ships.Count > 0)
            _buildingInfo.DrawShips(_spriteBatch, _pixel, _font, _world, _ships, panelMouse);
        else if (ShowsSelection)
        {
            // Priests: their spell menu like the original; else the selected units and the combat strength.
            if (PriestsSelected)
                _spellMenu.Draw(_spriteBatch, _pixel, _font, Own.Civilization, _world.Magic.MenuOf(0), _world.Magic.Mana(0),
                    _aiming ? _aimSpell : null, panelMouse);
            else
                _selectionPanel.Draw(_spriteBatch, _font, _settlerRenderer, PortraitsOf, Own.Civilization, _group,
                    _world.Military.CombatStrength(0), BuildMenu.Content.Y, panelMouse);
        }
        else if (_tabs.Current == PanelTab.System)
            _systemMenu.Draw(_spriteBatch, _font, panelMouse);
        else if (_tabs.Current == PanelTab.Event)
            DrawEventPage();
        else if (_tabs.Current == PanelTab.None)
        {
            // Empty after a click into the landscape.
        }
        else if (_tabs.Current == PanelTab.Build)
            _buildMenu!.Draw(_spriteBatch, _pixel, _font, _placing ? _placeable[_placeIndex] : null, panelMouse, OwnBuildingCounts(), _now);
        else
        {
            string? menu = _tabs.CurrentMenu;
            _tabs.DrawContent(_spriteBatch, _font, placeholder: menu is not ("soldiers" or "overview" or "professions" or "statistics" or "stock" or "tools" or "distribution" or "transport"),
                title: menu != "tools");
            if (menu == "soldiers")
                _soldiersMenu!.Draw(_spriteBatch, _pixel, _font, _world, 0, _portraits!, _settlerRenderer, Own.Civilization, panelMouse);
            else if (menu == "overview")
                _settlersOverview!.Draw(_spriteBatch, _font, _world, 0, _portraits!, _settlerRenderer, Own.Civilization);
            else if (menu == "transport")
                _transportMenu!.Draw(_spriteBatch, _pixel, _world, 0, panelMouse, (float)gameTime.ElapsedGameTime.TotalSeconds);
            else if (menu == "distribution")
                _distributionMenu!.Draw(_spriteBatch, _pixel, _font, _world, 0, panelMouse);
            else if (menu == "tools")
                _manufactureMenu!.Draw(_spriteBatch, _pixel, _font, _world, 0, panelMouse);
            else if (menu == "stock")
                _stockMenu!.Draw(_spriteBatch, _pixel, _font, _world, 0, panelMouse);
            else if (menu == "statistics")
                _statisticsMenu!.Draw(_spriteBatch, _pixel, _font, _world, 0, panelMouse);
            else if (menu == "professions")
                _professionsMenu!.Draw(_spriteBatch, _pixel, _font, _world, 0, _portraits!, _settlerRenderer, Own.Civilization, panelMouse);
        }
        _tabs.DrawTabs(_spriteBatch);
        _eventButtons.Draw(_spriteBatch, _now);
        _spriteBatch.End();

        _spriteBatch.Begin(samplerState: SamplerState.PointClamp);
        _controlGroups.Draw(_spriteBatch, _pixel, viewport.Width, SidePanel.Scale(viewport.Height), _font, _settlerRenderer, PortraitsOf, _group,
            _ships, Mouse.GetState().Position.ToVector2());
        _cheats.Draw(_spriteBatch, _pixel, viewport.Width, _font, Mouse.GetState().Position.ToVector2());
        _buildMenu?.DrawTooltip(_spriteBatch, _pixel, _font, Mouse.GetState().Position.ToVector2(), viewport.Bounds,
            SidePanel.Scale(viewport.Height), _now);
        DrawOutcome(viewport);
        float infoLeft = SidePanel.Width * SidePanel.Scale(viewport.Height) + 12;
        _info.Draw(_spriteBatch, _pixel, _font, infoLeft, viewport.Height - 10, viewport.Width - infoLeft - 12,
            Math.Max(14, viewport.Height / 48), _now, _chat.IsOpen ? _chat.Text : null);
        // Bottom right, stacked upwards: performance (F3), then the game time (T).
        int bottom = viewport.Height;
        if (_showPerf)
            bottom = DrawCornerText(_perf.Summary, bottom);
        if (_showClock)
            bottom = DrawCornerText(GameTime(), bottom);
        if (gameTime.TotalGameTime < _scalingShownUntil)
            DrawCornerText($"Skalierung: {_scaling}", bottom);
        _spriteBatch.End();
        if (_loading is not null)
        {
            // The loading screen over the loaded game: covering it at first, then fading out.
            _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
            _loading.Draw(_spriteBatch, viewport.Bounds, overGame: true);
            _spriteBatch.End();
        }
        _perf.EndDraw(GraphicsDevice);

        if (target is not null)
            Capture(target);

        base.Draw(gameTime);
    }

    /// <summary>Height of the bow above a bowman's feet, and of the chest an arrow hits, in pixels.</summary>
    private const float BowHeight = 14;
    private const float HitHeight = 10;

    /// <summary>A text box at the bottom right, ending at <paramref name="bottom"/>; returns its top.</summary>
    /// <summary>A decided game: "Spiel gewonnen" or "Spiel verloren" in white at the top of the map, how to go on below.</summary>
    private void DrawOutcome(Viewport viewport)
    {
        var outcome = Outcome;
        if (_paused)
            DrawBanner(viewport, Lang.T("system.paused"), Lang.T("system.pausedHint"));
        else if (outcome != MissionOutcome.Open)
            DrawBanner(viewport, Lang.T(outcome == MissionOutcome.Won ? "campaign.won" : "campaign.lost"),
                _mission is not null ? Lang.T(outcome == MissionOutcome.Won ? "campaign.wonHint" : "campaign.lostHint")
                    : Lang.T(outcome == MissionOutcome.Won ? "system.wonHint" : "system.lostHint"));
    }

    /// <summary>White text with a shadow at the top of the game view, a smaller hint under it (game won/lost, pause).</summary>
    private void DrawBanner(Viewport viewport, string text, string hint)
    {
        float left = SidePanel.Width * SidePanel.Scale(viewport.Height);
        float center = left + (viewport.Width - left) / 2;
        int size = Math.Max(24, viewport.Height / 22);
        var measured = _font.Measure(text, size);
        var at = new Vector2(MathF.Round(center - measured.X / 2), MathF.Round(viewport.Height * 0.06f));
        _font.Draw(_spriteBatch, text, at + new Vector2(2, 2), Color.Black * 0.8f, size);
        _font.Draw(_spriteBatch, text, at, Color.White, size);
        int small = Math.Max(13, size / 2);
        var hintSize = _font.Measure(hint, small);
        var hintAt = new Vector2(MathF.Round(center - hintSize.X / 2), MathF.Round(at.Y + measured.Y + 6));
        _font.Draw(_spriteBatch, hint, hintAt + new Vector2(1, 1), Color.Black * 0.8f, small);
        _font.Draw(_spriteBatch, hint, hintAt, Color.White, small);
    }

    private int DrawCornerText(string text, int bottom)
    {
        var size = _font.Measure(text);
        int width = GraphicsDevice.Viewport.Width;
        _spriteBatch.Draw(_pixel, new Rectangle(width - (int)size.X - 12, bottom - (int)size.Y - 8, (int)size.X + 8, (int)size.Y + 4),
            Color.Black * 0.6f);
        _font.Draw(_spriteBatch, text, new Vector2(width - size.X - 8, bottom - size.Y - 6), Color.White);
        return bottom - (int)size.Y - 8;
    }

    /// <summary>How long the game has run in game time (fast forward counts in full), h:mm:ss.</summary>
    private string GameTime()
    {
        var time = TimeSpan.FromSeconds(_world.Tick / (double)World.TicksPerSecond);
        return $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}";
    }

    /// <summary>
    /// Arrows in flight, on top of everything: from the bow (on the tower or the field) towards the
    /// target, in a flat arc, a little higher the further they fly.
    /// </summary>
    private void DrawArrows(float alpha)
    {
        // Buildings hit by war machines smoke and burn; where a shot lands, a small explosion.
        float seconds = (float)_time.TotalSeconds;
        foreach (var building in _world.Buildings)
            if (building.Exists && _world.WarMachines.DamageOf(building) is > 0 and var damage && (!FogOn || _vision.IsVisible(building.Position)))
                _machineRenderer.DrawDamage(_spriteBatch, building, damage, _terrain, seconds);
        foreach (var impact in _world.WarMachines.Impacts)
            if (!FogOn || _vision.IsVisible(impact.Position))
                _machineRenderer.DrawImpact(_spriteBatch, impact, _terrain, (_world.Tick - impact.Tick + alpha) / World.TicksPerSecond);
        foreach (var shot in _world.WarMachines.Shots)
            if (!FogOn || _vision.IsVisible(shot.From) || _vision.IsVisible(shot.To))
                _machineRenderer.DrawShot(_spriteBatch, shot, _settlerRenderer.PeopleOf(shot.Machine), _terrain, _world.Tick, alpha);
        foreach (var arrow in _world.Military.Arrows)
        {
            var start = ScreenProjection.ToWorld(arrow.From.X, arrow.From.Y, _terrain.HeightAt(arrow.From.X, arrow.From.Y));
            if (arrow.Tower is { } tower && _world.Military.Occupants(tower) is var occupants
                && Enumerable.Range(0, occupants.Count).FirstOrDefault(i => occupants[i] == arrow.Shooter, -1) is >= 0 and var place)
                start = EconomyRenderer.OccupantAnchor(tower, place, _terrain);
            start.Y -= BowHeight;
            var end = arrow.Target is { IsDead: false, IsInside: false } target
                ? SettlerRenderer.Anchor(target, _terrain, alpha)
                : ScreenProjection.ToWorld(arrow.To.X, arrow.To.Y, _terrain.HeightAt(arrow.To.X, arrow.To.Y));
            end.Y -= HitHeight;

            float progress = Math.Clamp((_world.Tick - arrow.Tick + alpha) / arrow.FlightTicks, 0, 1);
            float arc = 2 + 0.06f * Vector2.Distance(start, end);
            var position = Vector2.Lerp(start, end, progress) - new Vector2(0, arc * 4 * progress * (1 - progress));
            // Six flight directions as on screen (SW, W, NW, NE, E, SE), angle measured with y down.
            var delta = end - start;
            float degrees = MathHelper.ToDegrees(MathF.Atan2(delta.Y, delta.X));
            int direction = degrees switch
            {
                >= 90 and < 150 => 0,
                >= 150 or < -150 => 1,
                >= -150 and < -90 => 2,
                >= -90 and < -30 => 3,
                >= -30 and < 30 => 4,
                _ => 5,
            };
            _economyRenderer.DrawArrow(_spriteBatch, position, direction, Math.Min(2, (int)(progress * 3)));
        }
    }

    /// <summary>
    /// Spells: the area while aiming (white where there is a fight, red elsewhere), a ring in the spell's
    /// color where one took effect, and a mark above frozen (blue) and hasted (orange) soldiers.
    /// </summary>
    private const float SummonFlameDelay = 0.2f;
    private const float SummonFlameSeconds = 1f;

    /// <summary>Where the dock would go for the point under the cursor (computed again when either changes).</summary>
    private (Building? Building, GridPoint At, Homestead.Simulation.Economy.Dock? Dock) _dockPreview;

    /// <summary>The chosen priests cast <see cref="_aimSpell"/> at the point.</summary>
    private void CastSpell(GridPoint aimedAt)
    {
        var cast = _world.Magic.Cast(_group, aimedAt, _aimSpell);
        if (cast == CastResult.Started)
            PlayCommand();
        _message = cast switch
        {
            CastResult.Started => $"casting {_aimSpell?.Name} at {aimedAt.X},{aimedAt.Y}",
            CastResult.NoMana => "not enough mana",
            CastResult.NoTarget => "nothing there this spell works on",
            CastResult.Unreachable => "no way there",
            CastResult.NoPriest => "all priests are busy",
            _ => "this people knows no spell",
        };
    }

    private void DrawMagic(float alpha)
    {
        // Setting a market's route point: a flag where the click would put it.
        if (_buildingInfo.AimsRoute(_selectedBuilding) && !SidePanel.Contains(Mouse.GetState().Position.ToVector2(), GraphicsDevice.Viewport.Height)
            && _world.Trade.Reachable(_selectedBuilding!, ScreenToGrid(Mouse.GetState().Position.ToVector2()),
                destination: _buildingInfo.RouteSlot == Simulation.Economy.Trade.Stops) is { } routePoint)
            _economyRenderer.DrawGatheringFlag(_spriteBatch, routePoint, _terrain, PlayerColors.Of(0), _time);
        // Setting a shipyard's dock: the pier, faint, where the click would put it.
        if (_buildingInfo.AimsDock(_selectedBuilding) && !SidePanel.Contains(Mouse.GetState().Position.ToVector2(), GraphicsDevice.Viewport.Height))
        {
            var under = ScreenToGrid(Mouse.GetState().Position.ToVector2());
            if (_dockPreview.Building != _selectedBuilding || _dockPreview.At != under)
                _dockPreview = (_selectedBuilding, under, _world.Shipping.DockSpot(_selectedBuilding!, under));
            if (_dockPreview.Dock is { } dock)
                foreach (var p in dock.Pier)
                    _objects.DrawCode(_spriteBatch, p.X, p.Y, MapObjectCodes.Dock, Color.White * 0.6f);
        }
        if (_aiming && !SidePanel.Contains(Mouse.GetState().Position.ToVector2(), GraphicsDevice.Viewport.Height))
        {
            var target = ScreenToGrid(Mouse.GetState().Position.ToVector2());
            if (_aimSpell is { } spell)
                DrawRing(target, Math.Max(1, spell.Radius > 0 ? spell.Radius : Homestead.Simulation.Magic.Magic.SummonSpread),
                    _world.Magic.HasTargets(spell, 0, target) ? Color.White : Color.Red);
        }
        foreach (var effect in _world.Magic.Effects)
        {
            if (effect.Kind is SpellKind.SummonGoods or SpellKind.ForestFire)
            {
                // A flame over each pile, one after the other.
                for (int i = 0; i < effect.Points.Count; i++)
                {
                    float seconds = (_world.Tick - effect.Tick + alpha) / World.TicksPerSecond - i * SummonFlameDelay;
                    if (seconds >= 0 && seconds < SummonFlameSeconds)
                        _economyRenderer.DrawFlame(_spriteBatch, effect.Points[i], seconds, _terrain);
                }
                continue;
            }
            float age = (_world.Tick - effect.Tick + alpha) / SpellEffect.ShowTicks;
            var color = effect.Kind switch
            {
                SpellKind.Convert or SpellKind.Strengthen => Color.Gold,
                SpellKind.Haste => Color.OrangeRed,
                SpellKind.Freeze => Color.DeepSkyBlue,
                SpellKind.Shield or SpellKind.CursedArrows => Color.LightGreen,
                _ => Color.MediumPurple,
            };
            DrawRing(effect.Target, Math.Max(1, (int)Math.Round(effect.Radius * Math.Min(1, age * 2))), color * (1 - age));
        }
        // Spells working on units: the original's status symbols over their heads (only those the player sees).
        foreach (var settler in _world.Settlers)
            if (!settler.IsInside && !settler.IsDead && (settler.Player == 0 || !FogOn || _vision.IsVisible(settler.Position)))
                _statusIcons.Draw(_spriteBatch, settler, _world.Tick, SettlerRenderer.Anchor(settler, _terrain, alpha));
    }

    /// <summary>Dots on the points at <paramref name="radius"/> from <paramref name="center"/>.</summary>
    private void DrawRing(GridPoint center, int radius, Color color)
    {
        foreach (var p in PathFinder.Circle(center, radius + 1))
        {
            if (!_gameMap.Contains(p) || PathFinder.InCircle(center, p, radius - 1) || !PathFinder.InCircle(center, p, radius))
                continue;
            var position = ToWorld(p);
            _spriteBatch.Draw(_pixel, new Rectangle((int)position.X - 2, (int)position.Y - 2, 4, 4), color);
        }
    }

    /// <summary>Dots along the remaining path of a settler.</summary>
    private void DrawPath(Settler settler)
    {
        var p = settler.StepTarget;
        foreach (var step in settler.RemainingPath)
        {
            p = p.Neighbor(step);
            var position = ToWorld(p);
            _spriteBatch.Draw(_pixel, new Rectangle((int)position.X - 1, (int)position.Y - 1, 3, 3), Color.Yellow * 0.8f);
        }
    }

    /// <summary>Outline of the selection box being dragged (screen points, drawn in world space).</summary>
    private void DrawSelectionBox(Vector2 from, Vector2 to)
    {
        if (Vector2.Distance(from, to) < DragThreshold)
            return;
        var inverse = Matrix.Invert(View);
        var a = Vector2.Transform(from, inverse);
        var b = Vector2.Transform(to, inverse);
        var min = Vector2.Min(a, b);
        var size = Vector2.Max(a, b) - min;
        float line = 1 / _zoom;
        var color = Color.White * 0.8f;
        _spriteBatch.Draw(_pixel, min, null, color, 0, Vector2.Zero, new Vector2(size.X, line), SpriteEffects.None, 0);
        _spriteBatch.Draw(_pixel, min + new Vector2(0, size.Y), null, color, 0, Vector2.Zero, new Vector2(size.X, line), SpriteEffects.None, 0);
        _spriteBatch.Draw(_pixel, min, null, color, 0, Vector2.Zero, new Vector2(line, size.Y), SpriteEffects.None, 0);
        _spriteBatch.Draw(_pixel, min + new Vector2(size.X, 0), null, color, 0, Vector2.Zero, new Vector2(line, size.Y), SpriteEffects.None, 0);
    }

    /// <summary>A small white frame around the selected building, drawn only at its corners.</summary>
    private void DrawBuildingFrame(Building building)
    {
        var box = _economyRenderer.BuildingBounds(building, _terrain);
        box.Inflate(2, 2);
        float line = 3f / _zoom;
        float arm = Math.Min(8, Math.Min(box.Width, box.Height) / 4f);
        var color = Color.White * 0.9f;
        foreach (var (corner, dx, dy) in new[]
                 {
                     (new Vector2(box.Left, box.Top), 1, 1), (new Vector2(box.Right, box.Top), -1, 1),
                     (new Vector2(box.Left, box.Bottom), 1, -1), (new Vector2(box.Right, box.Bottom), -1, -1),
                 })
        {
            // Horizontal and vertical arm, each pointing into the frame from the corner.
            var horizontal = dx > 0 ? corner : corner - new Vector2(arm, 0);
            var vertical = dy > 0 ? corner : corner - new Vector2(0, arm);
            if (dy < 0)
                horizontal.Y -= line;
            if (dx < 0)
                vertical.X -= line;
            _spriteBatch.Draw(_pixel, horizontal, null, color, 0, Vector2.Zero, new Vector2(arm, line), SpriteEffects.None, 0);
            _spriteBatch.Draw(_pixel, vertical, null, color, 0, Vector2.Zero, new Vector2(line, arm), SpriteEffects.None, 0);
        }
    }

    /// <summary>Shading of a build spot dot: L light rim, H highlight, B body, D shadow.</summary>
    private static readonly string[] BuildSpotShape = [".LLL.", "LHBBB", "BBBBB", "BBBBD", ".DDD."];

    /// <summary>
    /// The health dot of a selected settler as in the original: dark ring, K outline, L light rim,
    /// H highlight, B body, D shadow.
    /// </summary>
    private static readonly string[] HealthDotShape =
        ["..KKKK..", ".KLLHBK.", "KLHHBBBK", "KLHBBBBK", "KBBBBBDK", "KBBBBDDK", ".KBDDDK.", "..KKKK.."];

    /// <summary>The left bracket above a selected settler (W white); the right one is its mirror image.</summary>
    private static readonly string[] SelectionBracketShape =
        ["..WWWWW", ".WWWWWW", "W......", "W......", "W......", "W......", "W......", "W......", "W......", "W......"];

    /// <summary>How far above the anchor (the feet) the brackets begin, and their width.</summary>
    private const int SelectionMarkerTop = 49, SelectionMarkerWidth = 28;

    /// <summary>
    /// The mark above a selected settler as in the original: two white brackets (the top corners of a
    /// frame) above the head, between them a round dot green to red by health left.
    /// </summary>
    private void DrawSelectionMarker(Settler settler, float alpha)
    {
        var anchor = SettlerRenderer.Anchor(settler, _terrain, alpha);
        DrawSelectionMarker(anchor, HealthLeft(settler));
        // A war machine's shots left, a small number above the dot (as in the original); none for the gong.
        if (settler.Profession == Profession.WarMachine && _world.WarMachines.KindOf(settler) is { Shots: > 0 })
        {
            string shots = settler.Ammo.ToString();
            var size = _font.Measure(shots, 11);
            var at = new Vector2(anchor.X - size.X / 2, anchor.Y - SelectionMarkerTop - 1 - size.Y);
            _font.Draw(_spriteBatch, shots, at + Vector2.One, Color.Black, 11);
            _font.Draw(_spriteBatch, shots, at, Color.White, 11);
        }
    }

    private void DrawSelectionMarker(Vector2 anchor, float health)
    {
        int left = (int)anchor.X - SelectionMarkerWidth / 2, top = (int)anchor.Y - SelectionMarkerTop;
        for (int y = 0; y < SelectionBracketShape.Length; y++)
        for (int x = 0; x < SelectionBracketShape[y].Length; x++)
            if (SelectionBracketShape[y][x] == 'W')
            {
                _spriteBatch.Draw(_pixel, new Rectangle(left + x, top + y, 1, 1), Color.White);
                _spriteBatch.Draw(_pixel, new Rectangle(left + SelectionMarkerWidth - 1 - x, top + y, 1, 1), Color.White);
            }
        DrawDot((int)anchor.X - HealthDotShape[0].Length / 2, top - 1, HealthColor(health), HealthDotShape);
    }

    /// <summary>A small round dot of <paramref name="body"/> color, lit from above, its top left corner given (a build spot unless another shape is given).</summary>
    private void DrawDot(int left, int top, Color body, string[]? shape = null)
    {
        shape ??= BuildSpotShape;
        var light = Color.Lerp(body, Color.White, 0.35f);
        var highlight = Color.Lerp(body, Color.White, 0.75f);
        var shadow = Color.Lerp(body, Color.Black, 0.45f);
        for (int y = 0; y < shape.Length; y++)
        for (int x = 0; x < shape[y].Length; x++)
        {
            Color? color = shape[y][x] switch
            {
                'K' => Color.Black, 'L' => light, 'H' => highlight, 'B' => body, 'D' => shadow, _ => null,
            };
            if (color is { } c)
                _spriteBatch.Draw(_pixel, new Rectangle(left + x, top + y, 1, 1), c);
        }
    }

    /// <summary>How old a checked build spot may get before it is checked again.</summary>
    private const int BuildSpotTicks = World.TicksPerSecond;

    /// <summary>Time per frame for checking build spots: a whole view at once would make the game stutter.</summary>
    private static readonly TimeSpan BuildSpotBudget = TimeSpan.FromMilliseconds(6);

    /// <summary>
    /// Collects where the chosen building fits in view (<see cref="_buildSpots"/>). Points are checked
    /// within <see cref="BuildSpotBudget"/> per frame, unchecked ones first, and again once older than
    /// <see cref="BuildSpotTicks"/>; until then the last result counts.
    /// </summary>
    private void UpdateBuildSpots(RectangleF visible)
    {
        var definition = _placeable[_placeIndex];
        if (_buildSpotsFor != definition)
        {
            _buildSpotChecks.Clear();
            _buildSpotsFor = definition;
        }
        // Points stand higher on screen than flat by their height: look a few rows further down.
        int maxRows = 255 * ScreenProjection.HeightStep / ScreenProjection.RowHeight + 1;
        int firstRow = Math.Max(0, (int)(visible.Y / ScreenProjection.RowHeight) - 1);
        int lastRow = Math.Min(_gameMap.Size - 1, (int)((visible.Y + visible.Height) / ScreenProjection.RowHeight) + maxRows);
        var inView = new List<GridPoint>();
        for (int y = firstRow; y <= lastRow; y++)
        {
            int firstX = Math.Max(0, (int)((visible.X + y * ScreenProjection.TileWidth / 2f) / ScreenProjection.TileWidth) - 1);
            int lastX = Math.Min(_gameMap.Size - 1,
                (int)((visible.X + visible.Width + y * ScreenProjection.TileWidth / 2f) / ScreenProjection.TileWidth) + 1);
            for (int x = firstX; x <= lastX; x++)
            {
                var p = new GridPoint(x, y);
                var at = ToWorld(p);
                if (at.X >= visible.X && at.X <= visible.X + visible.Width && at.Y >= visible.Y && at.Y <= visible.Y + visible.Height)
                    inView.Add(p);
            }
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (var p in inView.Where(p => !_buildSpotChecks.ContainsKey(p))
                     .Concat(inView.Where(p => _buildSpotChecks.TryGetValue(p, out var c) && _world.Tick >= c.Tick + BuildSpotTicks))
                     .ToList())
        {
            if (clock.Elapsed > BuildSpotBudget)
                break;
            _buildSpotChecks[p] = (_world.Tick, _world.BuildSpotEffort(definition, p));
        }
        _buildSpots.Clear();
        foreach (var p in inView)
            if (_buildSpotChecks.TryGetValue(p, out var check) && check.Effort is { } effort)
                _buildSpots.Add((p, effort));
    }

    /// <summary>
    /// The spot the chosen building fits on nearest to the screen point: the grid point under it or a
    /// neighbor. Null if there is none.
    /// </summary>
    private GridPoint? BuildSpotNear(Vector2 screen)
    {
        var definition = _placeable[_placeIndex];
        var world = Vector2.Transform(screen, Matrix.Invert(View));
        var under = ScreenToGrid(screen);
        return Enumerable.Range(0, DirectionExtensions.Count).Select(d => under.Neighbor((Direction)d)).Prepend(under)
            .Where(p => _gameMap.Contains(p) && _world.CanPlace(definition, p, out _))
            .Select(p => (GridPoint?)p)
            .MinBy(p => Vector2.DistanceSquared(world, ToWorld(p!.Value)));
    }

    /// <summary>Health left as a fraction of the maximum; settlers that cannot be hurt count as healthy.</summary>
    private float HealthLeft(Settler settler)
    {
        float max = settler.SoldierType is { } type ? _world.MaxHealth(settler.Player, type, settler.Level)
            : settler.Profession == Profession.Priest ? _world.Magic.Config.PriestHealth
            : settler.Profession == Profession.WarMachine ? _world.WarMachines.KindOf(settler)?.Health ?? 0
            : 0;
        return max > 0 ? Math.Clamp(settler.Health / max, 0, 1) : 1;
    }

    /// <summary>Health colors from none to full, evenly spaced; in between they blend.</summary>
    private static readonly Color[] HealthColors =
    [
        new(170, 10, 10), new(225, 35, 20), new(240, 95, 20), new(245, 150, 25), new(240, 205, 30),
        new(195, 215, 35), new(130, 205, 40), new(60, 195, 45), new(30, 180, 50),
    ];

    /// <summary>Dark red at no health over orange and yellow to green at full.</summary>
    private static Color HealthColor(float left)
    {
        float at = Math.Clamp(left, 0, 1) * (HealthColors.Length - 1);
        int i = Math.Min((int)at, HealthColors.Length - 2);
        return Color.Lerp(HealthColors[i], HealthColors[i + 1], at - i);
    }

    /// <summary>Debug overlay: a red marker on every point settlers cannot walk on.</summary>
    private void DrawBlockedRow(int y, RectangleF visible)
    {
        int firstX = Math.Max(0, (int)((visible.X + y * ScreenProjection.TileWidth / 2f) / ScreenProjection.TileWidth) - 1);
        int lastX = Math.Min(_gameMap.Size - 1,
            (int)((visible.X + visible.Width + y * ScreenProjection.TileWidth / 2f) / ScreenProjection.TileWidth) + 1);
        for (int x = firstX; x <= lastX; x++)
        {
            var p = new GridPoint(x, y);
            if (_gameMap.IsWalkable(p))
                continue;
            var position = ToWorld(p);
            _spriteBatch.Draw(_pixel, new Rectangle((int)position.X - 2, (int)position.Y - 2, 4, 4), Color.Red * 0.7f);
        }
    }

    /// <summary>
    /// Headless check: set up the demo economy and three construction sites at the first player's
    /// start, run four minutes of game time (long enough for grain to ripen), save a frame every eight game seconds, then quit.
    /// </summary>
    /// <summary>
    /// HOMESTEAD_CAPTURE_GEOLOGISTS=1: four carriers become geologists and search the own mountain
    /// nearest to the start; the camera follows them.
    /// </summary>
    private void CaptureGeologists(int frame)
    {
        if (frame == 240)
        {
            var (now, later) = _world.Geology.Convert(_world.Settlers.Where(s => s.Player == 0 && s.IsCarrier && !s.IsInside).Take(4).ToList());
            Console.WriteLine($"geologists: {now}, {later} more after their delivery");
        }
        else if (frame == 600)
        {
            ClearSelection();
            _group.AddRange(_world.Settlers.Where(s => s.Player == 0 && s.Profession == Profession.Geologist));
            var mountain = Enumerable.Range(0, _gameMap.Size * _gameMap.Size).Select(i => new GridPoint(i % _gameMap.Size, i / _gameMap.Size))
                .Where(p => _world.Geology.IsSearchable(p) && _gameMap.OwnerAt(p) == 0)
                .OrderBy(p => Simulation.Pathfinding.PathFinder.Distance(p, _start)).FirstOrDefault();
            Console.WriteLine($"geologists: {_world.Geology.Send(_group, mountain)} sent to {mountain.X},{mountain.Y}");
            _camera = ToWorld(mountain);
            _selectedBuilding = null;
        }
    }

    /// <summary>
    /// HOMESTEAD_CAPTURE_TRADE=1: a market near the start with a storehouse as destination and a stop on the way, all planks
    /// and stones to send, three donkeys and a donkey farm; the market is selected (the route shows on the map).
    /// </summary>
    private void CaptureTrade(GridPoint start)
    {
        Building? Place(string name) => PathFinder.Circle(start, 30).OrderBy(p => PathFinder.Distance(start, p))
            .Where(p => _world.CanPlace(Own[name], p, out _)).Select(p => _world.PlaceBuilding(Own[name], 0, p)).FirstOrDefault(b => b is not null);
        var market = Place("market_place");
        Place("donkey_farm");
        var stock = _world.Buildings.Where(b => b.Player == 0 && b.Exists && b.Piles.Any(p => p.Role == Simulation.Economy.PileRole.Storage))
            .MaxBy(b => market is null ? 0 : PathFinder.Distance(b.Position, market.Position)) ?? Place("stock");
        if (market is null || stock is null)
        {
            Console.WriteLine("trade: no room for a market or no storehouse");
            return;
        }
        _world.Trade.SetDestination(market, stock.Door);
        // Three stops in a wide loop, so the colored legs show.
        int stop = 0;
        foreach (var offset in new[] { new GridPoint(12, 0), new GridPoint(14, 12), new GridPoint(2, 16) })
            if (stop < Simulation.Economy.Trade.Stops && _world.Trade.SetStop(market, stop, new GridPoint(market.Door.X + offset.X, market.Door.Y + offset.Y)))
                stop++;
        _world.Trade.SetRemaining(market, Simulation.Economy.Material.Plank, Simulation.Economy.Trade.Unlimited);
        _world.Trade.SetRemaining(market, Simulation.Economy.Material.Stone, 12);
        for (int i = 0; i < 3; i++)
            _world.Spawn(market.Door, Profession.Donkey);
        Console.WriteLine($"trade: market at {market.Position}, destination {stock.Definition.Name} at {stock.Position}");
    }

    /// <summary>
    /// HOMESTEAD_CAPTURE_MACHINES=1: the own people's war machine works near the start (two loads of ammunition, a machine
    /// ordered, its material lying by), an enemy tower with a soldier and one war machine of every people firing at it, selected;
    /// the camera on them.
    /// </summary>
    private void CaptureMachines(GridPoint start)
    {
        var kind = _world.WarMachines.KindOf(_world.PeopleOf(0));
        var works = kind is null ? null : PathFinder.Circle(start, 30).OrderBy(p => PathFinder.Distance(start, p))
            .Where(p => _world.CanPlace(Own[kind.Works], p, out _)).Select(p => _world.PlaceBuilding(Own[kind.Works], 0, p)).FirstOrDefault(b => b is not null);
        if (works is not null)
        {
            if (kind!.Shots > 0)
                for (int i = 0; i < 2; i++)
                    _world.WarMachines.Finish(works, Simulation.Military.WarWork.Ammo);
            _world.WarMachines.Order(works, Simulation.Military.WarWork.Machine);
            var spots = PathFinder.Circle(works.Door, 8).OrderBy(p => PathFinder.Distance(works.Door, p)).Skip(20).GetEnumerator();
            foreach (var (material, count) in kind.Cost)
                while (spots.MoveNext() && _world.AddLooseGoods(spots.Current, material, count) is null) { }
        }
        // An enemy tower out of the own land, the machines on the way to it.
        var definition = Own["tower"];
        var tower = PathFinder.Circle(start, 40).Where(p => PathFinder.Distance(start, p) >= 30).OrderBy(p => PathFinder.Distance(start, p))
            .Where(p => definition.Blocked.Concat(definition.Protected).All(o => _world.Map!.Contains(p + o) && _world.Map.IsWalkable(p + o)))
            .Select(p => _world.PlaceBuilding(definition, 1, p, force: true)).FirstOrDefault(b => b is not null);
        if (tower is null)
        {
            Console.WriteLine("machines: no room for an enemy tower");
            return;
        }
        _world.SpawnSoldier(tower.Door, SoldierType.Swordsman, player: 1);
        var near = new GridPoint((start.X + 2 * tower.Position.X) / 3, (start.Y + 2 * tower.Position.Y) / 3);
        foreach (var people in Enum.GetValues<Civilization>())
            if (_world.WarMachines.Spawn(new GridPoint(near.X + 5 * (int)people - 8, near.Y + 3), 0, people) is { } machine)
                _group.Add(machine);
        _world.WarMachines.Attack(_group, tower);
        _controlGroups.Store(2, _group);
        _camera = ToWorld(near);
        Console.WriteLine($"machines: works {works?.Position}, enemy tower at {tower.Position}, {_group.Count} machines at {near}");
    }

    private void CaptureDock(GridPoint start, string mode)
    {
        var definition = Own["dockyard"];
        bool Water(GridPoint p) => _gameMap.Contains(p) && _gameMap.TerrainAt(p) == TerrainType.Water;
        bool Sea(GridPoint p) => _gameMap.Contains(p) && PathFinder.Circle(p, 3).All(Water);
        // The open water nearest the start (the land around it made own), then the place for a shipyard nearest it.
        var sea = PathFinder.Circle(start, 120).Where(Sea).OrderBy(p => PathFinder.Distance(start, p)).Cast<GridPoint?>().FirstOrDefault();
        if (sea is { } coast)
            foreach (var p in PathFinder.Circle(coast, 34).Where(_gameMap.Contains))
                _gameMap.SetOwner(p, 0);
        var spot = sea is not { } shore ? null : PathFinder.Circle(shore, 24).Where(_gameMap.Contains).OrderBy(p => PathFinder.Distance(shore, p))
            .Where(p => _world.CanPlace(definition, p, out _)).Cast<GridPoint?>().FirstOrDefault();
        if (spot is not { } at || _world.PlaceBuilding(definition, 0, at) is not { } dockyard)
        {
            Console.WriteLine($"dock: no shipyard near the coast ({sea})");
            return;
        }
        var water = PathFinder.Circle(at, 12).Where(Sea).OrderBy(p => PathFinder.Distance(at, p)).First();
        bool placed = mode != "none" && _world.Shipping.PlaceDock(dockyard, water);
        Console.WriteLine($"dock: shipyard at {at}, dock {(placed ? _world.Shipping.DockOf(dockyard).ToString() : "none")}");
        _camera = ToWorld(at);
        if (!placed || mode is not ("build" or "ships" or "ferry" or "harbor"))
            return;
        var near = PathFinder.Circle(dockyard.Door, 6).Where(p => _gameMap.IsWalkable(p) && _world.IsFreeGround(p)).ToList();
        _world.AddLooseGoods(near[0], Simulation.Economy.Material.Plank, 8);
        _world.AddLooseGoods(near[^1], Simulation.Economy.Material.Iron, 4);
        for (int i = 0; i < 3; i++)
            _world.Spawn(near[i * near.Count / 3], Profession.Carrier);
        if (mode == "build")
        {
            _world.Shipping.Order(dockyard, Simulation.Economy.ShipKind.Ferry);
            return;
        }
        if (mode == "harbor")
        {
            // A harbor next to it with its dock, a trade ship, goods to send and a destination along the coast; the harbor selected.
            var harborSpot = PathFinder.Circle(at, 20).Where(_gameMap.Contains).OrderBy(p => PathFinder.Distance(at, p))
                .Where(p => _world.CanPlace(Own["harbor"], p, out _)).Cast<GridPoint?>().FirstOrDefault();
            if (harborSpot is not { } h || _world.PlaceBuilding(Own["harbor"], 0, h) is not { } harbor
                || !_world.Shipping.PlaceDock(harbor, PathFinder.Circle(h, 14).Where(Sea).OrderBy(p => PathFinder.Distance(h, p)).First()))
            {
                Console.WriteLine("dock: no harbor near the coast");
                return;
            }
            _world.Shipping.Launch(dockyard, Simulation.Economy.ShipKind.TradeShip);
            var beach = PathFinder.Circle(water, 40).Where(p => PathFinder.Distance(water, p) >= 25)
                .FirstOrDefault(p => _world.Trade.SetDestination(harbor, p));
            _world.AddLooseGoods(near[near.Count / 2], Simulation.Economy.Material.Stone, 8);
            _world.AddLooseGoods(near[near.Count / 3], Simulation.Economy.Material.Coal, 6);
            _world.Trade.SetRemaining(harbor, Simulation.Economy.Material.Plank, 8);
            _world.Trade.SetRemaining(harbor, Simulation.Economy.Material.Stone, 8);
            _world.Trade.SetRemaining(harbor, Simulation.Economy.Material.Coal, 6);
            _selectedBuilding = harbor;
            _camera = ToWorld(_world.Shipping.DockOf(harbor)!.Value.Berth);
            Console.WriteLine($"dock: harbor at {h}, dock {_world.Shipping.DockOf(harbor)?.End}, destination {_world.Trade.DestinationOf(harbor)}");
            return;
        }
        if (mode == "ferry")
        {
            // A ferry at the dock, seven soldiers of all types (and one too many) going aboard; the ferry selected.
            _world.Shipping.Launch(dockyard, Simulation.Economy.ShipKind.Ferry);
            var ferry = _world.Shipping.Ships[^1];
            var units = Enumerable.Range(0, 8)
                .Select(i => _world.SpawnSoldier(near[(i * 5) % near.Count], (SoldierType)(i % 3), 1 + i % 3)).OfType<Settler>().ToList();
            int boarding = _world.Shipping.Board(units, ferry);
            ClearSelection();
            _ships.Add(ferry);
            _camera = ToWorld(ferry.Position);
            Console.WriteLine($"dock: ferry at {ferry.Position}, {boarding} of {units.Count} soldiers going aboard");
            return;
        }
        // Launched at once.
        _world.Shipping.Launch(dockyard, Simulation.Economy.ShipKind.Ferry);
        _world.Shipping.Launch(dockyard, Simulation.Economy.ShipKind.TradeShip);
        ClearSelection();
        _ships.AddRange(_world.Shipping.Ships);
        _controlGroups.Store(3, [], _ships);
        _world.Shipping.Move(_ships, water + new GridPoint(10, 10));
        Console.WriteLine($"dock: ships {string.Join(", ", _world.Shipping.Ships.Select(s => $"{s.Kind} at {s.Position} to {s.Destination}"))}");
    }

    /// <summary>
    /// HOMESTEAD_CAPTURE_PIONEERS=1: two pioneers appear at the edge of the own land nearest to the start,
    /// selected („Land einnehmen“ shows), and take land there; the camera looks at them.
    /// </summary>
    private void CapturePioneers(int frame)
    {
        if (frame != 60)
            return;
        var edge = Enumerable.Range(0, _gameMap.Size * _gameMap.Size).Select(i => new GridPoint(i % _gameMap.Size, i / _gameMap.Size))
            .Where(p => _gameMap.OwnerAt(p) == 0 && _gameMap.IsWalkable(p)
                        && Enumerable.Range(0, 6).Any(d => _gameMap.Contains(p.Neighbor((Direction)d)) && _gameMap.OwnerAt(p.Neighbor((Direction)d)) is null))
            .OrderBy(p => Simulation.Pathfinding.PathFinder.Distance(p, _start)).FirstOrDefault();
        ClearSelection();
        for (int i = 0; i < 2; i++)
            if (_world.Spawn(edge, Profession.Pioneer) is { } pioneer)
                _group.Add(pioneer);
        _selectedBuilding = null;
        Console.WriteLine($"pioneers: {_world.Pioneering.Claim(_group)} take land at {edge.X},{edge.Y}");
        _camera = ToWorld(edge);
    }

    /// <summary>
    /// HOMESTEAD_CAPTURE_MAGIC=1: a big temple and the own people's mana chain next to the start, mana,
    /// a fight nearby, and the priest casts its spell there; =summon: no fight, the priest summons goods there;
    /// =&lt;spell kind&gt; (e.g. Desertify, GrowForest; one of the own people's): that spell there, with a fight.
    /// </summary>
    private void CaptureMagic(int frame)
    {
        var battle = _world.Buildings.FirstOrDefault(b => b.Definition.Name == "big_temple") is { } temple
            ? temple.Position + new GridPoint(8, 6)
            : new GridPoint(_start.X + 16, _start.Y + 8);
        // Spells on the land: the most open grass nearby, to see what they do.
        if (frame >= 200 && CaptureSpell is SpellKind.Desertify or SpellKind.GrowForest or SpellKind.StoneCurse or SpellKind.ForestFire)
            battle = _captureLand ??= PathFinder.Circle(battle, 24).Where(p => (p.X + p.Y) % 3 == 0 && _gameMap.Contains(p))
                .MaxBy(p => PathFinder.Circle(p, 8).Count(q => _gameMap.Contains(q) && _world.IsFreeGround(q)
                    && _gameMap.TerrainAt(q) == TerrainType.Grass && (CaptureSpell == SpellKind.ForestFire) == MapObjectCodes.IsAdultTree(_gameMap.ObjectCodeAt(q))));
        if (frame == 1)
        {
            // Before the demo economy takes the room.
            var chain = Own.Civilization switch
            {
                Civilization.Roman => new[] { "big_temple", "winegrower", "temple" },
                Civilization.Asian => ["big_temple", "rice_farm", "distillery", "temple"],
                Civilization.Egyptian => ["big_temple", "brewery", "temple"],
                _ => ["big_temple", "beekeeping", "mead_brewery", "temple"],
            };
            for (int i = 0; i < chain.Length; i++)
                Console.WriteLine($"magic: {chain[i]} " + (DemoEconomy.PlaceNear(_world, Own[chain[i]], new GridPoint(_start.X - 12 + i * 8, _start.Y - 12), _random) is { } b
                    ? $"at {b.Position.X},{b.Position.Y}" : "does not fit"));
            _world.Magic.AddMana(0, 100);
        }
        else if (frame == 200)
        {
            _camera = ToWorld(battle);
            if (CaptureSpell is SpellKind.SummonGoods)
                return;
            for (int i = 0; i < 10; i++)
            {
                _world.SpawnSoldier(new GridPoint(battle.X - 1, battle.Y - 5 + i), SoldierType.Swordsman, level: 3);
                _world.SpawnSoldier(new GridPoint(battle.X + 1, battle.Y - 5 + i), SoldierType.Swordsman, level: 3, player: 1);
            }
            _camera = ToWorld(battle);
        }
        else if (frame == 240)
        {
            ClearSelection();
            _group.AddRange(_world.Settlers.Where(s => s.Player == 0 && s.Profession == Profession.Priest));
            var spell = CaptureSpell is { } kind ? _world.Magic.SpellsOf(0).FirstOrDefault(s => s.Kind == kind) : null;
            Console.WriteLine($"magic: {_group.Count} priests, mana {_world.Magic.Mana(0)}, cast: {_world.Magic.Cast(_group, battle, spell)}");
        }
        else if (frame == 400 && CaptureSpell is not null)
        {
            _camera = ToWorld(battle);
            Console.WriteLine("magic: ground around the target: " + string.Join(", ", PathFinder.Circle(battle, 10).Where(_gameMap.Contains)
                .GroupBy(_gameMap.TerrainAt).Select(g => $"{g.Key} {g.Count()}")) + $" at {battle.X},{battle.Y}");
            Console.WriteLine("magic: drawn as: " + string.Join(", ", PathFinder.Circle(battle, 10).Where(_gameMap.Contains)
                .GroupBy(p => _terrain.KindAt(p.X, p.Y)).Select(g => $"{g.Key} {g.Count()}")));
        }
    }

    private GridPoint? _captureLand;

    /// <summary>The spell a capture run casts (null: the people's own).</summary>
    private static SpellKind? CaptureSpell => Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_MAGIC") switch
    {
        "summon" => SpellKind.SummonGoods,
        var name when Enum.TryParse<SpellKind>(name, out var kind) => kind,
        _ => null,
    };

    /// <summary>The capture camera sweeps over the map (benchmark of a long game, see <see cref="StartBenchmark"/>).</summary>
    private bool _panning;

    /// <summary>
    /// HOMESTEAD_CAPTURE_MINUTES=<n>: the AI plays for player 1 too and the game runs n minutes ahead without drawing;
    /// then the camera sweeps fast over the whole map, as when scrolling around in a long game.
    /// </summary>
    private void StartBenchmark(int minutes)
    {
        _zoom = float.TryParse(Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_ZOOM"),
            System.Globalization.CultureInfo.InvariantCulture, out float zoom) ? zoom : 1;
        _ownAi = NewAi(0);
        _world.AiPlayers.Add(_ownAi);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < minutes * 60 * World.TicksPerSecond; i++)
            _world.Step();
        // HOMESTEAD_CAPTURE_VIEW=battle: plays on (up to 20 more minutes) until a big fight is going on.
        if (CaptureView == "battle")
            for (int i = 0; i < 20 * 60 * World.TicksPerSecond && BiggestFight() is not (_, >= 20); i++)
                _world.Step();
        _vision.Update(_world);
        _fog!.SetTargets(_vision);
        _minimap!.Update(_vision, all: !FogOn, _gameMap, IsAlly);
        Console.WriteLine($"benchmark: {_world.Tick / World.TicksPerSecond / 60} min played in {clock.Elapsed.TotalSeconds:F0} s, {_world.Settlers.Count} settlers, " +
                          $"{_world.Buildings.Count(b => b.Exists)} buildings");
        // HOMESTEAD_CAPTURE_VIEW=economy: the camera stays on player 1's home; battle: it follows the biggest fight.
        if (CaptureView == "economy")
            _camera = ToWorld(_world.Buildings.FirstOrDefault(b => b.Exists && b.Player == 0)?.Position ?? _start);
        else if (CaptureView == "battle")
            FollowFight();
        else
            _panning = true;
    }

    private static string? CaptureView => Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_VIEW");

    /// <summary>The fighting soldier with the most soldiers (of any player, also waiting or coming) within 10 fields, and how many those are.</summary>
    private (GridPoint Center, int Fighters)? BiggestFight()
    {
        var soldiers = _world.Settlers.Where(s => !s.IsDead && !s.IsInside && s.Profession == Profession.Soldier).ToList();
        var fighters = soldiers.Where(s => s.Activity is Activity.Fighting or Activity.Shooting).Select(s => s.Position).ToList();
        return fighters.Count == 0 ? null
            : fighters.Select(p => (p, soldiers.Count(s => PathFinder.Distance(p, s.Position) <= 10))).MaxBy(f => f.Item2);
    }

    /// <summary>Moves the capture camera smoothly towards the biggest fight, if there is one.</summary>
    private void FollowFight()
    {
        if (BiggestFight() is var (center, _))
            _camera = Vector2.Lerp(_camera, ToWorld(center), _capturedFrames <= 1 ? 1 : 0.05f);
    }

    /// <summary>A Lissajous sweep over the map at up to about 1000 world pixels per second (60 frames per second).</summary>
    private void PanCamera()
    {
        float t = _capturedFrames / 60f;
        float width = _gameMap.Size * ScreenProjection.TileWidth, height = _gameMap.Size * ScreenProjection.RowHeight;
        // The map is a parallelogram on screen: rows shift left by half a tile each.
        float y = height * (0.5f + 0.4f * MathF.Sin(t * 0.8f));
        float x = width * (0.5f + 0.35f * MathF.Sin(t * 0.5f)) - y / ScreenProjection.RowHeight * ScreenProjection.TileWidth / 2f;
        _camera = new Vector2(x, y);
    }

    /// <summary>HOMESTEAD_CAPTURE_SIZE=&lt;w&gt;x&lt;h&gt;: picture size (default 960x600), e.g. 1920x1080 to measure drawing at full screen.</summary>
    private RenderTarget2D NewCaptureTarget()
    {
        var size = Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_SIZE")?.Split('x');
        return size is [var w, var h] && int.TryParse(w, out int width) && int.TryParse(h, out int height)
            ? new RenderTarget2D(GraphicsDevice, width, height)
            : new RenderTarget2D(GraphicsDevice, 960, 600);
    }

    private void Capture(RenderTarget2D target)
    {
        GraphicsDevice.SetRenderTarget(null);
        // HOMESTEAD_CAPTURE_STRIKE=1: the focused building comes down (dust), carriers without room strike.
        bool strike = Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_STRIKE") == "1";
        bool magic = Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_MAGIC") is "1" || CaptureSpell is not null;
        // HOMESTEAD_CAPTURE_EVERY=<n>: a picture every n frames instead of every 120 (arrows in flight).
        int every = int.TryParse(Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_EVERY"), out int n) && n > 0 ? n : 120;
        if (_capturedFrames % every == every - 1 || strike && _capturedFrames is 1202 or 1206
            || magic && _world.Magic.Effects.Count > 0 && _capturedFrames % 6 == 0)
        {
            // While panning, pictures are rare: saving one takes longer than a frame and would show as the longest.
            if (!_panning || _capturedFrames % 1200 == 1199)
            {
                using var stream = File.Create(Path.Combine(_captureDirectory!, $"{_map.Name}_{_capturedFrames:D4}.png"));
                target.SaveAsPng(stream, target.Width, target.Height);
            }
            Console.WriteLine($"frame {_capturedFrames}: {_world.Settlers.Count} settlers · {_world.Military.Arrows.Count} arrows · {_perf.Summary} · {_sprites.PageCount} atlas pages"
                              + (CaptureView == "battle" ? $" · fight of {BiggestFight()?.Fighters ?? 0}" : ""));
        }
        target.Dispose();

        if (Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_GEOLOGISTS") == "1")
            CaptureGeologists(_capturedFrames + 1);
        if (Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_PIONEERS") == "1")
            CapturePioneers(_capturedFrames + 1);
        if (magic)
            CaptureMagic(_capturedFrames + 1);
        if (_panning)
            PanCamera();
        else if (_capturedFrames > 1 && CaptureView == "battle")
            FollowFight();
        // HOMESTEAD_CAPTURE_CHAT=<text>: players 1-3 say something (player 2 a line long enough to wrap), then the chat line is open with the text typed.
        if (_capturedFrames == 0 && Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_CHAT") is { } chat)
        {
            _world.Say(0, "Hello everybody");
            _world.Say(1, "Hi! Long time no see. Your towers at the river look rather lonely, I might drop by with a few friends before the winter comes.");
            _world.Say(2, "gl hf");
            _chat.Open();
            _chat.Paste(chat);
        }
        // HOMESTEAD_CAPTURE_SAVE=<slot>: saves the game into that slot (1-8) after 300 frames.
        if (_capturedFrames == 300 && int.TryParse(Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_SAVE"), out int saveSlot))
        {
            SaveGameTo(saveSlot - 1);
            Console.WriteLine(_message);
        }
        // HOMESTEAD_CAPTURE_STATS=<frame>: the statistics screen opens at that frame (its pages are saved, then the run ends).
        if (int.TryParse(Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_STATS"), out int statsFrame) && _capturedFrames + 1 == statsFrame)
            OpenStatistics(canResume: true);
        switch (++_capturedFrames)
        {
            case 1 when int.TryParse(Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_MINUTES"), out int minutes) && minutes > 0:
                StartBenchmark(minutes);
                break;
            case 1:
                // HOMESTEAD_CAPTURE_ZOOM: closer look at details (flags, marks) in a capture run.
                _zoom = float.TryParse(Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_ZOOM"),
                    System.Globalization.CultureInfo.InvariantCulture, out float zoom) ? zoom : 1;
                // HOMESTEAD_CAPTURE_SCALING=Sharp|Linear|CatmullRom|Lanczos2: the upscaling filter (F2).
                if (Enum.TryParse(Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_SCALING"), true, out Scaling scaling))
                    _scaling = scaling;
                var start = _start;
                Console.WriteLine(DemoEconomy.Create(_world, Own, start, _random));
                // HOMESTEAD_CAPTURE_FOCUS=temple: a small temple with mana, swordsmen upgraded once (upgrade buttons).
                if (Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_FOCUS") == "temple"
                    && PathFinder.Circle(start, 30).OrderBy(p => PathFinder.Distance(start, p))
                        .Any(p => _world.CanPlace(Own["temple"], p, out _) && _world.PlaceBuilding(Own["temple"], 0, p) is not null))
                {
                    _world.Magic.AddMana(0, 30);
                    _world.Magic.Upgrade(0, SoldierType.Swordsman);
                }
                // HOMESTEAD_CAPTURE_FOCUS=castle (or another military building): one with all its places wanted and soldiers to fill them.
                if (Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_FOCUS") is { } focus && Own.Find(focus) is { Occupiers.Count: > 0 } fort
                    && !_world.Buildings.Any(b => b.Definition == fort)
                    && PathFinder.Circle(start, 45).OrderBy(p => PathFinder.Distance(start, p))
                        .Where(p => fort.Blocked.Concat(fort.Protected).All(o => _world.Map!.IsWalkable(p + o)) && !_world.CanPlace(fort, p, out var why)
                                    && why == "not on own land")
                        .Select(p => _world.PlaceBuilding(fort, 0, p, force: true)).FirstOrDefault() is { } placed)
                {
                    foreach (var type in new[] { SoldierType.Bowman, SoldierType.Swordsman })
                        while (_world.Military.ChangeWanted(placed, type, 1)) { }
                    for (int i = 0; i < fort.Occupiers.Count; i++)
                        _world.SpawnSoldier(placed.Door, i % 2 == 0 ? SoldierType.Bowman : SoldierType.Swordsman);
                }
                // HOMESTEAD_CAPTURE_HIVES=1: four beehives (one per stage) on free ground next to the start, in view.
                if (Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_HIVES") == "1")
                {
                    var spots = PathFinder.Circle(start, 12).Where(p => _world.Map!.Contains(p) && _world.Map.IsWalkable(p)
                            && _world.Map.ObjectCodeAt(p) == MapObjectCodes.None && _world.IsFreeGround(p))
                        .OrderBy(p => PathFinder.Distance(start, p)).Where((_, i) => i % 3 == 0).Take(4).ToList();
                    for (int i = 0; i < spots.Count; i++)
                        _world.Map!.SetObjectCode(spots[i], (byte)(MapObjectCodes.FirstHive + i));
                    if (spots.Count > 0)
                        _camera = ToWorld(spots[0]);
                }
                // HOMESTEAD_CAPTURE_TRADE=1: a market sending all planks and stones by donkey over a stop to a storehouse (focused).
                if (Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_TRADE") == "1")
                    CaptureTrade(start);
                // HOMESTEAD_CAPTURE_MACHINES=1: war machines of every people fire at an enemy tower; the own works with loads (FOCUS=<works>).
                if (Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_MACHINES") == "1")
                    CaptureMachines(start);
                // HOMESTEAD_CAPTURE_DOCK=1: a shipyard at the own coast nearest the start with its dock (with FOCUS=dockyard); =none: without one;
                // =build: building a ferry, its material lying next to it; =ships: a ferry and a trade ship launched, both selected, sent out.
                if (Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_DOCK") is { } withDock)
                    CaptureDock(start, withDock);
                _selectedBuilding = _world.Buildings.FirstOrDefault(b => b.Definition.Name == (Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_FOCUS") ?? "lumberjack"));
                if (Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_TAB") is not null)
                    _selectedBuilding = null; // the tab's menu, not the building
                // HOMESTEAD_CAPTURE_STORE=<good>: the focused storehouse takes only that good.
                if (Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_STORE") is { } storeOnly && _selectedBuilding is { } store)
                    _world.Logistics.SetSpecialization(store, Homestead.Simulation.Economy.Materials.Parse(storeOnly.ToUpperInvariant()));
                if (_captureManualRecruitment)
                {
                    _world.Military.SetManualRecruitment(0, true);
                    _world.Military.ChangePlanned(0, SoldierType.Bowman, 3);
                }
                // HOMESTEAD_CAPTURE_BATTLE=1: enemy soldiers attack the focused military building, else the start tower.
                if (Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_BATTLE") == "1"
                    && (_selectedBuilding is { Player: 0, Definition.Occupiers.Count: > 0 } focused ? focused
                        : _world.Buildings.FirstOrDefault(b => b.Player == 0 && b.Definition.Occupiers.Count > 0)) is { } tower)
                {
                    foreach (var type in Enum.GetValues<SoldierType>())
                        for (int i = 0; i < 3; i++)
                            _world.SpawnSoldier(new GridPoint(tower.Door.X + 6 + i, tower.Door.Y + 6), type, level: 3, player: 1);
                    _world.Military.Attack(1, tower);
                }
                // HOMESTEAD_CAPTURE_BATTLE=big: two armies of 30 meet south of the start (zoomed in: the camera on them).
                GridPoint? battleMiddle = null;
                if (Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_BATTLE") == "big")
                {
                    var armies = new[] { new List<Settler>(), new List<Settler>() };
                    for (int side = 0; side < 2; side++)
                        for (int i = 0; i < 30; i++)
                        {
                            var at = new GridPoint(start.X + side * 22 + i % 6, start.Y + 14 + i / 6);
                            if (_world.SpawnSoldier(at, (SoldierType)(i % 3), level: 1 + i % 3, player: side) is { } soldier)
                                armies[side].Add(soldier);
                        }
                    var middle = new GridPoint(start.X + 14, start.Y + 16);
                    _world.Military.Move(armies[0], middle);
                    _world.Military.Move(armies[1], middle);
                    battleMiddle = middle;
                }
                // HOMESTEAD_CAPTURE_BATTLE=town[:n]: Asians and Amazons of n each (default 90) clash in the middle of the village.
                if (Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_BATTLE")?.Split(':') is ["town", ..] townBattle)
                {
                    int count = townBattle.Length > 1 && int.TryParse(townBattle[1], out int townCount) ? townCount : 90;
                    var armies = new[] { new List<Settler>(), new List<Settler>() };
                    for (int side = 0; side < 2; side++)
                        for (int i = 0; i < count; i++)
                        {
                            var at = new GridPoint(start.X + (side == 0 ? -34 : 24) + i % 10, start.Y + 2 + i / 10);
                            if (_world.SpawnSoldier(at, (SoldierType)(i % 3), level: 1 + i % 3, player: side) is { } soldier)
                                armies[side].Add(soldier);
                        }
                    var middle = new GridPoint(start.X + 4, start.Y + 6);
                    _world.Military.Move(armies[0], middle);
                    _world.Military.Move(armies[1], middle);
                    battleMiddle = middle;
                    _selectedBuilding = null; // no selection marks in the picture
                }
                // HOMESTEAD_CAPTURE_BATTLE=open: Asians and Amazons of 40 each meet on the nearest open ground outside the village.
                if (Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_BATTLE") == "open"
                    && PathFinder.Circle(start, 70).Where(p => PathFinder.Distance(start, p) >= 24).OrderBy(p => PathFinder.Distance(start, p))
                        .FirstOrDefault(m => Enumerable.Range(-20, 41).All(dx => Enumerable.Range(-7, 15)
                            .All(dy => m + new GridPoint(dx, dy) is var f && _world.IsFreeGround(f) && _world.Map!.IsWalkable(f)))) is { X: > 0 } open)
                {
                    var armies = new[] { new List<Settler>(), new List<Settler>() };
                    for (int side = 0; side < 2; side++)
                        for (int i = 0; i < 40; i++)
                        {
                            var at = new GridPoint(open.X + (side == 0 ? -19 : 12) + i % 8, open.Y - 2 + i / 8);
                            if (_world.SpawnSoldier(at, (SoldierType)(i % 3), level: 1 + i % 3, player: side) is { } soldier)
                                armies[side].Add(soldier);
                        }
                    _world.Military.Move(armies[0], open);
                    _world.Military.Move(armies[1], open);
                    battleMiddle = open;
                }
                // HOMESTEAD_CAPTURE_SELECT=1: own soldiers of every type and level are selected (units panel at the left),
                // stored as group 1, the bowmen as group 2 (icons at the top right). =mixed: also those of player 2's people
                // in the selection (as won-over soldiers would be; the panel squeezes them in).
                if (Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_SELECT") is "1" or "mixed")
                {
                    _selectedBuilding = null;
                    var players = Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_SELECT") == "mixed" ? new[] { 0, 1 } : [0];
                    foreach (int player in players)
                    foreach (var type in Enum.GetValues<SoldierType>())
                        for (int level = 1; level <= 3; level++)
                            for (int i = 0; i < level; i++)
                                if (_world.SpawnSoldier(new GridPoint(start.X - 10 + (int)type * 4 + i, start.Y + 6 + level + 4 * player), type, level, player) is { } soldier)
                                    _group.Add(soldier);
                    _controlGroups.Store(0, _group);
                    _controlGroups.Store(1, _group.Where(s => s.SoldierType == SoldierType.Bowman));
                    if (_zoom > 1)
                        _camera = ToWorld(new GridPoint(start.X - 6, start.Y + 8));
                }
                if (_zoom > 1 && battleMiddle is { } battle)
                    _camera = ToWorld(battle);
                else if (_zoom > 1 && _selectedBuilding is not null)
                    _camera = ToWorld(_selectedBuilding.Position);
                DemoEconomy.PlaceNear(_world, Own["medium_livinghouse"], new GridPoint(start.X + 10, start.Y - 4), _random);
                foreach (var (name, dx, dy) in new[] { ("lumberjack", -14, 10), ("forester", 8, 16), ("stonecutter", -4, 22) })
                    for (int r = 0; r < 12; r++)
                        if (_world.PlaceConstructionSite(Own[name], 0, new GridPoint(start.X + dx + r, start.Y + dy)) is not null)
                            break;
                // HOMESTEAD_CAPTURE_FOCUS=site: a fresh construction site.
                if (Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_FOCUS") == "site"
                    && _world.Buildings.LastOrDefault(b => b.Player == 0 && b.State != BuildingState.Finished) is { } site)
                {
                    _selectedBuilding = site;
                    _camera = ToWorld(site.Position);
                }
                // HOMESTEAD_CAPTURE_PLACE=<building>: placement mode for it, with its build spots.
                if (Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_PLACE") is { Length: > 0 } place
                    && Array.FindIndex(_placeable, b => b.Name == place) is >= 0 and var index)
                {
                    _selectedBuilding = null;
                    _placeIndex = index;
                    _placing = true;
                }
                break;
            case 1200 when strike && _selectedBuilding is { Exists: true } torn:
                // It comes down in dust; ten newcomers without room go on strike next to it.
                _camera = ToWorld(torn.Position);
                Console.WriteLine($"tearing down {torn.Definition.Name} at {torn.Position.X},{torn.Position.Y}");
                _world.RemoveBuilding(torn);
                for (int i = 0; i < 10; i++)
                    _world.Spawn(new GridPoint(torn.Position.X + 3 + i % 5, torn.Position.Y + 3 + i / 5), Profession.Carrier);
                break;
            case 3600:
                var production = _world.Production;
                Console.WriteLine($"buildings: {string.Join(", ", _world.Buildings.Where(b => b.Exists).Select(b => $"{b.Definition.Name}@{b.Position.X},{b.Position.Y}"))}");
                Console.WriteLine($"produced {production.Produced}, crop {production.CropHarvested}/{production.FieldsSown}, fish {production.FishCaught}, " +
                                  $"water {production.WaterDrawn}, ore {production.OreMined}, tools {production.ToolsMade}; " +
                                  $"kills {_world.Military.Kills}, captures {_world.Military.Captures}; " +
                                  $"residents {_world.Housing.Released}, room {_world.Housing.Room(0)} for {_world.Housing.Needing(0)}, striking {_world.Housing.Striking(0)}, settlers {_world.Settlers.Count}; diggers {_world.Settlers.Count(s => s.Profession == Profession.Digger)}, builders {_world.Settlers.Count(s => s.Profession == Profession.Builder)}; problems: " +
                                  string.Join(", ", production.Problems.Select(kv => $"{kv.Key} {kv.Value}x")));
                var carriers = _world.Settlers.Where(c => c.IsCarrier && !c.IsInside).ToList();
                Console.WriteLine($"magic: mana {_world.Magic.Mana(0)}, priests sent {_world.Magic.PriestsSent}, spells {_world.Magic.SpellsCast}");
                Console.WriteLine($"carriers {carriers.Count}: with a task {carriers.Count(c => c.HasTask)}, strolling {carriers.Count(c => c.IsStrolling)}, " +
                                  $"idle on building ground {carriers.Count(c => !c.HasTask && !c.IsStrolling && c.Destination is null && !_world.IsFreeGround(c.Position))}");
                foreach (var building in _world.Buildings.Where(b => b.Exists && b.Player == 0))
                    Console.WriteLine($"  {building.Definition.Name}@{building.Position.X},{building.Position.Y}: {_world.StatusOf(building)}");
                Exit();
                break;
        }
    }

    /// <summary>The game's own pointer over everything, on every screen, while the hardware cursor is off.</summary>
    protected override void EndDraw()
    {
        if (!IsMouseVisible && _cursor is not null && _captureDirectory is null && IsActive)
        {
            GraphicsDevice.SetRenderTarget(null);
            _spriteBatch.Begin(samplerState: SamplerState.PointClamp);
            _cursor.Draw(_spriteBatch, Mouse.GetState().Position, GraphicsDevice.Viewport.Height);
            _spriteBatch.End();
        }
        base.EndDraw();
    }

    protected override void UnloadContent()
    {
        _terrain?.Dispose();
        _fog?.Dispose();
        _fogEffect?.Dispose();
        _minimap?.Dispose();
        _sidePanel?.Dispose();
        _eventButtons?.Dispose();
        _title?.Dispose();
        _settingsScreen?.Dispose();
        _campaignScreen?.Dispose();
        _video?.Dispose();
        _afterVideo = null;
        _briefing?.Dispose();
        _missionSelect?.Dispose();
        _loadList?.Dispose();
        _cursor?.Dispose();
        _startScreen?.Dispose();
        _loading?.Dispose();
        _statistics?.Dispose();
        TextButton.Dispose();
        _sprites.Dispose();
        _buildMenu?.Dispose();
        _portraits?.Dispose();
        DisposePeoplePortraits();
        _soldiersMenu?.Dispose();
        _settlersOverview?.Dispose();
        _professionsMenu?.Dispose();
        _goodsGrid?.Dispose();
        _manufactureMenu?.Dispose();
        _transportMenu?.Dispose();
        _tabs?.Dispose();
        _font.Dispose();
        _atlas.Dispose();
        _effect.Dispose();
        UiSounds.Audio = null;
        _ambience.Dispose();
        _audio?.Dispose();
        base.UnloadContent();
    }
}
