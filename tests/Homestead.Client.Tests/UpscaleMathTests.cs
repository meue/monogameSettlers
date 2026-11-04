using Homestead.Assets.Gfx;
using Homestead.Client.Neural;

namespace Homestead.Client.Tests;

public class UpscaleMathTests
{
    private static uint Px(byte r, byte g, byte b, byte a = 255) =>
        r | (uint)g << 8 | (uint)b << 16 | (uint)a << 24;

    private static GfxBitmap Bitmap(int width, int height, uint[] pixels, int offsetX = 3, int offsetY = -5) =>
        new(width, height, offsetX, offsetY, pixels);

    private static void AssertPlane(float[] planes, int plane, params float[] expected)
    {
        Assert.Equal(expected.Length, planes.Length / 3f, 0);
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], planes[plane * expected.Length + i], 5);
    }

    [Fact]
    public void Opaque_pixels_become_planar_rgb_in_0_to_1()
    {
        var bitmap = Bitmap(2, 2, [Px(255, 0, 0), Px(0, 255, 0), Px(0, 0, 255), Px(10, 20, 30)]);

        float[] planes = UpscaleMath.ToNchwRgb(bitmap);

        Assert.Equal(12, planes.Length);
        // Each plane is row-major over the whole image: R plane, then G, then B.
        AssertPlane(planes, 0, 1f, 0f, 0f, 10 / 255f);
        AssertPlane(planes, 1, 0f, 1f, 0f, 20 / 255f);
        AssertPlane(planes, 2, 0f, 0f, 1f, 30 / 255f);
    }

    [Fact]
    public void Transparent_pixels_are_filled_with_black_even_with_garbage_rgb()
    {
        var bitmap = Bitmap(2, 1, [Px(200, 200, 200, 0), Px(1, 2, 3)]);

        float[] planes = UpscaleMath.ToNchwRgb(bitmap);

        AssertPlane(planes, 0, 0f, 1 / 255f);
        AssertPlane(planes, 1, 0f, 2 / 255f);
        AssertPlane(planes, 2, 0f, 3 / 255f);
    }

    [Fact]
    public void Halving_a_constant_image_returns_the_constant()
    {
        const float value = 64 / 255f;
        var planes = Enumerable.Repeat(value, 3 * 16 * 12).ToArray();

        float[] halved = UpscaleMath.HalveLanczos(planes, 16, 12);

        Assert.Equal(3 * 8 * 6, halved.Length);
        Assert.All(halved, pixel => Assert.Equal(value, pixel, 4));
    }

    [Fact]
    public void Halving_shrinks_to_half_size_in_both_dimensions()
    {
        var planes = new float[3 * 8 * 6];
        for (int i = 0; i < planes.Length; i++)
            planes[i] = i % 7 / 7f;

        float[] halved = UpscaleMath.HalveLanczos(planes, 8, 6);

        Assert.Equal(3 * 4 * 3, halved.Length);
        // No ringing beyond the range of the source taps.
        Assert.All(halved, value => Assert.InRange(value, -0.01f, 1.01f));
    }

    [Fact]
    public void Halving_rejects_odd_sizes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UpscaleMath.HalveLanczos(new float[3 * 3], 3, 1));
    }

    [Fact]
    public void Combine_packs_rgb_bytes_and_doubles_the_offsets()
    {
        // 1x1 source, constant halved planes: every output pixel is that color.
        var source = Bitmap(1, 1, [Px(0, 0, 0)], offsetX: 7, offsetY: -2);
        var halved = new float[3 * 2 * 2];
        for (int i = 0; i < 4; i++)
        {
            halved[i] = 1f;
            halved[8 + i] = 128 / 255f;
        }

        GfxBitmap result = UpscaleMath.Combine(halved, source);

        Assert.Equal(2, result.Width);
        Assert.Equal(2, result.Height);
        Assert.Equal(14, result.OffsetX);
        Assert.Equal(-4, result.OffsetY);
        Assert.All(result.Pixels, pixel => Assert.Equal(Px(255, 0, 128), pixel));
    }

    [Fact]
    public void Combine_replicates_the_source_alpha_over_2x2_blocks()
    {
        var source = Bitmap(2, 2, [Px(0, 0, 0), Px(0, 0, 0, 0), Px(0, 0, 0, 0), Px(0, 0, 0)]);
        var halved = new float[3 * 4 * 4];

        GfxBitmap result = UpscaleMath.Combine(halved, source);

        Assert.Equal(4, result.Width);
        Assert.Equal(4, result.Height);
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                bool opaque = x < 2 ? y < 2 : y >= 2;
                Assert.Equal(opaque ? (uint)0xFF00_0000 : 0u, result.Pixels[y * 4 + x] & 0xFF00_0000u);
            }
    }

    [Fact]
    public void Combine_clamps_out_of_range_values_into_bytes()
    {
        var source = Bitmap(1, 1, [Px(0, 0, 0)]);
        var halved = new float[3 * 2 * 2];
        for (int i = 0; i < 4; i++)
        {
            halved[i] = 1.5f;
            halved[4 + i] = -0.5f;
            halved[8 + i] = 1f;
        }

        GfxBitmap result = UpscaleMath.Combine(halved, source);

        Assert.All(result.Pixels, pixel => Assert.Equal(Px(255, 0, 255), pixel));
    }
}
