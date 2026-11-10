using System.Globalization;
using System.Text;

namespace Homestead.Simulation;

/// <summary>
/// Text other computers sent (names, chat, a server's reasons): what may be shown, logged and passed on. Control and format
/// characters go (line breaks would garble a list or a log line, bidirectional overrides turn text around), and so do
/// unpaired surrogates: the font throws on them while drawing, which would end the game of whoever sees the text. Cutting
/// never splits a surrogate pair. Pure function of the text, so it may run in the simulation.
/// </summary>
public static class PlayerText
{
    /// <summary>The text cleaned, trimmed and cut to at most <paramref name="maxLength"/> chars; empty for null.</summary>
    public static string Clean(string? text, int maxLength = int.MaxValue)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var clean = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                if (IsAllowed(CharUnicodeInfo.GetUnicodeCategory(text, i)))
                    clean.Append(c).Append(text[i + 1]);
                i++;
            }
            else if (!char.IsSurrogate(c) && IsAllowed(char.GetUnicodeCategory(c)))
                clean.Append(c);
        }
        string trimmed = clean.ToString().Trim();
        if (trimmed.Length <= maxLength)
            return trimmed;
        int end = maxLength > 0 && char.IsHighSurrogate(trimmed[maxLength - 1]) ? maxLength - 1 : maxLength;
        return trimmed[..end].TrimEnd();
    }

    /// <summary>The text with unpaired surrogates replaced, the rest untouched: safe to measure and draw, whatever its source.</summary>
    public static string Drawable(string text)
    {
        for (int i = 0; i < text.Length; i++)
            if (char.IsSurrogate(text[i]))
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    i++;
                else
                    return Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text)); // replaces them with U+FFFD
            }
        return text;
    }

    private static bool IsAllowed(UnicodeCategory category) => category is not (UnicodeCategory.Control or UnicodeCategory.Format
        or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate
        or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned);
}
