using System.Diagnostics;
using FontStashSharp;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Homestead.Simulation;

namespace Homestead.Client;

/// <summary>
/// Text for the menus: a TrueType font installed on the system, rendered at runtime by FontStashSharp
/// (MIT). No font comes with the project; without one found, text is left out.
/// </summary>
public sealed class UiFont : IDisposable
{
    /// <summary>Fonts tried in order (Windows; macOS) before searching the font directories.</summary>
    private static readonly string[] Candidates =
    [
        @"C:\Windows\Fonts\segoeui.ttf",
        @"C:\Windows\Fonts\arial.ttf",
        "/System/Library/Fonts/Supplemental/Arial.ttf",
        "/Library/Fonts/Arial.ttf",
    ];

    /// <summary>Preferred file names, searched recursively in <see cref="FontDirectories"/>; every distribution sorts them differently
    /// (Debian <c>truetype/dejavu</c>, Arch <c>TTF</c>, Fedora <c>dejavu-sans-fonts</c>, …).</summary>
    private static readonly string[] PreferredNames =
    [
        "DejaVuSans.ttf",
        "LiberationSans-Regular.ttf",
        "NotoSans-Regular.ttf",
        "Arial.ttf",
        "arial.ttf",
        "FreeSans.ttf",
        "Cantarell-Regular.ttf",
        "OpenSans-Regular.ttf",
        "Roboto-Regular.ttf",
        "Ubuntu-R.ttf",
    ];

    private static IEnumerable<string> FontDirectories()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(dataHome) && home.Length > 0)
            dataHome = Path.Combine(home, ".local", "share");
        yield return "/usr/share/fonts";
        yield return "/usr/local/share/fonts";
        yield return "/run/host/fonts"; // Flatpak
        if (!string.IsNullOrEmpty(dataHome))
            yield return Path.Combine(dataHome, "fonts");
        if (home.Length > 0)
            yield return Path.Combine(home, ".fonts");
        foreach (string dir in (Environment.GetEnvironmentVariable("XDG_DATA_DIRS") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
            yield return Path.Combine(dir, "fonts"); // NixOS and others without /usr/share/fonts
    }

    private readonly FontSystem? _system;

    public UiFont()
    {
        if (Locate() is not { } path)
        {
            Console.Error.WriteLine("No TrueType font found: menus are shown without text. " +
                                    "Install e.g. DejaVu Sans, Liberation Sans or Noto Sans.");
            return;
        }
        _system = new FontSystem();
        _system.AddFont(File.ReadAllBytes(path));
    }

    /// <summary>A known font in the usual places, then fontconfig's choice; null when neither has a TrueType font.</summary>
    internal static string? Locate()
    {
        if (Candidates.FirstOrDefault(File.Exists) is { } fixedPath)
            return fixedPath;
        List<string> files = FontDirectories().Distinct().Where(Directory.Exists).SelectMany(AllFonts).ToList();
        foreach (string name in PreferredNames)
            if (files.FirstOrDefault(f => Path.GetFileName(f) == name) is { } path)
                return path;
        return FontConfigMatch();
    }

    private static IEnumerable<string> AllFonts(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.ttf", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
            }).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Asks fontconfig (<c>fc-match</c>) for a sans-serif font covering Polish; only TrueType files are usable.</summary>
    private static string? FontConfigMatch()
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo("fc-match", ["-f", "%{file}", "sans-serif:lang=pl"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
                return null;
            string output = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(3000))
                return null;
            return output.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) && File.Exists(output) ? output : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null; // fontconfig not installed
        }
    }

    public void Draw(SpriteBatch batch, string text, Vector2 position, Color color, int size = 14)
    {
        if (_system is not null)
            batch.DrawString(_system.GetFont(size), PlayerText.Drawable(text), position, color);
    }

    public Vector2 Measure(string text, int size = 14) =>
        _system?.GetFont(size).MeasureString(PlayerText.Drawable(text)) ?? Vector2.Zero;

    /// <summary>Breaks <paramref name="text"/> into lines no wider than <paramref name="width"/>.</summary>
    public IEnumerable<string> Wrap(string text, int width, int size = 14)
    {
        string line = "";
        foreach (string word in text.Split(' '))
        {
            string candidate = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && Measure(candidate, size).X > width)
            {
                yield return line;
                line = word;
            }
            else
                line = candidate;
        }
        if (line.Length > 0)
            yield return line;
    }

    public void Dispose() => _system?.Dispose();
}
