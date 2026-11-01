using System.Runtime.InteropServices;

namespace Homestead.Client;

/// <summary>Finds SDL2 (the copy MonoGame DesktopGL brings) for this assembly's DllImports of "SDL2".</summary>
internal static class SdlLibrary
{
    private static int _registered;

    /// <summary>Registers the resolver once (an assembly can have only one).</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
            return;
        NativeLibrary.SetDllImportResolver(typeof(SdlLibrary).Assembly, (name, assembly, path) =>
        {
            if (name != "SDL2")
                return IntPtr.Zero;
            string[] files = ["libSDL2-2.0.so.0", "SDL2.dll", "libSDL2-2.0.0.dylib", "libSDL2.so"];
            foreach (string file in files)
                if (NativeLibrary.TryLoad(file, assembly, path, out var handle))
                    return handle;
            // MonoGame keeps its copy next to the program or in a folder per platform (osx-arm64, linux-x64, …).
            string[] folders = [AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, RuntimeInformation.RuntimeIdentifier)];
            foreach (string folder in folders)
                foreach (string file in files)
                    if (File.Exists(Path.Combine(folder, file)) && NativeLibrary.TryLoad(Path.Combine(folder, file), out var handle))
                        return handle;
            return IntPtr.Zero;
        });
    }
}
