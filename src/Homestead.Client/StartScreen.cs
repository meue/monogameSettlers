using System.Text.Json;
using Homestead.Assets.Gfx;
using Homestead.Assets.Maps;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Map.Generation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Homestead.Client;

/// <summary>
/// Start screen on the original's map selection background (<c>startScreen</c> in <c>data/ui.json</c>): the map
/// list in the upper panel with the category buttons right of it, below the preview in the slanted window with the
/// start positions, one row per player (people, color, team, hardcore), start and quit.
/// </summary>
/// <remarks>
/// The list shows one category at a time (buttons above it, as in the original), sorted by player count.
/// The last category is the random map generator: instead of the list its settings and the last random
/// maps played; every change makes the map again with the same seed, "Neu würfeln" draws a new one.
/// On multiplayer maps the start positions are drawn at random; a click on a position's "?" assigns it a
/// player (or makes it random again). Mouse: click selects, wheel scrolls the list, arrows change the
/// people, a click on the color or team opens its choice. Keys: up/down/PgUp/PgDn
/// map · Tab category · Enter start · Esc quit.
/// </remarks>
public sealed class StartScreen : IDisposable
{
    /// <summary>
    /// One player's settings: <c>null</c> as people or color (palette index) means drawn at random; team 1–4
    /// (0: none, against everybody).
    /// </summary>
    public sealed record PlayerSetup(Civilization? People, int? Color, bool Hardcore, int Team = 0);

    /// <summary>
    /// What was chosen: settings per player (entry 0 is the own one) and, on multiplayer maps, per start
    /// position (map player) the player taking it (<c>null</c>: drawn); <c>null</c> as a whole: as the map says.
    /// </summary>
    public sealed record Choice(int MapIndex, IReadOnlyList<PlayerSetup> Players, IReadOnlyList<int?>? Positions, RandomMap? Random = null);

    /// <summary>A random map: the generator makes it again from its settings and seed.</summary>
    public sealed record RandomMap(RandomMapSettings Settings, int Seed);

    /// <summary>The screen is laid out on the original's 800 × 600 menu background and scaled to the window.</summary>
    private const int Width = 800, Height = 600;
    private const int RowHeight = 20;
    /// <summary>Size of a start position mark on the preview.</summary>
    private const int Mark = 14;
    private const int MenuItemHeight = 18;
    private const int MenuWidth = 104;

    /// <summary>A map in the list: index into the map files, name, player count (0: unreadable), category.</summary>
    private sealed record MapEntry(int Index, string Name, int Players, int Category);

    /// <summary>Map categories (<c>start.categories.*</c>): the folders of the original's map directory (the tutorial counts as single player).</summary>
    private static readonly string[] Categories = ["singlePlayer", "multiplayer", "user", "random"];

    private static string CategoryName(int category) => Lang.T("start.categories." + Categories[category]);

    private const int RandomCategory = 3;

    /// <summary>
    /// One setting of the random map generator: its key (label <c>start.random.&lt;key&gt;</c>, hint
    /// <c>start.random.&lt;key&gt;Hint</c> if there is one), the values to choose from (numbers as they are, otherwise
    /// text keys) and how they map to the settings.
    /// </summary>
    private sealed record RandomOption(string Key, string[] ValueKeys, Func<RandomMapSettings, int> Get,
        Func<RandomMapSettings, int, RandomMapSettings> Set)
    {
        public string Label => Lang.T("start.random." + Key);
        public string? Hint => Lang.Find("start.random." + Key + "Hint");
        public string[] Values => ValueKeys.Select(v => char.IsDigit(v[0]) ? v : Lang.T(v)).ToArray();
    }

    private static readonly string[] Levels = ["start.levels.few", "start.levels.medium", "start.levels.many"];
    private static readonly string[] Symmetries = ["start.symmetry.none", "start.symmetry.mirror", "start.symmetry.repeat"];

    private static readonly RandomOption[] RandomOptions =
    [
        new("players", ["2", "3", "4", "5", "6"], s => s.Players - RandomMapSettings.MinPlayers,
            (s, v) => s with { Players = v + RandomMapSettings.MinPlayers }),
        new("symmetry", Symmetries, s => (int)s.Symmetry, (s, v) => s with { Symmetry = (MapSymmetry)v }),
        new("rivers", Levels, s => (int)s.Rivers, (s, v) => s with { Rivers = (Abundance)v }),
        new("resources", Levels, s => (int)s.Resources, (s, v) => s with { Resources = (Abundance)v }),
        new("stones", Levels, s => (int)s.Stones, (s, v) => s with { Stones = (Abundance)v }),
        new("forests", Levels, s => (int)s.Forests, (s, v) => s with { Forests = (Abundance)v }),
        new("treasures", ["start.levels.none", "start.levels.normal", "start.levels.many"], s => (int)s.Treasures,
            (s, v) => s with { Treasures = (TreasureLevel)v }),
        new("kingOfTheHill", ["common.off", "common.on"], s => s.KingOfTheHill ? 1 : 0, (s, v) => s with { KingOfTheHill = v == 1 }),
    ];

    private static int CategoryOf(string folder) => folder.ToUpperInvariant() switch
    {
        "MULTI" => 1,
        "USER" => 2,
        _ => 0,
    };

    private readonly GraphicsDevice _device;
    private readonly IReadOnlyList<string> _mapFiles;
    private readonly MapEntry[] _entries;

    /// <summary>Maps of the current category in list order.</summary>
    private MapEntry[] _shown = [];
    private int _category;

    private int _selected;
    private int _scroll;

    /// <summary>Peoples preset on the command line (player i: entry i modulo the count).</summary>
    private readonly IReadOnlyList<Civilization>? _presetPeoples;
    /// <summary>Settings per player; they grow with the maps' player counts and are kept across maps.</summary>
    private readonly List<Civilization?> _peoples = new();
    /// <summary>Palette index of the chosen color; <c>null</c>: random (the default).</summary>
    private readonly List<int?> _colors = new();
    /// <summary>The player gets the <see cref="Homestead.Simulation.Hardcore"/> head start.</summary>
    private readonly List<bool> _hardcore = new();
    /// <summary>Team of the player, 1–4; 0: none (the default, everybody against everybody).</summary>
    private readonly List<int> _teams = new();
    /// <summary>Per start position of the map the player assigned to it; <c>null</c>: drawn at the start.</summary>
    private int?[] _positions = [];
    /// <summary>Start position whose player menu is open.</summary>
    private int? _positionMenu;
    /// <summary>Player whose color choice is open below the swatch.</summary>
    private int? _colorMenu;
    /// <summary>Player whose team choice is open below the box.</summary>
    private int? _teamMenu;
    /// <summary>First player row shown when the players don't fit the table.</summary>
    private int _playerScroll;
    /// <summary>Palette index player 1 gets (<see cref="DisplaySettings.PreferredColor"/>).</summary>
    private readonly int _preferredColor;

    private RandomMapSettings _randomSettings = new();
    private int _randomSeed = Random.Shared.Next();

    private OriginalMap? _map;
    private string? _mapError;
    private Texture2D? _preview;

    // The original's menu graphics (startScreen in data/ui.json).
    private readonly Texture2D _background, _marble, _sheet, _rowBar, _scrollTop, _scrollTrack, _scrollBottom;
    private readonly Rectangle _oval, _ovalPressed, _handle;

    private KeyboardState _previousKeys = Keyboard.GetState();
    private MouseState _previousMouse = SdlMouse.GetState();

    /// <param name="mapDirectory">Map directory of the installation; its first level of folders gives the category.</param>
    /// <param name="peoples">Preset peoples (command line): player i gets entry i modulo the count.</param>
    /// <param name="preferredColor">Palette index of player 1's color.</param>
    public StartScreen(GraphicsDevice device, GfxDirectory gfx, IReadOnlyList<string> mapFiles, string mapDirectory, int selected,
        IReadOnlyList<Civilization>? peoples, int preferredColor = 0)
    {
        _device = device;
        _preferredColor = Math.Clamp(preferredColor, 0, PlayerColors.Palette.Length - 1);
        _mapFiles = mapFiles;
        _entries = mapFiles.Select((file, index) =>
        {
            string folder = Path.GetRelativePath(mapDirectory, file).Split(Path.DirectorySeparatorChar)[0];
            int players;
            try
            {
                players = OriginalMap.ReadPlayerCount(file);
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                players = 0;
            }
            return new MapEntry(index, Path.GetFileNameWithoutExtension(file), players, CategoryOf(folder));
        }).ToArray();
        _presetPeoples = peoples is { Count: > 0 } ? peoples : null;
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(BuildingCatalog.DefaultDataDirectory, "ui.json")));
        var look = document.RootElement.GetProperty("startScreen");
        var file = gfx.Open(look.GetProperty("file").GetInt32());
        Texture2D Image(string name)
        {
            var bitmap = file.ReadGui(look.GetProperty(name).GetInt32());
            var texture = new Texture2D(device, bitmap.Width, bitmap.Height);
            texture.SetData(bitmap.Pixels);
            return texture;
        }
        Rectangle Source(string name)
        {
            var e = look.GetProperty(name);
            return new Rectangle(e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32(), e[3].GetInt32());
        }
        _background = Image("background");
        _marble = Image("marble");
        _sheet = Image("sheet");
        _rowBar = Image("rowBar");
        _scrollTop = Image("scrollTop");
        _scrollTrack = Image("scrollTrack");
        _scrollBottom = Image("scrollBottom");
        _oval = Source("oval");
        _ovalPressed = Source("ovalPressed");
        _handle = Source("handle");
        var preset = _entries.FirstOrDefault(e => e.Index == selected);
        ShowCategory(preset?.Category ?? 1);
        Select(Math.Max(0, Array.IndexOf(_shown, preset)));
    }

    /// <summary>Shows the maps of <paramref name="category"/>: by player count, unreadable ones last, then by name.</summary>
    public void ShowCategory(int category)
    {
        category = Math.Clamp(category, 0, Categories.Length - 1);
        _category = category;
        _shown = _entries.Where(e => e.Category == category)
            .OrderBy(e => e.Players == 0 ? int.MaxValue : e.Players)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _scroll = 0;
        Select(0);
    }

    /// <summary>For capture runs: opens the color choice of <paramref name="player"/>.</summary>
    public void OpenColorChoice(int player) => _colorMenu = Math.Clamp(player, 0, PlayerCount - 1);

    /// <summary>For capture runs: opens the team choice of <paramref name="player"/>.</summary>
    public void OpenTeamChoice(int player) => _teamMenu = Math.Clamp(player, 0, PlayerCount - 1);

    private string NameOf(Civilization? people) =>
        people is { } p ? Lang.T("peoples." + p) : Lang.T("start.randomPeople");

    /// <summary>Players of the selected map (at least one row).</summary>
    private int PlayerCount => Math.Max(1, _map?.Players.Count ?? 2);

    /// <summary>Start positions are drawn on multiplayer maps; single player maps give each player its own.</summary>
    private bool RandomPositions => _map is { IsSinglePlayer: false };

    /// <summary>Start position of <paramref name="player"/>, if known yet.</summary>
    private int? PositionOf(int player)
    {
        if (!RandomPositions)
            return player;
        int index = Array.IndexOf(_positions, player);
        return index >= 0 ? index : null;
    }

    /// <summary>People the map gives the start position, if it gives one.</summary>
    private Civilization? FixedPeopleAt(int position) =>
        _map is not null && position < _map.Players.Count && _map.Players[position].Tribe is >= 0 and <= 3
            ? (Civilization)_map.Players[position].Tribe
            : null;

    /// <summary>People the map gives <paramref name="player"/> through its start position, if known.</summary>
    private Civilization? FixedPeople(int player) => PositionOf(player) is { } position ? FixedPeopleAt(position) : null;

    /// <summary>Grows the per-player settings to the player count of the selected map.</summary>
    private void EnsurePlayers()
    {
        while (_peoples.Count < PlayerCount)
        {
            int player = _peoples.Count;
            _peoples.Add(_presetPeoples is { } preset ? preset[player % preset.Count] : player == 0 ? Civilization.Roman : null);
            _colors.Add(player == 0 ? _preferredColor : null);
            _hardcore.Add(false);
            _teams.Add(0);
        }
    }

    private void Select(int index)
    {
        _selected = Math.Clamp(index, 0, Math.Max(0, _shown.Length - 1));
        if (_category == RandomCategory)
            Show(() => RandomMapGenerator.Generate(_randomSettings, _randomSeed));
        else if (_shown.Length == 0)
            Show(null, Lang.T("start.noMaps"));
        else
            Show(() => OriginalMap.Open(_mapFiles[_shown[_selected].Index]));
    }

    /// <summary>Shows the map <paramref name="open"/> gives (none: <paramref name="error"/>) with its preview.</summary>
    private void Show(Func<OriginalMap>? open, string? error = null)
    {
        _preview?.Dispose();
        _preview = null;
        _map = null;
        _mapError = error;
        _positionMenu = null;
        _colorMenu = null;
        _teamMenu = null;
        _playerScroll = 0;
        _positions = [];
        try
        {
            if (open is not null)
            {
                _map = open();
                _preview = new Texture2D(_device, _map.Area.Size, _map.Area.Size);
                _preview.SetData(MapOverview.Pixels(_map.Area));
                _positions = new int?[_map.Players.Count];
            }
        }
        catch (Exception e) when (e is InvalidDataException or IOException || _category == RandomCategory)
        {
            _map = null;
            _mapError = e.Message;
        }
        EnsurePlayers();
    }

    /// <summary>Makes the random map again with these settings and seed.</summary>
    private void ShowRandom(RandomMapSettings settings, int seed)
    {
        _randomSettings = settings;
        _randomSeed = seed;
        Select(0);
    }

    // Layout in the 800 × 600 space of the background (gui image 8 of file 02): the map list in the upper
    // panel, the category buttons right of it, the preview in the slanted window below, the players in the
    // lower panel, start and quit right of it.
    private static readonly Rectangle ListArea = new(92, 62, 590, 222);

    private static Rectangle CategoryButton(int category) => new(704, 64 + category * 54, 78, 46);

    private static readonly Rectangle StartButton = new(704, 436, 78, 46);
    private static readonly Rectangle QuitButton = new(704, 490, 78, 46);

    /// <summary>The preview window: a parallelogram, top edge from (160, 346), bottom edge from (78, 507), 161 wide.</summary>
    private const int PreviewTop = 346, PreviewHeight = 161, PreviewWidth = 161, PreviewLeft = 160, PreviewSlant = 82;

    /// <summary>Point of the preview at map fraction (<paramref name="u"/>, <paramref name="v"/>): rows shift left as in the game.</summary>
    private static Vector2 PreviewPoint(float u, float v) =>
        new(PreviewLeft - PreviewSlant * v + PreviewWidth * u, PreviewTop + PreviewHeight * v);

    // The random map generator's panel in place of the list: one row per setting, "Neu würfeln" with the seed below.
    private const int OptionRow = 24, OptionLabel = 100;

    private static int OptionY(int option) => 64 + option * OptionRow;

    private static Rectangle OptionValue(int option, int value)
    {
        const int left = 240, width = 440;
        int count = RandomOptions[option].ValueKeys.Length;
        // Two values don't stretch over the whole row.
        int slot = Math.Min(width / count, 110);
        return new Rectangle(left + value * slot, OptionY(option), slot - 3, OptionRow - 3);
    }

    private static readonly Rectangle RerollButton = new(OptionLabel - 4, OptionY(RandomOptions.Length) + 2, 136, 24);

    // The players' table in the lower panel: name, people with arrows, color, team, hardcore.
    private const int TableTop = 340, TableBottom = 536;

    private const int PlayerRow = 28;

    /// <summary>Player rows that fit below the header; more scroll.</summary>
    private const int ShownPlayers = (TableBottom - TableTop) / PlayerRow - 1;

    private static readonly Rectangle PlayerTable = new(340, TableTop, 360, TableBottom - TableTop);

    /// <summary>Top of the row of <paramref name="player"/> (-1: the header, which stays).</summary>
    private int RowY(int player) => TableTop + (player < 0 ? 0 : player - _playerScroll + 1) * PlayerRow;

    /// <summary>The players whose rows are shown.</summary>
    private IEnumerable<int> ShownRows => Enumerable.Range(_playerScroll, Math.Min(ShownPlayers, PlayerCount - _playerScroll));

    private const int Control = 20;

    private Rectangle Cell(int x, int player, int width = 0) =>
        new(x, RowY(player) + (PlayerRow - Control) / 2, width == 0 ? Control : width, Control);

    private Rectangle LeftArrow(int player) => Cell(430, player);

    private Rectangle RightArrow(int player) => Cell(540, player);

    private Rectangle ColorBox(int player) => Cell(566, player, 22);

    private Rectangle TeamBox(int player) => Cell(604, player, 22);

    private Rectangle HardcoreBox(int player) => Cell(652, player);

    // The color choice below a swatch: "?" (random) and the palette.
    private const int ColorColumns = 6, ColorCell = 22;

    /// <summary>Entry <paramref name="item"/> of the open color choice: 0 is "?", entry i + 1 palette color i.</summary>
    private Rectangle ColorItem(int item)
    {
        var swatch = ColorBox(_colorMenu!.Value);
        int rows = (PlayerColors.Palette.Length + ColorColumns) / ColorColumns;
        int width = ColorColumns * ColorCell + 6, height = rows * ColorCell + 6;
        // Below the swatch, above it if there is no room; kept inside the screen.
        int x = Math.Min(swatch.X - 4, Width - width - 4);
        int y = swatch.Bottom + 4 + height <= Height - 4 ? swatch.Bottom + 4 : swatch.Y - 4 - height;
        return new Rectangle(x + 3 + item % ColorColumns * ColorCell, y + 3 + item / ColorColumns * ColorCell, ColorCell - 2, ColorCell - 2);
    }

    /// <summary>Teams to choose in the order shown: 1–4, then 0 (none, "-").</summary>
    private static readonly int[] TeamChoices = [1, 2, 3, 4, 0];

    private static string TeamLabel(int team) => team == 0 ? "-" : team.ToString();

    /// <summary>Entry <paramref name="item"/> of the open team choice (team <see cref="TeamChoices"/>[item]), in a row below the box.</summary>
    private Rectangle TeamItem(int item)
    {
        var box = TeamBox(_teamMenu!.Value);
        int width = TeamChoices.Length * ColorCell + 6, height = ColorCell + 6;
        int x = Math.Min(box.X - 4, Width - width - 4);
        int y = box.Bottom + 4 + height <= Height - 4 ? box.Bottom + 4 : box.Y - 4 - height;
        return new Rectangle(x + 3 + item * ColorCell, y + 3, ColorCell - 2, ColorCell - 2);
    }

    /// <summary>Whether another player has the palette color.</summary>
    private bool Taken(int player, int color) => Enumerable.Range(0, PlayerCount).Any(other => other != player && _colors[other] == color);

    private static string HardcoreHint => Lang.T("start.hardcoreHint");

    /// <summary>The mark of start position <paramref name="position"/> on the preview.</summary>
    private Rectangle MarkAt(int position)
    {
        var home = _map!.Players[position];
        var at = PreviewPoint((home.StartX + 0.5f) / _map.Area.Size, (home.StartY + 0.5f) / _map.Area.Size);
        return new Rectangle((int)at.X - Mark / 2, (int)at.Y - Mark / 2, Mark, Mark);
    }

    /// <summary>Entry <paramref name="item"/> of the open position menu: 0 is "?", entry i + 1 player i.</summary>
    private Rectangle MenuItem(int item)
    {
        var mark = MarkAt(_positionMenu!.Value);
        int height = (PlayerCount + 1) * MenuItemHeight;
        // Right of the mark, kept inside the screen.
        int x = Math.Min(mark.Right + 6, Width - MenuWidth - 4);
        int y = Math.Clamp(mark.Y, 4, Math.Max(4, Height - height - 4));
        return new Rectangle(x, y + item * MenuItemHeight, MenuWidth, MenuItemHeight);
    }

    private static int VisibleRows => ListArea.Height / RowHeight;

    private static string PlayerName(int player) => Lang.T("start.player", player + 1);

    // From the 800 × 600 space to the window: scaled to fit, centered.
    private float _scale = 1;
    private Vector2 _origin;
    /// <summary>The mouse in window pixels while drawing.</summary>
    private Vector2 _mouse;

    private void Fit(Rectangle view)
    {
        _scale = Math.Min(view.Width / (float)Width, view.Height / (float)Height);
        _origin = new Vector2(view.X + (view.Width - Width * _scale) / 2, view.Y + (view.Height - Height * _scale) / 2);
    }

    private Vector2 ToScreen(Vector2 at) => _origin + at * _scale;

    private Rectangle ToScreen(Rectangle r)
    {
        var a = ToScreen(new Vector2(r.X, r.Y));
        var b = ToScreen(new Vector2(r.Right, r.Bottom));
        return new Rectangle((int)MathF.Round(a.X), (int)MathF.Round(a.Y), (int)MathF.Round(b.X) - (int)MathF.Round(a.X),
            (int)MathF.Round(b.Y) - (int)MathF.Round(a.Y));
    }

    private Point ToLayout(Point screen) => ((screen.ToVector2() - _origin) / _scale).ToPoint();

    /// <summary>A font size of the 800 × 600 space in window pixels.</summary>
    private int Px(int size) => Math.Max(8, (int)MathF.Round(size * _scale));

    /// <summary>Handles input; returns the choice once the game is started, <c>null</c> otherwise.</summary>
    public Choice? Update(Rectangle view, out bool quit)
    {
        Fit(view);
        var keys = Keyboard.GetState();
        var mouse = SdlMouse.GetState();
        bool Pressed(Keys k) => keys.IsKeyDown(k) && !_previousKeys.IsKeyDown(k);
        bool clicked = mouse.LeftButton == ButtonState.Pressed && _previousMouse.LeftButton == ButtonState.Released;
        bool rightClicked = mouse.RightButton == ButtonState.Pressed && _previousMouse.RightButton == ButtonState.Released;
        int wheel = mouse.ScrollWheelValue - _previousMouse.ScrollWheelValue;
        _previousKeys = keys;
        _previousMouse = mouse;
        quit = false;
        var at = ToLayout(mouse.Position);

        // An open position menu takes the next click (or Esc) for itself.
        if (_positionMenu is { } open)
        {
            if (Pressed(Keys.Escape))
                _positionMenu = null;
            else if (clicked || rightClicked)
            {
                for (int item = 0; item <= PlayerCount; item++)
                    if (clicked && MenuItem(item).Contains(at))
                        AssignPosition(open, item == 0 ? null : item - 1);
                _positionMenu = null;
            }
            return null;
        }
        // So does an open color choice.
        if (_colorMenu is { } chooser)
        {
            if (Pressed(Keys.Escape))
                _colorMenu = null;
            else if (clicked || rightClicked)
            {
                for (int item = 0; item <= PlayerColors.Palette.Length; item++)
                    if (clicked && ColorItem(item).Contains(at) && (item == 0 || !Taken(chooser, item - 1)))
                        _colors[chooser] = item == 0 ? null : item - 1;
                _colorMenu = null;
            }
            return null;
        }
        // And an open team choice.
        if (_teamMenu is { } member)
        {
            if (Pressed(Keys.Escape))
                _teamMenu = null;
            else if (clicked || rightClicked)
            {
                for (int item = 0; item < TeamChoices.Length; item++)
                    if (clicked && TeamItem(item).Contains(at))
                        _teams[member] = TeamChoices[item];
                _teamMenu = null;
            }
            return null;
        }

        quit = Pressed(Keys.Escape) || clicked && QuitButton.Contains(at);
        if (Pressed(Keys.Tab))
            ShowCategory((_category + 1) % Categories.Length);

        int rows = VisibleRows;
        int move = (Pressed(Keys.Down) ? 1 : 0) - (Pressed(Keys.Up) ? 1 : 0)
                   + (Pressed(Keys.PageDown) ? rows : 0) - (Pressed(Keys.PageUp) ? rows : 0);
        if (_category == RandomCategory)
        {
            move = 0;
            if (Pressed(Keys.R))
                ShowRandom(_randomSettings, Random.Shared.Next());
        }
        if (move != 0)
        {
            Select(_selected + move);
            _scroll = Math.Clamp(_scroll, _selected - rows + 1, _selected);
        }
        if (wheel != 0 && PlayerTable.Contains(at))
            _playerScroll -= Math.Sign(wheel);
        else if (wheel != 0)
            _scroll -= Math.Sign(wheel) * 3;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _shown.Length - rows));
        _playerScroll = Math.Clamp(_playerScroll, 0, Math.Max(0, PlayerCount - ShownPlayers));

        if (clicked)
        {
            if (_category == RandomCategory)
                ClickRandomPanel(at);
            else if (ListArea.Contains(at))
            {
                int index = _scroll + (at.Y - ListArea.Y) / RowHeight;
                if (index < _shown.Length && index != _selected)
                    Select(index);
            }
            for (int category = 0; category < Categories.Length; category++)
                if (CategoryButton(category).Contains(at) && category != _category)
                    ShowCategory(category);
            if (_map is not null && RandomPositions)
                for (int position = 0; position < _map.Players.Count; position++)
                    if (MarkAt(position).Contains(at))
                        _positionMenu = position;
            foreach (int player in ShownRows)
            {
                if (ColorBox(player).Contains(at))
                    _colorMenu = player;
                if (TeamBox(player).Contains(at))
                    _teamMenu = player;
                int step = LeftArrow(player).Contains(at) ? -1 : RightArrow(player).Contains(at) ? 1 : 0;
                if (step != 0 && FixedPeople(player) is null)
                    _peoples[player] = Cycle(_peoples[player], step);
                if (HardcoreBox(player).Contains(at))
                    _hardcore[player] = !_hardcore[player];
            }
            if (StartButton.Contains(at) && _map is not null)
                return Start();
        }
        if (Pressed(Keys.Enter) && _map is not null)
            return Start();
        return null;
    }

    private void ClickRandomPanel(Point at)
    {
        for (int option = 0; option < RandomOptions.Length; option++)
            for (int value = 0; value < RandomOptions[option].ValueKeys.Length; value++)
                if (OptionValue(option, value).Contains(at) && RandomOptions[option].Get(_randomSettings) != value)
                    ShowRandom(RandomOptions[option].Set(_randomSettings, value), _randomSeed);
        if (RerollButton.Contains(at))
            ShowRandom(_randomSettings, Random.Shared.Next());
    }

    private Choice Start()
    {
        var players = Enumerable.Range(0, PlayerCount).Select(p => new PlayerSetup(_peoples[p], _colors[p], _hardcore[p], _teams[p])).ToArray();
        var positions = RandomPositions ? _positions.ToArray() : null;
        if (_category != RandomCategory)
            return new Choice(_shown[_selected].Index, players, positions);
        return new Choice(-1, players, positions, new RandomMap(_randomSettings, _randomSeed));
    }

    /// <summary>
    /// Start position per player: the assigned ones, the others drawn from the positions left
    /// (entry i: the map player whose start player i takes).
    /// </summary>
    public static int[] ResolvePositions(IReadOnlyList<int?> chosen, Random random)
    {
        var positions = new int[chosen.Count];
        var assigned = new HashSet<int>();
        for (int position = 0; position < chosen.Count; position++)
            if (chosen[position] is { } player && player < chosen.Count)
            {
                positions[player] = position;
                assigned.Add(player);
            }
        var free = Enumerable.Range(0, chosen.Count).Where(p => chosen[p] is null || chosen[p] >= chosen.Count)
            .OrderBy(_ => random.Next()).ToList();
        foreach (int player in Enumerable.Range(0, chosen.Count).Where(p => !assigned.Contains(p)))
        {
            positions[player] = free[^1];
            free.RemoveAt(free.Count - 1);
        }
        return positions;
    }

    /// <summary>Gives the start position to <paramref name="player"/> (null: random); its previous position becomes random.</summary>
    private void AssignPosition(int position, int? player)
    {
        if (player is { } p && Array.IndexOf(_positions, p) is var previous and >= 0)
            _positions[previous] = null;
        _positions[position] = player;
    }

    /// <summary>Next people in the order random, Romans, Egyptians, Asians, Amazons.</summary>
    private static Civilization? Cycle(Civilization? people, int step)
    {
        var all = Enum.GetValues<Civilization>();
        int count = all.Length + 1;
        int index = people is { } p ? Array.IndexOf(all, p) + 1 : 0;
        index = ((index + step) % count + count) % count;
        return index == 0 ? null : all[index - 1];
    }

    /// <summary>The color shown for the player: its chosen one, grey while random.</summary>
    private Color ShownColor(int player) => _colors[player] is { } c ? PlayerColors.Palette[c] : Color.Gray;

    /// <summary>Black or white, whichever reads better on <paramref name="background"/>.</summary>
    private static Color TextOn(Color background) =>
        background.R * 0.3 + background.G * 0.59 + background.B * 0.11 > 140 ? Color.Black : Color.White;

    private static readonly Color Text = new(240, 225, 190);
    private static readonly Color HoverText = new(255, 240, 160);
    private static readonly Color Gold = new(232, 196, 92);
    private static readonly Color Dim = new(175, 175, 185);
    private static readonly Color Ink = new(14, 16, 22);

    public void Draw(SpriteBatch batch, Texture2D pixel, UiFont font, Rectangle view)
    {
        Fit(view);
        var mouse = SdlMouse.GetState().Position;
        _mouse = mouse.ToVector2();
        var hover = ToLayout(mouse);
        string? hint = null;

        // The background; beside it marble with leaves at the window's edge (a plain strip of the marble image,
        // inside its gold line and left of its relief; flipped on the right).
        var page = ToScreen(new Rectangle(0, 0, Width, Height));
        if (page.X > view.X)
        {
            var strip = new Rectangle(14, 0, (int)Math.Min(100, Math.Ceiling(page.X / _scale)), _marble.Height);
            batch.Draw(_marble, new Rectangle(view.X, view.Y, page.X - view.X, view.Height), strip, Color.White);
            batch.Draw(_marble, new Rectangle(page.Right, view.Y, view.Right - page.Right, view.Height), strip, Color.White,
                0, Vector2.Zero, SpriteEffects.FlipHorizontally, 0);
        }
        else if (page.Y > view.Y)
            batch.Draw(_marble, view, new Rectangle(14, 0, _marble.Width - 28, _marble.Height), Color.White);
        batch.Draw(_background, page, Color.White);

        // Heading and keys on the stone above the list.
        Write(batch, font, _category == RandomCategory ? Lang.T("start.randomHeading") : Lang.T("start.mapsHeading", CategoryName(_category)), 92, 22, Gold, 18);
        string keys = _category == RandomCategory
            ? Lang.T("start.keysRandom")
            : Lang.T("start.keys");
        Write(batch, font, keys, 700 - Measure(font, keys, 11), 30, Dim, 11);

        for (int category = 0; category < Categories.Length; category++)
            Oval(batch, font, CategoryButton(category), category == RandomCategory ? Lang.T("start.randomShort") : CategoryName(category), hover,
                category == _category);
        Oval(batch, font, StartButton, Lang.T("start.start"), hover, enabled: _map is not null);
        Oval(batch, font, QuitButton, Lang.T("start.quit"), hover);

        if (_category == RandomCategory)
            hint = DrawRandomPanel(batch, pixel, font, mouse, hover);
        else
            DrawList(batch, font, hover);

        // Selected map: preview in the slanted window with its start positions, name and facts below.
        if (_preview is not null && _map is not null)
        {
            // One strip per window row, shifted like the rows of the map.
            var top = ToScreen(new Vector2(0, PreviewTop));
            int rows = (int)MathF.Round(PreviewHeight * _scale);
            int width = (int)MathF.Round(PreviewWidth * _scale);
            for (int row = 0; row < rows; row++)
            {
                float v = (row + 0.5f) / rows;
                var left = ToScreen(PreviewPoint(0, v));
                int source = Math.Min(_map.Area.Size - 1, (int)(v * _map.Area.Size));
                batch.Draw(_preview, new Rectangle((int)MathF.Round(left.X), (int)top.Y + row, width, 1),
                    new Rectangle(0, source, _map.Area.Size, 1), Color.White);
            }
            for (int position = 0; position < _map.Players.Count; position++)
            {
                // A drawn position is a black box with "?"; an assigned one shows its player's color and number.
                var mark = MarkAt(position);
                int? player = RandomPositions ? _positions[position] : position;
                bool hot = RandomPositions && (mark.Contains(hover) || _positionMenu == position);
                if (RandomPositions && mark.Contains(hover))
                    hint = Lang.T("start.positionHint");
                batch.Draw(pixel, ToScreen(new Rectangle(mark.X - 1, mark.Y - 1, mark.Width + 2, mark.Height + 2)), hot ? Gold : Color.Black);
                var fill = player is { } p ? ShownColor(p) : Color.Black;
                batch.Draw(pixel, ToScreen(new Rectangle(mark.X + 1, mark.Y + 1, mark.Width - 2, mark.Height - 2)), fill);
                string label = player is { } q ? (q + 1).ToString() : "?";
                Centered(batch, font, label, mark, player is null ? Color.White : TextOn(fill), 11, shadow: false);
            }
            Write(batch, font, Lang.MapName(_map.Name), 82, 512, Gold, 15);
            string kind = Lang.T(_category == RandomCategory ? "start.kind.random" : _map.IsSinglePlayer ? "start.kind.singlePlayer" : "start.kind.multiplayer");
            string facts = Lang.T("start.facts", kind, _map.Players.Count, _map.Area.Size)
                           + (_map.Buildings.Count > 0 ? Lang.T("start.factsBuildings", _map.Buildings.Count) : "");
            Write(batch, font, facts, 82, 531, Text, 11);
        }
        else
            Write(batch, font, Lang.T("start.unreadable", _mapError), 82, 512, Color.OrangeRed, 12);

        hint = DrawPlayers(batch, pixel, font, hover) ?? hint;

        // The position menu over everything else.
        if (_positionMenu is not null)
        {
            var first = MenuItem(0);
            var box = ToScreen(new Rectangle(first.X, first.Y, MenuWidth, (PlayerCount + 1) * MenuItemHeight));
            batch.Draw(pixel, new Rectangle(box.X - 2, box.Y - 2, box.Width + 4, box.Height + 4), Gold);
            batch.Draw(pixel, box, Ink);
            for (int item = 0; item <= PlayerCount; item++)
            {
                var entry = MenuItem(item);
                if (entry.Contains(hover))
                    batch.Draw(_rowBar, ToScreen(entry), Color.White);
                if (item == 0)
                    Write(batch, font, Lang.T("start.positionRandom"), entry.X + 8, entry.Y + 2, Text, 12);
                else
                {
                    batch.Draw(pixel, ToScreen(new Rectangle(entry.X + 6, entry.Y + 4, 10, 10)), ShownColor(item - 1));
                    Write(batch, font, PlayerName(item - 1), entry.X + 22, entry.Y + 2, Text, 12);
                }
            }
        }
        // The color choice over everything else: "?" and the palette; colors of other players crossed out.
        if (_colorMenu is { } chooser)
        {
            var first = ColorItem(0);
            int rows = (PlayerColors.Palette.Length + ColorColumns) / ColorColumns;
            var frame = new Rectangle(first.X - 3, first.Y - 3, ColorColumns * ColorCell + 4, rows * ColorCell + 4);
            batch.Draw(pixel, ToScreen(new Rectangle(frame.X - 2, frame.Y - 2, frame.Width + 4, frame.Height + 4)), Gold);
            batch.Draw(pixel, ToScreen(frame), Ink);
            for (int item = 0; item <= PlayerColors.Palette.Length; item++)
            {
                var cell = ColorItem(item);
                int? color = item == 0 ? null : item - 1;
                bool taken = color is { } c && Taken(chooser, c);
                bool current = _colors[chooser] == color;
                var edge = current ? Gold : !taken && cell.Contains(hover) ? HoverText : new Color(90, 100, 115);
                batch.Draw(pixel, ToScreen(cell), edge);
                var inner = new Rectangle(cell.X + 2, cell.Y + 2, cell.Width - 4, cell.Height - 4);
                batch.Draw(pixel, ToScreen(inner), color is { } k ? PlayerColors.Palette[k] : Ink);
                if (color is null)
                    Centered(batch, font, "?", inner, Color.White, 12);
                else if (taken)
                    Centered(batch, font, "×", inner, TextOn(PlayerColors.Palette[color.Value]) * 0.8f, 14, shadow: false);
            }
        }
        // The team choice likewise: 1–4 and "-".
        if (_teamMenu is { } member)
        {
            var first = TeamItem(0);
            var frame = new Rectangle(first.X - 3, first.Y - 3, TeamChoices.Length * ColorCell + 4, ColorCell + 4);
            batch.Draw(pixel, ToScreen(new Rectangle(frame.X - 2, frame.Y - 2, frame.Width + 4, frame.Height + 4)), Gold);
            batch.Draw(pixel, ToScreen(frame), Ink);
            for (int item = 0; item < TeamChoices.Length; item++)
            {
                var cell = TeamItem(item);
                bool current = _teams[member] == TeamChoices[item];
                batch.Draw(pixel, ToScreen(cell), current ? Gold : cell.Contains(hover) ? HoverText : new Color(90, 100, 115));
                var inner = new Rectangle(cell.X + 2, cell.Y + 2, cell.Width - 4, cell.Height - 4);
                batch.Draw(pixel, ToScreen(inner), Ink);
                Centered(batch, font, TeamLabel(TeamChoices[item]), inner, Color.White, 12);
            }
        }
        if (hint is not null)
            Hint(batch, pixel, font, hint, mouse, view);
    }

    /// <summary>The maps of the category in the upper panel: the selected one on a stone bar, a scroll bar if they don't fit.</summary>
    private void DrawList(SpriteBatch batch, UiFont font, Point hover)
    {
        int rows = VisibleRows;
        bool scrolls = _shown.Length > rows;
        int right = ListArea.Right - (scrolls ? 18 : 0);
        for (int r = 0; r < rows && _scroll + r < _shown.Length; r++)
        {
            int index = _scroll + r;
            var entry = _shown[index];
            var row = new Rectangle(ListArea.X, ListArea.Y + r * RowHeight, right - ListArea.X, RowHeight);
            bool selected = index == _selected;
            if (selected)
                batch.Draw(_rowBar, ToScreen(row), Color.White);
            var color = selected ? Color.White : row.Contains(hover) ? HoverText : Text;
            Write(batch, font, entry.Name, row.X + 8, row.Y + 2, color, 13);
            string players = entry.Players > 0 ? Lang.T("start.playerCount", entry.Players) : "?";
            Write(batch, font, players, row.Right - 10 - Measure(font, players, 12), row.Y + 3, selected ? color : Dim, 12);
        }
        if (_shown.Length == 0)
            Write(batch, font, Lang.T("start.noMapsInCategory"), ListArea.X + 8, ListArea.Y + 4, Dim, 13);
        if (!scrolls)
            return;
        // Scroll bar: the original's groove with a stone handle.
        var track = new Rectangle(ListArea.Right - 12, ListArea.Y, 12, ListArea.Height);
        batch.Draw(_scrollTop, ToScreen(new Rectangle(track.X, track.Y, 12, 8)), Color.White);
        batch.Draw(_scrollTrack, ToScreen(new Rectangle(track.X, track.Y + 8, 12, track.Height - 16)), Color.White);
        batch.Draw(_scrollBottom, ToScreen(new Rectangle(track.X, track.Bottom - 8, 12, 8)), Color.White);
        int height = Math.Max(16, (track.Height - 16) * rows / _shown.Length);
        int y = track.Y + 8 + (track.Height - 16 - height) * _scroll / Math.Max(1, _shown.Length - rows);
        batch.Draw(_sheet, ToScreen(new Rectangle(track.X + 2, y, 8, height)), _handle, Color.White);
    }

    /// <summary>One row per player in the lower panel: people, color, team, hardcore. Returns the hint under the mouse.</summary>
    private string? DrawPlayers(SpriteBatch batch, Texture2D pixel, UiFont font, Point hover)
    {
        string? hint = null;
        int size = Math.Min(13, Control - 3);
        int header = RowY(-1) + (PlayerRow - 11) / 2;
        Write(batch, font, Lang.T("start.columns.player"), 346, header, Gold, 11);
        Centered(batch, font, Lang.T("start.columns.people"), new Rectangle(LeftArrow(0).X, header, RightArrow(0).Right - LeftArrow(0).X, 12), Gold, 11);
        Centered(batch, font, Lang.T("start.columns.color"), new Rectangle(ColorBox(0).X - 10, header, ColorBox(0).Width + 20, 12), Gold, 11);
        Centered(batch, font, Lang.T("start.columns.team"), new Rectangle(TeamBox(0).X - 10, header, TeamBox(0).Width + 20, 12), Gold, 11);
        Centered(batch, font, Lang.T("start.columns.hardcore"), new Rectangle(HardcoreBox(0).X - 20, header, HardcoreBox(0).Width + 40, 12), Gold, 11);
        var fixedHint = Lang.T("start.fixedPeopleHint");
        foreach (int player in ShownRows)
        {
            var left = LeftArrow(player);
            var right = RightArrow(player);
            Write(batch, font, PlayerName(player) + (player == 0 ? Lang.T("start.you") : ""), 346, left.Y + (left.Height - size) / 2 - 1,
                player == 0 ? HoverText : Text, size);

            var fixedPeople = FixedPeople(player);
            var people = fixedPeople ?? _peoples[player];
            if (fixedPeople is null)
            {
                Small(batch, pixel, font, left, "<");
                Small(batch, pixel, font, right, ">");
            }
            var name = new Rectangle(left.Right, left.Y, right.X - left.Right, left.Height);
            var label = NameOf(people) + (fixedPeople is null ? "" : Lang.T("start.byMap"));
            Centered(batch, font, label, name, fixedPeople is null ? Text : Dim, Measure(font, label, size) > name.Width - 4 ? 11 : size);
            if (fixedPeople is not null && name.Contains(hover))
                hint = fixedHint;

            // Color: gold edge under the mouse, "?" while random.
            var swatch = ColorBox(player);
            batch.Draw(pixel, ToScreen(new Rectangle(swatch.X - 2, swatch.Y - 2, swatch.Width + 4, swatch.Height + 4)),
                swatch.Contains(hover) ? Gold : new Color(120, 135, 150));
            batch.Draw(pixel, ToScreen(swatch), _colors[player] is { } color ? PlayerColors.Palette[color] : Ink);
            if (_colors[player] is null)
                Centered(batch, font, "?", swatch, Color.White, size);
            if (swatch.Contains(hover))
                hint = Lang.T("start.colorHint");

            // Team: its number, "-" without one.
            var team = TeamBox(player);
            batch.Draw(pixel, ToScreen(new Rectangle(team.X - 2, team.Y - 2, team.Width + 4, team.Height + 4)),
                team.Contains(hover) ? Gold : new Color(120, 135, 150));
            batch.Draw(pixel, ToScreen(team), Ink);
            Centered(batch, font, TeamLabel(_teams[player]), team, _teams[player] == 0 ? Dim : Color.White, size);
            if (team.Contains(hover))
                hint = Lang.T("start.teamHint");

            // Hardcore: a stone box, pressed and lit while on.
            var box = HardcoreBox(player);
            TextButton.Draw(batch, pixel, font, ToScreen(box), "", _mouse, selected: _hardcore[player]);
            if (_hardcore[player])
                batch.Draw(pixel, ToScreen(new Rectangle(box.X + 5, box.Y + 5, box.Width - 10, box.Height - 10)), Gold);
            if (box.Contains(hover))
                hint = HardcoreHint;
        }
        if (PlayerCount > ShownPlayers)
        {
            // Scroll bar like the map list's, beside the rows.
            var track = new Rectangle(686, RowY(_playerScroll), 10, ShownPlayers * PlayerRow);
            batch.Draw(_scrollTop, ToScreen(new Rectangle(track.X, track.Y, track.Width, 8)), Color.White);
            batch.Draw(_scrollTrack, ToScreen(new Rectangle(track.X, track.Y + 8, track.Width, track.Height - 16)), Color.White);
            batch.Draw(_scrollBottom, ToScreen(new Rectangle(track.X, track.Bottom - 8, track.Width, 8)), Color.White);
            int height = Math.Max(16, (track.Height - 16) * ShownPlayers / PlayerCount);
            int y = track.Y + 8 + (track.Height - 16 - height) * _playerScroll / (PlayerCount - ShownPlayers);
            batch.Draw(_sheet, ToScreen(new Rectangle(track.X + 1, y, track.Width - 2, height)), _handle, Color.White);
        }
        return hint;
    }

    /// <summary>The random map generator in place of the list: settings, "Neu würfeln". Returns the hint under the mouse.</summary>
    private string? DrawRandomPanel(SpriteBatch batch, Texture2D pixel, UiFont font, Point mouse, Point hover)
    {
        TextButton.Draw(batch, pixel, font, ToScreen(RerollButton), Lang.T("start.reroll"), mouse.ToVector2(), size: Px(13));
        Write(batch, font, $"#{(uint)_randomSeed:X8}", RerollButton.Right + 10, RerollButton.Y + 5, Dim, 12);

        string? hint = null;
        for (int option = 0; option < RandomOptions.Length; option++)
        {
            var o = RandomOptions[option];
            int y = OptionY(option);
            // Long labels in smaller type, so they end before the first value.
            int labelSize = Measure(font, o.Label, 13) > OptionValue(option, 0).X - OptionLabel - 4 ? 11 : 13;
            Write(batch, font, o.Label, OptionLabel, y + (labelSize == 13 ? 3 : 4), Text, labelSize);
            if (new Rectangle(OptionLabel - 4, y, 590, OptionRow).Contains(hover))
                hint = o.Hint;
            int chosen = o.Get(_randomSettings);
            var values = o.Values;
            for (int value = 0; value < values.Length; value++)
            {
                var box = OptionValue(option, value);
                int size = Measure(font, values[value], 12) > box.Width - 8 ? 10 : 12;
                TextButton.Draw(batch, pixel, font, ToScreen(box), values[value], mouse.ToVector2(), size: Px(size), selected: value == chosen);
            }
        }
        return hint;
    }

    /// <summary>The original's oval stone button: pressed while chosen or held, the text lit under the mouse.</summary>
    private void Oval(SpriteBatch batch, UiFont font, Rectangle rect, string label, Point hover, bool selected = false, bool enabled = true)
    {
        bool over = enabled && rect.Contains(hover);
        bool down = selected || over && SdlMouse.GetState().LeftButton == ButtonState.Pressed;
        batch.Draw(_sheet, ToScreen(rect), down ? _ovalPressed : _oval, enabled ? Color.White : new Color(140, 140, 140));
        int size = Measure(font, label, 13) > rect.Width - 14 ? 11 : 13;
        Centered(batch, font, label, new Rectangle(rect.X, rect.Y + (down ? 1 : 0), rect.Width, rect.Height),
            !enabled ? Dim : selected ? Gold : over ? HoverText : Text, size);
    }

    /// <summary>A small square stone button with a sign.</summary>
    private void Small(SpriteBatch batch, Texture2D pixel, UiFont font, Rectangle rect, string sign) =>
        TextButton.Draw(batch, pixel, font, ToScreen(rect), sign, _mouse, size: Px(Math.Min(13, rect.Height - 4)));

    /// <summary>Text at a point of the 800 × 600 space, with a shadow.</summary>
    private void Write(SpriteBatch batch, UiFont font, string text, float x, float y, Color color, int size, bool shadow = true)
    {
        var at = ToScreen(new Vector2(x, y));
        at = new Vector2(MathF.Round(at.X), MathF.Round(at.Y));
        if (shadow)
            font.Draw(batch, text, at + new Vector2(1, 1), Color.Black * 0.8f, Px(size));
        font.Draw(batch, text, at, color, Px(size));
    }

    private void Centered(SpriteBatch batch, UiFont font, string text, Rectangle rect, Color color, int size, bool shadow = true)
    {
        var measured = font.Measure(text, Px(size)) / _scale;
        Write(batch, font, text, rect.Center.X - measured.X / 2, rect.Y + (rect.Height - measured.Y) / 2, color, size, shadow);
    }

    /// <summary>Width of the text in the 800 × 600 space.</summary>
    private float Measure(UiFont font, string text, int size) => font.Measure(text, Px(size)).X / _scale;

    /// <summary>A wrapped hint next to the mouse, kept inside the window.</summary>
    private void Hint(SpriteBatch batch, Texture2D pixel, UiFont font, string text, Point mouse, Rectangle view)
    {
        int size = Px(12), line = (int)(size * 1.35f);
        var lines = font.Wrap(text, Px(260), size).ToList();
        int width = (int)lines.Max(l => font.Measure(l, size).X) + 16, height = lines.Count * line + 10;
        int x = mouse.X + 16, y = mouse.Y + 16;
        if (x + width > view.Right)
            x = mouse.X - 12 - width;
        if (y + height > view.Bottom)
            y = mouse.Y - 12 - height;
        var box = new Rectangle(x, y, width, height);
        batch.Draw(pixel, new Rectangle(box.X - 1, box.Y - 1, box.Width + 2, box.Height + 2), Gold * 0.8f);
        batch.Draw(pixel, box, Ink * 0.95f);
        for (int i = 0; i < lines.Count; i++)
            font.Draw(batch, lines[i], new Vector2(x + 8, y + 5 + i * line), Text, size);
    }

    public void Dispose()
    {
        _preview?.Dispose();
        foreach (var texture in new[] { _background, _marble, _sheet, _rowBar, _scrollTop, _scrollTrack, _scrollBottom })
            texture.Dispose();
    }
}
