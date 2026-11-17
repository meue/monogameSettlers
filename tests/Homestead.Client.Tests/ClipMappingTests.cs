using Homestead.Client;
using Microsoft.Xna.Framework;

namespace Homestead.Client.Tests;

public class ClipMappingTests
{
    // A 20x30 sprite whose 2x image (40x60 texels) sits in the atlas at (10, 20), anchored at (100, 200).
    private static readonly Rectangle HiRes = new(10, 20, 40, 60);
    private static readonly Rectangle LoRes = new(10, 20, 20, 30);
    private static readonly Vector2 At = new(100, 200);

    [Fact]
    public void An_8px_strip_shows_8_screen_pixels_of_the_doubled_slice()
    {
        var clip = new Rectangle(100, 150, 8, 200);

        var part = SettlerSprites.ClipMapping(HiRes, At, visibleFromBottom: 1, clip, scale: 0.5f);

        Assert.NotNull(part);
        Assert.Equal(new Rectangle(100, 200, 8, 30), part!.Value.Dest);
        Assert.Equal(new Rectangle(10, 20, 16, 60), part.Value.Source);
    }

    [Fact]
    public void The_same_clip_covers_the_same_screen_rect_on_the_1x_and_2x_path()
    {
        var clip = new Rectangle(104, 210, 10, 60);

        var lo = SettlerSprites.ClipMapping(LoRes, At, visibleFromBottom: 0.5f, clip, scale: 1);
        var hi = SettlerSprites.ClipMapping(HiRes, At, visibleFromBottom: 0.5f, clip, scale: 0.5f);

        Assert.NotNull(lo);
        Assert.NotNull(hi);
        // The 1x path is pinned to the exact rects the unscaled draw has always produced.
        Assert.Equal(new Rectangle(104, 215, 10, 15), lo!.Value.Dest);
        Assert.Equal(new Rectangle(14, 35, 10, 15), lo.Value.Source);
        // The 2x path covers the same screen area with the doubled slice of the same sprite part.
        Assert.Equal(lo.Value.Dest, hi!.Value.Dest);
        Assert.Equal(LoRes.X + 2 * (lo.Value.Source.X - LoRes.X), hi.Value.Source.X);
        Assert.Equal(LoRes.Y + 2 * (lo.Value.Source.Y - LoRes.Y), hi.Value.Source.Y);
        Assert.Equal(2 * lo.Value.Source.Width, hi.Value.Source.Width);
        Assert.Equal(2 * lo.Value.Source.Height, hi.Value.Source.Height);
    }

    [Fact]
    public void A_clip_beyond_the_true_footprint_draws_nothing()
    {
        // True 2x footprint: x in [100, 120); a texel-sized box would wrongly reach x = 140.
        var clip = new Rectangle(125, 150, 10, 200);

        var part = SettlerSprites.ClipMapping(HiRes, At, visibleFromBottom: 1, clip, scale: 0.5f);

        Assert.Null(part);
    }
}
