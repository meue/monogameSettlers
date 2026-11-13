using Homestead.Assets.Gfx;
using Homestead.Client.Neural;

namespace Homestead.Client.Tests;

public class FakeUpscalerTests
{
    private static uint Px(byte r, byte g, byte b, byte a = 255) =>
        r | (uint)g << 8 | (uint)b << 16 | (uint)a << 24;

    [Fact]
    public void Every_pixel_becomes_an_exact_2x2_block()
    {
        uint a = Px(1, 2, 3), b = Px(4, 5, 6), c = Px(7, 8, 9, 0), d = Px(10, 11, 12, 128);
        var source = new GfxBitmap(2, 2, 9, -4, [a, b, c, d]);

        GfxBitmap result = new FakeUpscaler().Upscale(source)!;

        Assert.Equal(4, result.Width);
        Assert.Equal(4, result.Height);
        Assert.Equal(18, result.OffsetX);
        Assert.Equal(-8, result.OffsetY);
        uint[] expected = [a, a, b, b, a, a, b, b, c, c, d, d, c, c, d, d];
        Assert.Equal(expected, result.Pixels);
    }

    [Fact]
    public void Inputs_larger_than_the_cap_stay_1x()
    {
        var wide = new GfxBitmap(513, 8, 0, 0, new uint[513 * 8]);
        var tall = new GfxBitmap(8, 513, 0, 0, new uint[8 * 513]);

        Assert.Null(new FakeUpscaler().Upscale(wide));
        Assert.Null(new FakeUpscaler().Upscale(tall));
    }

    [Fact]
    public void The_cap_itself_is_still_upscaled()
    {
        var source = new GfxBitmap(UpscaleMath.MaxUpscaleInput, UpscaleMath.MaxUpscaleInput, 0, 0,
            new uint[UpscaleMath.MaxUpscaleInput * UpscaleMath.MaxUpscaleInput]);

        GfxBitmap result = new FakeUpscaler().Upscale(source)!;

        Assert.Equal(UpscaleMath.MaxUpscaleInput * 2, result.Width);
        Assert.Equal(UpscaleMath.MaxUpscaleInput * 2, result.Height);
    }

    [Fact]
    public void NullUpscaler_passes_everything_through()
    {
        var source = new GfxBitmap(2, 2, 0, 0, new uint[4]);

        Assert.Null(NullUpscaler.Instance.Upscale(source));
    }
}
