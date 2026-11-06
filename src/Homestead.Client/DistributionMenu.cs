using System.Text.Json;
using Homestead.Simulation;
using Homestead.Simulation.Commands;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Economy;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Homestead.Client;

/// <summary>
/// Waren › Verteilung in the blue panel, like the original's: at the left the goods with more than one
/// kind of user, at the right for the chosen one each user – construction sites („Baustelle“) and every
/// building type of the people that takes it – with its part in percent and a bar for its share
/// (<see cref="Logistics.ShareOf"/>, <see cref="ShareBar"/>). Goods in <c>data/ui.json</c>
/// (<c>distributionMenu</c>). Panel space.
/// </summary>
public sealed class DistributionMenu
{
    private static readonly Color Text = new(240, 225, 190);
    private static readonly Color Selected = new(220, 30, 20);
    private const int IconStep = 34;

    private readonly GoodsGrid _grid;
    private readonly List<(GoodsGrid.Good Good, List<(string Target, string Name)> Targets)> _goods = new();
    private int _chosen;

    /// <summary>The chosen good and font the caption line counts were wrapped for, and the counts.</summary>
    private (int Chosen, UiFont? Font, int[] Lines) _captionLines = (-1, null, Array.Empty<int>());

    /// <summary>The target whose bar is being dragged, while the button is held.</summary>
    private string? _dragging;

    public DistributionMenu(GoodsGrid grid, BuildingCatalog catalog, Func<BuildingDefinition, string> nameOf)
    {
        _grid = grid;
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(BuildingCatalog.DefaultDataDirectory, "ui.json")));
        var buildings = catalog.All.Where(b => b.Name != "stock").ToList();
        foreach (var entry in document.RootElement.GetProperty("distributionMenu").GetProperty("goods").EnumerateArray())
        {
            var material = Materials.Parse(entry.GetString()!)!.Value;
            var targets = new List<(string, string)>();
            if (buildings.Any(b => b.ConstructionPiles.Any(p => p.Material == material)))
                targets.Add((Logistics.Construction, Lang.T("distribution.construction")));
            targets.AddRange(buildings.Where(b => b.RequestPiles.Any(p => p.Material == material)).Select(b => (b.Name, nameOf(b))));
            if (targets.Count >= 2 && grid.Goods.FirstOrDefault(g => g.Material == material) is { } good)
                _goods.Add((good, targets));
        }
        _chosen = Math.Max(0, _goods.FindIndex(g => g.Good.Material == Material.Plank));
    }

    /// <summary>Shows the distribution of this good, if the menu has it.</summary>
    public void Choose(Material material)
    {
        int index = _goods.FindIndex(g => g.Good.Material == material);
        if (index >= 0)
            _chosen = index;
    }

    // Layout (panel space), top down in the content area.
    private static int Left => BuildMenu.Content.X + 4;
    private static int Width => BuildMenu.Content.Width - 8;
    private static int Top => BuildMenu.Content.Y + 26; // below the title

    private static Rectangle Icon(int i) => new(Left, Top + i * IconStep, GoodsGrid.IconSize, GoodsGrid.IconSize);

    private static int EntryX => Left + 42;

    /// <summary>Line count of each of the chosen good's target captions, wrapped once per chosen good and font.</summary>
    private int[] CaptionLines(UiFont font)
    {
        if (_captionLines.Chosen != _chosen || !ReferenceEquals(_captionLines.Font, font))
            _captionLines = (_chosen, font, _goods.Count == 0 ? Array.Empty<int>()
                : _goods[_chosen].Targets.Select(t => font.Wrap(t.Name, Left + Width - EntryX, 15).Count()).ToArray());
        return _captionLines.Lines;
    }

    /// <summary>A row with a one-line caption; every caption line beyond the first makes every row this much taller.</summary>
    private const int RowBaseHeight = 46;
    private const int CaptionLineHeight = 17;

    /// <summary>How far the percent text and its bar sit below the caption's last line.</summary>
    private const int PercentOffset = 20;

    /// <summary>Every row is as tall as the chosen good's tallest caption: one line's height per extra line
    /// of the tallest. The rows are uniform; a row's content starts at its top.</summary>
    private int EntryHeight(UiFont font) => _goods.Count == 0 ? RowBaseHeight : RowBaseHeight + CaptionLineHeight * (CaptionLines(font).Max() - 1);

    private int EntryY(int i, UiFont font) => Top + i * EntryHeight(font);

    private bool RowFits(int i, UiFont font) => EntryY(i, font) + EntryHeight(font) <= BuildMenu.Content.Bottom;

    private int PercentY(int i, UiFont font) => EntryY(i, font) + (CaptionLines(font)[i] - 1) * CaptionLineHeight + PercentOffset;

    private Rectangle Bar(int i, UiFont font) => new(EntryX + 46, PercentY(i, font) + 1, Left + Width - EntryX - 46, ShareBar.Height);

    public void Draw(SpriteBatch batch, Texture2D pixel, UiFont font, World world, int player, Vector2 cursor)
    {
        for (int i = 0; i < _goods.Count; i++)
        {
            var icon = Icon(i);
            batch.Draw(_goods[i].Good.Icon, icon, Color.White);
            if (i == _chosen)
                Frame(batch, pixel, icon);
            else if (icon.Contains(cursor))
                batch.Draw(pixel, icon, Color.White * 0.15f);
        }
        if (_goods.Count == 0)
            return;
        var (good, targets) = _goods[_chosen];
        double total = targets.Sum(t => world.Logistics.ShareOf(player, good.Material, t.Target));
        for (int i = 0; i < targets.Count; i++)
        {
            if (!RowFits(i, font))
                break;
            var (target, name) = targets[i];
            double share = world.Logistics.ShareOf(player, good.Material, target);
            int y = EntryY(i, font);
            foreach (string line in font.Wrap(name, Left + Width - EntryX, 15))
            {
                SelectionPanel.Shadowed(batch, font, line, new Vector2(EntryX, y), 15, Text);
                y += CaptionLineHeight;
            }
            string percent = $"{(total > 0 ? share / total * 100 : 0):0}%";
            SelectionPanel.Shadowed(batch, font, percent, new Vector2(EntryX, PercentY(i, font)), 15, Text);
            ShareBar.Draw(batch, pixel, Bar(i, font), share);
        }
    }

    /// <summary>
    /// Mouse handling (panel space): a press on a good chooses it, a press on a bar starts dragging it;
    /// while held, the dragged bar follows the cursor. Returns a message, or null.
    /// </summary>
    public string? Update(Vector2 panel, UiFont font, bool pressed, bool justPressed, bool onMenu, World world, int player, IssueCommand issue)
    {
        if (!pressed || _goods.Count == 0)
        {
            _dragging = null;
            return null;
        }
        var (good, targets) = _goods[_chosen];
        if (justPressed && onMenu)
        {
            for (int i = 0; i < _goods.Count; i++)
                if (Icon(i).Contains(panel))
                {
                    _chosen = i;
                    return $"{_goods[i].Good.Name}: distribution";
                }
            for (int i = 0; i < targets.Count; i++)
                if (RowFits(i, font) && Bar(i, font).Contains(panel))
                    _dragging = targets[i].Target;
        }
        if (_dragging is not { } dragged || targets.FindIndex(t => t.Target == dragged) is var index && index < 0)
            return null;
        double share = ShareBar.ValueAt(Bar(index, font), panel.X);
        issue(new SetDistribution(player, good.Material, dragged, share));
        return $"{good.Name} for {targets[index].Name}: share {share * 100:0} %";
    }

    /// <summary>What the element under the point does (hover text), or null.</summary>
    public string? Hover(Vector2 panel, UiFont font)
    {
        for (int i = 0; i < _goods.Count; i++)
            if (Icon(i).Contains(panel))
                return $"{_goods[i].Good.Name}: show its distribution";
        if (_goods.Count > 0)
        {
            var (good, targets) = _goods[_chosen];
            for (int i = 0; i < targets.Count; i++)
                if (RowFits(i, font) && Bar(i, font).Contains(panel))
                    return $"{good.Name} for {targets[i].Name} (click or drag; empty: only when no one else wants it)";
        }
        return null;
    }

    private static void Frame(SpriteBatch batch, Texture2D pixel, Rectangle rect)
    {
        batch.Draw(pixel, new Rectangle(rect.X, rect.Y, rect.Width, 2), Selected);
        batch.Draw(pixel, new Rectangle(rect.X, rect.Bottom - 2, rect.Width, 2), Selected);
        batch.Draw(pixel, new Rectangle(rect.X, rect.Y, 2, rect.Height), Selected);
        batch.Draw(pixel, new Rectangle(rect.Right - 2, rect.Y, 2, rect.Height), Selected);
    }
}
