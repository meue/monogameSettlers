using System.Text.Json;
using Homestead.Assets.Gfx;
using Homestead.Simulation.Buildings;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Homestead.Client;

/// <summary>
/// One row of a <see cref="ListScreen"/>: its name on the left, a note on the right (lit: gold, otherwise dim);
/// a row that cannot be chosen is shown faint.
/// </summary>
public sealed record ListRow(string Name, string Note, bool Lit = false, bool Enabled = true);

/// <summary>
/// A list to choose from on the original's background (<c>missionScreen</c> in <c>data/ui.json</c>, scaled from
/// 800 × 600, marble beside it): a heading, the rows, "Zurück" (Esc). A longer list scrolls (wheel, arrow keys,
/// the start screen's scroll bar). Used for the campaign's missions and the saved games.
/// </summary>
public sealed class ListScreen : IDisposable
{
    private const int Width = 800, Height = 600;
    private const int RowHeight = 34;

    private static readonly Color Text = new(240, 225, 190);
    private static readonly Color Gold = new(232, 196, 92);
    private static readonly Color Dim = new(175, 175, 185);

    private static readonly Rectangle BackButton = new(680, 555, 97, 25);

    private readonly Texture2D _background, _marble, _sheet, _scrollTop, _scrollTrack, _scrollBottom;
    private readonly Rectangle _headingBox, _list, _handle;
    private readonly string _heading;
    private readonly IReadOnlyList<ListRow> _rows;
    /// <summary>The first row shown.</summary>
    private int _scroll;

    private float _scale = 1;
    private Vector2 _origin;
    private KeyboardState _previousKeys = Keyboard.GetState();
    private MouseState _previousMouse = SdlMouse.GetState();

    /// <param name="shown">A row to show at first (scrolled so it is the lowest one on the page if need be).</param>
    public ListScreen(GraphicsDevice device, GfxDirectory gfx, string heading, IReadOnlyList<ListRow> rows, int shown = 0)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(BuildingCatalog.DefaultDataDirectory, "ui.json")));
        var look = document.RootElement.GetProperty("missionScreen");
        // The scroll bar's pieces are the start screen's.
        var scrollLook = document.RootElement.GetProperty("startScreen");
        Texture2D Image(string name, JsonElement? from = null)
        {
            var source = from ?? look;
            var bitmap = gfx.Open(source.GetProperty("file").GetInt32()).ReadGui(source.GetProperty(name).GetInt32());
            var texture = new Texture2D(device, bitmap.Width, bitmap.Height);
            texture.SetData(bitmap.Pixels);
            return texture;
        }
        static Rectangle Box(JsonElement e) => new(e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32(), e[3].GetInt32());
        _background = Image("background");
        _marble = Image("marble");
        _sheet = Image("sheet", scrollLook);
        _scrollTop = Image("scrollTop", scrollLook);
        _scrollTrack = Image("scrollTrack", scrollLook);
        _scrollBottom = Image("scrollBottom", scrollLook);
        _handle = Box(scrollLook.GetProperty("handle"));
        _headingBox = Box(look.GetProperty("heading"));
        _list = Box(look.GetProperty("list"));
        _heading = heading;
        _rows = rows;
        _scroll = Math.Clamp(shown - VisibleRows + 1, 0, MaxScroll);
    }

    /// <summary>The row (its index) clicked in the last <see cref="Update"/>, if any.</summary>
    public int? Chosen { get; private set; }

    private int VisibleRows => (_list.Height - 14) / RowHeight;

    private int MaxScroll => Math.Max(0, _rows.Count - VisibleRows);

    private Rectangle Track => new(_list.Right - 22, _list.Y + 14, 12, VisibleRows * RowHeight - 4);

    /// <summary>The place of the n-th shown row (narrower beside the scroll bar).</summary>
    private Rectangle Row(int shown) =>
        new(_list.X + 12, _list.Y + 14 + shown * RowHeight, _list.Width - 24 - (MaxScroll > 0 ? 18 : 0), RowHeight - 4);

    /// <summary>The row under the point (its index), if any.</summary>
    private int? At(Point layout)
    {
        for (int shown = 0; shown < VisibleRows && _scroll + shown < _rows.Count; shown++)
            if (Row(shown).Contains(layout))
                return _scroll + shown;
        return null;
    }

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

    private int Px(float size) => Math.Max(8, (int)MathF.Round(size * _scale));

    /// <summary>One frame; true: back. A chosen row is in <see cref="Chosen"/>.</summary>
    public bool Update(Rectangle view)
    {
        Fit(view);
        var keys = Keyboard.GetState();
        var mouse = SdlMouse.GetState();
        bool clicked = mouse.LeftButton == ButtonState.Released && _previousMouse.LeftButton == ButtonState.Pressed;
        bool Pressed(Keys key) => keys.IsKeyDown(key) && !_previousKeys.IsKeyDown(key);
        bool escape = Pressed(Keys.Escape);
        int wheel = mouse.ScrollWheelValue - _previousMouse.ScrollWheelValue;
        var previous = _previousMouse;
        _previousMouse = mouse;
        var at = ToLayout(mouse.Position);
        if (wheel != 0)
            _scroll -= Math.Sign(wheel);
        if (Pressed(Keys.Down))
            _scroll++;
        if (Pressed(Keys.Up))
            _scroll--;
        if (Pressed(Keys.PageDown))
            _scroll += VisibleRows - 1;
        if (Pressed(Keys.PageUp))
            _scroll -= VisibleRows - 1;
        _previousKeys = keys;
        // The scroll bar: a click or drag on it moves there.
        if (mouse.LeftButton == ButtonState.Pressed && Track.Contains(ToLayout(previous.Position)) && MaxScroll > 0)
            _scroll = (int)MathF.Round((at.Y - Track.Y - 8) / (float)(Track.Height - 16) * MaxScroll);
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
        Chosen = clicked && At(at) is { } row && _rows[row].Enabled ? row : null;
        return escape || clicked && BackButton.Contains(at);
    }

    /// <summary>Back from another screen: keys and buttons still held there do not count here.</summary>
    public void Resume()
    {
        _previousKeys = Keyboard.GetState();
        _previousMouse = SdlMouse.GetState();
    }

    public void Draw(SpriteBatch batch, Texture2D pixel, UiFont font, Rectangle view)
    {
        Fit(view);
        var page = ToScreen(new Rectangle(0, 0, Width, Height));
        if (page.X > view.X || page.Y > view.Y)
            batch.Draw(_marble, view, new Rectangle(14, 0, _marble.Width - 28, _marble.Height), Color.White);
        batch.Draw(_background, page, Color.White);

        WriteCentered(batch, font, _heading, _headingBox, Gold, 13);
        var hover = At(ToLayout(SdlMouse.GetState().Position));
        for (int index = _scroll; index < Math.Min(_rows.Count, _scroll + VisibleRows); index++)
        {
            var (name, note, gold, enabled) = _rows[index];
            var row = Row(index - _scroll);
            bool lit = enabled && index == hover;
            if (lit)
                batch.Draw(pixel, ToScreen(row), Gold * 0.18f);
            float noteWidth = font.Measure(note, Px(12)).X / _scale;
            Write(batch, font, Shorten(font, name, row.Width - 24 - noteWidth, 14), row.X + 8, row.Y + 7,
                !enabled ? Text * 0.5f : lit ? Gold : Text, 14);
            Write(batch, font, note, row.Right - 8 - noteWidth, row.Y + 9, gold ? Gold : Dim * (enabled ? 1 : 0.6f), 12);
        }
        if (MaxScroll > 0)
        {
            var track = Track;
            batch.Draw(_scrollTop, ToScreen(new Rectangle(track.X, track.Y, 12, 8)), Color.White);
            batch.Draw(_scrollTrack, ToScreen(new Rectangle(track.X, track.Y + 8, 12, track.Height - 16)), Color.White);
            batch.Draw(_scrollBottom, ToScreen(new Rectangle(track.X, track.Bottom - 8, 12, 8)), Color.White);
            int height = Math.Max(16, (track.Height - 16) * VisibleRows / _rows.Count);
            int y = track.Y + 8 + (track.Height - 16 - height) * _scroll / MaxScroll;
            batch.Draw(_sheet, ToScreen(new Rectangle(track.X + 2, y, 8, height)), _handle, Color.White);
        }
        TextButton.Draw(batch, pixel, font, ToScreen(BackButton), Lang.T("campaign.back"), SdlMouse.GetState().Position.ToVector2(), size: Px(13));
    }

    /// <summary>The text, cut with "…" to fit the width (in the 800 × 600 space).</summary>
    private string Shorten(UiFont font, string text, float width, int size)
    {
        if (font.Measure(text, Px(size)).X <= width * _scale)
            return text;
        while (text.Length > 1 && font.Measure(text + "…", Px(size)).X > width * _scale)
            text = text[..^1];
        return text + "…";
    }

    private void Write(SpriteBatch batch, UiFont font, string text, float x, float y, Color color, int size)
    {
        var at = _origin + new Vector2(x, y) * _scale;
        at = new Vector2(MathF.Round(at.X), MathF.Round(at.Y));
        font.Draw(batch, text, at + new Vector2(1, 1), Color.Black * 0.8f, Px(size));
        font.Draw(batch, text, at, color, Px(size));
    }

    private void WriteCentered(SpriteBatch batch, UiFont font, string text, Rectangle box, Color color, int size)
    {
        var measured = font.Measure(text, Px(size)) / _scale;
        Write(batch, font, text, box.Center.X - measured.X / 2, box.Center.Y - measured.Y / 2, color, size);
    }

    public void Dispose()
    {
        foreach (var texture in new[] { _background, _marble, _sheet, _scrollTop, _scrollTrack, _scrollBottom })
            texture.Dispose();
    }
}
