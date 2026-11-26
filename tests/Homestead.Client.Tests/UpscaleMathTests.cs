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
    public void Transparent_pixels_take_the_color_of_their_neighbors_not_their_garbage()
    {
        var bitmap = Bitmap(2, 1, [Px(200, 200, 200, 0), Px(1, 2, 3)]);

        float[] planes = UpscaleMath.ToNchwRgb(bitmap);

        AssertPlane(planes, 0, 1 / 255f, 1 / 255f);
        AssertPlane(planes, 1, 2 / 255f, 2 / 255f);
        AssertPlane(planes, 2, 3 / 255f, 3 / 255f);
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

    private static uint[] Solid(int count) => Enumerable.Repeat(Px(0, 0, 0), count).ToArray();

    [Fact]
    public void Combine_packs_rgb_bytes_and_doubles_the_offsets()
    {
        var source = Bitmap(5, 5, Solid(25), offsetX: 7, offsetY: -2);
        var halved = new float[3 * 10 * 10];
        for (int i = 0; i < 100; i++)
        {
            halved[i] = 1f;
            halved[200 + i] = 128 / 255f;
        }

        GfxBitmap result = UpscaleMath.Combine(halved, source);

        Assert.Equal((10, 10, 14, -4), (result.Width, result.Height, result.OffsetX, result.OffsetY));
        Assert.Equal(Px(255, 0, 128), result.Pixels[5 * 10 + 5]); // Inside the sprite: untouched.
    }

    [Fact]
    public void Combine_clamps_out_of_range_values_into_bytes()
    {
        var source = Bitmap(5, 5, Solid(25));
        var halved = new float[3 * 10 * 10];
        for (int i = 0; i < 100; i++)
        {
            halved[i] = 1.5f;
            halved[100 + i] = -0.5f;
            halved[200 + i] = 1f;
        }

        Assert.Equal(Px(255, 0, 255), UpscaleMath.Combine(halved, source).Pixels[5 * 10 + 5]);
    }

    [Fact]
    public void Combine_premultiplies_the_color_on_a_soft_edge()
    {
        var pixels = Solid(25);
        for (int i = 0; i < 25; i += 5) // The left two columns transparent: a vertical edge.
            pixels[i] = pixels[i + 1] = 0;
        var halved = Enumerable.Repeat(1f, 3 * 10 * 10).ToArray();

        GfxBitmap result = UpscaleMath.Combine(halved, Bitmap(5, 5, pixels));

        var soft = result.Pixels.Single2DRowMiddle(10);
        Assert.Contains(soft, pixel => pixel >> 24 is > 0 and < 255);
        Assert.All(soft, pixel => Assert.Equal(pixel >> 24, pixel & 0xFF));
    }

    [Fact]
    public void Smooth_alpha_keeps_the_inside_opaque_and_the_far_outside_clear()
    {
        var pixels = Solid(49);
        for (int y = 0; y < 7; y++)
            for (int x = 0; x < 3; x++)
                pixels[y * 7 + x] = 0;

        float[] alpha = UpscaleMath.SmoothAlpha(Bitmap(7, 7, pixels));

        Assert.Equal(0, alpha[7 * 14 + 1], 4);
        Assert.Equal(1, alpha[7 * 14 + 11], 4);
    }

    [Fact]
    public void Smooth_alpha_turns_a_staircase_into_more_than_two_levels()
    {
        var pixels = new uint[64];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                pixels[y * 8 + x] = x >= y ? Px(0, 0, 0) : 0;

        float[] alpha = UpscaleMath.SmoothAlpha(Bitmap(8, 8, pixels));

        Assert.True(alpha.Select(value => MathF.Round(value, 2)).Distinct().Count() > 4);
    }

    [Fact]
    public void Smooth_alpha_keeps_the_level_of_a_shadow()
    {
        var pixels = Enumerable.Repeat(Px(0, 0, 0, 128), 25).ToArray();

        Assert.Equal(128 / 255f, UpscaleMath.SmoothAlpha(Bitmap(5, 5, pixels))[5 * 10 + 5], 3);
    }

    [Fact]
    public void A_shadow_stays_semi_transparent_black_without_the_model()
    {
        var pixels = Enumerable.Repeat(0x8800_0000u, 25).ToArray();

        GfxBitmap result = UpscaleMath.UpscaleShadow(Bitmap(5, 5, pixels, offsetX: 7, offsetY: -2));

        Assert.Equal((10, 10, 14, -4), (result.Width, result.Height, result.OffsetX, result.OffsetY));
        Assert.Equal(0x8800_0000u, result.Pixels[5 * 10 + 5]);
        Assert.All(result.Pixels, pixel => Assert.Equal(0u, pixel & 0x00FF_FFFFu));
    }

    [Fact]
    public void A_single_pixel_line_survives()
    {
        var pixels = new uint[49];
        for (int y = 0; y < 7; y++)
            pixels[y * 7 + 3] = Px(0, 0, 0);

        float[] alpha = UpscaleMath.SmoothAlpha(Bitmap(7, 7, pixels));

        Assert.True(alpha[7 * 14 + 6] > 0.5f);
    }
}

internal static class RowHelpers
{
    /// <summary>The middle row of a square image of this width.</summary>
    public static uint[] Single2DRowMiddle(this uint[] pixels, int width) => pixels.Skip(width / 2 * width).Take(width).ToArray();
}
