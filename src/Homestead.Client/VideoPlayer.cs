using System.Collections.Concurrent;
using System.Diagnostics;
using Homestead.Assets.Video;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Homestead.Client;

/// <summary>
/// Plays one of the original's videos (<see cref="AviFile"/>) over the whole window, 4 : 3 on black. The pictures are
/// Indeo 5, which an installed <c>ffmpeg</c> decodes in its own process (found through <c>HOMESTEAD_FFMPEG</c>, next to
/// the program or on the PATH); the PCM sound comes from the file itself. Pictures follow the clock that starts with the
/// sound, so a slow frame is skipped rather than shifting the sound. Esc, Space, Enter or a click ends it.
/// </summary>
public sealed class VideoPlayer : IDisposable
{
    private const int FramesAhead = 8;

    private readonly AviFile _avi;
    private readonly Process _ffmpeg;
    private readonly Texture2D _picture;
    private readonly BlockingCollection<byte[]> _frames = new(FramesAhead);
    private readonly SoundEffect? _sound;
    private SoundEffectInstance? _voice;
    private readonly Stopwatch _clock = new();
    private readonly float _volume;
    private KeyboardState _previousKeys = Keyboard.GetState();
    private MouseState _previousMouse = SdlMouse.GetState();
    private bool _skipped;

    private VideoPlayer(GraphicsDevice device, AviFile avi, Process ffmpeg, float volume)
    {
        _avi = avi;
        _ffmpeg = ffmpeg;
        _volume = volume;
        _picture = new Texture2D(device, avi.Width, avi.Height);
        if (avi.Audio is { BitsPerSample: 16, Channels: 1 or 2 } format && volume > 0)
            _sound = new SoundEffect(avi.ReadAudio(), format.SampleRate, format.Channels == 2 ? AudioChannels.Stereo : AudioChannels.Mono);
        var reader = new Thread(ReadFrames) { IsBackground = true, Name = "Video frames" };
        reader.Start();
    }

    /// <summary>The frame shown last (0 = the first); −1 before the first one.</summary>
    public int Shown { get; private set; } = -1;

    /// <summary>The video played to its end or was skipped.</summary>
    public bool Finished => _skipped || _frames.IsCompleted || _clock.IsRunning && _clock.Elapsed.TotalSeconds > _avi.Seconds + 1;

    /// <summary>
    /// Starts the video at <paramref name="path"/>; null (with a note on the console) if it cannot be played,
    /// e.g. without ffmpeg.
    /// </summary>
    public static VideoPlayer? TryOpen(GraphicsDevice device, string path, float volume)
    {
        AviFile avi;
        try
        {
            avi = AviFile.Open(path);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Video skipped: {path} unreadable ({e.Message})");
            return null;
        }
        if (FindFfmpeg() is not { } ffmpeg)
        {
            Console.WriteLine($"Video skipped: no ffmpeg found (PATH, next to the program or HOMESTEAD_FFMPEG) for {path}");
            return null;
        }
        var start = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in new[] { "-v", "error", "-i", path, "-an", "-f", "rawvideo", "-pix_fmt", "rgba", "-" })
            start.ArgumentList.Add(argument);
        try
        {
            var process = Process.Start(start)!;
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    Console.WriteLine($"ffmpeg: {e.Data}");
            };
            process.BeginErrorReadLine();
            return new VideoPlayer(device, avi, process, volume);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.WriteLine($"Video skipped: ffmpeg did not start ({e.Message})");
            return null;
        }
    }

    /// <summary>
    /// The ffmpeg program: <c>HOMESTEAD_FFMPEG</c>, next to the program (also in a folder <c>ffmpeg</c>), or on the PATH;
    /// on macOS also where Homebrew puts it (an app started from the Finder does not get the shell's PATH).
    /// </summary>
    public static string? FindFfmpeg()
    {
        string name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        if (Environment.GetEnvironmentVariable("HOMESTEAD_FFMPEG") is { Length: > 0 } configured && File.Exists(configured))
            return configured;
        var places = new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "ffmpeg") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            .Concat(OperatingSystem.IsMacOS() ? ["/opt/homebrew/bin", "/usr/local/bin"] : []);
        return places.Select(p => Path.Combine(p, name)).FirstOrDefault(File.Exists);
    }

    /// <summary>Background thread: whole frames from ffmpeg into the queue, until its end.</summary>
    private void ReadFrames()
    {
        int size = _avi.Width * _avi.Height * 4;
        var output = _ffmpeg.StandardOutput.BaseStream;
        try
        {
            while (true)
            {
                var frame = new byte[size];
                int read = 0;
                while (read < size)
                {
                    int n = output.Read(frame, read, size - read);
                    if (n == 0)
                        return;
                    read += n;
                }
                _frames.Add(frame);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Stopped while reading (skipped).
        }
        finally
        {
            _frames.CompleteAdding();
        }
    }

    public void Update()
    {
        var keys = Keyboard.GetState();
        var mouse = SdlMouse.GetState();
        bool Pressed(Keys key) => keys.IsKeyDown(key) && !_previousKeys.IsKeyDown(key);
        if (Pressed(Keys.Escape) || Pressed(Keys.Space) || Pressed(Keys.Enter)
            || mouse.LeftButton == ButtonState.Released && _previousMouse.LeftButton == ButtonState.Pressed)
            _skipped = true;
        _previousKeys = keys;
        _previousMouse = mouse;
        if (_skipped)
            return;

        // The clock (and the sound) start with the first picture, so ffmpeg's start does not shift them apart.
        if (!_clock.IsRunning)
        {
            if (!_frames.TryTake(out var first))
                return;
            Show(first, 0);
            _clock.Start();
            if (_sound is not null)
            {
                _voice = _sound.CreateInstance();
                _voice.Volume = Math.Clamp(_volume, 0, 1);
                _voice.Play();
            }
            return;
        }
        int due = (int)(_clock.Elapsed.TotalSeconds * _avi.FramesPerSecond);
        byte[]? latest = null;
        int index = Shown;
        while (index < due && _frames.TryTake(out var frame))
        {
            latest = frame;
            index++;
        }
        if (latest is not null)
            Show(latest, index);
    }

    private void Show(byte[] frame, int index)
    {
        _picture.SetData(frame);
        Shown = index;
    }

    public void Draw(SpriteBatch batch, Texture2D pixel, Rectangle view)
    {
        batch.Draw(pixel, view, Color.Black);
        if (Shown < 0)
            return;
        float scale = Math.Min(view.Width / (float)_avi.Width, view.Height / (float)_avi.Height);
        int width = (int)(_avi.Width * scale), height = (int)(_avi.Height * scale);
        batch.Draw(_picture, new Rectangle(view.X + (view.Width - width) / 2, view.Y + (view.Height - height) / 2, width, height), Color.White);
    }

    public void Dispose()
    {
        _voice?.Stop();
        _voice?.Dispose();
        _sound?.Dispose();
        try
        {
            if (!_ffmpeg.HasExited)
                _ffmpeg.Kill();
        }
        catch (InvalidOperationException)
        {
        }
        // Unblock the reader if it waits for room in the queue.
        while (_frames.TryTake(out _))
        {
        }
        _ffmpeg.Dispose();
        _picture.Dispose();
    }
}
