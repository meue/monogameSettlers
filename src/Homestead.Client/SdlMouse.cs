using System.Runtime.InteropServices;
using Microsoft.Xna.Framework.Input;

namespace Homestead.Client;

/// <summary>
/// The mouse with buttons from SDL's events instead of MonoGame's polled button state. Polling once a frame misses a
/// press and release in between (a tap on a trackpad), and on macOS the trackpad's secondary click (two fingers)
/// comes as a right button event, whatever the polled state says. A button counts as pressed in
/// a frame if it is down or was pressed since the frame before, so the next frame sees it released: a click.
/// Without SDL at hand the buttons stay MonoGame's.
/// </summary>
public static class SdlMouse
{
    private const int MouseButtonDown = 0x401, MouseButtonUp = 0x402;
    private const int Left = 1, Middle = 2, Right = 4;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EventFilter(IntPtr userdata, IntPtr sdlEvent);

    [DllImport("SDL2", EntryPoint = "SDL_AddEventWatch", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SdlAddEventWatch(IntPtr filter, IntPtr userdata);

    /// <summary>Kept alive as long as SDL may call it.</summary>
    private static EventFilter? _watch;

    private static bool _tried, _installed;

    /// <summary>The buttons down by the events, those pressed since the last frame, and those of this frame.</summary>
    private static int _down, _pressed, _frame;

    static SdlMouse() => SdlLibrary.Register();

    /// <summary>Starts listening to SDL's mouse button events (once, with SDL running).</summary>
    private static void Install()
    {
        _tried = true;
        try
        {
            _watch = OnEvent;
            SdlAddEventWatch(Marshal.GetFunctionPointerForDelegate(_watch), IntPtr.Zero);
            _installed = true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _watch = null;
        }
    }

    /// <summary>A new frame (first thing in the game's Update): its buttons are those down now or pressed since the frame before.</summary>
    public static void NextFrame()
    {
        if (!_tried)
            Install();
        int pressed = Interlocked.Exchange(ref _pressed, 0);
        _frame = Volatile.Read(ref _down) | pressed;
    }

    /// <summary>MonoGame's mouse state (position, wheel) with this frame's buttons.</summary>
    public static MouseState GetState()
    {
        var state = Mouse.GetState();
        if (!_installed)
            return state;
        ButtonState Of(int button) => (_frame & button) != 0 ? ButtonState.Pressed : ButtonState.Released;
        return new MouseState(state.X, state.Y, state.ScrollWheelValue, Of(Left), Of(Middle), Of(Right),
            state.XButton1, state.XButton2, state.HorizontalScrollWheelValue);
    }

    private static int OnEvent(IntPtr userdata, IntPtr sdlEvent)
    {
        // SDL_MouseButtonEvent: type, timestamp, windowID, which (4 bytes each), then button and state.
        int type = Marshal.ReadInt32(sdlEvent);
        if (type is not (MouseButtonDown or MouseButtonUp))
            return 1;
        int button = Marshal.ReadByte(sdlEvent, 16) switch { 1 => Left, 2 => Middle, 3 => Right, _ => 0 };
        if (type == MouseButtonDown)
        {
            Interlocked.Or(ref _down, button);
            Interlocked.Or(ref _pressed, button);
        }
        else
            Interlocked.And(ref _down, ~button);
        return 1;
    }
}
