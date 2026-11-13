using Homestead.Assets.Gfx;
using Homestead.Client.Neural;

namespace Homestead.Client.Tests;

/// <summary>
/// The telemetry opt-out, checked on every test run — unlike the model-contract tests it needs no game
/// installation (the model is embedded, the runtime comes with the build), so CI runs it too. A real session
/// is the one thing that would switch ONNX Runtime's 1DS telemetry on (persistent device ID, SQLite event
/// queue); the opt-out must sit in the native environment before it initializes, and nothing may appear.
/// The stronger black-box variant, a built binary playing a real game, is <c>tools/check-telemetry.sh</c>.
/// </summary>
public class OnnxTelemetryTests
{
    [Fact]
    public void A_real_session_sets_the_native_opt_out_and_leaves_no_telemetry()
    {
        var before = OnnxTelemetry.Artifacts();
        using var upscaler = new OnnxUpscaler();
        upscaler.Upscale(new GfxBitmap(2, 2, 0, 0, new uint[4])); // Loads (or fails to load) the session.

        if (!OperatingSystem.IsWindows())
        {
            // Native getenv, not Environment.GetEnvironmentVariable: the managed view would prove nothing.
            Assert.Equal("1", OnnxTelemetry.NativeEnvironmentVariable("ORT_DISABLE_TELEMETRY"));
            Assert.Equal(before, OnnxTelemetry.Artifacts());
        }
    }
}
