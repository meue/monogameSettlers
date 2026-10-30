using System.Text.Json;
using Homestead.Assets.Gfx;
using Homestead.Simulation;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Economy;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Homestead.Client;

/// <summary>What the statistics screen asks the game to do.</summary>
public enum StatisticsAction
{
    None,
    Resume,
    Quit,
}

/// <summary>
/// The statistics screen, shown when the game ends („Spiel beenden“) or Esc is pressed: on the original's blue
/// marble (<c>statisticsBackground</c> in <c>data/ui.json</c>) five pages of tables and charts over the whole
/// game for every player – overview (table, share of the land), economy, military, goods (any good per minute)
/// and society – from <see cref="GameHistory"/> and <see cref="ProductionStatistics"/>. „Weiterspielen“ (also Esc)
/// goes back into the game while it is not over, „Zum Hauptmenü“ ends it.
/// </summary>
public sealed class StatisticsScreen : IDisposable
{
    /// <summary>The pages (names: <c>statistics.pages.*</c>).</summary>
    public static readonly string[] Pages = ["overview", "economy", "military", "goods", "society"];

    private static readonly Color[] SoldierColors = [new(150, 170, 200), new(120, 170, 90), new(200, 150, 80)];

    private readonly Texture2D _background;
    private readonly World _world;
    private readonly string _mapName;
    private readonly GoodsGrid? _goods;
    private readonly bool _canResume;
    /// <summary>Text of the button that leaves the statistics (to the main menu, or on to the next mission).</summary>
    private readonly string _quitLabel;
    private readonly List<(int Player, string Name, Color Color)> _players;
    private MouseState _previousMouse = SdlMouse.GetState();
    private KeyboardState _previousKeys = Keyboard.GetState();
    private Material _good = Material.Plank;

    /// <summary>The page shown (index into <see cref="Pages"/>).</summary>
    public int Page { get; set; }

    public StatisticsScreen(GraphicsDevice device, GfxDirectory gfx, World world, string mapName, GoodsGrid? goods, bool canResume,
        string? quitLabel = null)
    {
        _quitLabel = quitLabel ?? Lang.T("statistics.mainMenu");
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(BuildingCatalog.DefaultDataDirectory, "ui.json")));
        var root = document.RootElement;
        var background = root.GetProperty("statisticsBackground");
        var bitmap = gfx.Open(background.GetProperty("file").GetInt32()).ReadGui(background.GetProperty("gui").GetInt32());
        _background = new Texture2D(device, bitmap.Width, bitmap.Height);
        _background.SetData(bitmap.Pixels);

        _world = world;
        _mapName = mapName;
        _goods = goods;
        _canResume = canResume;
        world.History.Sample(); // up to this moment
        _players = world.History.Players.Select(p => (p, PlayerNames.Of(world, p), PlayerColors.Of(p))).ToList();
    }

    public void Dispose() => _background.Dispose();

    // ---- layout ----

    private const int Margin = 24;

    private static Rectangle Content(Rectangle bounds) =>
        new(bounds.X + Margin, bounds.Y + 110, bounds.Width - 2 * Margin, bounds.Height - 110 - 64);

    private static Rectangle PageButton(Rectangle bounds, int page)
    {
        int width = Math.Min(170, (bounds.Width - 2 * Margin - 4 * 8) / Pages.Length);
        return new Rectangle(bounds.X + Margin + page * (width + 8), bounds.Y + 66, width, 34);
    }

    private static Rectangle ResumeButton(Rectangle bounds) => new(bounds.Right - Margin - 2 * 200 - 12, bounds.Bottom - 52, 200, 38);

    private static Rectangle QuitButton(Rectangle bounds) => new(bounds.Right - Margin - 200, bounds.Bottom - 52, 200, 38);

    /// <summary>Cells of a grid of charts in the content area.</summary>
    private static Rectangle Cell(Rectangle area, int columns, int rows, int column, int row, int spanColumns = 1, int spanRows = 1)
    {
        const int gap = 10;
        int w = (area.Width - (columns - 1) * gap) / columns, h = (area.Height - (rows - 1) * gap) / rows;
        return new Rectangle(area.X + column * (w + gap), area.Y + row * (h + gap), w * spanColumns + gap * (spanColumns - 1), h * spanRows + gap * (spanRows - 1));
    }

    // ---- input ----

    public StatisticsAction Update(Rectangle bounds)
    {
        var mouse = SdlMouse.GetState();
        var keys = Keyboard.GetState();
        bool clicked = mouse.LeftButton == ButtonState.Released && _previousMouse.LeftButton == ButtonState.Pressed;
        bool Pressed(Keys key) => keys.IsKeyDown(key) && !_previousKeys.IsKeyDown(key);
        bool escape = Pressed(Keys.Escape);
        // Left/right (or Tab) turn the pages.
        if (Pressed(Keys.Right) || Pressed(Keys.Tab))
            Page = (Page + 1) % Pages.Length;
        if (Pressed(Keys.Left))
            Page = (Page + Pages.Length - 1) % Pages.Length;
        var cursor = mouse.Position.ToVector2();
        _previousMouse = mouse;
        _previousKeys = keys;
        if (escape && _canResume)
            return StatisticsAction.Resume;
        if (!clicked)
            return StatisticsAction.None;
        if (_canResume && ResumeButton(bounds).Contains(cursor))
            return StatisticsAction.Resume;
        if (QuitButton(bounds).Contains(cursor))
            return StatisticsAction.Quit;
        for (int i = 0; i < Pages.Length; i++)
            if (PageButton(bounds, i).Contains(cursor))
                Page = i;
        if (Page == 3 && GoodAt(bounds, cursor) is { } good)
            _good = good.Material;
        return StatisticsAction.None;
    }

    // ---- drawing ----

    public void Draw(SpriteBatch batch, Texture2D pixel, UiFont font, Rectangle bounds)
    {
        // The marble covers the screen (cut at the sides or top and bottom to keep its shape).
        float scale = Math.Max(bounds.Width / (float)_background.Width, bounds.Height / (float)_background.Height);
        var size = new Point((int)(_background.Width * scale), (int)(_background.Height * scale));
        batch.Draw(_background, new Rectangle(bounds.Center.X - size.X / 2, bounds.Center.Y - size.Y / 2, size.X, size.Y), Color.White);

        var cursor = SdlMouse.GetState().Position.ToVector2();
        long minutes = _world.Tick / World.TicksPerSecond / 60;
        Charts.Write(batch, font, Lang.T("statistics.title"), new Vector2(bounds.X + Margin, bounds.Y + 14), new Color(255, 215, 90), 30);
        Charts.Write(batch, font, Lang.T("statistics.subtitle", _mapName, $"{minutes / 60}:{minutes % 60:00}", _players.Count),
            new Vector2(bounds.X + Margin + 170, bounds.Y + 26), Charts.Text, 15);
        for (int i = 0; i < Pages.Length; i++)
            TextButton.Draw(batch, pixel, font, PageButton(bounds, i), Lang.T("statistics.pages." + Pages[i]), cursor, selected: Page == i, size: 15);

        var content = Content(bounds);
        switch (Page)
        {
            case 0: Overview(batch, pixel, font, content, cursor); break;
            case 1: Economy(batch, pixel, font, content, cursor); break;
            case 2: Military(batch, pixel, font, content, cursor); break;
            case 3: Goods(batch, pixel, font, content, cursor); break;
            case 4: Society(batch, pixel, font, content, cursor); break;
        }

        // Legend: the players.
        float x = bounds.X + Margin;
        foreach (var (_, name, color) in _players)
        {
            batch.Draw(pixel, new Rectangle((int)x, bounds.Bottom - 38, 12, 12), color);
            Charts.Write(batch, font, name, new Vector2(x + 16, bounds.Bottom - 41), Charts.Text, 13);
            x += font.Measure(name, 13).X + 34;
            if (x > ResumeButton(bounds).X - 200)
                break;
        }
        if (_canResume)
            TextButton.Draw(batch, pixel, font, ResumeButton(bounds), Lang.T("statistics.resume"), cursor, size: 16);
        TextButton.Draw(batch, pixel, font, QuitButton(bounds), _quitLabel, cursor, size: 16);
    }

    /// <summary>A value of every player's samples as chart lines.</summary>
    private List<ChartSeries> Series(Func<HistorySample, float> value) =>
        _players.Select(p => new ChartSeries(Short(p.Player), p.Color,
            _world.History.Samples(p.Player).Select(s => new Vector2(s.Tick / (float)World.TicksPerSecond / 60, value(s))).ToList())).ToList();

    /// <summary>A running total of the samples turned into units per minute (averaged over 2 minutes).</summary>
    private List<ChartSeries> PerMinute(Func<HistorySample, float> total) =>
        _players.Select(p =>
        {
            var samples = _world.History.Samples(p.Player);
            var points = new List<Vector2>();
            for (int i = 1; i < samples.Count; i++)
            {
                int from = Math.Max(0, i - 4);
                float minutesBetween = (samples[i].Tick - samples[from].Tick) / (float)World.TicksPerSecond / 60;
                if (minutesBetween > 0)
                    points.Add(new Vector2(samples[i].Tick / (float)World.TicksPerSecond / 60, (total(samples[i]) - total(samples[from])) / minutesBetween));
            }
            return new ChartSeries(Short(p.Player), p.Color, points);
        }).ToList();

    private string Short(int player) => PlayerNames.Of(_world, player);

    private HistorySample? Last(int player) => _world.History.Samples(player) is { Count: > 0 } s ? s[^1] : null;

    private void Overview(SpriteBatch batch, Texture2D pixel, UiFont font, Rectangle area, Vector2 cursor)
    {
        // The table: one row per player, the strongest land first.
        string[] headings = ["land", "settlers", "soldiers", "buildings", "produced", "kills", "losses", "captured", "lost", "combatStrength"];
        headings = headings.Select(h => Lang.T("statistics.columns." + h)).ToArray();
        int tableHeight = 36 + 26 * _players.Count + 8;
        var table = new Rectangle(area.X, area.Y, area.Width, tableHeight);
        var inner = Charts.Panel(batch, pixel, font, table, null);
        int nameWidth = Math.Min(330, inner.Width / 3);
        int column = (inner.Width - nameWidth) / headings.Length;
        for (int c = 0; c < headings.Length; c++)
            Charts.Write(batch, font, headings[c], new Vector2(inner.X + nameWidth + c * column, inner.Y), Charts.Label, 13);
        int row = 0;
        foreach (var (player, name, color) in _players.OrderByDescending(p => Last(p.Player)?.Land ?? 0))
        {
            if (Last(player) is not { } s)
                continue;
            int y = inner.Y + 24 + row++ * 26;
            if (row % 2 == 0)
                batch.Draw(pixel, new Rectangle(inner.X - 4, y - 3, inner.Width + 8, 24), Color.White * 0.04f);
            batch.Draw(pixel, new Rectangle(inner.X, y + 2, 12, 12), color);
            Charts.Write(batch, font, $"{row}. {name}", new Vector2(inner.X + 18, y - 1), Charts.Text, 14);
            string[] values = [Charts.Number(s.Land), $"{s.Settlers}", $"{s.Soldiers}", $"{s.Buildings}", Charts.Number(s.Produced),
                $"{s.Kills}", $"{s.Losses}", $"{s.Captured}", $"{s.Lost}", $"{s.CombatStrength * 100:0} %"];
            for (int c = 0; c < values.Length; c++)
                Charts.Write(batch, font, values[c], new Vector2(inner.X + nameWidth + c * column, y - 1), Charts.Text, 14);
        }

        var charts = new Rectangle(area.X, table.Bottom + 10, area.Width, area.Bottom - table.Bottom - 10);
        Charts.ShareChart(batch, pixel, font, Cell(charts, 3, 2, 0, 0, 2, 1), Lang.T("statistics.charts.landShare"), Series(s => s.Land), cursor);
        Charts.LineChart(batch, pixel, font, Cell(charts, 3, 2, 2, 0), Lang.T("statistics.charts.land"), Series(s => s.Land), cursor);
        Charts.LineChart(batch, pixel, font, Cell(charts, 3, 2, 0, 1), Lang.T("statistics.charts.settlers"), Series(s => s.Settlers), cursor);
        Charts.LineChart(batch, pixel, font, Cell(charts, 3, 2, 1, 1), Lang.T("statistics.charts.soldiers"), Series(s => s.Soldiers), cursor);
        Charts.LineChart(batch, pixel, font, Cell(charts, 3, 2, 2, 1), Lang.T("statistics.charts.buildings"), Series(s => s.Buildings), cursor);
    }

    private void Economy(SpriteBatch batch, Texture2D pixel, UiFont font, Rectangle area, Vector2 cursor)
    {
        Charts.LineChart(batch, pixel, font, Cell(area, 3, 2, 0, 0), Lang.T("statistics.charts.producedPerMinute"), PerMinute(s => s.Produced), cursor);
        Charts.LineChart(batch, pixel, font, Cell(area, 3, 2, 1, 0), Lang.T("statistics.charts.producedTotal"), Series(s => s.Produced), cursor);
        Charts.LineChart(batch, pixel, font, Cell(area, 3, 2, 2, 0), Lang.T("statistics.charts.stock"), Series(s => s.Stock), cursor);
        Charts.LineChart(batch, pixel, font, Cell(area, 3, 2, 0, 1), Lang.T("statistics.charts.finished"), Series(s => s.Buildings), cursor);
        Charts.LineChart(batch, pixel, font, Cell(area, 3, 2, 1, 1), Lang.T("statistics.charts.sites"), Series(s => s.Sites), cursor);
        Charts.LineChart(batch, pixel, font, Cell(area, 3, 2, 2, 1), Lang.T("statistics.charts.specialists"), Series(s => s.Specialists), cursor);
    }

    private void Military(SpriteBatch batch, Texture2D pixel, UiFont font, Rectangle area, Vector2 cursor)
    {
        Charts.LineChart(batch, pixel, font, Cell(area, 3, 2, 0, 0), Lang.T("statistics.charts.soldiers"), Series(s => s.Soldiers), cursor);
        Charts.LineChart(batch, pixel, font, Cell(area, 3, 2, 1, 0), Lang.T("statistics.charts.combatStrength"), Series(s => s.CombatStrength * 100), cursor, v => $"{v:0} %");
        Charts.LineChart(batch, pixel, font, Cell(area, 3, 2, 2, 0), Lang.T("statistics.charts.military"), Series(s => s.MilitaryBuildings), cursor);
        Charts.LineChart(batch, pixel, font, Cell(area, 3, 2, 0, 1), Lang.T("statistics.charts.kills"), Series(s => s.Kills), cursor);
        Charts.LineChart(batch, pixel, font, Cell(area, 3, 2, 1, 1), Lang.T("statistics.charts.losses"), Series(s => s.Losses), cursor);
        var rows = _players.Where(p => Last(p.Player) is not null).Select(p =>
        {
            var s = Last(p.Player)!;
            return (Short(p.Player), (IReadOnlyList<(float, Color)>)[(s.Swordsmen, SoldierColors[0]), (s.Bowmen, SoldierColors[1]), (s.Pikemen, SoldierColors[2])]);
        }).ToList();
        Charts.Bars(batch, pixel, font, Cell(area, 3, 2, 2, 1), Lang.T("statistics.charts.army"), rows, cursor,
            [(Lang.T("statistics.sword"), SoldierColors[0]), (Lang.T("statistics.bow"), SoldierColors[1]), (Lang.T("statistics.spear"), SoldierColors[2])]);
    }

    private void Society(SpriteBatch batch, Texture2D pixel, UiFont font, Rectangle area, Vector2 cursor)
    {
        Charts.LineChart(batch, pixel, font, Cell(area, 3, 2, 0, 0), Lang.T("statistics.charts.carriers"), Series(s => s.Carriers), cursor);
        Charts.LineChart(batch, pixel, font, Cell(area, 3, 2, 1, 0), Lang.T("statistics.charts.room"), Series(s => s.Room), cursor);
        Charts.LineChart(batch, pixel, font, Cell(area, 3, 2, 2, 0), Lang.T("statistics.charts.striking"), Series(s => s.Striking), cursor);
        Charts.LineChart(batch, pixel, font, Cell(area, 3, 2, 0, 1), Lang.T("statistics.charts.mana"), Series(s => s.ManaEarned), cursor);
        Charts.LineChart(batch, pixel, font, Cell(area, 3, 2, 1, 1), Lang.T("statistics.charts.captured"), Series(s => s.Captured), cursor);
        Charts.LineChart(batch, pixel, font, Cell(area, 3, 2, 2, 1), Lang.T("statistics.charts.destroyed"), Series(s => s.Destroyed), cursor);
    }

    // ---- goods ----

    private const int GoodColumns = 6, GoodSize = 40;

    private Rectangle GoodsPanel(Rectangle area) => new(area.X, area.Y, GoodColumns * (GoodSize + 6) + 22, area.Height);

    private Rectangle GoodRect(Rectangle area, GoodsGrid.Good good)
    {
        var panel = GoodsPanel(area);
        return new Rectangle(panel.X + 11 + good.Column * (GoodSize + 6), panel.Y + 34 + good.Row * (GoodSize + 6), GoodSize, GoodSize);
    }

    private GoodsGrid.Good? GoodAt(Rectangle bounds, Vector2 cursor) =>
        _goods?.Goods.FirstOrDefault(g => GoodRect(Content(bounds), g).Contains(cursor));

    private string GoodName(Material material) => _goods?.Goods.FirstOrDefault(g => g.Material == material)?.Name ?? material.ToString();

    private void Goods(SpriteBatch batch, Texture2D pixel, UiFont font, Rectangle area, Vector2 cursor)
    {
        var panel = GoodsPanel(area);
        Charts.Panel(batch, pixel, font, panel, Lang.T("statistics.chooseGood"));
        if (_goods is not null)
        {
            foreach (var good in _goods.Goods)
            {
                var rect = GoodRect(area, good);
                batch.Draw(pixel, rect, good.Material == _good ? new Color(200, 170, 70) * 0.5f : rect.Contains(cursor) ? Color.White * 0.12f : Color.Black * 0.25f);
                batch.Draw(good.Icon, new Rectangle(rect.X + 4, rect.Y + 2, rect.Width - 8, rect.Height - 4), Color.White);
            }
            if (_goods.Goods.FirstOrDefault(g => GoodRect(area, g).Contains(cursor)) is { } hovered)
                Charts.Tooltip(batch, pixel, font, cursor, area, [(hovered.Name, Charts.Text)]);
        }

        var right = new Rectangle(panel.Right + 10, area.Y, area.Right - panel.Right - 10, area.Height);
        var statistics = _world.Statistics;
        int periods = statistics.CurrentPeriod + 1;
        // Units per minute, averaged over 2 minutes (4 periods of 30 seconds).
        var perMinute = _players.Select(p => new ChartSeries(Short(p.Player), p.Color, Enumerable.Range(0, periods).Select(i =>
            new Vector2((i + 1) * ProductionStatistics.PeriodTicks / (float)World.TicksPerSecond / 60,
                Enumerable.Range(i - 3, 4).Sum(k => statistics.Produced(p.Player, _good, k)) / 2f)).ToList())).ToList();
        string name = GoodName(_good);
        Charts.LineChart(batch, pixel, font, Cell(right, 2, 2, 0, 0, 2, 1), Lang.T("statistics.charts.goodPerMinute", name), perMinute, cursor);
        var totals = _players.Select(p => (Short(p.Player), (IReadOnlyList<(float, Color)>)[(statistics.Total(p.Player, _good), p.Color)])).ToList();
        Charts.Bars(batch, pixel, font, Cell(right, 2, 2, 0, 1), Lang.T("statistics.charts.goodTotal", name), totals, cursor);
        // What the own people made most.
        int own = _players.FirstOrDefault().Player;
        var top = Enum.GetValues<Material>().Select(m => (Material: m, Total: statistics.Total(own, m))).Where(t => t.Total > 0)
            .OrderByDescending(t => t.Total).Take(8)
            .Select(t => (GoodName(t.Material), (IReadOnlyList<(float, Color)>)[(t.Total, PlayerColors.Of(own))])).ToList();
        Charts.Bars(batch, pixel, font, Cell(right, 2, 2, 1, 1), Lang.T("statistics.charts.mostMade", Short(own)), top, cursor);
    }
}
