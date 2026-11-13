using System.Runtime.InteropServices;

namespace Homestead.Client.Tests;

/// <summary>
/// What native ONNX Runtime would see: the C environment (its telemetry opt-out is read there, not in the
/// managed view) and the telemetry stores 1DS would leave under the user's profile.
/// </summary>
internal static class OnnxTelemetry
{
    [DllImport("libc", CharSet = CharSet.Ansi)]
    private static extern IntPtr getenv(string name);

    /// <summary>Unix only (the Windows telemetry backend is ETW, which the opt-out variable does not cover).</summary>
    public static string? NativeEnvironmentVariable(string name) => Marshal.PtrToStringAnsi(getenv(name));

    /// <summary>Every ONNX Runtime telemetry directory under the user's profile (they do not exist unless 1DS ran).</summary>
    public static List<string> Artifacts()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Path.Combine(home, ".cache", "Microsoft"),
            Path.Combine(home, "Library", "Application Support", "Microsoft"),
            Path.Combine(home, ".local", "share", "Microsoft"),
        };
        var found = new List<string>();
        foreach (string root in roots.Where(Directory.Exists))
            foreach (string dir in Directory.EnumerateDirectories(root, ".onnxruntime", SearchOption.AllDirectories))
                found.Add(dir);
        return found;
    }
}
