using System.Text.Json;
using Homestead.Assets;
using Homestead.Assets.Gfx;
using Homestead.Assets.Maps;
using Homestead.Simulation.Buildings;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Homestead.Client;

/// <summary>What the briefing asks the game to do.</summary>
public enum BriefingAction { None, Back, Start }

/// <summary>
/// A campaign mission's description as in the original, on the map selection's background (<c>startScreen</c> in
/// <c>data/ui.json</c>, GUI 8 of file 02): the mission text (GFX file 07, scrolls with the wheel, the arrow keys and the
/// scroll bar) in the upper panel, the map in the slanted window with the players' short names at their start
/// positions (or the original's finished picture, Amazons' campaign), the hints right of it, "Zurück" (Esc) and "Weiter" (Enter) below. The spoken briefing plays meanwhile.
/// </summary>
public sealed class BriefingScreen : IDisposable
{
    private const int Width = 800, Height = 600;

    private static readonly Color Ink = new(236, 214, 72);
    private static readonly Color Own = new(60, 200, 60);
    private static readonly Color Enemy = new(200, 30, 30);

    // Layout in the 800 × 600 space of the background, measured on it and on the original.
    private static readonly Rectangle TextArea = new(96, 64, 590, 222);
    private static readonly Rectangle HintArea = new(340, 338, 354, 198);
    private static readonly Rectangle BackButton = new(561, 555, 97, 25);
    private static readonly Rectangle NextButton = new(680, 555, 97, 25);
    private const int TextSize = 11, LineHeight = 13;
    private const int PreviewTop = 346, PreviewHeight = 161, PreviewWidth = 161, PreviewLeft = 160, PreviewSlant = 82;

    private readonly GraphicsDevice _device;
    private readonly Texture2D _background, _marble, _sheet, _scrollTop, _scrollTrack, _scrollBottom;
    private readonly Rectangle _handle;
    private readonly OriginalMap _map;
    private readonly Texture2D _preview;
    /// <summary>The original's finished picture of the mission for the slanted window, if it has one.</summary>
    private readonly Texture2D? _picture;
    private readonly string _text, _hint;
    private readonly IReadOnlyList<string> _labels;
    private readonly SoundEffect? _voice;
    private SoundEffectInstance? _speaking;
    private List<string>? _lines;
    private int _wrappedFor;
    private int _scroll;

    private float _scale = 1;
    private Vector2 _origin;
    private KeyboardState _previousKeys = Keyboard.GetState();
    private MouseState _previousMouse = Mouse.GetState();

    /// <param name="language">Language of the text table (<see cref="Lang.TextIndex"/>).</param>
    public BriefingScreen(GraphicsDevice device, GfxDirectory gfx, GameInstallation installation, CampaignMission mission, Language language,
        float volume)
    {
        _device = device;
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
        _background = Image("background");
        _marble = Image("marble");
        _sheet = Image("sheet");
        _scrollTop = Image("scrollTop");
        _scrollTrack = Image("scrollTrack");
        _scrollBottom = Image("scrollBottom");
        var handle = look.GetProperty("handle");
        _handle = new Rectangle(handle[0].GetInt32(), handle[1].GetInt32(), handle[2].GetInt32(), handle[3].GetInt32());

        _map = OriginalMap.Open(installation.FindPath(mission.Map) ?? throw new FileNotFoundException("Mission map missing", mission.Map));
        _preview = new Texture2D(device, _map.Area.Size, _map.Area.Size);
        _preview.SetData(MapOverview.Pixels(_map.Area));
        var texts = gfx.Open(7).ReadTexts(Lang.TextIndex(language), Lang.CodePage(language));
        _text = Clean(mission.Text < texts.Count ? texts[mission.Text] : "");
        _hint = Clean(mission.Hint < texts.Count ? texts[mission.Hint] : "");
        _labels = mission.Labels;
        if (mission.Preview is { } picture)
        {
            var bitmap = gfx.Open(picture.File).ReadSettler(picture.Sequence, picture.Frame);
            _picture = new Texture2D(device, bitmap.Width, bitmap.Height);
            _picture.SetData(bitmap.Pixels);
        }
        MapPath = _map.Path;

        if (volume > 0 && mission.Voice is not null && installation.FindPath(mission.Voice) is { } voice)
        {
            try
            {
                using var stream = File.OpenRead(voice);
                _voice = SoundEffect.FromStream(stream);
                _speaking = _voice.CreateInstance();
                _speaking.Volume = Math.Clamp(volume, 0, 1);
                _speaking.Play();
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                Console.WriteLine($"Briefing voice skipped: {voice} ({e.Message})");
            }
        }
    }

    /// <summary>The mission's map file.</summary>
    public string MapPath { get; }

    private static string Clean(string text) => text.Replace("\r", "").TrimEnd();

    private void Fit(Rectangle view)
    {
        _scale = Math.Min(view.Width / (float)Width, view.Height / (float)Height);
        _origin = new Vector2(view.X + (view.Width - Width * _scale) / 2, view.Y + (view.Height - Height * _scale) / 2);
    }

    private Vector2 ToScreen(Vector2 p) => _origin + p * _scale;

    private Rectangle ToScreen(Rectangle r)
    {
        var a = ToScreen(new Vector2(r.X, r.Y));
        var b = ToScreen(new Vector2(r.Right, r.Bottom));
        return new Rectangle((int)MathF.Round(a.X), (int)MathF.Round(a.Y), (int)MathF.Round(b.X) - (int)MathF.Round(a.X),
            (int)MathF.Round(b.Y) - (int)MathF.Round(a.Y));
    }

    private Point ToLayout(Point screen) => ((screen.ToVector2() - _origin) / _scale).ToPoint();

    private int Px(float size) => Math.Max(8, (int)MathF.Round(size * _scale));

    private static int VisibleLines => TextArea.Height / LineHeight;

    private int MaxScroll => Math.Max(0, (_lines?.Count ?? 0) - VisibleLines);

    private static Rectangle Track => new(TextArea.Right - 12, TextArea.Y, 12, TextArea.Height);

    public BriefingAction Update(Rectangle view)
    {
        Fit(view);
        var keys = Keyboard.GetState();
        var mouse = Mouse.GetState();
        bool Pressed(Keys key) => keys.IsKeyDown(key) && !_previousKeys.IsKeyDown(key);
        bool clicked = mouse.LeftButton == ButtonState.Released && _previousMouse.LeftButton == ButtonState.Pressed;
        int wheel = mouse.ScrollWheelValue - _previousMouse.ScrollWheelValue;
        var previous = _previousMouse;
        _previousKeys = keys;
        _previousMouse = mouse;
        var at = ToLayout(mouse.Position);

        if (Pressed(Keys.Escape) || clicked && BackButton.Contains(at))
            return BriefingAction.Back;
        if (Pressed(Keys.Enter) || clicked && NextButton.Contains(at))
            return BriefingAction.Start;
        if (wheel != 0)
            _scroll -= Math.Sign(wheel) * 3;
        if (Pressed(Keys.Down))
            _scroll++;
        if (Pressed(Keys.Up))
            _scroll--;
        if (Pressed(Keys.PageDown))
            _scroll += VisibleLines - 1;
        if (Pressed(Keys.PageUp))
            _scroll -= VisibleLines - 1;
        // The scroll bar: a click or drag on it moves there.
        if (mouse.LeftButton == ButtonState.Pressed && Track.Contains(ToLayout(previous.Position)) && MaxScroll > 0)
            _scroll = (int)MathF.Round((at.Y - Track.Y - 8) / (float)(Track.Height - 16) * MaxScroll);
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
        return BriefingAction.None;
    }

    public void Draw(SpriteBatch batch, Texture2D pixel, UiFont font, Rectangle view)
    {
        Fit(view);
        var page = ToScreen(new Rectangle(0, 0, Width, Height));
        if (page.X > view.X)
        {
            // Marble beside it as on the start screen (flipped on the right).
            var strip = new Rectangle(14, 0, (int)Math.Min(100, Math.Ceiling(page.X / _scale)), _marble.Height);
            batch.Draw(_marble, new Rectangle(view.X, view.Y, page.X - view.X, view.Height), strip, Color.White);
            batch.Draw(_marble, new Rectangle(page.Right, view.Y, view.Right - page.Right, view.Height), strip, Color.White,
                0, Vector2.Zero, SpriteEffects.FlipHorizontally, 0);
        }
        else if (page.Y > view.Y)
            batch.Draw(_marble, view, new Rectangle(14, 0, _marble.Width - 28, _marble.Height), Color.White);
        batch.Draw(_background, page, Color.White);

        // The text, wrapped for the current size.
        if (_lines is null || _wrappedFor != Px(TextSize))
        {
            _wrappedFor = Px(TextSize);
            _lines = Wrap(font, _text, TextArea.Width - 20);
            _scroll = Math.Clamp(_scroll, 0, MaxScroll);
        }
        for (int line = 0; line < VisibleLines && _scroll + line < _lines.Count; line++)
            Write(batch, font, _lines[_scroll + line], TextArea.X, TextArea.Y + line * LineHeight, Ink, TextSize);
        if (MaxScroll > 0)
        {
            var track = Track;
            batch.Draw(_scrollTop, ToScreen(new Rectangle(track.X, track.Y, 12, 8)), Color.White);
            batch.Draw(_scrollTrack, ToScreen(new Rectangle(track.X, track.Y + 8, 12, track.Height - 16)), Color.White);
            batch.Draw(_scrollBottom, ToScreen(new Rectangle(track.X, track.Bottom - 8, 12, 8)), Color.White);
            int height = Math.Max(16, (track.Height - 16) * VisibleLines / _lines.Count);
            int y = track.Y + 8 + (track.Height - 16 - height) * _scroll / MaxScroll;
            batch.Draw(_sheet, ToScreen(new Rectangle(track.X + 2, y, 8, height)), _handle, Color.White);
        }

        var hints = Wrap(font, _hint, HintArea.Width - 8);
        for (int line = 0; line < hints.Count && (line + 1) * LineHeight <= HintArea.Height; line++)
            Write(batch, font, hints[line], HintArea.X, HintArea.Y + line * LineHeight, Ink, TextSize);

        DrawPreview(batch, font);
        var cursor = Mouse.GetState().Position.ToVector2();
        TextButton.Draw(batch, pixel, font, ToScreen(BackButton), Lang.T("campaign.back"), cursor, size: Px(13));
        TextButton.Draw(batch, pixel, font, ToScreen(NextButton), Lang.T("campaign.next"), cursor, size: Px(13));
    }

    /// <summary>The map in the slanted window, one strip per row shifted like the map's rows, with the players' short names.</summary>
    private void DrawPreview(SpriteBatch batch, UiFont font)
    {
        // The original's picture fills the window (its corners are transparent).
        if (_picture is not null)
        {
            batch.Draw(_picture, ToScreen(new Rectangle(PreviewLeft - PreviewSlant + 1, PreviewTop + 1, _picture.Width, _picture.Height)), Color.White);
            return;
        }
        Vector2 Point(float u, float v) => new(PreviewLeft - PreviewSlant * v + PreviewWidth * u, PreviewTop + PreviewHeight * v);
        int size = _map.Area.Size;
        var top = ToScreen(new Vector2(0, PreviewTop));
        int rows = (int)MathF.Round(PreviewHeight * _scale);
        int width = (int)MathF.Round(PreviewWidth * _scale);
        for (int row = 0; row < rows; row++)
        {
            float v = (row + 0.5f) / rows;
            var left = ToScreen(Point(0, v));
            int source = Math.Min(size - 1, (int)(v * size));
            batch.Draw(_preview, new Rectangle((int)MathF.Round(left.X), (int)top.Y + row, width, 1), new Rectangle(0, source, size, 1), Color.White);
        }
        for (int player = 0; player < _labels.Count && player < _map.Players.Count; player++)
        {
            var home = _map.Players[player];
            var at = Point((home.StartX + 0.5f) / size, (home.StartY + 0.5f) / size);
            var measured = font.Measure(_labels[player], Px(18)) / _scale;
            Write(batch, font, _labels[player], at.X - measured.X / 2, at.Y - measured.Y / 2, player == 0 ? Own : Enemy, 18);
        }
    }

    /// <summary>Lines of <paramref name="text"/> at most <paramref name="width"/> wide (800 × 600 space); line breaks and empty lines kept.</summary>
    private List<string> Wrap(UiFont font, string text, int width)
    {
        var lines = new List<string>();
        foreach (string paragraph in text.Split('\n'))
        {
            if (paragraph.Trim().Length == 0)
            {
                lines.Add("");
                continue;
            }
            lines.AddRange(font.Wrap(paragraph, (int)(width * _scale), Px(TextSize)));
        }
        return lines;
    }

    private void Write(SpriteBatch batch, UiFont font, string text, float x, float y, Color color, int size)
    {
        var at = ToScreen(new Vector2(x, y));
        at = new Vector2(MathF.Round(at.X), MathF.Round(at.Y));
        font.Draw(batch, text, at + new Vector2(1, 1), Color.Black * 0.8f, Px(size));
        font.Draw(batch, text, at, color, Px(size));
    }

    public void Dispose()
    {
        _speaking?.Stop();
        _speaking?.Dispose();
        _voice?.Dispose();
        _preview.Dispose();
        _picture?.Dispose();
        foreach (var texture in new[] { _background, _marble, _sheet, _scrollTop, _scrollTrack, _scrollBottom })
            texture.Dispose();
    }
}
