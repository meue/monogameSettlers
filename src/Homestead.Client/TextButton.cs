using System.Text.Json;
using Homestead.Assets.Gfx;
using Homestead.Simulation.Buildings;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Homestead.Client;

/// <summary>
/// The wide stone button with a text on it, as in the original's menus before the game (<c>textButton</c> in
/// <c>data/ui.json</c>): stretched to any size with its frame kept (nine slices), pressed while the left button
/// is held on it. Loaded once (<see cref="Load"/>); without it a plain dark box is drawn.
/// </summary>
public static class TextButton
{
    private static readonly Color Text = new(240, 225, 190);
    private static readonly Color HoverText = new(255, 240, 160);
    private static readonly Color DisabledText = new(120, 110, 95);

    private static Texture2D? _sheet;
    private static Rectangle _normal, _pressed;
    private static int _border;

    public static void Load(GraphicsDevice device, GfxDirectory gfx)
    {
        if (_sheet is not null)
            return;
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(BuildingCatalog.DefaultDataDirectory, "ui.json")));
        var config = document.RootElement.GetProperty("textButton");
        var bitmap = gfx.Open(config.GetProperty("file").GetInt32()).ReadGui(config.GetProperty("gui").GetInt32());
        _sheet = new Texture2D(device, bitmap.Width, bitmap.Height);
        _sheet.SetData(bitmap.Pixels);
        _normal = Rect(config.GetProperty("normal"));
        _pressed = Rect(config.GetProperty("pressed"));
        _border = config.GetProperty("border").GetInt32();
    }

    private static Rectangle Rect(JsonElement e) => new(e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32(), e[3].GetInt32());

    public static void Dispose()
    {
        _sheet?.Dispose();
        _sheet = null;
    }

    /// <summary>
    /// Draws the button with its label centered; <paramref name="cursor"/> in the same space as
    /// <paramref name="rect"/> (hover lights the text, holding the left button on it presses it).
    /// </summary>
    public static void Draw(SpriteBatch batch, Texture2D pixel, UiFont font, Rectangle rect, string label, Vector2 cursor,
        bool enabled = true, int size = 14, bool selected = false)
    {
        bool over = enabled && rect.Contains(cursor);
        bool down = selected || over && SdlMouse.GetState().LeftButton == ButtonState.Pressed;
        if (_sheet is null)
            batch.Draw(pixel, rect, over ? Color.White * 0.15f : Color.Black * 0.3f);
        else
            NineSlice(batch, _sheet, down ? _pressed : _normal, rect, enabled ? Color.White : new Color(150, 150, 150));
        var measured = font.Measure(label, size);
        var at = new Vector2(rect.Center.X - measured.X / 2, rect.Center.Y - measured.Y / 2 + (down ? 1 : 0));
        SelectionPanel.Shadowed(batch, font, label, at, size, !enabled ? DisabledText : over || selected ? HoverText : Text);
    }

    /// <summary>The source stretched to the destination; corners keep their size (smaller if the destination is lower than the source).</summary>
    private static void NineSlice(SpriteBatch batch, Texture2D texture, Rectangle source, Rectangle dest, Color color)
    {
        int b = _border;
        int d = Math.Max(2, (int)Math.Round(b * Math.Min(1f, Math.Min(dest.Height, dest.Width) / (float)Math.Min(source.Height, source.Width))));
        int[] sx = [source.X, source.X + b, source.Right - b, source.Right];
        int[] sy = [source.Y, source.Y + b, source.Bottom - b, source.Bottom];
        int[] dx = [dest.X, dest.X + d, dest.Right - d, dest.Right];
        int[] dy = [dest.Y, dest.Y + d, dest.Bottom - d, dest.Bottom];
        for (int row = 0; row < 3; row++)
            for (int column = 0; column < 3; column++)
                batch.Draw(texture,
                    new Rectangle(dx[column], dy[row], dx[column + 1] - dx[column], dy[row + 1] - dy[row]),
                    new Rectangle(sx[column], sy[row], sx[column + 1] - sx[column], sy[row + 1] - sy[row]), color);
    }
}
