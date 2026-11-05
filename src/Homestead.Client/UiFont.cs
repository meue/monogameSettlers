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
    /// <summary>Fonts tried in order (Linux, also inside the Flatpak runtime; Windows; macOS).</summary>
    private static readonly string[] Candidates =
    [
        "/usr/share/fonts/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/run/host/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/liberation-sans/LiberationSans-Regular.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
        @"C:\Windows\Fonts\segoeui.ttf",
        @"C:\Windows\Fonts\arial.ttf",
        "/System/Library/Fonts/Supplemental/Arial.ttf",
        "/Library/Fonts/Arial.ttf",
    ];

    private readonly FontSystem? _system;

    public UiFont()
    {
        if (Candidates.FirstOrDefault(File.Exists) is not { } path)
        {
            Console.Error.WriteLine("No TrueType font found: menus are shown without text.");
            return;
        }
        _system = new FontSystem();
        _system.AddFont(File.ReadAllBytes(path));
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
