using System.Diagnostics;
using Homestead.Assets.Gfx;
using Homestead.Client.Neural;
using Xunit.Abstractions;

namespace Homestead.Client.Tests;

/// <summary>
/// The real model with a real execution provider (CoreML on macOS, CPU elsewhere). Only on demand via
/// HOMESTEAD_NN_TEST=1: it loads and compiles the network, which has no place in the regular test run.
/// Without the env var the test returns without asserting anything, so the green default run proves
/// nothing about the ONNX contract; the RequiresModel trait makes that skip visible and machine-filterable
/// (`dotnet test --filter Category=RequiresModel`).
/// </summary>
[Trait("Category", "RequiresModel")]
public class OnnxRealModelTests(ITestOutputHelper output)
{
    [Fact]
    public void Upscales_to_exact_2x_deterministically()
    {
        if (Environment.GetEnvironmentVariable("HOMESTEAD_NN_TEST") != "1")
            return;

        var source = new GfxBitmap(24, 16, 3, -4, Pixels(24, 16));
        using var upscaler = new OnnxUpscaler();

        var first = upscaler.Upscale(source);
        Assert.NotNull(first);
        var clock = Stopwatch.StartNew();
        var second = upscaler.Upscale(source);
        double perCallMs = clock.Elapsed.TotalMilliseconds;

        Assert.True(second is not null, $"the upscaler stopped working; per-call latency {perCallMs:F1} ms");
        Assert.Equal(48, first!.Width);
        Assert.Equal(32, first.Height);
        Assert.Equal(6, first.OffsetX);
        Assert.Equal(-8, first.OffsetY);
        output.WriteLine($"per-call latency after the first (compiling) call: {perCallMs:F1} ms");
        Assert.True(first.Pixels.SequenceEqual(second!.Pixels),
            $"per-call latency {perCallMs:F1} ms; the model is not deterministic");
        // The alpha is reconstructed from the 1-bit coverage (Catmull-Rom, threshold 0.28): binary, but placed
        // with 2x sub-pixel accuracy, never a value in between. The RGB is premultiplied: transparent is black.
        for (int y = 0; y < first.Height; y++)
            for (int x = 0; x < first.Width; x++)
            {
                uint pixel = first.Pixels[y * first.Width + x];
                Assert.Equal(pixel == 0 ? 0u : 0xFF00_0000u, pixel & 0xFF00_0000u);
                if (pixel >> 24 == 0)
                    Assert.Equal(0u, pixel & 0xFF_FFFFu);
            }
    }

    private static uint[] Pixels(int width, int height)
    {
        var pixels = new uint[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                byte value = (byte)(x * 255 / (width - 1));
                byte alpha = (byte)(y % 3 == 0 ? 0 : 255);
                pixels[y * width + x] = (uint)value | (uint)value << 8 | (uint)value << 16 | (uint)alpha << 24;
            }
        return pixels;
    }
}
