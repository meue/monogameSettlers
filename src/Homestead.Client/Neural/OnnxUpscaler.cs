using System.Runtime.InteropServices;
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
/// stays 1x (<see cref="Ready"/> tells the library, which stops trying and shows a notice).
/// </summary>
public sealed class OnnxUpscaler : IUpscaler, IDisposable
{
    private const string ResourceName = "realesr-animevideov3-x4.onnx";
    private const string InputName = "input";

    static OnnxUpscaler()
    {
        // ONNX Runtime's official binaries phone home by default: 1DS telemetry with a persistent device ID and
        // OS/CPU/RAM/process events for a Microsoft server, queued as soon as a session initializes (the
        // package's Privacy.md). The documented opt-out is the ORT_DISABLE_TELEMETRY environment variable, read
        // once when the native library initializes — so it must sit in the native (C) environment before
        // anything ORT runs. Environment.SetEnvironmentVariable is not enough: it updates only the managed
        // view, which native getenv never sees (verified: the telemetry database still appeared). This class is
        // the only place ORT is touched, and a static constructor runs before the first of its calls. The game
        // sends nothing, ever.
        SetNativeEnvironmentVariable("ORT_DISABLE_TELEMETRY", "1");
    }

    [DllImport("libc", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int setenv(string name, string? value, int overwrite);

    [DllImport("api-ms-win-crt-environment-l1-1-0.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int _putenv_s(string name, string? value);

    /// <summary>The variable into the native environment (what libraries' getenv reads) and the managed one.</summary>
    private static void SetNativeEnvironmentVariable(string name, string value)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                _putenv_s(name, value);
            else
                setenv(name, value, 1);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // A platform without the C runtime entry (none of the supported ones): managed-only then.
        }
        Environment.SetEnvironmentVariable(name, value);
    }

    private InferenceSession? _session;
    private bool _loaded;

    /// <summary>False until a session exists, and forever on a platform without the native library (osx-x64).</summary>
    public bool Ready { get; private set; }

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
            return Ready;
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
        catch (Exception e) when (e is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException
            or TypeInitializationException or OutOfMemoryException)
        {
            // Without CoreML (other platforms): the CPU execution provider, which every platform ships.
            try
            {
                _session = new InferenceSession(model);
                Console.WriteLine($"Neural upscale: CPU execution provider ({e.Message})");
            }
            catch (Exception inner) when (inner is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException
                or TypeInitializationException or OutOfMemoryException)
            {
                // No native ONNX Runtime at all (osx-x64 ships none) or it cannot load: the feature stays off.
                _session = null;
                Console.WriteLine($"Neural upscale unavailable on this system: {inner.Message}");
            }
        }
        Ready = _session is not null;
        return Ready;
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
