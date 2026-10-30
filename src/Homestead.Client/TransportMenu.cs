using System.Text.Json;
using Homestead.Assets.Gfx;
using Homestead.Simulation;
using Homestead.Simulation.Commands;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Economy;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Homestead.Client;

/// <summary>
/// Waren › Warentransport in the blue panel, like the original's: the people's goods in the player's
/// transport order (<see cref="Logistics.TransportOrder"/>, highest first; rows of six, each icon a little
/// lower than the one before), a dot on each telling whether it goes to storehouses (green) or not (red).
/// A click chooses a good; the buttons below – store, do not store, top, one up, one down, bottom – work on
/// the chosen one. Reordered icons glide to their new place. Button images in <c>data/ui.json</c>
/// (<c>transportMenu</c>). Panel space.
/// </summary>
public sealed class TransportMenu : IDisposable
{
    private static readonly Color Selected = new(220, 30, 20);
    internal static readonly Color StoredDot = new(60, 210, 60);
    internal static readonly Color NotStoredDot = new(225, 45, 30);
    private const int Columns = 6;
    private const int ColumnStep = 31;
    private const int Stair = 4;
    private const int RowStep = 42;

    /// <summary>How fast icons glide to a new place (per second, exponential).</summary>
    private const float GlideRate = 12f;

    private readonly GoodsGrid _grid;
    private readonly Texture2D[] _buttons;
    private readonly Dictionary<Material, Vector2> _shown = new();
    private Material? _chosen;

    public TransportMenu(GraphicsDevice device, GfxDirectory gfx, GoodsGrid grid)
    {
        _grid = grid;
        var gui = gfx.Open(3);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(BuildingCatalog.DefaultDataDirectory, "ui.json")));
        _buttons = document.RootElement.GetProperty("transportMenu").GetProperty("buttons").EnumerateArray().Select(i =>
        {
            var bitmap = gui.ReadGui(i.GetInt32());
            var texture = new Texture2D(device, bitmap.Width, bitmap.Height);
            texture.SetData(bitmap.Pixels);
            return texture;
        }).ToArray();
    }

    /// <summary>Chooses a good (as a click on it does).</summary>
    public void Choose(Material material) => _chosen = material;

    // Layout (panel space), top down in the content area.
    private static int Left => BuildMenu.Content.X + 4;
    private static int Width => BuildMenu.Content.Width - 8;
    private static int Top => BuildMenu.Content.Y + 30; // below the title

    private static Vector2 Place(int index) =>
        new(Left + (Width - Columns * ColumnStep) / 2 + index % Columns * ColumnStep, Top + index / Columns * RowStep + index % Columns * Stair);

    /// <summary>The buttons: store, do not store at the left; top, up, down, bottom at the right.</summary>
    private static Rectangle Button(int i)
    {
        int y = BuildMenu.Content.Bottom - 36;
        return i < 2 ? new Rectangle(Left + i * 32, y, 30, 30) : new Rectangle(Left + Width - (6 - i) * 32 + 2, y, 30, 30);
    }

    /// <summary>The people's goods in the player's transport order.</summary>
    private List<GoodsGrid.Good> Ordered(World world, int player) => world.Logistics.TransportOrder(player)
        .Select(m => _grid.Goods.FirstOrDefault(g => g.Material == m))
        .OfType<GoodsGrid.Good>()
        .Where(g => _grid.IsMakeable(g.Material))
        .ToList();

    public void Draw(SpriteBatch batch, Texture2D pixel, World world, int player, Vector2 cursor, float seconds)
    {
        var goods = Ordered(world, player);
        float glide = 1 - MathF.Exp(-GlideRate * seconds);
        for (int i = 0; i < goods.Count; i++)
        {
            var good = goods[i];
            var target = Place(i);
            var at = _shown.TryGetValue(good.Material, out var shown) ? Vector2.Lerp(shown, target, glide) : target;
            if (Vector2.DistanceSquared(at, target) < 0.01f)
                at = target;
            _shown[good.Material] = at;
            var rect = new Rectangle((int)MathF.Round(at.X), (int)MathF.Round(at.Y), GoodsGrid.IconSize, GoodsGrid.IconSize);
            batch.Draw(good.Icon, rect, Color.White);
            if (good.Material == _chosen)
                Frame(batch, pixel, rect, Selected, 2);
            else if (rect.Contains(cursor))
                batch.Draw(pixel, rect, Color.White * 0.15f);
            Dot(batch, pixel, new Point(rect.X + 3, rect.Y - 3), world.Logistics.IsStored(player, good.Material) ? StoredDot : NotStoredDot);
        }

        for (int i = 0; i < _buttons.Length; i++)
        {
            var rect = Button(i);
            batch.Draw(_buttons[i], rect, _chosen is null ? Color.White * 0.4f : Color.White);
            if (_chosen is not null && rect.Contains(cursor))
                batch.Draw(pixel, rect, Color.White * 0.15f);
        }
    }

    /// <summary>A click (panel space): chooses a good or applies a button to the chosen one; returns a message, or null.</summary>
    public string? Click(Vector2 panel, World world, int player, IssueCommand issue)
    {
        var goods = Ordered(world, player);
        for (int i = 0; i < goods.Count; i++)
            if (IconAt(goods[i]).Contains(panel))
            {
                _chosen = goods[i].Material;
                return $"{goods[i].Name}: place {i + 1} in the transport order, {(world.Logistics.IsStored(player, goods[i].Material) ? "stored" : "not stored")}";
            }
        for (int i = 0; i < _buttons.Length; i++)
        {
            if (!Button(i).Contains(panel))
                continue;
            if (_chosen is not { } chosen)
                return "choose a good first";
            // Moves count among the goods shown: one up means past the next shown good, not a hidden one.
            int index = goods.FindIndex(g => g.Material == chosen);
            string name = goods[index].Name;
            switch (i)
            {
                case 0:
                case 1:
                    issue(new SetStored(player, chosen, i == 0));
                    UiSounds.Toggle();
                    return i == 0 ? $"{name} goes to storehouses" : $"{name} stays with its producer";
                case 2:
                    issue(new MoveInTransportOrder(player, chosen, int.MinValue), _ => PlaceMessage(world, player, chosen));
                    break;
                case 3 when index > 0:
                    MoveTo(world, player, chosen, goods[index - 1].Material, issue);
                    break;
                case 4 when index < goods.Count - 1:
                    MoveTo(world, player, chosen, goods[index + 1].Material, issue);
                    break;
                case 5:
                    issue(new MoveInTransportOrder(player, chosen, int.MaxValue), _ => PlaceMessage(world, player, chosen));
                    break;
            }
            if (i is >= 2 and <= 5)
                UiSounds.Step(i <= 3);
            return null; // the place once the order changed (PlaceMessage)
        }
        return null;
    }

    /// <summary>Moves the good to the place of another one in the full order (it swaps past it).</summary>
    /// <summary>The good's place among the goods shown.</summary>
    private string PlaceMessage(World world, int player, Material good)
    {
        var goods = Ordered(world, player);
        int now = goods.FindIndex(g => g.Material == good);
        return now < 0 ? "" : $"{goods[now].Name}: place {now + 1} in the transport order";
    }

    private void MoveTo(World world, int player, Material good, Material other, IssueCommand issue)
    {
        var order = world.Logistics.TransportOrder(player).ToList();
        issue(new MoveInTransportOrder(player, good, order.IndexOf(other) - order.IndexOf(good)), _ => PlaceMessage(world, player, good));
    }

    /// <summary>What the element under the point does (hover text), or null.</summary>
    public string? Hover(Vector2 panel, World world, int player)
    {
        foreach (var good in Ordered(world, player))
            if (IconAt(good).Contains(panel))
                return $"{good.Name}: {(world.Logistics.IsStored(player, good.Material) ? "goes to storehouses" : "stays with its producer")}";
        string[] names = ["store", "do not store", "highest priority", "priority +1", "priority -1", "lowest priority"];
        for (int i = 0; i < _buttons.Length && i < names.Length; i++)
            if (Button(i).Contains(panel))
                return _chosen is null ? $"{names[i]} (choose a good first)" : names[i];
        return null;
    }

    private Rectangle IconAt(GoodsGrid.Good good)
    {
        var at = _shown.GetValueOrDefault(good.Material);
        return new Rectangle((int)MathF.Round(at.X), (int)MathF.Round(at.Y), GoodsGrid.IconSize, GoodsGrid.IconSize);
    }

    /// <summary>A small round dot with a dark rim (also the storehouse's panel).</summary>
    internal static void Dot(SpriteBatch batch, Texture2D pixel, Point at, Color color)
    {
        batch.Draw(pixel, new Rectangle(at.X + 1, at.Y, 5, 7), Color.Black * 0.7f);
        batch.Draw(pixel, new Rectangle(at.X, at.Y + 1, 7, 5), Color.Black * 0.7f);
        batch.Draw(pixel, new Rectangle(at.X + 2, at.Y + 1, 3, 5), color);
        batch.Draw(pixel, new Rectangle(at.X + 1, at.Y + 2, 5, 3), color);
    }

    private static void Frame(SpriteBatch batch, Texture2D pixel, Rectangle rect, Color color, int width)
    {
        batch.Draw(pixel, new Rectangle(rect.X, rect.Y, rect.Width, width), color);
        batch.Draw(pixel, new Rectangle(rect.X, rect.Bottom - width, rect.Width, width), color);
        batch.Draw(pixel, new Rectangle(rect.X, rect.Y, width, rect.Height), color);
        batch.Draw(pixel, new Rectangle(rect.Right - width, rect.Y, width, rect.Height), color);
    }

    public void Dispose()
    {
        foreach (var button in _buttons)
            button.Dispose();
    }
}
