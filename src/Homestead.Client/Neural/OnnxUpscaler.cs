using Homestead.Assets.Gfx;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Homestead.Client.Neural;

/// <summary>
/// The real upscaler: Real-ESRGAN's realesr-animevideov3 as an embedded ONNX model. The model outputs x4; the
/// official x2 variant is that same graph followed by a halving resize (the ncnn x2 conversion shows PixelShuffle
/// x4, then the 0.5 Interp: https://github.com/upscayl/upscayl/blob/main/models/realesr-animevideov3-x2.param),
/// so the x4 output is halved here. The session is created lazily on the first call — the background worker
/// thread, because CoreML compiles the graph on first use — and reused; if it cannot be loaded, everything
/// stays 1x.
/// </summary>
public sealed class OnnxUpscaler : IUpscaler, IDisposable
{
    private const string ResourceName = "realesr-animevideov3-x4.onnx";
    private const string InputName = "input";

    private InferenceSession? _session;
    private bool _loaded;

    public GfxBitmap? Upscale(GfxBitmap bitmap)
    {
        if (bitmap.Width == 0 || bitmap.Height == 0
            || bitmap.Width > UpscaleMath.MaxUpscaleInput || bitmap.Height > UpscaleMath.MaxUpscaleInput)
            return null;
        if (!TryLoadSession())
            return null;

        var input = new DenseTensor<float>(UpscaleMath.ToNchwRgb(bitmap), [1, 3, bitmap.Height, bitmap.Width]);
        using var results = _session!.Run([NamedOnnxValue.CreateFromTensor(InputName, input)]);
        var output = results.First().AsTensor<float>()!.ToArray();
        return UpscaleMath.Combine(UpscaleMath.HalveLanczos(output, bitmap.Width * 4, bitmap.Height * 4), bitmap);
    }

    public void Dispose() => _session?.Dispose();

    private bool TryLoadSession()
    {
        if (_loaded)
            return _session is not null;
        _loaded = true;
        byte[] model = ReadModel();
        if (model.Length == 0)
            return false;
        try
        {
            var options = new SessionOptions();
            try
            {
                // CoreML on macOS, all compute units; whatever it cannot run goes to the CPU provider anyway.
                options.AppendExecutionProvider("CoreML", new Dictionary<string, string> { ["MLComputeUnits"] = "ALL" });
                _session = new InferenceSession(model, options);
            }
            finally
            {
                options.Dispose();
            }
            Console.WriteLine("Neural upscale: CoreML execution provider");
        }
        catch (OnnxRuntimeException e)
        {
            // Without CoreML (other platforms): the CPU execution provider, which every platform ships.
            try
            {
                _session = new InferenceSession(model);
                Console.WriteLine($"Neural upscale: CPU execution provider ({e.Message})");
            }
            catch (OnnxRuntimeException inner)
            {
                _session = null;
                Console.WriteLine($"Neural upscale off: {inner.Message}");
            }
        }
        return _session is not null;
    }

    private static byte[] ReadModel()
    {
        using var stream = typeof(OnnxUpscaler).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
            return [];
        using var model = new MemoryStream();
        stream.CopyTo(model);
        return model.ToArray();
    }
}
