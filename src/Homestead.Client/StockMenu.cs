using Homestead.Simulation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Homestead.Client;

/// <summary>
/// Waren › Lagerbestand in the blue panel, like the original's: every good (<see cref="GoodsGrid"/>) with
/// how many units the player has in stock (<see cref="World.InStock"/>) below it; goods the people cannot
/// make show their number only when there are some. Panel space.
/// </summary>
public sealed class StockMenu
{
    private static readonly Color Text = new(240, 225, 190);
    private const int RowHeight = 46;

    private readonly GoodsGrid _grid;

    public StockMenu(GoodsGrid grid)
    {
        _grid = grid;
    }

    private static int GridTop => BuildMenu.Content.Y + 32; // below the title

    public void Draw(SpriteBatch batch, Texture2D pixel, UiFont font, World world, int player, Vector2 cursor)
    {
        _grid.Draw(batch, GridTop, RowHeight);
        foreach (var good in _grid.Goods)
        {
            var rect = GoodsGrid.IconRect(good, GridTop, RowHeight);
            if (rect.Contains(cursor))
                batch.Draw(pixel, rect, Color.White * 0.15f);
            int count = world.InStock(player, good.Material);
            if (count == 0 && !_grid.IsMakeable(good.Material))
                continue;
            string text = count.ToString();
            var size = font.Measure(text, 15);
            SelectionPanel.Shadowed(batch, font, text, new Vector2(rect.Center.X - size.X / 2, rect.Bottom), 15, Text);
        }
    }

    /// <summary>The good under the point and how many are in stock (hover text), or null.</summary>
    public string? Hover(Vector2 panel, World world, int player) =>
        _grid.GoodAt(panel, GridTop, RowHeight) is { } good ? $"{good.Name}: {world.InStock(player, good.Material)} in stock" : null;

    /// <summary>The tooltip of the good under the panel point (its name), or null.</summary>
    public string? TooltipAt(Vector2 panel) => _grid.GoodAt(panel, GridTop, RowHeight) is { } good ? good.Name : null;
}
