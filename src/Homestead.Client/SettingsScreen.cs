using System.Text.Json;
using Homestead.Assets.Gfx;
using Homestead.Simulation;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Economy;
using Homestead.Simulation.Magic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Homestead.Client;

/// <summary>
/// The settings screen from the title screen, on a background of the original's menus (<c>settingsScreen</c> in
/// <c>data/ui.json</c>, scaled from 800 × 600, marble beside it). The square boxes at the top hold one tab per topic
/// with an icon of the original's statistics menus (dark – pressed in – while chosen, bright otherwise; Tab and
/// Shift+Tab switch); the chosen topic's settings fill the large dark area, one row of choices each. "Ok" (Enter)
/// writes the <see cref="GameSettings"/> and <see cref="DisplaySettings"/> into <c>settings.json</c> (the game applies
/// the latter, <see cref="Saved"/>), "Abbrechen" (Esc) drops the changes, "Standard" resets the open tab (game
/// settings: <c>production.json</c>, <c>magic.json</c>).
/// </summary>
public sealed class SettingsScreen : IDisposable
{
    private const int Width = 800, Height = 600;

    private static readonly Color Text = new(240, 225, 190);
    private static readonly Color Gold = new(232, 196, 92);
    private static readonly Color Dim = new(175, 175, 185);
    private static readonly Color Error = new(240, 120, 100);

    /// <summary>Everything the screen edits.</summary>
    private sealed record State(GameSettings Game, DisplaySettings Display);

    /// <summary>
    /// One setting: its text key (label <c>settings.&lt;tab&gt;.&lt;key&gt;</c>, hint with <c>Hint</c> appended), a row of
    /// values to choose from – each with its label (a text key if it has a dot, otherwise shown as it is) or, with
    /// <paramref name="Swatches"/>, a color patch per value instead.
    /// </summary>
    private sealed record Choice(string Key, (string Label, int Value)[] Values, Func<State, int> Get, Func<State, int, State> Set,
        Color[]? Swatches = null)
    {
        public string Label => Lang.T(Key);
        public string Hint => Lang.T(Key + "Hint");
    }

    private static string ValueLabel(string label) => label.Contains('.') ? Lang.T(label) : label;

    /// <summary>
    /// A tab: its key in <c>tabs</c> of <c>settingsScreen</c> (also for capture runs; heading <c>settings.tabs.&lt;id&gt;</c>,
    /// line below it with <c>Subtitle</c> appended), settings, when they apply (a text key).
    /// </summary>
    private sealed record Tab(string Id, Choice[] Choices, string Note = "settings.notes.newGames")
    {
        public string Name => Lang.T("settings.tabs." + Id);
        public string Subtitle => Lang.T("settings.tabs." + Id + "Subtitle");
    }

    private static readonly (string, int)[] DepletionValues = [("100 %", 100), ("75 %", 75), ("50 %", 50), ("25 %", 25), ("10 %", 10), ("∞", 0)];

    private static readonly (string, int)[] VolumeValues = [("100 %", 100), ("75 %", 75), ("50 %", 50), ("25 %", 25), ("common.off", 0)];

    private static readonly (string, int)[] YesNo = [("common.yes", 1), ("common.no", 0)];

    private static readonly (string, int)[] OnOff = [("common.on", 1), ("common.off", 0)];

    private static Choice Depletion(string key, Func<Depletion, int> get, Func<Depletion, int, Depletion> set) =>
        new("settings.resources." + key, DepletionValues, s => get(s.Game.Depletion),
            (s, v) => s with { Game = s.Game with { Depletion = set(s.Game.Depletion, v) } });

    private static Choice Display(string key, (string, int)[] values, Func<DisplaySettings, int> get, Func<DisplaySettings, int, DisplaySettings> set,
        Color[]? swatches = null) =>
        new("settings.display." + key, values, s => get(s.Display), (s, v) => s with { Display = set(s.Display, v) }, swatches);

    private static readonly Tab[] Tabs =
    [
        new("resources",
        [
            Depletion("stone", d => d.Stone, (d, v) => d with { Stone = v }),
            Depletion("mountain", d => d.Mountain, (d, v) => d with { Mountain = v }),
            Depletion("fish", d => d.Fish, (d, v) => d with { Fish = v }),
        ]),
        new("economy", []),
        new("military",
        [
            new("settings.military.spellCost", [("settings.military.spellCostConstant", 0), ("+10 %", 10), ("+25 %", 25), ("+50 %", 50)],
                s => s.Game.SpellCostIncrease, (s, v) => s with { Game = s.Game with { SpellCostIncrease = v } }),
        ]),
        new("display",
        [
            Display("hardwareCursor", OnOff, d => d.HardwareCursor ? 1 : 0, (d, v) => d with { HardwareCursor = v == 1 }),
            Display("fullScreen", OnOff, d => d.FullScreen ? 1 : 0, (d, v) => d with { FullScreen = v == 1 }),
            // Each language by its own name.
            Display("language", [("Deutsch", (int)Language.German), ("English", (int)Language.English), ("Polski", (int)Language.Polish)],
                d => (int)d.Language, (d, v) => d with { Language = (Language)v }),
            Display("theme", Enum.GetValues<Theme>().Select(t => ("settings.display.themes." + t.ToString().ToLowerInvariant(), (int)t)).ToArray(),
                d => (int)d.Theme, (d, v) => d with { Theme = (Theme)v }),
            Display("music", VolumeValues, d => d.MusicVolume, (d, v) => d with { MusicVolume = v }),
            Display("peopleMusic", YesNo, d => d.PeopleMusic ? 1 : 0, (d, v) => d with { PeopleMusic = v == 1 }),
            Display("effects", VolumeValues, d => d.EffectsVolume, (d, v) => d with { EffectsVolume = v }),
            Display("autoSave", YesNo, d => d.AutoSave ? 1 : 0, (d, v) => d with { AutoSave = v == 1 }),
            Display("introVideo", YesNo, d => d.IntroVideo ? 1 : 0, (d, v) => d with { IntroVideo = v == 1 }),
            Display("preferredColor", PlayerColors.Palette.Select((_, i) => ("", i)).ToArray(), d => d.PreferredColor,
                (d, v) => d with { PreferredColor = v }, PlayerColors.Palette),
        ], "settings.notes.immediately"),
    ];

    // Layout in the 800 × 600 space of the background, measured on it.
    private static Rectangle TabBox(int tab) => new(19 + tab * 60, 19, 51, 50);
    private static readonly Rectangle TitleField = new(500, 20, 280, 44);
    private static readonly Rectangle Content = new(20, 88, 760, 452);
    private static readonly Rectangle NoteField = new(22, 556, 338, 22);
    private static readonly Rectangle DefaultButton = new(400, 555, 116, 30);
    private static readonly Rectangle OkButton = new(528, 555, 116, 30);
    private static readonly Rectangle CancelButton = new(656, 555, 116, 30);

    private const int RowTop = 150, RowHeight = 36;

    private static Rectangle Row(int row) => new(Content.X + 16, RowTop + row * RowHeight, Content.Width - 32, RowHeight - 6);

    /// <summary>The value's button: six fill the row, fewer keep that size up to a limit and start at the left.</summary>
    private static Rectangle ValueBox(Choice choice, int row, int value)
    {
        const int left = 300, width = 460;
        int slot = Math.Min(width / choice.Values.Length, 116);
        return new Rectangle(left + value * slot, Row(row).Y, slot - 4, 28);
    }

    private readonly Texture2D _background, _marble;
    private readonly (Texture2D Bright, Texture2D Dark)[] _icons;
    private readonly State _defaults;
    private State _state;
    private int _tab;
    private string? _error;

    private float _scale = 1;
    private Vector2 _origin;
    private KeyboardState _previousKeys = Keyboard.GetState();
    private MouseState _previousMouse = Mouse.GetState();

    public SettingsScreen(GraphicsDevice device, GfxDirectory gfx)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(BuildingCatalog.DefaultDataDirectory, "ui.json")));
        var look = document.RootElement.GetProperty("settingsScreen");
        Texture2D Image(int file, int index)
        {
            var bitmap = gfx.Open(file).ReadGui(index);
            var texture = new Texture2D(device, bitmap.Width, bitmap.Height);
            texture.SetData(bitmap.Pixels);
            return texture;
        }
        int file = look.GetProperty("file").GetInt32();
        _background = Image(file, look.GetProperty("background").GetInt32());
        _marble = Image(file, look.GetProperty("marble").GetInt32());
        var tabs = look.GetProperty("tabs");
        int tabFile = tabs.GetProperty("file").GetInt32();
        _icons = Tabs.Select(tab =>
        {
            var icon = tabs.GetProperty(tab.Id);
            return (Image(tabFile, icon[0].GetInt32()), Image(tabFile, icon[1].GetInt32()));
        }).ToArray();
        var production = ProductionConfig.Load();
        var magic = MagicConfig.Load();
        _defaults = new State(GameSettings.Defaults(production, magic), new DisplaySettings());
        _state = new State(GameSettings.Load(production, magic), DisplaySettings.Load());
    }

    /// <summary>The display settings once "Ok" saved them, for the game to apply.</summary>
    public DisplaySettings? Saved { get; private set; }

    /// <summary>For capture runs: shows the tab with this key (<c>resources</c>, <c>economy</c>, <c>military</c>, <c>display</c>).</summary>
    public void Show(string tab) => _tab = Math.Max(0, Array.FindIndex(Tabs, t => t.Id == tab));

    private void Fit(Rectangle view)
    {
        _scale = Math.Min(view.Width / (float)Width, view.Height / (float)Height);
        _origin = new Vector2(view.X + (view.Width - Width * _scale) / 2, view.Y + (view.Height - Height * _scale) / 2);
    }

    private Rectangle ToScreen(Rectangle r)
    {
        var a = _origin + new Vector2(r.X, r.Y) * _scale;
        var b = _origin + new Vector2(r.Right, r.Bottom) * _scale;
        return new Rectangle((int)MathF.Round(a.X), (int)MathF.Round(a.Y), (int)MathF.Round(b.X) - (int)MathF.Round(a.X),
            (int)MathF.Round(b.Y) - (int)MathF.Round(a.Y));
    }

    private Point ToLayout(Point screen) => ((screen.ToVector2() - _origin) / _scale).ToPoint();

    private int Px(int size) => Math.Max(8, (int)MathF.Round(size * _scale));

    /// <summary>True once the screen is closed (saved or cancelled).</summary>
    public bool Update(Rectangle view)
    {
        Fit(view);
        var keys = Keyboard.GetState();
        var mouse = Mouse.GetState();
        bool Pressed(Keys key) => keys.IsKeyDown(key) && !_previousKeys.IsKeyDown(key);
        bool escape = Pressed(Keys.Escape), enter = Pressed(Keys.Enter), tab = Pressed(Keys.Tab);
        bool clicked = mouse.LeftButton == ButtonState.Released && _previousMouse.LeftButton == ButtonState.Pressed;
        _previousKeys = keys;
        _previousMouse = mouse;
        var at = ToLayout(mouse.Position);

        if (escape || clicked && CancelButton.Contains(at))
            return true;
        if (enter || clicked && OkButton.Contains(at))
            return Save();
        if (tab)
            _tab = (_tab + (keys.IsKeyDown(Keys.LeftShift) || keys.IsKeyDown(Keys.RightShift) ? Tabs.Length - 1 : 1)) % Tabs.Length;
        if (!clicked)
            return false;
        if (DefaultButton.Contains(at))
            foreach (var choice in Tabs[_tab].Choices)
                _state = choice.Set(_state, choice.Get(_defaults));
        if (TabAt(at) is { } clickedTab)
            _tab = clickedTab;
        var choices = Tabs[_tab].Choices;
        for (int row = 0; row < choices.Length; row++)
            for (int value = 0; value < choices[row].Values.Length; value++)
                if (ValueBox(choices[row], row, value).Contains(at))
                    _state = choices[row].Set(_state, choices[row].Values[value].Value);
        return false;
    }

    /// <summary>Writes the settings; on failure the screen stays open with the reason.</summary>
    private bool Save()
    {
        try
        {
            _state.Game.Save();
            _state.Display.Save();
            Saved = _state.Display;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            _error = Lang.T("settings.notSaved", e.Message);
            return false;
        }
    }

    public void Draw(SpriteBatch batch, Texture2D pixel, UiFont font, Rectangle view)
    {
        Fit(view);
        var mouse = Mouse.GetState().Position;
        var hover = ToLayout(mouse);
        var page = ToScreen(new Rectangle(0, 0, Width, Height));
        if (page.X > view.X || page.Y > view.Y)
            batch.Draw(_marble, view, new Rectangle(14, 0, _marble.Width - 28, _marble.Height), Color.White);
        batch.Draw(_background, page, Color.White);

        for (int t = 0; t < Tabs.Length; t++)
        {
            var box = TabBox(t);
            var (bright, dark) = _icons[t];
            // The chosen tab pressed in (the dark icon), the others raised.
            var icon = t == _tab ? dark : bright;
            var at = new Rectangle(box.Center.X - icon.Width / 2, box.Center.Y - icon.Height / 2, icon.Width, icon.Height);
            batch.Draw(icon, ToScreen(at), Color.White);
        }

        string heading = Lang.T("settings.title");
        var title = font.Measure(heading, Px(18)) / _scale;
        Write(batch, font, heading, TitleField.Center.X - title.X / 2, TitleField.Center.Y - title.Y / 2, Gold, 18);

        var current = Tabs[_tab];
        Write(batch, font, current.Name, Content.X + 20, 104, Gold, 15);
        Write(batch, font, current.Subtitle, Content.X + 20, 124, Dim, 11);
        if (current.Choices.Length == 0)
            Write(batch, font, Lang.T("settings.nothingYet"), Content.X + 20, RowTop + 6, Text, 13);
        string? hint = null;
        for (int row = 0; row < current.Choices.Length; row++)
        {
            var choice = current.Choices[row];
            var rect = Row(row);
            Write(batch, font, choice.Label, rect.X + 4, rect.Y + 6, Text, 13);
            if (rect.Contains(hover))
                hint = choice.Hint;
            int chosen = choice.Get(_state);
            for (int value = 0; value < choice.Values.Length; value++)
            {
                var (label, number) = choice.Values[value];
                var box = ValueBox(choice, row, value);
                TextButton.Draw(batch, pixel, font, ToScreen(box), ValueLabel(label),
                    mouse.ToVector2(), size: Px(label == "∞" ? 18 : 12), selected: number == chosen);
                if (choice.Swatches is { } swatches)
                {
                    // The chosen color with a gold edge.
                    int inset = number == chosen ? 4 : 6;
                    if (number == chosen)
                        batch.Draw(pixel, ToScreen(new Rectangle(box.X + inset - 2, box.Y + inset - 2, box.Width - 2 * inset + 4, box.Height - 2 * inset + 4)), Gold);
                    batch.Draw(pixel, ToScreen(new Rectangle(box.X + inset, box.Y + inset, box.Width - 2 * inset, box.Height - 2 * inset)), swatches[value]);
                }
            }
        }
        if (hint is not null)
        {
            float y = Content.Bottom - 46;
            foreach (string line in font.Wrap(hint, Px(Content.Width - 40), Px(11)))
            {
                Write(batch, font, line, Content.X + 20, y, Dim, 11);
                y += 15;
            }
        }

        Write(batch, font, _error ?? Lang.T(current.Note), NoteField.X + 6, NoteField.Y + 4,
            _error is null ? Dim : Error, 11);
        TextButton.Draw(batch, pixel, font, ToScreen(DefaultButton), Lang.T("settings.defaults"), mouse.ToVector2(), size: Px(13));
        TextButton.Draw(batch, pixel, font, ToScreen(OkButton), Lang.T("common.ok"), mouse.ToVector2(), size: Px(13));
        TextButton.Draw(batch, pixel, font, ToScreen(CancelButton), Lang.T("common.cancel"), mouse.ToVector2(), size: Px(13));
    }

    private static int? TabAt(Point at)
    {
        for (int t = 0; t < Tabs.Length; t++)
            if (TabBox(t).Contains(at))
                return t;
        return null;
    }

    /// <summary>Text at a point of the 800 × 600 space, with a shadow.</summary>
    private void Write(SpriteBatch batch, UiFont font, string text, float x, float y, Color color, int size)
    {
        var at = _origin + new Vector2(x, y) * _scale;
        at = new Vector2(MathF.Round(at.X), MathF.Round(at.Y));
        font.Draw(batch, text, at + new Vector2(1, 1), Color.Black * 0.8f, Px(size));
        font.Draw(batch, text, at, color, Px(size));
    }

    public void Dispose()
    {
        _background.Dispose();
        _marble.Dispose();
        foreach (var (bright, dark) in _icons)
        {
            bright.Dispose();
            dark.Dispose();
        }
    }
}
