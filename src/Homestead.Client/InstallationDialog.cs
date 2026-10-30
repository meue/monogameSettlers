using Homestead.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Homestead.Client;

/// <summary>
/// Asks for the original installation when none is configured or the configured one fails the check (on every
/// start): a window of its own before the game, drawn without any original graphics. A path field (typing, Ctrl+V,
/// Enter opens it below), a folder browser (click enters, wheel scrolls), the installations found in the usual
/// places (<see cref="InstallationCheck.Detect"/>, click takes one) and the check of the chosen folder – or of the
/// installation found next to it (<see cref="InstallationCheck.FindRoot"/>). "Spiel starten" (Enter on a valid one)
/// sets <see cref="Chosen"/>; "Beenden" (Esc) leaves it null.
/// </summary>
public sealed class InstallationDialog : Game
{
    private const int Width = 900, Height = 620;

    private static readonly Color Background = new(24, 30, 40);
    private static readonly Color Panel = new(38, 48, 62);
    private static readonly Color Field = new(16, 20, 28);
    private static readonly Color Line = new(92, 110, 132);
    private static readonly Color Gold = new(232, 196, 92);
    private static readonly Color Text = new(236, 228, 210);
    private static readonly Color Dim = new(160, 168, 182);
    private static readonly Color Good = new(130, 210, 120);
    private static readonly Color Bad = new(236, 120, 100);
    private static readonly Color Hover = new(62, 78, 100);

    private static readonly Rectangle PathField = new(30, 150, 840, 30);
    private static readonly Rectangle Browser = new(30, 196, 520, 340);
    private static readonly Rectangle FoundBox = new(566, 196, 304, 130);
    private static readonly Rectangle CheckBox = new(566, 336, 304, 200);
    private static readonly Rectangle StartButton = new(700, 560, 170, 36);
    private static readonly Rectangle QuitButton = new(30, 560, 140, 36);
    private const int BrowserRow = 22, FoundRow = 20;

    private readonly GraphicsDeviceManager _graphics;
    private SpriteBatch _batch = null!;
    private Texture2D _pixel = null!;
    private UiFont _font = null!;

    private string _path;
    private string _browsed = "";
    private string[] _folders = [];
    private HashSet<string> _installations = [];
    private int _scroll;
    private readonly List<string> _found = [];
    private bool _searching = true;
    /// <summary>Nothing configured: the first installation found goes into the field.</summary>
    private bool _takeFirstFound;
    private int _checkVersion;
    private (int Version, string? Root, InstallationReport Report)? _check;
    private KeyboardState _previousKeys;
    private MouseState _previousMouse;
    private int _previousWheel;
    private double _now;

    private float _scale = 1;
    private Vector2 _origin;

    /// <summary>
    /// HOMESTEAD_CAPTURE_DIALOG=&lt;file.png&gt;: saves the dialog once the checks are done (after at most 10 s) and
    /// closes it, taking a valid installation as if started.
    /// </summary>
    private readonly string? _capture = Environment.GetEnvironmentVariable("HOMESTEAD_CAPTURE_DIALOG");

    /// <summary>The installation to use, once the player started the game with it.</summary>
    public string? Chosen { get; private set; }

    /// <param name="configured">The folder configured so far (it failed the check), to start from.</param>
    public InstallationDialog(string? configured)
    {
        _graphics = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = Width, PreferredBackBufferHeight = Height };
        IsMouseVisible = true;
        Window.AllowUserResizing = true;
        Window.Title = Lang.T("install.windowTitle");
        _path = configured ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _takeFirstFound = configured is null;
        Window.TextInput += (_, e) =>
        {
            if (e.Key == Keys.Back)
            {
                if (_path.Length > 0)
                    SetPath(_path[..^1], browse: false);
            }
            else if (!char.IsControl(e.Character))
                SetPath(_path + e.Character, browse: false);
        };
        Task.Run(() =>
        {
            foreach (string root in InstallationCheck.Detect())
                lock (_found)
                    _found.Add(root);
            _searching = false;
        });
    }

    protected override void LoadContent()
    {
        _batch = new SpriteBatch(GraphicsDevice);
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData([Color.White]);
        _font = new UiFont();
        SetPath(_path, browse: true);
        _previousKeys = Keyboard.GetState();
        _previousMouse = SdlMouse.GetState();
        _previousWheel = _previousMouse.ScrollWheelValue;
    }

    /// <summary>A new path: checked in the background; the browser follows when it is a folder (or always, <paramref name="browse"/>).</summary>
    private void SetPath(string path, bool browse)
    {
        _path = path;
        string? folder = Directory.Exists(path) ? path : browse ? NearestFolder(path) : null;
        if (folder is not null)
            Browse(folder);
        int version = ++_checkVersion;
        Task.Run(() =>
        {
            string? root = InstallationCheck.FindRoot(path);
            var report = InstallationCheck.Check(root ?? path);
            if (version == _checkVersion)
                _check = (version, root, report);
        });
    }

    private static string NearestFolder(string path)
    {
        string? folder = path;
        while (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
            folder = Path.GetDirectoryName(folder);
        return string.IsNullOrEmpty(folder) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : folder;
    }

    /// <summary>Lists the subfolders (hidden ones left out); those with GFX and Map in them are marked.</summary>
    private void Browse(string folder)
    {
        folder = Path.GetFullPath(folder);
        if (folder == _browsed)
            return;
        _browsed = folder;
        _scroll = 0;
        try
        {
            _folders = Directory.GetDirectories(folder).Where(d => !Path.GetFileName(d).StartsWith('.'))
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _folders = [];
        }
        _installations = _folders.Where(d => InstallationCheck.Subdirectory(d, "GFX") is not null
                                             && InstallationCheck.Subdirectory(d, "Map") is not null).ToHashSet();
    }

    private string? ValidRoot => _check is { Report.IsValid: true } check ? check.Report.Root : null;

    protected override void Update(GameTime gameTime)
    {
        SdlMouse.NextFrame();
        _now = gameTime.TotalGameTime.TotalSeconds;
        Fit();
        var keys = Keyboard.GetState();
        var mouse = SdlMouse.GetState();
        bool Pressed(Keys key) => keys.IsKeyDown(key) && !_previousKeys.IsKeyDown(key);
        bool clicked = mouse.LeftButton == ButtonState.Released && _previousMouse.LeftButton == ButtonState.Pressed;
        int wheel = mouse.ScrollWheelValue - _previousWheel;
        _previousKeys = keys;
        _previousMouse = mouse;
        _previousWheel = mouse.ScrollWheelValue;
        var at = ToLayout(mouse.Position);

        if (Pressed(Keys.Escape) || clicked && QuitButton.Contains(at))
            Exit();
        bool control = keys.IsKeyDown(Keys.LeftControl) || keys.IsKeyDown(Keys.RightControl);
        if (control && Pressed(Keys.V) && SdlClipboard.Text() is { } pasted)
            SetPath(pasted.Trim().Trim('"', '\'').Replace("file://", ""), browse: true);
        if (Pressed(Keys.Enter))
        {
            if (ValidRoot is not null)
                Start();
            else
                SetPath(_path, browse: true);
        }
        if (clicked && StartButton.Contains(at) && ValidRoot is not null)
            Start();

        int visible = Browser.Height / BrowserRow - 2;
        if (wheel != 0 && Browser.Contains(at))
            _scroll = Math.Clamp(_scroll - Math.Sign(wheel) * 3, 0, Math.Max(0, _folders.Length + 1 - visible));
        if (clicked && BrowserEntry(at) is { } entry)
            SetPath(entry < 0 ? Path.GetDirectoryName(_browsed) ?? _browsed : _folders[entry], browse: true);
        lock (_found)
            if (_takeFirstFound && _found.Count > 0)
            {
                _takeFirstFound = false;
                SetPath(_found[0], browse: true);
            }
        if (clicked && FoundEntry(at) is { } found)
            lock (_found)
                SetPath(_found[found], browse: true);
        base.Update(gameTime);
    }

    private void Start()
    {
        Chosen = ValidRoot;
        Exit();
    }

    /// <summary>The browser row under the point: -1 the parent folder, otherwise the subfolder's index.</summary>
    private int? BrowserEntry(Point at)
    {
        if (!Browser.Contains(at) || at.Y < Browser.Y + BrowserRow + 6)
            return null;
        int row = (at.Y - Browser.Y - BrowserRow - 6) / BrowserRow + _scroll;
        bool hasParent = Path.GetDirectoryName(_browsed) is not null;
        if (hasParent && row == 0)
            return -1;
        int index = row - (hasParent ? 1 : 0);
        return index >= 0 && index < _folders.Length ? index : null;
    }

    private int? FoundEntry(Point at)
    {
        if (!FoundBox.Contains(at) || at.Y < FoundBox.Y + 28)
            return null;
        int index = (at.Y - FoundBox.Y - 28) / FoundRow;
        lock (_found)
            return index < _found.Count ? index : null;
    }

    protected override void Draw(GameTime gameTime)
    {
        bool capture = _capture is not null && (!_searching && _check?.Version == _checkVersion || _now > 10);
        using var target = capture ? new RenderTarget2D(GraphicsDevice, GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height) : null;
        GraphicsDevice.SetRenderTarget(target);
        Fit();
        GraphicsDevice.Clear(Background);
        var hover = ToLayout(SdlMouse.GetState().Position);
        _batch.Begin(samplerState: SamplerState.LinearClamp);

        Write(Lang.T("install.heading"), 30, 24, Gold, 22);
        float y = 64;
        foreach (string line in _font.Wrap(Lang.T("install.explanation"), Px(840), Px(13)))
        {
            Write(line, 30, y, Text, 13);
            y += 19;
        }

        // The path field with a blinking caret.
        Box(PathField, Field, Line);
        string shown = Shorten(_path, PathField.Width - 20, 13, fromStart: true);
        Write(shown + (_now % 1 < 0.5 ? "|" : ""), PathField.X + 8, PathField.Y + 6, Text, 13);

        DrawBrowser(hover);
        DrawFound(hover);
        DrawCheck();

        Button(QuitButton, Lang.T("install.quit"), hover, enabled: true);
        Button(StartButton, Lang.T("install.start"), hover, enabled: ValidRoot is not null);
        Write(Lang.T("install.keys"), 190, 570, Dim, 11);
        _batch.End();
        if (target is not null)
        {
            GraphicsDevice.SetRenderTarget(null);
            using (var stream = File.Create(_capture!))
                target.SaveAsPng(stream, target.Width, target.Height);
            Start();
        }
        base.Draw(gameTime);
    }

    private void DrawBrowser(Point hover)
    {
        Box(Browser, Panel, Line);
        Write(Shorten(_browsed, Browser.Width - 16, 12, fromStart: true), Browser.X + 8, Browser.Y + 6, Gold, 12);
        bool hasParent = Path.GetDirectoryName(_browsed) is not null;
        var rows = (hasParent ? ["↑  .."] : Array.Empty<string>()).Concat(_folders.Select(Path.GetFileName)).ToArray();
        int visible = Browser.Height / BrowserRow - 2;
        for (int row = 0; row < visible && row + _scroll < rows.Length; row++)
        {
            int index = row + _scroll;
            var rect = new Rectangle(Browser.X + 4, Browser.Y + BrowserRow + 6 + row * BrowserRow, Browser.Width - 8, BrowserRow);
            if (rect.Contains(hover))
                _batch.Draw(_pixel, ToScreen(rect), Hover);
            int folder = index - (hasParent ? 1 : 0);
            bool installation = folder >= 0 && _installations.Contains(_folders[folder]);
            Write(Shorten(rows[index]!, rect.Width - 120, 13, fromStart: false), rect.X + 6, rect.Y + 3, installation ? Good : Text, 13);
            if (installation)
                Write(Lang.T("install.marked"), rect.Right - 108, rect.Y + 4, Good, 11);
        }
        if (rows.Length == 0)
            Write(Lang.T("install.noSubfolders"), Browser.X + 10, Browser.Y + BrowserRow + 10, Dim, 12);
        if (rows.Length > visible)
        {
            // Scroll position at the right edge.
            int track = Browser.Height - BrowserRow - 12;
            int handle = Math.Max(16, track * visible / rows.Length);
            int top = (track - handle) * _scroll / Math.Max(1, rows.Length - visible);
            _batch.Draw(_pixel, ToScreen(new Rectangle(Browser.Right - 6, Browser.Y + BrowserRow + 6 + top, 3, handle)), Line);
        }
    }

    private void DrawFound(Point hover)
    {
        Box(FoundBox, Panel, Line);
        Write(Lang.T(_searching ? "install.foundSearching" : "install.found"), FoundBox.X + 8, FoundBox.Y + 6, Gold, 12);
        lock (_found)
        {
            if (_found.Count == 0 && !_searching)
                Write(Lang.T("install.nothingFound"), FoundBox.X + 10, FoundBox.Y + 30, Dim, 12);
            for (int i = 0; i < _found.Count && 28 + (i + 1) * FoundRow <= FoundBox.Height; i++)
            {
                var rect = new Rectangle(FoundBox.X + 4, FoundBox.Y + 28 + i * FoundRow, FoundBox.Width - 8, FoundRow);
                if (rect.Contains(hover))
                    _batch.Draw(_pixel, ToScreen(rect), Hover);
                Write(Shorten(_found[i], rect.Width - 12, 12, fromStart: true), rect.X + 6, rect.Y + 3, Good, 12);
            }
        }
    }

    private void DrawCheck()
    {
        Box(CheckBox, Panel, Line);
        Write(Lang.T("install.check"), CheckBox.X + 8, CheckBox.Y + 6, Gold, 12);
        if (_check is not { } check || check.Version != _checkVersion)
        {
            Write(Lang.T("install.checking"), CheckBox.X + 10, CheckBox.Y + 30, Dim, 12);
            return;
        }
        var r = check.Report;
        float y = CheckBox.Y + 30;
        void Item(bool ok, string text)
        {
            Write(ok ? "✓" : "✗", CheckBox.X + 10, y, ok ? Good : Bad, 14);
            foreach (string line in _font.Wrap(text, Px(CheckBox.Width - 44), Px(12)))
            {
                Write(line, CheckBox.X + 30, y + 1, Text, 12);
                y += 17;
            }
            y += 4;
        }
        if (!r.Exists)
        {
            Item(false, Lang.T("install.noFolder"));
            return;
        }
        if (check.Root is not null && !SamePath(check.Root, _path))
            Item(true, Lang.T("install.foundAt", Shorten(check.Root, CheckBox.Width - 44, 12, fromStart: true)));
        Item(r.GfxOk, !r.HasGfx ? Lang.T("install.noGfx")
            : r.MissingGfx.Count == 0 ? Lang.T("install.gfx", r.GfxFiles)
            : Lang.T("install.gfxMissing", string.Join(", ", r.MissingGfx.Take(8)) + (r.MissingGfx.Count > 8 ? " …" : "")));
        Item(r.SoundsOk, !r.HasSounds ? Lang.T("install.noSounds")
            : r.MissingSounds.Count == 0 ? Lang.T("install.sounds") : Lang.T("install.soundsMissing", string.Join(", ", r.MissingSounds)));
        Item(r.MapsOk, !r.HasMaps ? Lang.T("install.noMaps") : r.Maps == 0 ? Lang.T("install.emptyMaps") : Lang.T("install.maps", r.Maps));
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void Button(Rectangle rect, string label, Point hover, bool enabled)
    {
        bool over = enabled && rect.Contains(hover);
        Box(rect, over ? Hover : Panel, enabled ? Gold : Line);
        var size = _font.Measure(label, Px(14)) / _scale;
        Write(label, rect.Center.X - size.X / 2, rect.Center.Y - size.Y / 2, enabled ? Text : Dim, 14);
    }

    private void Box(Rectangle rect, Color fill, Color edge)
    {
        var r = ToScreen(rect);
        _batch.Draw(_pixel, r, fill);
        _batch.Draw(_pixel, new Rectangle(r.X, r.Y, r.Width, 1), edge);
        _batch.Draw(_pixel, new Rectangle(r.X, r.Bottom - 1, r.Width, 1), edge);
        _batch.Draw(_pixel, new Rectangle(r.X, r.Y, 1, r.Height), edge);
        _batch.Draw(_pixel, new Rectangle(r.Right - 1, r.Y, 1, r.Height), edge);
    }

    /// <summary>The text cut to the width, with "…" at the start (paths: the end matters) or at the end.</summary>
    private string Shorten(string text, int width, int size, bool fromStart)
    {
        if (_font.Measure(text, Px(size)).X <= width * _scale)
            return text;
        while (text.Length > 1 && _font.Measure("…" + text, Px(size)).X > width * _scale)
            text = fromStart ? text[1..] : text[..^1];
        return fromStart ? "…" + text : text + "…";
    }

    private void Write(string text, float x, float y, Color color, int size)
    {
        var at = _origin + new Vector2(x, y) * _scale;
        _font.Draw(_batch, text, new Vector2(MathF.Round(at.X), MathF.Round(at.Y)), color, Px(size));
    }

    private void Fit()
    {
        var view = GraphicsDevice.Viewport.Bounds;
        _scale = Math.Min(view.Width / (float)Width, view.Height / (float)Height);
        _origin = new Vector2((view.Width - Width * _scale) / 2, (view.Height - Height * _scale) / 2);
    }

    private Rectangle ToScreen(Rectangle r)
    {
        var a = _origin + new Vector2(r.X, r.Y) * _scale;
        var b = _origin + new Vector2(r.Right, r.Bottom) * _scale;
        return new Rectangle((int)a.X, (int)a.Y, (int)b.X - (int)a.X, (int)b.Y - (int)a.Y);
    }

    private Point ToLayout(Point screen) => ((screen.ToVector2() - _origin) / _scale).ToPoint();

    private int Px(int size) => Math.Max(8, (int)MathF.Round(size * _scale));

    protected override void UnloadContent()
    {
        _pixel.Dispose();
        _batch.Dispose();
        _font.Dispose();
    }
}
