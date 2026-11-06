using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Homestead.Client;

/// <summary>
/// The tooltip of a control the cursor rests on: a dark box with a gold frame next to the cursor in screen
/// space (so it may reach over the map), the shared look of the build menu's and the side-panel menus'
/// icon-only controls.
/// </summary>
public static class UiTooltip
{
    /// <summary>Seconds the cursor rests on a control before its tooltip appears.</summary>
    public const double Delay = 1;

    /// <summary>Longest the tooltip's place may stay undrawn before its timer starts anew (its control stopped being drawn).</summary>
    public const double Pause = 0.2;

    /// <summary>
    /// The hover timing of one tooltip place: the timer starts anew on another key (the tooltip's text) or
    /// after the owner stopped drawing in between (its menu was hidden).
    /// </summary>
    public sealed class Hover
    {
        private string? _key;
        private double _since, _lastDrawn = double.NegativeInfinity;

        /// <summary>Feeds the hovered tooltip's key once per frame; tells whether its tooltip is due.</summary>
        public bool Update(string? key, double now)
        {
            if (key != _key || now - _lastDrawn > Pause)
            {
                _key = key;
                _since = now;
            }
            _lastDrawn = now;
            return _key is not null && now - _since >= Delay;
        }

        /// <summary>Whether the tooltip of the key last fed is due (after <see cref="Update"/> hid it: never).</summary>
        public bool Due(double now) => _key is not null && now - _lastDrawn <= Pause && now - _since >= Delay;
    }

    /// <summary>Draws the text next to the cursor, kept inside <paramref name="area"/> (screen space).</summary>
    public static void Draw(SpriteBatch batch, Texture2D pixel, UiFont font, string text, Vector2 cursor, Rectangle area, float scale)
    {
        const int TextSize = 14, MinTextSize = 12;
        const int PaddingX = 8, PaddingY = 5;
        // The box opens to the lower right of the cursor; past the area's edge it flips to the other side.
        const int GapRight = 14, GapBelow = 18, GapAbove = 8;
        const float BoxOpacity = 0.92f, FrameOpacity = 0.7f;
        int size = Math.Max(MinTextSize, (int)MathF.Round(TextSize * scale));
        var measured = font.Measure(text, size);
        int width = (int)measured.X + 2 * PaddingX, height = (int)measured.Y + 2 * PaddingY;
        int x = (int)cursor.X + GapRight, y = (int)cursor.Y + GapBelow;
        if (x + width > area.Right)
            x = (int)cursor.X - GapRight - width;
        if (y + height > area.Bottom)
            y = (int)cursor.Y - GapAbove - height;
        var box = new Rectangle(x, y, width, height);
        batch.Draw(pixel, box, new Color(10, 10, 14) * BoxOpacity);
        Charts.Frame(batch, pixel, box, new Color(200, 170, 70) * FrameOpacity);
        Charts.Write(batch, font, text, new Vector2(x + PaddingX, y + PaddingY), Charts.Text, size);
    }
}
