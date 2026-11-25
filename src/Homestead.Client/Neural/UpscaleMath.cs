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

    /// <summary>How far colors grow into the transparent area: the model looks a few pixels around each one.</summary>
    private const int BleedPasses = 4;

    /// <summary>Where the contour lies in the interpolated coverage: below 0.5, so that thin lines (poles, spears) survive.</summary>
    private const float EdgeLevel = 0.28f;

    /// <summary>Sample points per axis of a 2x pixel for the edge's coverage.</summary>
    private const int EdgeSamples = 4;

    /// <summary>
    /// Planar RGB in [0,1] (plane, row, column) for the model's NCHW input. The RGB under transparent pixels is garbage
    /// that would poison the inference, and black would show as a dark seam on soft edges, so the colors of the opaque
    /// neighbors grow outwards over <see cref="BleedPasses"/> pixels; black only beyond that.
    /// </summary>
    public static float[] ToNchwRgb(GfxBitmap bitmap)
    {
        uint[] colors = BleedColors(bitmap);
        var planes = new float[3 * bitmap.Width * bitmap.Height];
        int plane = bitmap.Width * bitmap.Height;
        for (int i = 0; i < colors.Length; i++)
        {
            uint pixel = colors[i];
            planes[i] = (pixel & 0xFF) / 255f;
            planes[plane + i] = ((pixel >> 8) & 0xFF) / 255f;
            planes[2 * plane + i] = ((pixel >> 16) & 0xFF) / 255f;
        }
        return planes;
    }

    /// <summary>Opaque pixels as they are; a transparent one takes the mean color of its colored neighbors, pass by pass (alpha byte: colored or not).</summary>
    private static uint[] BleedColors(GfxBitmap bitmap)
    {
        int width = bitmap.Width, height = bitmap.Height;
        var colors = new uint[bitmap.Pixels.Length];
        for (int i = 0; i < colors.Length; i++)
            colors[i] = bitmap.Pixels[i] >> 24 == 0 ? 0 : bitmap.Pixels[i] | 0xFF00_0000u;
        for (int pass = 0; pass < BleedPasses; pass++)
        {
            var next = (uint[])colors.Clone();
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    if (colors[y * width + x] >> 24 != 0)
                        continue;
                    int r = 0, g = 0, b = 0, count = 0;
                    for (int ny = Math.Max(y - 1, 0); ny <= Math.Min(y + 1, height - 1); ny++)
                        for (int nx = Math.Max(x - 1, 0); nx <= Math.Min(x + 1, width - 1); nx++)
                        {
                            uint color = colors[ny * width + nx];
                            if (color >> 24 == 0)
                                continue;
                            r += (int)(color & 0xFF);
                            g += (int)((color >> 8) & 0xFF);
                            b += (int)((color >> 16) & 0xFF);
                            count++;
                        }
                    if (count > 0)
                        next[y * width + x] = (uint)(r / count) | (uint)(g / count) << 8 | (uint)(b / count) << 16 | 0xFF00_0000u;
                }
            colors = next;
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
    /// The halved RGB back into a 2x bitmap: bytes clamped to [0,1], premultiplied (as SpriteBatch expects) by the
    /// anti-aliased alpha of <see cref="SmoothAlpha"/>, offsets doubled.
    /// </summary>
    public static GfxBitmap Combine(float[] halved, GfxBitmap source)
    {
        int width = source.Width * 2, height = source.Height * 2;
        if (halved.Length != 3 * width * height)
            throw new ArgumentException("Halved buffer is not twice the source size in every dimension.", nameof(halved));
        int plane = width * height;
        var pixels = new uint[plane];
        float[] alpha = SmoothAlpha(source);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                float a = alpha[i];
                pixels[i] = ToByte(halved[i] * a) | ToByte(halved[plane + i] * a) << 8 | ToByte(halved[2 * plane + i] * a) << 16 | ToByte(a) << 24;
            }
        return new GfxBitmap(width, height, source.OffsetX * 2, source.OffsetY * 2, pixels);
    }

    /// <summary>
    /// The 2x image of a shadow layer without the model: shadows are one constant semi-transparent black, so there is
    /// no color for it to improve, only the contour of <see cref="SmoothAlpha"/> (which keeps their level). Premultiplied
    /// black: the alpha byte alone. Offsets doubled.
    /// </summary>
    public static GfxBitmap UpscaleShadow(GfxBitmap source)
    {
        float[] alpha = SmoothAlpha(source);
        var pixels = new uint[alpha.Length];
        for (int i = 0; i < alpha.Length; i++)
            pixels[i] = ToByte(alpha[i]) << 24;
        return new GfxBitmap(source.Width * 2, source.Height * 2, source.OffsetX * 2, source.OffsetY * 2, pixels);
    }

    /// <summary>
    /// The 1-bit (shadows: one constant value) alpha at 2x with soft edges. Catmull-Rom over the source makes a smooth
    /// contour out of the staircase (outside the image transparent); each 2x pixel is covered by what share of its
    /// <see cref="EdgeSamples"/>² sample points lie inside that contour. Pixels without an edge nearby skip all that.
    /// </summary>
    public static float[] SmoothAlpha(GfxBitmap source)
    {
        int width = source.Width, height = source.Height;
        float peak = 0;
        var coverage = new float[width * height];
        for (int i = 0; i < coverage.Length; i++)
            peak = MathF.Max(peak, coverage[i] = (source.Pixels[i] >> 24) / 255f);
        var result = new float[4 * width * height];
        if (peak == 0)
            return result;
        for (int i = 0; i < coverage.Length; i++)
            coverage[i] = coverage[i] > 0 ? 1 : 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float flat = Flat(coverage, width, height, x, y);
                for (int by = 0; by < 2; by++)
                    for (int bx = 0; bx < 2; bx++)
                        result[(2 * y + by) * 2 * width + 2 * x + bx] =
                            peak * (flat >= 0 ? flat : Covered(coverage, width, height, x + bx / 2f, y + by / 2f));
            }
        return result;
    }

    /// <summary>The value of the 5x5 pixels around a pixel if they all agree (nothing to smooth), else -1.</summary>
    private static float Flat(float[] coverage, int width, int height, int x, int y)
    {
        float Value(int sx, int sy) => sx >= 0 && sy >= 0 && sx < width && sy < height ? coverage[sy * width + sx] : 0;
        float first = Value(x, y);
        for (int sy = y - 2; sy <= y + 2; sy++)
            for (int sx = x - 2; sx <= x + 2; sx++)
                if (Value(sx, sy) != first)
                    return -1;
        return first;
    }

    /// <summary>Share of the sample points of the half-pixel area at (x, y) (source pixels, top left) inside the contour.</summary>
    private static float Covered(float[] coverage, int width, int height, float x, float y)
    {
        int inside = 0;
        for (int sy = 0; sy < EdgeSamples; sy++)
            for (int sx = 0; sx < EdgeSamples; sx++)
            {
                // Pixel centers lie on whole coordinates here: a pixel's area reaches half a pixel to each side.
                float u = x - 0.5f + (sx + 0.5f) / (2 * EdgeSamples), v = y - 0.5f + (sy + 0.5f) / (2 * EdgeSamples);
                int left = (int)MathF.Floor(u), top = (int)MathF.Floor(v);
                float[] wx = CatmullRom(u - left), wy = CatmullRom(v - top);
                float sum = 0;
                for (int ty = 0; ty < 4; ty++)
                    for (int tx = 0; tx < 4; tx++)
                    {
                        int px = left - 1 + tx, py = top - 1 + ty;
                        if (px >= 0 && py >= 0 && px < width && py < height)
                            sum += wx[tx] * wy[ty] * coverage[py * width + px];
                    }
                if (sum >= EdgeLevel)
                    inside++;
            }
        return inside / (float)(EdgeSamples * EdgeSamples);
    }

    /// <summary>Catmull-Rom weights of the taps at -1, 0, 1, 2 around the fraction t.</summary>
    private static float[] CatmullRom(float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return [-0.5f * t3 + t2 - 0.5f * t, 1.5f * t3 - 2.5f * t2 + 1, -1.5f * t3 + 2 * t2 + 0.5f * t, 0.5f * t3 - 0.5f * t2];
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
