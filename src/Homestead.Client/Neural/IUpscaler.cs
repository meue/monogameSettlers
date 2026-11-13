using Homestead.Assets.Gfx;

namespace Homestead.Client.Neural;

/// <summary>Upscales a sprite bitmap to twice its size, or returns null to keep the 1x image (input too
/// large, or upscaling unavailable). Disposable for the model session an implementation may hold.
/// <see cref="Ready"/> becomes false for good when upscaling cannot work on this system: the library
/// stops queueing work then and tells the user, instead of retrying every sequence.</summary>
public interface IUpscaler : IDisposable
{
    /// <summary>Whether this upscaler can produce images: false after a failed load, true for the fakes.</summary>
    bool Ready { get; }

    GfxBitmap? Upscale(GfxBitmap bitmap);
}

/// <summary>Keeps everything at 1x: upscaling off or the model failed to load.</summary>
public sealed class NullUpscaler : IUpscaler
{
    public static readonly NullUpscaler Instance = new();

    public bool Ready => false;

    public GfxBitmap? Upscale(GfxBitmap bitmap) => null;

    public void Dispose()
    {
    }
}

/// <summary>Deterministic nearest-neighbor x2 without any model, for tests of the plumbing around the real
/// upscaler.</summary>
public sealed class FakeUpscaler : IUpscaler
{
    public bool Ready => true;

    public GfxBitmap? Upscale(GfxBitmap bitmap)
    {
        if (bitmap.Width > UpscaleMath.MaxUpscaleInput || bitmap.Height > UpscaleMath.MaxUpscaleInput)
            return null;
        var pixels = new uint[bitmap.Width * bitmap.Height * 4];
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                uint pixel = bitmap.Pixels[y * bitmap.Width + x];
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                        pixels[(2 * y + dy) * bitmap.Width * 2 + 2 * x + dx] = pixel;
            }
        return new GfxBitmap(bitmap.Width * 2, bitmap.Height * 2, bitmap.OffsetX * 2, bitmap.OffsetY * 2, pixels);
    }

    public void Dispose()
    {
    }
}
