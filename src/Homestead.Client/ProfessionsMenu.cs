using System.Text.Json;
using Homestead.Assets.Gfx;
using Homestead.Simulation;
using Homestead.Simulation.Commands;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Economy;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Homestead.Client;

/// <summary>How the find buttons of <see cref="ProfessionsMenu"/> select a kind of settler.</summary>
public enum FindMode
{
    /// <summary>All of the kind on the whole map.</summary>
    World,

    /// <summary>All of the kind on the screen.</summary>
    Screen,

    /// <summary>The next one of the kind (one after another).</summary>
    One,
}

/// <summary>
/// Siedler › Berufe in the blue panel, like the original's: the shares of the settlers (carriers kept at
/// least, diggers and builders at most – a spinner each, 5 % a click) and the special professions:
/// geologists, pioneers, scouts and pioneers back to carriers, each with three find buttons (all on the
/// map, all on the screen, the next one) and two buttons turning 5 or 1 carriers into them (or pioneers
/// into carriers). Graphics in
/// <c>data/ui.json</c> (<c>professionsMenu</c>). Panel space.
/// </summary>
public sealed class ProfessionsMenu : IDisposable
{
    private static readonly Color Text = new(240, 225, 190);
    private const double Step = 0.05;
    private static readonly int[] Amounts = [5, 1];

    /// <summary>The shares: at least (&gt;) or at most (&lt;), name <c>professions.&lt;key&gt;</c>.</summary>
    private static readonly (Share Share, string Sign, string Key)[] Shares =
    [
        (Share.Carriers, ">", "carriers"),
        (Share.Diggers, "<", "diggers"),
        (Share.Builders, "<", "builders"),
    ];

    /// <summary>The special professions: top left, top right, bottom left, bottom right.</summary>
    private static readonly Entry[] Entries =
    [
        new("GEOLOGIST", Profession.Geologist, "geologists", Available: true),
        new("PIONEER", Profession.Pioneer, "pioneers", Available: true),
        new("THIEF", Profession.Scout, "scouts", Available: true),
        new("BEARER", Profession.Pioneer, "pioneers", Available: true, ToCarriers: true),
    ];

    private readonly Texture2D _spinner;
    private readonly Texture2D[] _find;
    private readonly Texture2D[] _convert;

    public ProfessionsMenu(GraphicsDevice device, GfxDirectory gfx)
    {
        var gui = gfx.Open(3);
        Texture2D Load(int index)
        {
            var bitmap = gui.ReadGui(index);
            var texture = new Texture2D(device, bitmap.Width, bitmap.Height);
            texture.SetData(bitmap.Pixels);
            return texture;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(BuildingCatalog.DefaultDataDirectory, "ui.json")));
        var menu = document.RootElement.GetProperty("professionsMenu");
        _spinner = Load(menu.GetProperty("spinner").GetInt32());
        _find = menu.GetProperty("find").EnumerateArray().Select(i => Load(i.GetInt32())).ToArray();
        _convert = menu.GetProperty("convert").EnumerateArray().Select(i => Load(i.GetInt32())).ToArray();
    }

    // Layout (panel space), top down in the content area.
    private static int Left => BuildMenu.Content.X + 4;
    private static int Width => BuildMenu.Content.Width - 8;
    private static int Top => BuildMenu.Content.Y + 24; // below the title
    private const int ShareHeight = 34;
    private static Rectangle Spinner(int row) => new(Left, Top + row * ShareHeight, 20, 30);
    private static int HeadingY => Top + Shares.Length * ShareHeight + 6;
    private const int CellHeight = 68;

    private static Point Cell(int i) => new(Left + i % 2 * (Width / 2), HeadingY + 26 + i / 2 * CellHeight);

    private static Rectangle FindButton(int entry, int i) => new(Cell(entry).X, Cell(entry).Y + i * 20, 20, 20);

    private static Rectangle Portrait(int entry) => new(Cell(entry).X + 21, Cell(entry).Y, 40, 60);

    private static Rectangle ConvertButton(int entry, int i) => new(Cell(entry).X + 62, Cell(entry).Y + i * 30, 30, 30);

    public void Draw(SpriteBatch batch, Texture2D pixel, UiFont font, World world, int player, Portraits portraits, SettlerRenderer settlers,
        Civilization people, Vector2 cursor)
    {
        for (int row = 0; row < Shares.Length; row++)
        {
            var (share, sign, key) = Shares[row];
            string name = Lang.T("professions." + key);
            var spinner = Spinner(row);
            batch.Draw(_spinner, spinner, Color.White);
            if (spinner.Contains(cursor))
                batch.Draw(pixel, SpinnerHalf(spinner, cursor.Y < spinner.Center.Y), Color.White * 0.15f);
            int percent = (int)Math.Round(world.Recruitment.ShareOf(player, share) * 100);
            Write(batch, font, $"{sign} {percent} % {name}", new Vector2(spinner.Right + 8, spinner.Y + 6), Text, 15);
        }

        string special = Lang.T("professions.special");
        var heading = font.Measure(special, 15);
        Write(batch, font, special, new Vector2(Left + (Width - heading.X) / 2, HeadingY), Text, 15);
        for (int e = 0; e < Entries.Length; e++)
        {
            var entry = Entries[e];
            var shade = entry.Available ? Color.White : Color.White * 0.35f;
            for (int i = 0; i < _find.Length; i++)
            {
                var rect = FindButton(e, i);
                batch.Draw(_find[i], rect, shade);
                if (entry.Available && rect.Contains(cursor))
                    batch.Draw(pixel, rect, Color.White * 0.15f);
            }
            var portrait = Portrait(e);
            portraits.Draw(batch, entry.Portrait, portrait, settlers, people, PlayerColors.Of(player));
            if (!entry.Available)
                batch.Draw(pixel, portrait, Color.Black * 0.6f);
            for (int i = 0; i < _convert.Length; i++)
            {
                var rect = ConvertButton(e, i);
                batch.Draw(_convert[i], rect, shade);
                if (entry.Available && rect.Contains(cursor))
                    batch.Draw(pixel, rect, Color.White * 0.15f);
            }
        }
    }

    private static Rectangle SpinnerHalf(Rectangle spinner, bool upper) =>
        new(spinner.X, upper ? spinner.Y : spinner.Center.Y, spinner.Width, spinner.Height / 2);

    /// <summary>
    /// A click (panel space): changes a share or converts carriers (by commands; their messages follow once applied); a find
    /// button returns what to select in <paramref name="find"/> (the caller selects), the ids of converted carriers go to
    /// <paramref name="chosen"/> once applied (also those that follow after their delivery), taken from <paramref name="among"/>
    /// in its order if given. Returns a message, or null.
    /// </summary>
    public string? Click(Vector2 panel, World world, int player, IssueCommand issue, out (Profession Profession, FindMode Mode)? find,
        Action<int[]>? chosen = null, IReadOnlyList<Settler>? among = null)
    {
        find = null;
        for (int row = 0; row < Shares.Length; row++)
            if (Spinner(row).Contains(panel))
            {
                var (share, _, name) = Shares[row];
                bool up = panel.Y < Spinner(row).Center.Y;
                issue(new SetRecruitmentShare(player, share, world.Recruitment.ShareOf(player, share) + (up ? Step : -Step)),
                    _ => $"{name}: {world.Recruitment.ShareOf(player, share) * 100:0} %");
                UiSounds.Step(up);
                return null;
            }
        for (int e = 0; e < Entries.Length; e++)
        {
            var entry = Entries[e];
            for (int i = 0; i < _find.Length; i++)
                if (FindButton(e, i).Contains(panel))
                {
                    if (entry.Profession is not { } profession)
                        return $"no {entry.Name} yet";
                    find = (profession, (FindMode)i);
                    return null;
                }
            for (int i = 0; i < Amounts.Length; i++)
                if (ConvertButton(e, i).Contains(panel))
                {
                    if (entry.Profession is not { } converted)
                        return $"no {entry.Name} yet";
                    if (entry.ToCarriers)
                    {
                        issue(new PioneersToCarriers(player, Amounts[i]),
                            result => (int)result! is var back && back == 0 ? "no pioneers to turn into carriers" : $"{back} pioneers became carriers");
                        return null;
                    }
                    bool carriersAmong = among is null || among.Any(s => s.IsCarrier && s.Player == player);
                    issue(new ConvertCarriers(player, Amounts[i], converted, among?.Select(s => s.Id).ToArray()), result =>
                    {
                        var (now, later, ids) = ((int, int, int[]))result!;
                        chosen?.Invoke(ids);
                        return now + later == 0 && !carriersAmong ? $"no carrier on the screen to become one of the {entry.Name}"
                            : now + later == 0 ? $"no carrier may become one of the {entry.Name} (carrier share)"
                            : later == 0 ? $"{now} carriers became {entry.Name}"
                            : $"{now} carriers became {entry.Name}, {later} more after their delivery";
                    });
                    return null;
                }
        }
        return null;
    }

    /// <summary>What the element under the point does (hover text), or null.</summary>
    public string? Hover(Vector2 panel)
    {
        for (int row = 0; row < Shares.Length; row++)
            if (Spinner(row).Contains(panel))
                return Shares[row].Share == Share.Carriers
                    ? "carriers kept at least (up/down: 5 % more/less)"
                    : $"{Shares[row].Key} at most (up/down: 5 % more/less)";
        for (int e = 0; e < Entries.Length; e++)
        {
            var entry = Entries[e];
            string suffix = entry.Available ? "" : " (not there yet)";
            for (int i = 0; i < _find.Length; i++)
                if (FindButton(e, i).Contains(panel))
                    return (FindMode)i switch
                    {
                        FindMode.World => $"select all {entry.Name}",
                        FindMode.Screen => $"select all {entry.Name} on the screen",
                        _ => $"select the next of the {entry.Name}",
                    } + suffix;
            for (int i = 0; i < Amounts.Length; i++)
                if (ConvertButton(e, i).Contains(panel))
                    return (entry.ToCarriers ? $"{Amounts[i]} pioneers become carriers" : $"{Amounts[i]} carriers become {entry.Name}") + suffix;
            if (Portrait(e).Contains(panel))
                return entry.Name + suffix;
        }
        return null;
    }

    private static void Write(SpriteBatch batch, UiFont font, string text, Vector2 position, Color color, int size = 14) =>
        SelectionPanel.Shadowed(batch, font, text, position, size, color);

    public void Dispose()
    {
        _spinner.Dispose();
        foreach (var texture in _find.Concat(_convert))
            texture.Dispose();
    }

    /// <param name="Portrait">Portrait key (<see cref="Portraits"/>).</param>
    /// <param name="Profession">What the find buttons select; null while the kind does not exist.</param>
    private sealed record Entry(string Portrait, Profession? Profession, string Name, bool Available, bool ToCarriers = false);
}
