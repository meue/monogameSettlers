using Homestead.Client;
using Microsoft.Xna.Framework;

namespace Homestead.Client.Tests;

public class UpscaleModeTests
{
    [Fact]
    public void The_2x_image_draws_at_half_the_scale_with_the_doubled_offset()
    {
        var offset = new Vector2(3, -5);
        const float scale = 0.75f;

        var resolved = SettlerSprites.ResolveResolution(hiRes: true, offset, scale);

        Assert.True(resolved.High);
        Assert.Equal(new Vector2(6, -10), resolved.Offset);
        Assert.Equal(0.375f, resolved.Scale);
    }

    [Fact]
    public void The_2x_image_covers_exactly_the_1x_screen_area()
    {
        var offset = new Vector2(3, -5);
        const float scale = 0.75f;

        var resolved = SettlerSprites.ResolveResolution(hiRes: true, offset, scale);

        Assert.Equal(offset * scale, resolved.Offset * resolved.Scale);
    }

    [Fact]
    public void Without_a_2x_image_nothing_moves()
    {
        var offset = new Vector2(7, 2);
        const float scale = 3;

        var resolved = SettlerSprites.ResolveResolution(hiRes: false, offset, scale);

        Assert.False(resolved.High);
        Assert.Equal(offset, resolved.Offset);
        Assert.Equal(scale, resolved.Scale);
    }

    [Fact]
    public void A_sequence_is_queued_only_in_the_mode_and_only_once()
    {
        Assert.True(SpriteLibrary.ShouldQueueUpscale(modeEnabled: true, alreadyAttempted: false));
        Assert.False(SpriteLibrary.ShouldQueueUpscale(modeEnabled: true, alreadyAttempted: true));
        Assert.False(SpriteLibrary.ShouldQueueUpscale(modeEnabled: false, alreadyAttempted: false));
        Assert.False(SpriteLibrary.ShouldQueueUpscale(modeEnabled: false, alreadyAttempted: true));
    }
}
