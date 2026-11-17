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
    public void Transparent_pixels_inherit_their_opaque_neighbors_color()
    {
        // The RGB under a transparent pixel is garbage that would poison the inference; the neighbor's
        // color gives the model something plausible at the edge instead of black seams.
        var bitmap = Bitmap(2, 1, [Px(200, 200, 200, 0), Px(1, 2, 3)]);

        float[] planes = UpscaleMath.ToNchwRgb(bitmap);

        AssertPlane(planes, 0, 1 / 255f, 1 / 255f);
        AssertPlane(planes, 1, 2 / 255f, 2 / 255f);
        AssertPlane(planes, 2, 3 / 255f, 3 / 255f);
    }

    [Fact]
    public void Fully_transparent_bitmaps_stay_black()
    {
        var bitmap = Bitmap(3, 2, new uint[6].Select(_ => Px(90, 80, 70, 0)).ToArray());

        Assert.All(UpscaleMath.ToNchwRgb(bitmap), value => Assert.Equal(0, value, 5));
    }

    [Fact]
    public void Transparent_regions_connected_to_opaque_fill_completely()
    {
        // A wide transparent stretch between two opaque ends: the fill propagates across all of it (the model
        // gets plausible colors everywhere near the sprite, not just one ring in).
        int width = 3 * UpscaleMath.ColorFillPasses;
        var pixels = new uint[width];
        pixels[0] = Px(10, 20, 30);
        pixels[^1] = Px(10, 20, 30);
        var bitmap = Bitmap(width, 1, pixels);

        uint[] colors = UpscaleMath.FillTransparentRgb(bitmap);

        Assert.All(colors, color => Assert.NotEqual(0u, color));
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
    public void Combine_places_a_vertical_edge_between_the_2x_pixels()
    {
        // Left half opaque, right half transparent: the reconstructed contour (Catmull-Rom over the binary
        // coverage, threshold 0.28) falls exactly where the source edge is, doubled: between output columns
        // 3 and 4. A hard 2x2 replication would put the step two columns wide.
        var source = Bitmap(4, 4, Enumerable.Range(0, 16).Select(i => i % 4 < 2 ? Px(0, 0, 0) : Px(0, 0, 0, 0)).ToArray());
        var halved = new float[3 * 8 * 8];
        for (int i = 0; i < halved.Length; i++)
            halved[i] = 0.5f;

        GfxBitmap result = UpscaleMath.Combine(halved, source);

        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                uint pixel = result.Pixels[y * 8 + x];
                if (x <= 3)
                {
                    Assert.Equal(0xFF00_0000u, pixel & 0xFF00_0000u);
                    Assert.Equal(Px(128, 128, 128), pixel); // the model's color, opaque so unpremultiplied
                }
                else
                    // Premultiplied: nothing of the color extrapolated past the edge may show.
                    Assert.Equal(0u, pixel);
            }
    }

    [Fact]
    public void Combine_keeps_a_one_pixel_line_two_pixels_wide()
    {
        // A single covered source column peaks at ~0.87 coverage in both of its 2x columns (and ~0.23 in the
        // neighbors): the threshold of 0.28 keeps the line, a threshold of 0.5 would drop it entirely.
        var source = Bitmap(4, 4, Enumerable.Range(0, 16).Select(i => i % 4 == 1 ? Px(0, 0, 0) : Px(0, 0, 0, 0)).ToArray());
        var halved = new float[3 * 8 * 8];

        GfxBitmap result = UpscaleMath.Combine(halved, source);

        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                Assert.Equal(x is 2 or 3 ? 0xFF00_0000u : 0u, result.Pixels[y * 8 + x] & 0xFF00_0000u);
    }

    [Fact]
    public void UpscaleShadow_keeps_the_semi_transparent_black_and_the_contour()
    {
        // Shadow pixels are the format's constant 0x88000000: the 2x shadow may keep that translucency — the
        // model path's opaque alpha is what made shadows solid black — and its contour falls where Combine's
        // falls (the same coverage reconstruction), here between output columns 3 and 4.
        uint shadow = 0x8800_0000u;
        var source = Bitmap(4, 4, Enumerable.Range(0, 16).Select(i => i % 4 < 2 ? shadow : 0u).ToArray());

        GfxBitmap result = UpscaleMath.UpscaleShadow(source);

        Assert.Equal(8, result.Width);
        Assert.Equal(8, result.Height);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                Assert.Equal(x <= 3 ? shadow : 0u, result.Pixels[y * 8 + x]);
    }

    [Fact]
    public void UpscaleShadow_doubles_the_offsets_and_leaves_empty_shadows_empty()
    {
        var source = Bitmap(2, 2, new uint[4], offsetX: 7, offsetY: -2);

        GfxBitmap result = UpscaleMath.UpscaleShadow(source);

        Assert.Equal(4, result.Width);
        Assert.Equal(4, result.Height);
        Assert.Equal(14, result.OffsetX);
        Assert.Equal(-4, result.OffsetY);
        Assert.All(result.Pixels, pixel => Assert.Equal(0u, pixel));
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
