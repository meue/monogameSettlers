using System.Globalization;
using Homestead.Simulation;
using Homestead.Simulation.Economy;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Homestead.Client;

/// <summary>
/// Siedler › Produktion in the blue panel, like the original's: a chart of how much of one good the player
/// produced over the last hour (units per minute averaged over two minutes, one bar per period of
/// <see cref="ProductionStatistics.PeriodTicks"/>, now at the right), and below it the goods to choose
/// from (<see cref="GoodsGrid"/>). Panel space.
/// </summary>
public sealed class StatisticsMenu
{
    private static readonly Color Text = new(240, 225, 190);
    private static readonly Color Bar = new(230, 20, 10);
    private static readonly Color Selected = new(220, 30, 20);
    private const int Minutes = 60;
    private const int Smoothing = 2;
    private const int RowHeight = 31;

    /// <summary>Scales of the chart (units per minute at the top line): the first that fits the highest bar.</summary>
    private static readonly double[] Scales = [1, 2, 3, 5, 10, 15, 20, 30, 50, 100, 200, 500, 1000];

    private readonly GoodsGrid _grid;
    private Material _chosen = Material.Trunk;

    public StatisticsMenu(GoodsGrid grid)
    {
        _grid = grid;
    }

    // Layout (panel space), top down in the content area.
    private static int Left => BuildMenu.Content.X + 4;
    private static int Width => BuildMenu.Content.Width - 8;
    private static int Top => BuildMenu.Content.Y + 24; // below the title
    /// <summary>The chart begins below the chosen good's name, which takes this row.</summary>
    private const int SeriesLabelRow = 18;
    private const int ChartHeight = 92;
    private static Rectangle Chart => new(Left + 2, Top + SeriesLabelRow, Width - 4, ChartHeight); // below the good's name
    /// <summary>Width kept free at the chart's right for the gridline values.</summary>
    private const int ValueStrip = 34;
    private static int GridTop => Chart.Bottom + 22;

    public void Draw(SpriteBatch batch, Texture2D pixel, UiFont font, World world, int player, Vector2 cursor)
    {
        var chart = Chart;
        var statistics = world.Statistics;
        int periodsPerMinute = 60 * World.TicksPerSecond / ProductionStatistics.PeriodTicks;
        int periods = Minutes * periodsPerMinute;
        // The last finished period at the right edge; the one under way is left out (it would always dip).
        int last = statistics.CurrentPeriod - 1;
        // Units per minute, averaged over the periods of the last Smoothing minutes (single periods jump).
        int window = Smoothing * periodsPerMinute;
        var perMinute = Enumerable.Range(0, periods)
            .Select(i => last - periods + 1 + i)
            .Select(period => Enumerable.Range(period - window + 1, window).Sum(p => statistics.Produced(player, _chosen, p)) / (double)Smoothing)
            .ToArray();
        double scale = Scales.FirstOrDefault(s => s >= perMinute.Max(), Scales[^1]);

        // The chosen good's name, then the bars, the dashed gridlines with their values in the strip at the
        // right, the axes, the minutes below; the plot stops short of the values so nothing touches the frame.
        string series = _grid.Goods.FirstOrDefault(g => g.Material == _chosen)?.Name ?? _chosen.ToString();
        var seriesSize = font.Measure(series, 13);
        Write(batch, font, series, new Vector2(Left + (Width - seriesSize.X) / 2, Top), 13);
        var plot = new Rectangle(chart.X, chart.Y, chart.Width - ValueStrip, chart.Height);
        for (int i = 0; i < periods; i++)
        {
            if (perMinute[i] <= 0)
                continue;
            int x0 = plot.X + i * plot.Width / periods, x1 = plot.X + (i + 1) * plot.Width / periods;
            int height = (int)Math.Round(Math.Min(1, perMinute[i] / scale) * plot.Height);
            batch.Draw(pixel, new Rectangle(x0, plot.Bottom - height, Math.Max(1, x1 - x0), height), Bar);
        }
        const int Dash = 3, DashGap = 3;
        const float GridlineAlpha = 0.65f, AxisAlpha = 0.9f;
        for (int line = 1; line <= 5; line++)
        {
            int y = plot.Bottom - line * plot.Height / 5;
            for (int x = plot.X; x < plot.Right; x += Dash + DashGap)
                batch.Draw(pixel, new Rectangle(x, y, Math.Min(Dash, plot.Right - x), 1), Color.White * GridlineAlpha);
            string value = (scale * line / 5).ToString("0.##", CultureInfo.GetCultureInfo("de-DE"));
            var size = font.Measure(value, 13);
            Write(batch, font, value, new Vector2(chart.Right - size.X, y - size.Y / 2 - 1), 13);
        }
        batch.Draw(pixel, new Rectangle(chart.X, chart.Bottom, chart.Width, 1), Color.White * AxisAlpha);
        batch.Draw(pixel, new Rectangle(plot.Right - 1, chart.Y - 4, 1, chart.Height + 8), Color.White * AxisAlpha);
        for (int minutes = Minutes; minutes > 0; minutes -= 15)
        {
            int x = plot.Right - minutes * plot.Width / Minutes;
            batch.Draw(pixel, new Rectangle(x, chart.Bottom, 1, 4), Color.White * AxisAlpha);
            string label = minutes.ToString(CultureInfo.InvariantCulture);
            var size = font.Measure(label, 13);
            Write(batch, font, label, new Vector2(Math.Max(Left, x - size.X / 2), chart.Bottom + 4), 13);
        }

        _grid.Draw(batch, GridTop, RowHeight);
        foreach (var good in _grid.Goods)
        {
            var rect = GoodsGrid.IconRect(good, GridTop, RowHeight);
            if (good.Material == _chosen)
                Frame(batch, pixel, rect, Selected, 2);
            else if (rect.Contains(cursor))
                batch.Draw(pixel, rect, Color.White * 0.15f);
        }
    }

    /// <summary>A click on a good shows its chart; returns a message, or null if it hit nothing.</summary>
    public string? Click(Vector2 panel, World world, int player)
    {
        if (_grid.GoodAt(panel, GridTop, RowHeight) is not { } good)
            return null;
        _chosen = good.Material;
        return Describe(good, world, player);
    }

    /// <summary>The good under the point and how much of it was made (hover text), or null.</summary>
    public string? Hover(Vector2 panel, World world, int player) =>
        _grid.GoodAt(panel, GridTop, RowHeight) is { } good ? Describe(good, world, player) : null;

    /// <summary>The tooltip of the good under the panel point (its name), or null.</summary>
    public string? TooltipAt(Vector2 panel) => _grid.GoodAt(panel, GridTop, RowHeight) is { } good ? good.Name : null;

    private string Describe(GoodsGrid.Good good, World world, int player)
    {
        if (!_grid.IsMakeable(good.Material))
            return $"{good.Name}: not made by this people";
        int periodsPerHour = Minutes * 60 * World.TicksPerSecond / ProductionStatistics.PeriodTicks;
        int now = world.Statistics.CurrentPeriod;
        int lastHour = Enumerable.Range(now - periodsPerHour + 1, periodsPerHour).Sum(p => world.Statistics.Produced(player, good.Material, p));
        return $"{good.Name}: {lastHour} in the last hour, {world.Statistics.Total(player, good.Material)} in all";
    }

    private static void Frame(SpriteBatch batch, Texture2D pixel, Rectangle rect, Color color, int width)
    {
        batch.Draw(pixel, new Rectangle(rect.X, rect.Y, rect.Width, width), color);
        batch.Draw(pixel, new Rectangle(rect.X, rect.Bottom - width, rect.Width, width), color);
        batch.Draw(pixel, new Rectangle(rect.X, rect.Y, width, rect.Height), color);
        batch.Draw(pixel, new Rectangle(rect.Right - width, rect.Y, width, rect.Height), color);
    }

    private static void Write(SpriteBatch batch, UiFont font, string text, Vector2 position, int size) =>
        SelectionPanel.Shadowed(batch, font, text, position, size, Text);
}
