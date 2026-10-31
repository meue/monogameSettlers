using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Homestead.Assets.Gfx;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Persistence;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Homestead.Client;

/// <summary>What the title screen asks the game to do.</summary>
public enum TitleAction
{
    None,
    /// <summary>Load the game saved last (<see cref="SaveSlots.Latest"/>).</summary>
    Continue,
    /// <summary>Open the campaign (<see cref="CampaignScreen"/>).</summary>
    Campaign,
    /// <summary>Open the Mission CD's campaign.</summary>
    MissionCd,
    /// <summary>Open the Amazons' campaign.</summary>
    AmazonCampaign,
    /// <summary>Open the map selection (<see cref="StartScreen"/>).</summary>
    SinglePlayer,
    /// <summary>Open the list of saved games.</summary>
    Load,
    /// <summary>Open the settings (<see cref="SettingsScreen"/>).</summary>
    Settings,
    Quit,
}

/// <summary>
/// The title screen: the Gold Edition's main menu picture (<c>titleScreen</c> in <c>data/ui.json</c>, scaled from
/// 800 × 600 to the window, plain gold beside it) with a column of engraved buttons on its left side as in the
/// original, "Beenden" set apart at the bottom. On top "Weiterspielen" (the game saved last; dimmed without one), then the campaigns ("Kampagne", "Missions-CD", "Amazonen-Kampagne"), then the
/// free games; "Spiel laden" opens the list of saved games, "Einstellungen" the settings screen; Esc quits. Set apart below, readme, contribution and licenses open a popup (the
/// original's message box) offering to show <c>README.md</c>, <c>CONTRIBUTING.md</c> or <c>THIRD_PARTY_NOTICES.md</c> of the
/// repository in the browser.
/// </summary>
public sealed class TitleScreen : IDisposable
{
    private const int Width = 800, Height = 600;

    private const string Repository = "https://github.com/meue/monogameSettlers";

    /// <summary>A popup offering to open a page of the repository in the browser; its text is <c>title.links.&lt;name&gt;</c>.</summary>
    private sealed record Link(string Name, string Url)
    {
        public string Text => Lang.T("title.links." + Name);
    }

    private static readonly Link Readme = new("readme", Repository + "/blob/main/README.md");
    private static readonly Link Contributing = new("contribution", Repository + "/blob/main/CONTRIBUTING.md");
    private static readonly Link Licenses = new("licenses", Repository + "/blob/main/THIRD_PARTY_NOTICES.md");

    /// <summary>The buttons (label <c>title.buttons.&lt;key&gt;</c>): what they ask the game to do, or the popup (<c>Link</c>).</summary>
    private static readonly (string Key, TitleAction Action, Link? Link)[] Buttons =
    [
        ("continue", TitleAction.Continue, null),
        ("campaign", TitleAction.Campaign, null),
        ("missionCd", TitleAction.MissionCd, null),
        ("amazonCampaign", TitleAction.AmazonCampaign, null),
        ("singlePlayer", TitleAction.SinglePlayer, null),
        ("settings", TitleAction.Settings, null),
        ("load", TitleAction.Load, null),
        ("readme", TitleAction.None, Readme),
        ("contribution", TitleAction.None, Contributing),
        ("licenses", TitleAction.None, Licenses),
    ];

    /// <summary>Buttons shown dimmed, without effect: what is not there yet.</summary>
    private static readonly HashSet<string> NotYet = [];

    /// <summary>Whether there is a saved game to continue (looked at on opening and on coming back).</summary>
    private bool _canContinue = SaveSlots.Latest() is not null;

    private bool Dimmed(string key) => NotYet.Contains(key) || key == "continue" && !_canContinue;

    private static string Label(string key) => Lang.T("title.buttons." + key);

    // The original's engraved buttons: a fill a little darker than the background, a dark line around it and a light
    // one just outside to the lower right (per theme in ui.json).
    private readonly Color Fill, PressedFill, Dark, Light, Shadow;
    /// <summary>Right edge of the version in the 800 × 600 space.</summary>
    private readonly float _versionRight = 773;
    private static readonly Color Text = new(250, 246, 225);
    private static readonly Color HoverText = new(255, 236, 140);
    private static readonly Color Version = new(240, 222, 70);

    private readonly Texture2D _plain, _popupImage;
    /// <summary>The main menu picture per language (the Gold Edition has a German and an English one).</summary>
    private readonly Dictionary<Language, Texture2D> _backgrounds = new();
    private readonly Rectangle _plainStrip;
    private Link? _popup;
    private bool _browserFailed;
    /// <summary>A saved game that did not load: told in the same message box, with an Ok.</summary>
    private SaveLoadFailure? _failure;

    private float _scale = 1;
    private Vector2 _origin;
    private KeyboardState _previousKeys = Keyboard.GetState();
    private MouseState _previousMouse = Mouse.GetState();

    /// <param name="theme">The look (<see cref="DisplaySettings.Theme"/>); normal is gold.</param>
    public TitleScreen(GraphicsDevice device, GfxDirectory gfx, Theme theme)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(BuildingCatalog.DefaultDataDirectory, "ui.json")));
        var screen = document.RootElement.GetProperty("titleScreen");
        var look = screen.GetProperty("themes").GetProperty(theme switch
        {
            Theme.Classic => "classic",
            Theme.Amazons => "amazons",
            _ => "gold",
        });
        Color Rgb(string name)
        {
            var rgb = look.GetProperty(name);
            return new Color(rgb[0].GetInt32(), rgb[1].GetInt32(), rgb[2].GetInt32());
        }
        (Fill, PressedFill, Dark, Light, Shadow) = (Rgb("fill"), Rgb("pressed"), Rgb("dark"), Rgb("light"), Rgb("shadow"));
        if (look.TryGetProperty("versionRight", out var versionRight))
            _versionRight = versionRight.GetSingle();
        var file = gfx.Open(look.GetProperty("file").GetInt32());
        Texture2D Image(string name) => Gui(look.GetProperty(name).GetInt32());
        Texture2D Gui(int index)
        {
            var bitmap = file.ReadGui(index);
            var texture = new Texture2D(device, bitmap.Width, bitmap.Height);
            texture.SetData(bitmap.Pixels);
            return texture;
        }
        // A language without a picture of its own (Polish) shows the English one.
        var backgrounds = look.GetProperty("background");
        foreach (var language in Enum.GetValues<Language>())
            _backgrounds[language] = Gui((backgrounds.TryGetProperty(Lang.Code(language), out var index) ? index : backgrounds.GetProperty("en")).GetInt32());
        _plain = Image("plain");
        var popup = screen.GetProperty("popup");
        var popupBitmap = gfx.Open(popup[0].GetInt32()).ReadGui(popup[1].GetInt32());
        _popupImage = new Texture2D(device, popupBitmap.Width, popupBitmap.Height);
        _popupImage.SetData(popupBitmap.Pixels);
        var strip = look.GetProperty("plainStrip");
        _plainStrip = new Rectangle(strip[0].GetInt32(), strip[1].GetInt32(), strip[2].GetInt32(), strip[3].GetInt32());
    }

    // Layout in the 800 × 600 space of the picture, measured on the original menu.
    // The campaigns sit a little apart from "Weiterspielen", the free games (from the first one on) from the campaigns,
    // the links to the repository (from the first one on) from the game's buttons.
    private const int FirstCampaign = 1, FirstGame = 4, FirstLink = 7, GroupGap = 16;

    /// <summary>The version of this build (from the git tags, docs/VERSIONING.md), with the commit shortened.</summary>
    private static readonly string GameVersion = "v" + (typeof(TitleScreen).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion is { } version
        ? version.Split('+') is [var number, var commit] ? $"{number}+{commit[..Math.Min(7, commit.Length)]}" : version
        : "?");

    private static Rectangle Button(int index) =>
        new(74, 19 + index * 40 + (index >= FirstCampaign ? GroupGap : 0) + (index >= FirstGame ? GroupGap : 0)
                                + (index >= FirstLink ? GroupGap : 0), 175, 31);

    private static readonly Rectangle QuitButton = new(74, 540, 175, 31);

    /// <summary>The popup (its picture's size, centered), the dark text area in it and its two buttons on the marble below.</summary>
    private static readonly Rectangle Popup = new(200, 200, 401, 201);
    private static readonly Rectangle PopupText = new(Popup.X + 28, Popup.Y + 24, Popup.Width - 56, 112);
    private static readonly Rectangle OpenButton = new(Popup.X + 24, Popup.Y + 149, 170, 30);
    private static readonly Rectangle CancelButton = new(Popup.Right - 24 - 170, Popup.Y + 149, 170, 30);
    private static readonly Rectangle OkButton = new(Popup.Center.X - 85, Popup.Y + 149, 170, 30);

    private void Fit(Rectangle view)
    {
        _scale = Math.Min(view.Width / (float)Width, view.Height / (float)Height);
        _origin = new Vector2(view.X + (view.Width - Width * _scale) / 2, view.Y + (view.Height - Height * _scale) / 2);
    }

    private Rectangle ToScreen(Rectangle r)
    {
        var a = _origin + new Vector2(r.X, r.Y) * _scale;
        var b = _origin + new Vector2(r.Right, r.Bottom) * _scale;
        return new Rectangle((int)MathF.Round(a.X), (int)MathF.Round(a.Y), (int)MathF.Round(b.X - a.X), (int)MathF.Round(b.Y - a.Y));
    }

    private Point ToLayout(Point screen) => ((screen.ToVector2() - _origin) / _scale).ToPoint();

    private int Px(float size) => Math.Max(8, (int)MathF.Round(size * _scale));

    public TitleAction Update(Rectangle view)
    {
        Fit(view);
        var keys = Keyboard.GetState();
        var mouse = Mouse.GetState();
        bool escape = keys.IsKeyDown(Keys.Escape) && !_previousKeys.IsKeyDown(Keys.Escape);
        bool enter = keys.IsKeyDown(Keys.Enter) && !_previousKeys.IsKeyDown(Keys.Enter);
        bool clicked = mouse.LeftButton == ButtonState.Released && _previousMouse.LeftButton == ButtonState.Pressed;
        _previousKeys = keys;
        _previousMouse = mouse;
        var at = ToLayout(mouse.Position);

        if (_failure is not null)
        {
            if (escape || enter || clicked && OkButton.Contains(at))
                _failure = null;
            return TitleAction.None;
        }
        if (_popup is not null)
        {
            if (escape || clicked && CancelButton.Contains(at))
                _popup = null;
            else if (enter || clicked && OpenButton.Contains(at))
                OpenInBrowser(_popup);
            return TitleAction.None;
        }
        if (escape)
            return TitleAction.Quit;
        if (enter)
            return TitleAction.SinglePlayer;
        if (!clicked)
            return TitleAction.None;
        if (QuitButton.Contains(at))
            return TitleAction.Quit;
        for (int index = 0; index < Buttons.Length; index++)
            if (Button(index).Contains(at) && !Dimmed(Buttons[index].Key))
            {
                var (_, action, link) = Buttons[index];
                if (link is null)
                    return action;
                ShowLink(link.Name);
                return TitleAction.None;
            }
        return TitleAction.None;
    }

    /// <summary>Back from another screen: keys and buttons still held there (Esc, Enter, a click) do not count here.</summary>
    public void Resume()
    {
        _previousKeys = Keyboard.GetState();
        _previousMouse = Mouse.GetState();
        _canContinue = SaveSlots.Latest() is not null;
    }

    /// <summary>
    /// For capture runs: opens the popup of <c>readme</c>, <c>contribution</c> or <c>licenses</c>, or the message about a
    /// saved game that did not load (<c>loadError</c>).
    /// </summary>
    public void Show(string name)
    {
        if (name == "loadError")
            ShowLoadFailure(SaveLoadFailure.Of(new SaveIncompatibleException("A type of the saved game no longer exists: capture")));
        else
            ShowLink(name);
    }

    /// <summary>The chosen saved game did not load: told over the title screen.</summary>
    public void ShowLoadFailure(SaveLoadFailure failure) => _failure = failure;

    private void ShowLink(string name)
    {
        _popup = new[] { Readme, Contributing, Licenses }.FirstOrDefault(link => link.Name == name);
        _browserFailed = false;
    }

    /// <summary>Hands the page to the system's browser; the popup closes, or stays with a note if that failed.</summary>
    private void OpenInBrowser(Link link)
    {
        try
        {
            Process.Start(new ProcessStartInfo(link.Url) { UseShellExecute = true });
            _popup = null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException
                                              or PlatformNotSupportedException)
        {
            _browserFailed = true;
        }
    }

    public void Draw(SpriteBatch batch, Texture2D pixel, UiFont font, Rectangle view)
    {
        Fit(view);
        var hover = ToLayout(Mouse.GetState().Position);
        bool held = Mouse.GetState().LeftButton == ButtonState.Pressed;
        batch.Draw(_plain, view, _plainStrip, Color.White);
        batch.Draw(_backgrounds[Lang.Current], ToScreen(new Rectangle(0, 0, Width, Height)), Color.White);

        for (int index = 0; index < Buttons.Length; index++)
            Engraved(batch, pixel, font, Button(index), Label(Buttons[index].Key), hover, held,
                _popup is not null && Buttons[index].Link == _popup, Dimmed(Buttons[index].Key));
        Engraved(batch, pixel, font, QuitButton, Label("quit"), hover, held, false);

        Write(batch, font, Lang.T("title.disclaimer"), 27, 577, Version, 10);
        float width = font.Measure(GameVersion, Px(10)).X / _scale;
        Write(batch, font, GameVersion, _versionRight - width, 577, Version, 10);
        if (_popup is not null)
            DrawPopup(batch, pixel, font, _popup);
        if (_failure is not null)
            DrawFailure(batch, pixel, font, _failure);
    }

    /// <summary>The message box over a dimmed screen with what went wrong, the error code and an Ok.</summary>
    private void DrawFailure(SpriteBatch batch, Texture2D pixel, UiFont font, SaveLoadFailure failure)
    {
        batch.Draw(pixel, ToScreen(new Rectangle(0, 0, Width, Height)), Color.Black * 0.45f);
        batch.Draw(_popupImage, ToScreen(Popup), Color.White);
        float y = PopupText.Y;
        foreach (string line in font.Wrap(Lang.T("title.loadFailed." + failure.Problem.ToString().ToLowerInvariant()), Px(PopupText.Width), Px(12)))
        {
            Write(batch, font, line, PopupText.X, y, Text, 12);
            y += 16;
        }
        Write(batch, font, Lang.T("title.loadFailed.code", failure.Code), PopupText.X, y + 8, HoverText, 12);
        TextButton.Draw(batch, pixel, font, ToScreen(OkButton), Lang.T("common.ok"), Mouse.GetState().Position.ToVector2(), size: Px(12));
    }

    /// <summary>The popup over a dimmed screen: the text, the address and what happens, then the two buttons.</summary>
    private void DrawPopup(SpriteBatch batch, Texture2D pixel, UiFont font, Link link)
    {
        batch.Draw(pixel, ToScreen(new Rectangle(0, 0, Width, Height)), Color.Black * 0.45f);
        batch.Draw(_popupImage, ToScreen(Popup), Color.White);
        float y = PopupText.Y;
        void Paragraph(string text, Color color, int size)
        {
            foreach (string line in font.Wrap(text, Px(PopupText.Width), Px(size)))
            {
                Write(batch, font, line, PopupText.X, y, color, size);
                y += size + 4;
            }
            y += 6;
        }
        Paragraph(link.Text, Text, 12);
        Paragraph(_browserFailed
            ? Lang.T("title.popup.browserFailed")
            : Lang.T("title.popup.willOpen"), Text, 12);
        Paragraph(link.Url, HoverText, 10);

        var cursor = Mouse.GetState().Position.ToVector2();
        TextButton.Draw(batch, pixel, font, ToScreen(OpenButton), Lang.T("title.popup.open"), cursor, size: Px(12));
        TextButton.Draw(batch, pixel, font, ToScreen(CancelButton), Lang.T("common.cancel"), cursor, size: Px(12));
    }

    /// <summary>
    /// One of the original's engraved buttons: pressed (darker) while open or held, the text lit under the mouse;
    /// <paramref name="dimmed"/>: faint text, never lit or pressed.
    /// </summary>
    private void Engraved(SpriteBatch batch, Texture2D pixel, UiFont font, Rectangle rect, string label, Point hover, bool held, bool open,
        bool dimmed = false)
    {
        bool over = rect.Contains(hover) && !dimmed;
        bool down = open || over && held;
        batch.Draw(pixel, ToScreen(rect), down ? PressedFill : Fill);
        Frame(batch, pixel, rect, Dark, Light);
        var size = font.Measure(label, Px(12)) / _scale;
        Write(batch, font, label, rect.Center.X - size.X / 2 + (down ? 1 : 0), rect.Center.Y - size.Y / 2 + (down ? 1 : 0),
            dimmed ? Dark : over || open ? HoverText : Text, 12);
    }

    /// <summary>An engraved frame: the dark line on the edge, the light one a pixel outside to the lower right.</summary>
    private void Frame(SpriteBatch batch, Texture2D pixel, Rectangle rect, Color dark, Color light)
    {
        var outer = ToScreen(rect);
        int line = Math.Max(1, (int)MathF.Round(_scale));
        void Box(Rectangle r, Color color)
        {
            batch.Draw(pixel, new Rectangle(r.X, r.Y, r.Width, line), color);
            batch.Draw(pixel, new Rectangle(r.X, r.Bottom - line, r.Width, line), color);
            batch.Draw(pixel, new Rectangle(r.X, r.Y, line, r.Height), color);
            batch.Draw(pixel, new Rectangle(r.Right - line, r.Y, line, r.Height), color);
        }
        Box(new Rectangle(outer.X + line, outer.Y + line, outer.Width, outer.Height), light);
        Box(outer, dark);
    }

    /// <summary>Text at a point of the 800 × 600 space with a dark shadow, as on the original's buttons.</summary>
    private void Write(SpriteBatch batch, UiFont font, string text, float x, float y, Color color, int size)
    {
        var at = _origin + new Vector2(x, y) * _scale;
        at = new Vector2(MathF.Round(at.X), MathF.Round(at.Y));
        font.Draw(batch, text, at + new Vector2(1, 1), Shadow * 0.9f, Px(size));
        font.Draw(batch, text, at, color, Px(size));
    }

    public void Dispose()
    {
        foreach (var background in _backgrounds.Values)
            background.Dispose();
        _plain.Dispose();
        _popupImage.Dispose();
    }
}
