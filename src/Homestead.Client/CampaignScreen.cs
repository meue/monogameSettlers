using System.Text.Json;
using Homestead.Assets.Gfx;
using Homestead.Simulation.Buildings;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Homestead.Client;

/// <summary>
/// A campaign's first page: choosing the side on the original's background (<c>campaignScreens</c> in
/// <c>data/ui.json</c> by campaign, scaled from 800 × 600, marble beside it) – a people in the settlers' campaign (its
/// picture framed in gold under the mouse; on the Mission CD's a brighter picture of the figure), the Amazons or the
/// three peoples in theirs (a brighter picture of the figures laid over them). A click on a side's picture or name chooses it (<see cref="Chosen"/>), "Zurück" (Esc; the
/// original has none) goes back to the title screen.
/// </summary>
public sealed class CampaignScreen : IDisposable
{
    private const int Width = 800, Height = 600;

    private static readonly Color Text = new(240, 225, 190);
    private static readonly Color Gold = new(232, 196, 92);

    private static readonly Rectangle BackButton = new(680, 555, 97, 25);

    private readonly Texture2D _background, _marble;
    private readonly Rectangle _heading;
    private readonly int _headingSize;
    /// <summary>
    /// Per side: its id, picture and name field, the brighter picture laid over it under the mouse (if any), the text
    /// key for the name field (if not the side's name).
    /// </summary>
    private readonly (string Side, Rectangle Picture, Rectangle Name, Texture2D? Lit, string? Label)[] _sides;
    private readonly IReadOnlyDictionary<string, CampaignSide> _config;

    private float _scale = 1;
    private Vector2 _origin;
    private KeyboardState _previousKeys = Keyboard.GetState();
    private MouseState _previousMouse = SdlMouse.GetState();

    /// <param name="campaign">The campaign (<c>settlers</c>, <c>missionCd</c>, <c>amazons</c>).</param>
    public CampaignScreen(GraphicsDevice device, GfxDirectory gfx, string campaign)
    {
        Campaign = campaign;
        _config = CampaignConfig.Load().Campaigns[campaign];
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(BuildingCatalog.DefaultDataDirectory, "ui.json")));
        var look = document.RootElement.GetProperty("campaignScreens").GetProperty(campaign);
        int file = look.GetProperty("file").GetInt32();
        Texture2D Texture(GfxBitmap bitmap)
        {
            var texture = new Texture2D(device, bitmap.Width, bitmap.Height);
            texture.SetData(bitmap.Pixels);
            return texture;
        }
        // An image of the screen's file, or [file, image] of another.
        Texture2D Image(JsonElement e) => e.ValueKind == JsonValueKind.Array
            ? Texture(gfx.Open(e[0].GetInt32()).ReadGui(e[1].GetInt32()))
            : Texture(gfx.Open(file).ReadGui(e.GetInt32()));
        static Rectangle Box(JsonElement e) => new(e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32(), e[3].GetInt32());
        _background = Image(look.GetProperty("background"));
        _marble = Image(look.GetProperty("marble"));
        _heading = Box(look.GetProperty("heading"));
        _headingSize = look.TryGetProperty("headingSize", out var headingSize) ? headingSize.GetInt32() : 15;
        // The brighter picture: [file, sequence, frame] of a settler sequence, placed on the picture's rectangle.
        _sides = look.GetProperty("sides").EnumerateObject()
            .Select(s => (s.Name, Box(s.Value.GetProperty("picture")), Box(s.Value.GetProperty("name")),
                s.Value.TryGetProperty("lit", out var lit) ? Texture(gfx.Open(lit[0].GetInt32()).ReadSettler(lit[1].GetInt32(), lit[2].GetInt32())) : null,
                s.Value.TryGetProperty("label", out var label) ? label.GetString() : null))
            .ToArray();
    }

    /// <summary>The campaign whose sides are shown.</summary>
    public string Campaign { get; }

    /// <summary>The side clicked in the last <see cref="Update"/>, if any.</summary>
    public string? Chosen { get; private set; }

    /// <summary>For capture runs: the side shown as if under the mouse.</summary>
    public string? Hovered { get; set; }

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

    /// <summary>The side under the mouse (its picture or its name).</summary>
    private string? At(Point layout) =>
        _sides.FirstOrDefault(s => s.Picture.Contains(layout) || s.Name.Contains(layout)).Side;

    /// <summary>One frame; true: back to the title screen. A chosen side is in <see cref="Chosen"/>.</summary>
    public bool Update(Rectangle view)
    {
        Fit(view);
        var keys = Keyboard.GetState();
        var mouse = SdlMouse.GetState();
        bool escape = keys.IsKeyDown(Keys.Escape) && !_previousKeys.IsKeyDown(Keys.Escape);
        bool clicked = mouse.LeftButton == ButtonState.Released && _previousMouse.LeftButton == ButtonState.Pressed;
        _previousKeys = keys;
        _previousMouse = mouse;
        var at = ToLayout(mouse.Position);
        Chosen = clicked ? At(at) : null;
        if (Chosen is not null)
            UiSounds.Toggle();
        return escape || clicked && BackButton.Contains(at);
    }

    /// <summary>Back from the video: keys and buttons still held there do not count here.</summary>
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

        WriteCentered(batch, font, Lang.T(Campaign == "amazons" ? "campaign.chooseSide" : "campaign.choosePeople"), _heading, Gold, _headingSize);
        var hover = Hovered ?? At(ToLayout(SdlMouse.GetState().Position));
        foreach (var (side, picture, name, brighter, key) in _sides)
        {
            bool lit = side == hover;
            if (lit && brighter is not null)
                batch.Draw(brighter, ToScreen(picture), Color.White);
            else if (lit)
                Outline(batch, pixel, ToScreen(new Rectangle(picture.X - 3, picture.Y - 3, picture.Width + 6, picture.Height + 6)), Gold);
            string label = Lang.T(key ?? _config[side].Name);
            bool Fits(string text, int size) => font.Measure(text, Px(size)).X / _scale <= name.Width - 6;
            int size = Fits(label, 16) ? 16 : Fits(label, 14) ? 14 : 12;
            // Too wide even small: two lines, broken at the space nearest the middle.
            int space = label.Length / 2;
            while (!Fits(label, size) && space > 0 && label[space] != ' ')
                space--;
            if (!Fits(label, size) && space > 0)
            {
                WriteCentered(batch, font, label[..space], name with { Height = name.Height / 2 + 2 }, lit ? Gold : Text, size);
                WriteCentered(batch, font, label[(space + 1)..], name with { Y = name.Center.Y - 2, Height = name.Height / 2 + 2 }, lit ? Gold : Text, size);
            }
            else
                WriteCentered(batch, font, label, name, lit ? Gold : Text, size);
        }
        TextButton.Draw(batch, pixel, font, ToScreen(BackButton), Lang.T("campaign.back"), SdlMouse.GetState().Position.ToVector2(), size: Px(13));
    }

    private void Outline(SpriteBatch batch, Texture2D pixel, Rectangle r, Color color)
    {
        int line = Math.Max(2, (int)MathF.Round(2 * _scale));
        batch.Draw(pixel, new Rectangle(r.X, r.Y, r.Width, line), color);
        batch.Draw(pixel, new Rectangle(r.X, r.Bottom - line, r.Width, line), color);
        batch.Draw(pixel, new Rectangle(r.X, r.Y, line, r.Height), color);
        batch.Draw(pixel, new Rectangle(r.Right - line, r.Y, line, r.Height), color);
    }

    private void WriteCentered(SpriteBatch batch, UiFont font, string text, Rectangle box, Color color, int size)
    {
        var measured = font.Measure(text, Px(size)) / _scale;
        var at = _origin + new Vector2(box.Center.X - measured.X / 2, box.Center.Y - measured.Y / 2) * _scale;
        at = new Vector2(MathF.Round(at.X), MathF.Round(at.Y));
        font.Draw(batch, text, at + new Vector2(1, 1), Color.Black * 0.8f, Px(size));
        font.Draw(batch, text, at, color, Px(size));
    }

    public void Dispose()
    {
        _background.Dispose();
        _marble.Dispose();
        foreach (var side in _sides)
            side.Lit?.Dispose();
    }
}
