using Homestead.Assets.Gfx;

namespace Homestead.Client.Neural;

/// <summary>
/// The pixel math of the neural upscaler: RGBA bitmaps into planar tensors and back. Pure arrays and
/// <see cref="GfxBitmap"/>, no MonoGame types, so it is testable without a graphics device.
/// </summary>
public static class UpscaleMath
{
    /// <summary>
    /// Inputs wider or taller than this stay 1x: settlers are ~30-40 px, buildings up to ~250 px, so 512 bounds
    /// the worst inference latency, and a 2x output (≤1024 px) still packs into a 4096 atlas page.
    /// </summary>
    public const int MaxUpscaleInput = 512;

    /// <summary>
    /// A 2x pixel is opaque when the Catmull-Rom resampled binary coverage of the source reaches this. Above
    /// half (0.5) thin lines would drop out (a one-pixel line peaks at ~0.87 in one and ~0.23 in the neighbor);
    /// 0.28 keeps those lines while still cutting the half-covered pixels at a sharp edge.
    /// </summary>
    public const float CoverageThreshold = 0.28f;

    /// <summary>
    /// How many scan passes <see cref="FillTransparentRgb"/> makes at most: one colors everything connected to
    /// an opaque pixel, so this only bounds the work on pathological inputs (the passes stop early when nothing
    /// changes).
    /// </summary>
    public const int ColorFillPasses = 16;

    /// <summary>
    /// Planar RGB in [0,1] (plane, row, column) for the model's NCHW input. Fully transparent pixels inherit
    /// the nearest opaque neighbor's color (<see cref="FillTransparentRgb"/>) instead of the garbage under the
    /// 1-bit alpha: black would bleed back out of the model as dark seams at the edges.
    /// </summary>
    public static float[] ToNchwRgb(GfxBitmap bitmap)
    {
        var planes = new float[3 * bitmap.Width * bitmap.Height];
        uint[] colors = FillTransparentRgb(bitmap);
        int plane = bitmap.Width * bitmap.Height;
        for (int i = 0; i < colors.Length; i++)
        {
            uint color = colors[i];
            planes[i] = (color & 0xFF) / 255f;
            planes[plane + i] = ((color >> 8) & 0xFF) / 255f;
            planes[2 * plane + i] = ((color >> 16) & 0xFF) / 255f;
        }
        return planes;
    }

    /// <summary>
    /// The RGB of every pixel with transparent ones filled from their surroundings: each still-empty pixel takes
    /// the average of the eight neighbors that already have a color (opaque ones to begin with, ones filled
    /// earlier in the same scan for a start). In practice one pass colors everything connected to an opaque
    /// pixel; the passes only bound the work (they stop early when nothing changes). All-transparent bitmaps
    /// stay black.
    /// </summary>
    public static uint[] FillTransparentRgb(GfxBitmap bitmap)
    {
        int width = bitmap.Width, height = bitmap.Height;
        var colors = new uint[bitmap.Pixels.Length];
        bool filled = false;
        for (int i = 0; i < bitmap.Pixels.Length; i++)
            if (bitmap.Pixels[i] >> 24 != 0)
            {
                colors[i] = bitmap.Pixels[i] & 0xFF_FFFFu;
                filled = true;
            }
        if (!filled)
            return colors;
        for (int pass = 0; pass < ColorFillPasses; pass++)
        {
            bool changed = false;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    if (colors[i] != 0)
                        continue;
                    int r = 0, g = 0, b = 0, count = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= width || ny >= height)
                                continue;
                            uint neighbor = colors[ny * width + nx];
                            if (neighbor == 0)
                                continue;
                            r += (int)(neighbor & 0xFF);
                            g += (int)(neighbor >> 8 & 0xFF);
                            b += (int)(neighbor >> 16 & 0xFF);
                            count++;
                        }
                    if (count > 0)
                    {
                        colors[i] = (uint)(r / count) | (uint)(g / count) << 8 | (uint)(b / count) << 16;
                        changed = true;
                    }
                }
            if (!changed)
                break;
        }
        return colors;
    }

    /// <summary>
    /// Halves a planar buffer in both dimensions with the Lanczos2 kernel of <c>Shaders/Upscale.fx</c>. The
    /// official x2 model is the x4 body followed by a halving resize; this is that halving.
    /// </summary>
    public static float[] HalveLanczos(float[] planes, int width, int height)
    {
        if (width % 2 != 0 || height % 2 != 0)
            throw new ArgumentOutOfRangeException(nameof(planes), "Only even-sized buffers can be halved (the x4 output always is).");
        float[] kernel = HalvingKernel();
        int halfWidth = width / 2, halfHeight = height / 2, plane = width * height, halfPlane = halfWidth * halfHeight;
        var result = new float[3 * halfPlane];
        for (int c = 0; c < 3; c++)
            for (int oy = 0; oy < halfHeight; oy++)
                for (int ox = 0; ox < halfWidth; ox++)
                {
                    float sum = 0, weightSum = 0;
                    for (int ty = 0; ty < 4; ty++)
                    {
                        int sy = Math.Clamp(2 * oy - 1 + ty, 0, height - 1);
                        for (int tx = 0; tx < 4; tx++)
                        {
                            int sx = Math.Clamp(2 * ox - 1 + tx, 0, width - 1);
                            float weight = kernel[tx] * kernel[ty];
                            sum += weight * planes[c * plane + sy * width + sx];
                            weightSum += weight;
                        }
                    }
                    // Edges repeat taps through the clamp, so the weights no longer sum to 1 there.
                    result[c * halfPlane + oy * halfWidth + ox] = sum / weightSum;
                }
        return result;
    }

    /// <summary>
    /// The halved RGB back into a 2x bitmap. The 1-bit alpha is not replicated as blocks: the binary coverage is
    /// resampled with Catmull-Rom (the kernel of <c>Upscale.fx</c>, 16 taps per 2x pixel, taps clamped at the
    /// edges) and thresholded at <see cref="CoverageThreshold"/>, which places the contour with 2x sub-pixel
    /// accuracy instead of jagged steps. Where no edge is near (the 5×5 source window around a pixel is all in or
    /// all out) the coverage is simply copied. The RGB is the model's output premultiplied by that alpha, as the
    /// game blends premultiplied: fully transparent pixels are black instead of showing the colors the model
    /// extrapolated past the edge. Offsets doubled.
    /// </summary>
    public static GfxBitmap Combine(float[] halved, GfxBitmap source)
    {
        int width = source.Width * 2, height = source.Height * 2;
        if (halved.Length != 3 * width * height)
            throw new ArgumentException("Halved buffer is not twice the source size in every dimension.", nameof(halved));
        int plane = width * height;
        bool[] coverage = Coverage(source);
        bool[] edges = EdgeMap(coverage, source.Width, source.Height);
        var pixels = new uint[plane];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                int sx = x / 2, sy = y / 2;
                bool opaque = edges[sy * source.Width + sx]
                    ? CoverageAt(coverage, source.Width, source.Height, x, y) >= CoverageThreshold
                    : coverage[sy * source.Width + sx];
                if (!opaque)
                    continue; // Premultiplied: nothing to see, the color stays zero.
                pixels[i] = ToByte(halved[i]) | ToByte(halved[plane + i]) << 8 | ToByte(halved[2 * plane + i]) << 16 | 0xFF00_0000u;
            }
        return new GfxBitmap(width, height, source.OffsetX * 2, source.OffsetY * 2, pixels);
    }

    /// <summary>The 1-bit coverage of a source bitmap: opaque pixels count as fully covered, transparent as empty.</summary>
    private static bool[] Coverage(GfxBitmap source)
    {
        var coverage = new bool[source.Pixels.Length];
        for (int i = 0; i < source.Pixels.Length; i++)
            coverage[i] = source.Pixels[i] >> 24 != 0;
        return coverage;
    }

    /// <summary>
    /// Which source pixels have an edge nearby: the 5×5 window around them holds covered and uncovered pixels
    /// both (the 4×4 Catmull-Rom footprint of any of their 2x pixels reaches two source pixels out). Only these
    /// need the resampling; everywhere else the coverage is copied.
    /// </summary>
    private static bool[] EdgeMap(bool[] coverage, int width, int height)
    {
        var edges = new bool[coverage.Length];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                bool any = false, anyEmpty = false;
                for (int dy = -2; dy <= 2 && !(any && anyEmpty); dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                            continue;
                        if (coverage[ny * width + nx])
                            any = true;
                        else
                            anyEmpty = true;
                    }
                edges[y * width + x] = any && anyEmpty;
            }
        return edges;
    }

    /// <summary>
    /// The Catmull-Rom resampled coverage at 2x pixel (<paramref name="x"/>, <paramref name="y"/>): the sample
    /// point is the pixel's center in source coordinates (the same <c>-0.5</c> convention as <c>Upscale.fx</c>),
    /// the four taps per axis clamp at the bitmap's edge like the shader's sampler.
    /// </summary>
    private static float CoverageAt(bool[] coverage, int width, int height, int x, int y)
    {
        // The center of 2x pixel x lies at (x + 0.5) / 2 - 0.5 in source pixels: inside source pixel x / 2, a
        // quarter from its left edge (x even) or from its right edge (x odd, fraction 0.25).
        float fx = (x + 0.5f) / 2 - 0.5f, fy = (y + 0.5f) / 2 - 0.5f;
        var wx = CatmullRom(fx - MathF.Floor(fx));
        var wy = CatmullRom(fy - MathF.Floor(fy));
        float sum = 0;
        int rowStart = (int)MathF.Floor(fy) - 1, columnStart = (int)MathF.Floor(fx) - 1;
        for (int tapY = 0; tapY < 4; tapY++)
        {
            int sy = Math.Clamp(rowStart + tapY, 0, height - 1);
            for (int tapX = 0; tapX < 4; tapX++)
            {
                int sx = Math.Clamp(columnStart + tapX, 0, width - 1);
                if (coverage[sy * width + sx])
                    sum += wx[tapX] * wy[tapY];
            }
        }
        return sum;
    }

    /// <summary>The Catmull-Rom weights of the taps at -1, 0, 1, 2 around <paramref name="t"/> in [0,1),
    /// like <c>Upscale.fx</c>.</summary>
    private static float[] CatmullRom(float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return
        [
            -0.5f * t3 + t2 - 0.5f * t,
            1.5f * t3 - 2.5f * t2 + 1,
            -1.5f * t3 + 2 * t2 + 0.5f * t,
            0.5f * t3 - 0.5f * t2,
        ];
    }

    /// <summary>Lanczos2 (a = 2) weights of the four taps around a half-pixel, like <c>Upscale.fx</c>'s kernel.</summary>
    private static float[] HalvingKernel()
    {
        var weights = new float[4];
        float sum = 0;
        for (int tap = 0; tap < 4; tap++)
        {
            float px = MathF.Max(MathF.Abs(1.5f - tap), 1e-4f) * MathF.PI;
            sum += weights[tap] = MathF.Sin(px) * MathF.Sin(px * 0.5f) / (px * px * 0.5f);
        }
        for (int tap = 0; tap < 4; tap++)
            weights[tap] /= sum;
        return weights;
    }

    private static uint ToByte(float value) => (uint)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
}
