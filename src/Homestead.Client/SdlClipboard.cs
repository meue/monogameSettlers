using System.Runtime.InteropServices;

namespace Homestead.Client;

/// <summary>The system clipboard: MonoGame has none; DesktopGL runs on SDL2, which has one.</summary>
public static class SdlClipboard
{
    [DllImport("SDL2", EntryPoint = "SDL_GetClipboardText")]
    private static extern IntPtr SdlGetClipboardText();

    [DllImport("SDL2", EntryPoint = "SDL_free")]
    private static extern void SdlFree(IntPtr memory);

    static SdlClipboard() => SdlLibrary.Register();

    /// <summary>The text on the clipboard; null when there is none or SDL is not at hand.</summary>
    public static string? Text()
    {
        try
        {
            var text = SdlGetClipboardText();
            if (text == IntPtr.Zero)
                return null;
            string? value = Marshal.PtrToStringUTF8(text);
            SdlFree(text);
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }
}
