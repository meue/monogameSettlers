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
    /// Planar RGB in [0,1] (plane, row, column) for the model's NCHW input. Fully transparent pixels are filled
    /// with black: the alpha is 1-bit, the RGB under it is garbage that would poison the inference.
    /// </summary>
    public static float[] ToNchwRgb(GfxBitmap bitmap)
    {
        var planes = new float[3 * bitmap.Width * bitmap.Height];
        int plane = bitmap.Width * bitmap.Height;
        for (int i = 0; i < bitmap.Pixels.Length; i++)
        {
            uint pixel = bitmap.Pixels[i];
            if (pixel >> 24 == 0)
                continue;
            planes[i] = (pixel & 0xFF) / 255f;
            planes[plane + i] = ((pixel >> 8) & 0xFF) / 255f;
            planes[2 * plane + i] = ((pixel >> 16) & 0xFF) / 255f;
        }
        return planes;
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
    /// The halved RGB back into a 2x bitmap: bytes clamped to [0,1], the source's alpha replicated over each
    /// 2×2 block (1-bit alpha, so the sprite's edge stays sharp and no halos appear), offsets doubled.
    /// </summary>
    public static GfxBitmap Combine(float[] halved, GfxBitmap source)
    {
        int width = source.Width * 2, height = source.Height * 2;
        if (halved.Length != 3 * width * height)
            throw new ArgumentException("Halved buffer is not twice the source size in every dimension.", nameof(halved));
        int plane = width * height;
        var pixels = new uint[plane];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                uint alpha = source.Pixels[y / 2 * source.Width + x / 2] & 0xFF00_0000u;
                pixels[i] = ToByte(halved[i]) | ToByte(halved[plane + i]) << 8 | ToByte(halved[2 * plane + i]) << 16 | alpha;
            }
        return new GfxBitmap(width, height, source.OffsetX * 2, source.OffsetY * 2, pixels);
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
