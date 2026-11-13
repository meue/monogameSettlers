using Homestead.Assets.Gfx;
using Homestead.Simulation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Homestead.Client;

/// <summary>
/// Images of one settler sequence (body, torso, shadow per frame, packed into a <see cref="SpriteAtlas"/>), plus the knowledge how
/// walk sequences are laid out: six blocks of 12 frames (SW, W, NW, NE, E, SE), frame 0 of a
/// block is standing, frames 1-11 are the walk cycle.
/// </summary>
public sealed class SettlerSprites
{
    public const int FramesPerDirection = 12;
    public const int WalkCycleFrames = FramesPerDirection - 1;

    private readonly Layer?[] _body;
    private readonly Layer?[] _torso;
    private readonly Layer?[] _shadow;

    public SettlerSprites(SpriteAtlas atlas, GfxFile file, int sequence)
    {
        int frames = file.GetSettlerFrameCount(sequence);
        _body = new Layer?[frames];
        _torso = new Layer?[frames];
        _shadow = new Layer?[frames];
        for (int f = 0; f < frames; f++)
        {
            _body[f] = Layer.From(atlas, file.ReadSettler(sequence, f));
            _torso[f] = Layer.From(atlas, file.ReadSettlerTorso(sequence, f));
            _shadow[f] = Layer.From(atlas, file.ReadSettlerShadow(sequence, f));
        }
    }

    public int FrameCount => _body.Length;

    public static int WalkFrame(Direction facing, float stepProgress)
    {
        int cycle = Math.Clamp((int)(stepProgress * WalkCycleFrames), 0, WalkCycleFrames - 1);
        return Block(facing) * FramesPerDirection + 1 + cycle;
    }

    public static int StandFrame(Direction facing) => Block(facing) * FramesPerDirection;

    /// <summary>
    /// Draws shadow, body and player-colored torso with the frame's anchor at <paramref name="anchor"/>.
    /// <paramref name="visibleFromBottom"/> &lt; 1 shows only the lower part (growing construction sites);
    /// the shadow is left out then, as without <paramref name="shadow"/>. <paramref name="tint"/> multiplies all layers (placement preview).
    /// With <paramref name="clip"/> (unscaled only) just what lies within it.
    /// </summary>
    public void Draw(SpriteBatch batch, int frame, Vector2 anchor, float scale, Color playerColor,
        float visibleFromBottom = 1, Color? tint = null, bool shadow = true, Rectangle? clip = null)
    {
        if ((uint)frame >= (uint)FrameCount || visibleFromBottom <= 0)
            return;
        var white = tint ?? Color.White;
        if (visibleFromBottom >= 1 && shadow)
            _shadow[frame]?.Draw(batch, anchor, scale, white, 1, clip);
        _body[frame]?.Draw(batch, anchor, scale, white, visibleFromBottom, clip);
        _torso[frame]?.Draw(batch, anchor, scale, Multiply(playerColor, white), visibleFromBottom, clip);
    }

    /// <summary>Draws the frame unscaled like <see cref="Draw"/>, but only what lies within <paramref name="clip"/>.</summary>
    public void DrawClipped(SpriteBatch batch, int frame, Vector2 anchor, Rectangle clip, Color playerColor) =>
        Draw(batch, frame, anchor, 1, playerColor, clip: clip);

    /// <summary>The body of a frame relative to its anchor (unscaled); null if the frame is empty.</summary>
    public Rectangle? Bounds(int frame) =>
        (uint)frame < (uint)FrameCount && _body[frame] is { } body
            ? new Rectangle((int)body.Offset.X, (int)body.Offset.Y, body.Image.Source.Width, body.Image.Source.Height)
            : null;

    /// <summary>The shadow of a frame relative to its anchor (unscaled); null if the frame has none.</summary>
    public Rectangle? ShadowBounds(int frame) =>
        (uint)frame < (uint)FrameCount && _shadow[frame] is { } shadow
            ? new Rectangle((int)shadow.Offset.X, (int)shadow.Offset.Y, shadow.Image.Source.Width, shadow.Image.Source.Height)
            : null;

    /// <summary>
    /// Scale and anchor that fit <paramref name="bounds"/> (relative to the anchor) centered into
    /// <paramref name="box"/>, at most <paramref name="maxScale"/>.
    /// </summary>
    public static (Vector2 Anchor, float Scale) Fit(Rectangle bounds, Rectangle box, float maxScale)
    {
        float scale = Math.Min(maxScale, Math.Min(box.Width / (float)Math.Max(1, bounds.Width), box.Height / (float)Math.Max(1, bounds.Height)));
        var center = new Vector2(bounds.X + bounds.Width / 2f, bounds.Y + bounds.Height / 2f);
        return (new Vector2(box.Center.X, box.Center.Y) - center * scale, scale);
    }

    private static Color Multiply(Color a, Color b) =>
        new(a.R * b.R / 255, a.G * b.G / 255, a.B * b.B / 255, a.A * b.A / 255);

    /// <summary>Index of a direction in the original's order (SW, W, NW, NE, E, SE).</summary>
    public static int Block(Direction facing) => facing switch
    {
        Direction.SouthWest => 0,
        Direction.West => 1,
        Direction.NorthWest => 2,
        Direction.NorthEast => 3,
        Direction.East => 4,
        Direction.SouthEast => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(facing)),
    };

    private sealed record Layer(AtlasImage Image, Vector2 Offset)
    {
        public static Layer? From(SpriteAtlas atlas, GfxBitmap? bitmap)
        {
            if (bitmap is not { Width: > 0, Height: > 0 })
                return null;
            return new Layer(atlas.Add(bitmap), new Vector2(bitmap.OffsetX, bitmap.OffsetY));
        }

        /// <summary>Unscaled, only the lower <paramref name="visibleFromBottom"/> part within <paramref name="clip"/>.</summary>
        private void DrawClipped(SpriteBatch batch, Vector2 anchor, Rectangle clip, Color tint, float visibleFromBottom)
        {
            var source = Image.Source;
            var at = anchor + Offset;
            var rect = new Rectangle((int)Math.Round(at.X), (int)Math.Round(at.Y), source.Width, source.Height);
            int hidden = source.Height - (int)(source.Height * Math.Min(1, visibleFromBottom));
            var visible = Rectangle.Intersect(new Rectangle(rect.X, rect.Y + hidden, rect.Width, rect.Height - hidden), clip);
            if (visible.Width <= 0 || visible.Height <= 0)
                return;
            batch.Draw(Image.Texture, new Vector2(visible.X, visible.Y),
                new Rectangle(source.X + visible.X - rect.X, source.Y + visible.Y - rect.Y, visible.Width, visible.Height), tint);
        }

        public void Draw(SpriteBatch batch, Vector2 anchor, float scale, Color tint, float visibleFromBottom, Rectangle? clip)
        {
            if (clip is { } box)
            {
                DrawClipped(batch, anchor, box, tint, visibleFromBottom);
                return;
            }
            var source = Image.Source;
            if (visibleFromBottom >= 1)
            {
                batch.Draw(Image.Texture, anchor + Offset * scale, source, tint, 0, Vector2.Zero, scale, SpriteEffects.None, 0);
                return;
            }
            int visible = (int)(source.Height * visibleFromBottom);
            int hidden = source.Height - visible;
            batch.Draw(Image.Texture, anchor + (Offset + new Vector2(0, hidden)) * scale,
                new Rectangle(source.X, source.Y + hidden, source.Width, visible), tint, 0, Vector2.Zero, scale, SpriteEffects.None, 0);
        }
    }
}
