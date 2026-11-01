using System.Text.Json;
using System.Text.RegularExpressions;
using Homestead.Assets.Gfx;
using Homestead.Simulation;
using Homestead.Simulation.Commands;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Economy;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Homestead.Client;

/// <summary>
/// Shown at the left edge instead of the build menu while a building is selected: its name and image;
/// for own buildings also its state, the goods in its piles (construction sites: material delivered
/// of needed) and a mine's hit rate, at the top right the original's tear-down icon (then only the image stays, the question over it),
/// at the top left for production buildings and construction sites the original's icon to stop or restart them, for construction
/// sites beside it the highest priority (the name below these icons). A finished
/// military building also offers its garrison per soldier type with buttons for more or fewer (the soldier
/// upgrades are in Siedler › Krieger, <see cref="SoldiersMenu"/>); a market its trade route and goods (BuildingInfo.Market).
/// Names and states come from <c>data/ui.json</c>.
/// </summary>
public sealed partial class BuildingInfo
{
    // The content area of the blue panel (like the build menu), no background of its own.
    private static readonly int Left = BuildMenu.Content.X + 4; // clear of the vines on the panel's frame
    private static readonly int Top = BuildMenu.Content.Y;
    private static readonly int Bottom = BuildMenu.Content.Bottom;
    private static readonly int Inner = BuildMenu.Content.Width - 8;
    private static readonly Color HoverColor = Color.White * 0.15f;
    private static readonly Color Text = new(240, 225, 190);
    private static readonly Color Label = new(190, 165, 120);
    private static readonly Color Selected = new(220, 30, 20);

    /// <summary>What the simulation's status contains → the state shown (<c>buildingStatus.*</c>); the first match counts.</summary>
    private readonly List<(string Contains, string State)> _states = new();

    /// <summary>The building whose tear-down button was pressed: the panel asks whether to go on.</summary>
    private Building? _confirming;

    /// <summary>The building the panel asks about tearing down, if any.</summary>
    public Building? Asking => _confirming;

    /// <summary>Drops the tear-down question (the player did something else).</summary>
    public void Cancel() => _confirming = null;

    /// <summary>The tear-down icon (GUI image of file 03, <c>buildingInfo.demolish</c> in <c>ui.json</c>; set by <see cref="LoadIcons"/>).</summary>
    private Texture2D? _demolishIcon;

    private int _demolishImage;

    /// <summary>Stop work (house), go on (hand), the highest priority and the same given (<c>buildingInfo</c> in <c>ui.json</c>).</summary>
    private Texture2D? _pauseIcon, _pausedIcon, _priorityIcon, _priorityOnIcon;

    private readonly int[] _headImages;

    public BuildingInfo()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(BuildingCatalog.DefaultDataDirectory, "ui.json")));
        var root = document.RootElement;
        foreach (var state in root.GetProperty("status").EnumerateArray())
            _states.Add((state[0].GetString()!, state[1].GetString()!));
        var info = root.GetProperty("buildingInfo");
        _demolishImage = info.GetProperty("demolish").GetInt32();
        _headImages = new[] { "pause", "paused", "priority", "priorityOn" }.Select(n => info.GetProperty(n).GetInt32()).ToArray();
    }

    /// <summary>Loads the tear-down icon (once; the same for every people).</summary>
    public void LoadIcons(GraphicsDevice device, GfxDirectory gfx)
    {
        if (_demolishIcon is not null)
            return;
        var gui = gfx.Open(3);
        var bitmap = gui.ReadGui(_demolishImage);
        _demolishIcon = new Texture2D(device, bitmap.Width, bitmap.Height);
        _demolishIcon.SetData(bitmap.Pixels);
        var head = _headImages.Select(i =>
        {
            var image = gui.ReadGui(i);
            var texture = new Texture2D(device, image.Width, image.Height);
            texture.SetData(image.Pixels);
            return texture;
        }).ToArray();
        (_pauseIcon, _pausedIcon, _priorityIcon, _priorityOnIcon) = (head[0], head[1], head[2], head[3]);
        LoadMarketIcons(device, gui);
        LoadDockyardIcons(device, gfx, gui);
        LoadWarWorksIcons(device, gfx);
    }

    /// <summary>Height of the building image (cut off at the top when it is taller); less when the panel is full.</summary>
    private const int ImageHeight = 92;
    private const int MinImageHeight = 36;

    /// <summary>Below this the goods are left out to give the image room.</summary>
    private const int GoodImageHeight = 64;

    /// <summary>The goods of a storehouse's panel (set once the people is known).</summary>
    public GoodsGrid? StorehouseGoods { get; set; }

    private const int StorehouseRow = 31;

    /// <summary>A finished own storehouse shows the goods to specialize it on.</summary>
    private bool OffersStorehouse(Building building) =>
        StorehouseGoods is not null && building.State == BuildingState.Finished && building.Piles.Any(p => p.Role == PileRole.Storage);

    private int StorehouseTop => Bottom - (StorehouseGoods!.Goods.Max(g => g.Row) + 1) * StorehouseRow;

    /// <summary>Where the buttons and rows at the bottom of an own building's panel begin.</summary>
    private int BlockTop(World world, Building building)
    {
        if (OffersStorehouse(building))
            return StorehouseTop - 4;
        if (OffersGarrison(building))
            return GarrisonRowRect(0, 0).Y - 16 - 4;
        return Bottom;
    }

    /// <summary>Text with a dark shadow, readable on the blue panel.</summary>
    private static void Write(SpriteBatch batch, UiFont font, string text, Vector2 position, Color color, int size = 14) =>
        SelectionPanel.Shadowed(batch, font, text, position, size, color);

    private const int GarrisonRow = 38;

    /// <summary>The garrison rows of a military building, one per soldier type, at the bottom.</summary>
    private static Rectangle GarrisonRowRect(int height, int index) =>
        new(Left, Bottom - 4 - (Enum.GetValues<SoldierType>().Length - index) * (GarrisonRow + 2), Inner, GarrisonRow);

    /// <summary>The up/down spinner of a garrison row (as in Waren › Herstellung): upper half one more, lower half one fewer.</summary>
    private static Rectangle GarrisonSpinner(int height, int index)
    {
        var row = GarrisonRowRect(height, index);
        return new Rectangle(row.Right - 19 - 2, row.Y + (row.Height - 28) / 2, 19, 28);
    }

    private static Rectangle SpinnerHalf(Rectangle spinner, int delta) =>
        new(spinner.X, delta > 0 ? spinner.Y : spinner.Center.Y, spinner.Width, spinner.Height / 2);

    /// <summary>The spinner image (that of Waren › Herstellung; set once loaded).</summary>
    public Texture2D? Spinner { get; set; }

    private static bool OffersGarrison(Building building) =>
        building.Definition.Occupiers.Count > 0 && building.State == BuildingState.Finished;

    /// <summary>The soldier type and change (+1 or -1) of the garrison button of an own military building under the screen point.</summary>
    public (SoldierType Type, int Delta)? GarrisonChangeAt(Vector2 screen, int height, Building building, int ownPlayer)
    {
        if (building.Player != ownPlayer || !OffersGarrison(building) || _confirming == building)
            return null;
        var types = Enum.GetValues<SoldierType>();
        for (int i = 0; i < types.Length; i++)
            foreach (int delta in new[] { -1, 1 })
                if (SpinnerHalf(GarrisonSpinner(height, i), delta).Contains(screen))
                    return (types[i], delta);
        return null;
    }

    /// <summary>
    /// Per soldier type its figure, name, how many are inside of how many the building wants, and a spinner
    /// for one more or one fewer (a half greyed out where the building would be left empty or overfull).
    /// </summary>
    private void DrawGarrison(SpriteBatch batch, Texture2D pixel, int height, UiFont font, SettlerRenderer settlers, Civilization people,
        World world, Building building, Vector2 cursor)
    {
        var military = world.Military;
        var types = Enum.GetValues<SoldierType>();
        Write(batch, font, Lang.T("building.garrison"), new Vector2(Left, GarrisonRowRect(height, 0).Y - 16), Label, 12);
        var occupants = military.Occupants(building);
        for (int i = 0; i < types.Length; i++)
        {
            var type = types[i];
            var row = GarrisonRowRect(height, i);
            int level = world.Magic.SoldierLevel(building.Player, type);
            settlers.DrawIcon(batch, people, $"{type.ToString().ToUpperInvariant()}_L{level}", new Rectangle(row.X + 2, row.Y + 3, 24, row.Height - 6),
                1.2f, PlayerColors.Of(building.Player));
            Write(batch, font, SoldierName(type), new Vector2(row.X + 28, row.Y + 2), Text, 12);
            int inside = occupants.Count(s => s?.SoldierType == type);
            Write(batch, font, $"{inside} / {military.Wanted(building, type)}", new Vector2(row.X + 28, row.Y + 19), Text, 12);
            var spinner = GarrisonSpinner(height, i);
            if (Spinner is not null)
                batch.Draw(Spinner, spinner, Color.White);
            foreach (int delta in new[] { -1, 1 })
            {
                var half = SpinnerHalf(spinner, delta);
                // Greyed out where the building would be left empty or overfull.
                if (!military.CanChangeWanted(building, type, delta))
                    batch.Draw(pixel, half, Color.Black * 0.55f);
                else if (half.Contains(cursor))
                    batch.Draw(pixel, half, HoverColor);
            }
        }
    }

    public string SoldierName(SoldierType type) => Lang.Find("soldiers.types." + type) ?? type.ToString();

    public string LevelName(int level) => Lang.T("soldiers.levels." + Math.Clamp(level, 1, 3));

    public string NameOf(BuildingDefinition definition) => Lang.Find("buildings." + definition.Name) ?? definition.Name;

    private const int DemolishSize = 30;

    /// <summary>The tear-down icon at the top right, next to the name (as in the original).</summary>
    private static Rectangle DemolishButton(int height) => new(Left + Inner - DemolishSize, Top, DemolishSize, DemolishSize);

    /// <summary>Whether the screen point is on the tear-down icon of an own building (for its hover text).</summary>
    public bool DemolishAt(Vector2 screen, int height, Building building, int ownPlayer) =>
        building.Player == ownPlayer && _confirming != building && DemolishButton(height).Contains(screen);

    /// <summary>The icon stopping or restarting work, at the top left (as in the original).</summary>
    private static Rectangle PauseButton(int height) => new(Left, Top, DemolishSize, DemolishSize);

    /// <summary>The highest priority of a construction site, beside it.</summary>
    private static Rectangle PriorityButton(int height) => new(Left + DemolishSize + 4, Top, DemolishSize, DemolishSize);

    private static bool OffersPause(World world, Building building) =>
        world.CanPause(building) && !OffersGarrison(building);

    private static bool OffersPriority(Building building) => building.State != BuildingState.Finished;

    /// <summary>Whether the screen point is on the stop/restart icon of an own production building or construction site.</summary>
    public bool PauseAt(Vector2 screen, int height, World world, Building building, int ownPlayer) =>
        building.Player == ownPlayer && _confirming != building && OffersPause(world, building) && PauseButton(height).Contains(screen);

    /// <summary>Whether the screen point is on the highest-priority icon of an own construction site.</summary>
    public bool PriorityAt(Vector2 screen, int height, Building building, int ownPlayer) =>
        building.Player == ownPlayer && _confirming != building && OffersPriority(building) && PriorityButton(height).Contains(screen);

    // Plain "Ja"/"Nein" at the bottom corners, as in "Spiel beenden" (SystemMenu).
    private static Rectangle YesButton(int height) => new(Left, Bottom - 28, 70, 24);

    private static Rectangle NoButton(int height) => new(Left + Inner - 70, Bottom - 28, 70, 24);

    /// <summary>
    /// A left click on the panel. True if it confirmed tearing down <paramref name="building"/>: first the
    /// icon, then "Ja"; any other click (as "Nein") goes back.
    /// </summary>
    public bool Click(Vector2 screen, int height, Building building, int ownPlayer)
    {
        if (building.Player != ownPlayer)
            return false;
        if (_confirming != building)
        {
            if (DemolishButton(height).Contains(screen))
            {
                _confirming = building;
                UiSounds.Demolish();
            }
            return false;
        }
        if (YesButton(height).Contains(screen))
        {
            _confirming = null;
            return true;
        }
        _confirming = null;
        return false;
    }

    public void Draw(SpriteBatch batch, Texture2D pixel, int height, UiFont font, EconomyRenderer economy, SettlerRenderer settlers,
        Civilization people, World world, Building building, int ownPlayer, Vector2 cursor)
    {
        // Asking whether to tear it down: only the image stays where it is, the question over the rest (as in the original).
        bool asking = _confirming == building;
        int y = Top;
        bool own = building.Player == ownPlayer;
        // Own buildings: the name gives the tear-down icon room.
        // A market's own title (as in the original).
        string title = own && OffersMarket(world, building) ? Lang.T(world.Trade.IsHarbor(building) ? "building.seaTrade" : "building.trade")
            : NameOf(building.Definition);
        // Icons at the top left too: the name below them (as in the original).
        bool head = own && (OffersPause(world, building) || OffersPriority(building));
        if (head)
            y = HeadBottom;
        foreach (string line in font.Wrap(title, own && !head ? Inner - DemolishSize - 4 : Inner, 16))
        {
            if (!asking)
                Write(batch, font, line, new Vector2(Left, y), Text, 16);
            y += 19;
        }
        if (own)
            y = Math.Max(y, Top + DemolishSize);
        y += 4;
        // A market shows its trade route and goods instead of the image (as in the original).
        if (own && !asking && OffersMarket(world, building))
        {
            DrawDemolish(batch, pixel, height, font, building, cursor);
            DrawMarket(batch, pixel, font, world, building, cursor);
            return;
        }
        // A shipyard: the ships and the dock's hammer, the image beside the ships (as in the original).
        bool dockyard = own && OffersDockyard(world, building);
        if (dockyard)
        {
            if (!asking)
                DrawDockyard(batch, pixel, world, building, people, cursor);
            y = DockyardImageTop;
        }
        // A war machine works: what to order at the left, the image beside it.
        bool warWorks = own && OffersWarWorks(world, building);
        if (warWorks && !asking)
            DrawWarWorks(batch, pixel, font, world, building, cursor);
        var state = StateOf(world, building);
        double? chance = world.Production.MineChance(building);
        var goods = own && !OffersStorehouse(building) ? Goods(world, building) : [];
        // The image gives way when the panel is full (cut off at the top).
        int limit = own ? BlockTop(world, building) : Bottom;
        int text = 15 + font.Wrap(state, Inner).Count() * 17 + (chance is null ? 0 : 21);
        // When it gets tight the goods go first, then the image shrinks.
        if (goods.Count > 0 && limit - y - 8 - text - 4 - (24 + 32) < GoodImageHeight)
            goods = [];
        if (goods.Count > 0)
            text += 24 + 32;
        int imageHeight = own ? Math.Clamp(limit - y - 8 - text - 4, MinImageHeight, ImageHeight) : ImageHeight;
        var imageBox = dockyard ? new Rectangle(DockyardImageLeft, y, Left + Inner - DockyardImageLeft, imageHeight)
            : warWorks ? new Rectangle(WarWorksImageLeft, y, Left + Inner - WarWorksImageLeft, imageHeight)
            : new Rectangle(Left, y, Inner, imageHeight);
        economy.DrawBuildingImage(batch, building.Definition, imageBox, PlayerColors.Of(building.Player));
        y = Math.Max(imageBox.Bottom, dockyard ? TradeShipButton.Bottom : warWorks ? WarWorksBottom(world, building) : 0) + 8;
        if (asking)
        {
            DrawQuestion(batch, height, font, cursor);
            return;
        }
        if (!own)
            return;
        DrawDemolish(batch, pixel, height, font, building, cursor);
        if (OffersPause(world, building))
            Icon(batch, pixel, building.IsPaused ? _pausedIcon : _pauseIcon, PauseButton(height), cursor);
        if (OffersPriority(building))
            Icon(batch, pixel, building.HasPriority ? _priorityOnIcon : _priorityIcon, PriorityButton(height), cursor);
        DrawHint(batch, font, world, building, cursor);
        if (OffersGarrison(building))
            DrawGarrison(batch, pixel, height, font, settlers, people, world, building, cursor);
        if (OffersStorehouse(building))
            DrawStorehouse(batch, pixel, world, building, cursor);

        // State.
        Write(batch, font, Lang.T("building.status"), new Vector2(Left, y), Label, 12);
        y += 15;
        foreach (string line in font.Wrap(state, Inner))
        {
            Write(batch, font, line, new Vector2(Left, y), Text);
            y += 17;
        }
        if (chance is { } c)
        {
            y += 4;
            int percent = (int)Math.Round(c * 100);
            Write(batch, font, percent == 0 && c > 0 ? Lang.T("building.chanceBelowOne") : Lang.T("building.chance", percent), new Vector2(Left, y), Text);
            y += 17;
        }

        // Goods: on a construction site delivered of needed, otherwise what lies in the piles (as far as there is room).
        if (goods.Count == 0 || y + 8 + 16 + 30 > limit)
            return;
        y += 8;
        bool assembling = world.Shipping.Building(building) is not null || world.WarMachines.Current(building) is not null;
        Write(batch, font, Lang.T(building.State == BuildingState.Finished && !assembling ? "building.goods" : "building.material"), new Vector2(Left, y), Label, 12);
        y += 16;
        for (int i = 0; i < goods.Count; i++)
        {
            var cell = new Rectangle(Left + i % 2 * (Inner / 2), y + i / 2 * 32, Inner / 2, 30);
            if (cell.Bottom > limit)
                break;
            economy.DrawMaterialIcon(batch, goods[i].Material, new Rectangle(cell.X, cell.Y, 26, 28));
            Write(batch, font, goods[i].Text, new Vector2(cell.X + 29, cell.Y + 6), Text);
        }
    }

    /// <summary>
    /// The storehouse's goods: without a specialization a dot on each from the player's rule (green: stored,
    /// red: not), with one only that good framed red.
    /// </summary>
    private void DrawStorehouse(SpriteBatch batch, Texture2D pixel, World world, Building building, Vector2 cursor)
    {
        var grid = StorehouseGoods!;
        int top = StorehouseTop;
        grid.Draw(batch, top, StorehouseRow);
        var only = world.Logistics.SpecializationOf(building);
        foreach (var good in grid.Goods)
        {
            var rect = GoodsGrid.IconRect(good, top, StorehouseRow);
            if (good.Material == only)
            {
                batch.Draw(pixel, new Rectangle(rect.X, rect.Y, rect.Width, 2), Selected);
                batch.Draw(pixel, new Rectangle(rect.X, rect.Bottom - 2, rect.Width, 2), Selected);
                batch.Draw(pixel, new Rectangle(rect.X, rect.Y, 2, rect.Height), Selected);
                batch.Draw(pixel, new Rectangle(rect.Right - 2, rect.Y, 2, rect.Height), Selected);
            }
            else if (rect.Contains(cursor))
                batch.Draw(pixel, rect, Color.White * 0.15f);
            if (only is null)
                TransportMenu.Dot(batch, pixel, new Point(rect.X + 3, rect.Y - 3),
                    world.Logistics.IsStored(building.Player, good.Material) ? TransportMenu.StoredDot : TransportMenu.NotStoredDot);
        }
    }

    /// <summary>Gives the panel's orders (set by the game; see <see cref="IssueCommand"/>).</summary>
    public IssueCommand? Issue { get; set; }

    /// <summary>
    /// A click on a storehouse's goods (panel space): specializes it on that good, or – on its own good –
    /// gives it the general rule again. Returns a message, or null if it hit nothing.
    /// </summary>
    public string? StorehouseClick(Vector2 panel, World world, Building building, int ownPlayer)
    {
        if (building.Player != ownPlayer || !OffersStorehouse(building) || _confirming == building
            || StorehouseGoods!.GoodAt(panel, StorehouseTop, StorehouseRow) is not { } good)
            return null;
        bool clear = world.Logistics.SpecializationOf(building) == good.Material;
        Issue!(new SetSpecialization(ownPlayer, building.Id, clear ? null : good.Material));
        UiSounds.Toggle();
        return clear ? "storehouse: the general rule again (Waren › Warentransport)" : $"storehouse: only {good.Name}";
    }

    /// <summary>What the storehouse's good under the point means (hover text), or null.</summary>
    public string? StorehouseHover(Vector2 panel, World world, Building building, int ownPlayer)
    {
        if (building.Player != ownPlayer || !OffersStorehouse(building) || _confirming == building
            || StorehouseGoods!.GoodAt(panel, StorehouseTop, StorehouseRow) is not { } good)
            return null;
        return world.Logistics.SpecializationOf(building) == good.Material
            ? $"only {good.Name} here (click: the general rule again)"
            : $"{good.Name}: click to take only this here";
    }

    /// <summary>Below the icons at the top: where the name goes when there are some at the left.</summary>
    private static int HeadBottom => Top + DemolishSize + 4;

    private static void Icon(SpriteBatch batch, Texture2D pixel, Texture2D? icon, Rectangle rect, Vector2 cursor)
    {
        if (icon is not null)
            batch.Draw(icon, rect, Color.White);
        if (rect.Contains(cursor))
            batch.Draw(pixel, rect, HoverColor);
    }

    /// <summary>What the icon at the top under the mouse does, over the icons (as in the original).</summary>
    private static void DrawHint(SpriteBatch batch, UiFont font, World world, Building building, Vector2 cursor)
    {
        string? hint = DemolishButton(0).Contains(cursor) ? Lang.T("building.demolish")
            : OffersPause(world, building) && PauseButton(0).Contains(cursor) ? Lang.T(building.IsPaused ? "building.resume" : "building.pause")
            : OffersPriority(building) && PriorityButton(0).Contains(cursor) ? Lang.T("building.priority")
            : null;
        if (hint is not null)
            Write(batch, font, hint, new Vector2(Left, Top), Color.White, 16);
    }

    /// <summary>The tear-down icon at the top right.</summary>
    private void DrawDemolish(SpriteBatch batch, Texture2D pixel, int height, UiFont font, Building building, Vector2 cursor)
    {
        var icon = DemolishButton(height);
        if (_demolishIcon is not null)
            batch.Draw(_demolishIcon, icon, Color.White);
        if (icon.Contains(cursor))
            batch.Draw(pixel, icon, HoverColor);
    }

    private const int QuestionSize = 18;
    private const int QuestionLine = 22;

    /// <summary>
    /// Asking whether to tear the building down, as in the original: everything but the building's image gives way,
    /// the question at the bottom and "Ja"/"Nein" below it.
    /// </summary>
    private static void DrawQuestion(SpriteBatch batch, int height, UiFont font, Vector2 cursor)
    {
        var lines = font.Wrap(Lang.T("building.demolishQuestion"), Inner, QuestionSize).ToList();
        int top = YesButton(height).Y - 10 - lines.Count * QuestionLine;
        for (int i = 0; i < lines.Count; i++)
            SystemMenu.Centered(batch, font, lines[i], new Rectangle(Left, top + i * QuestionLine, Inner, QuestionLine), QuestionSize, false);
        SystemMenu.Centered(batch, font, Lang.T("common.yes"), YesButton(height), QuestionSize, YesButton(height).Contains(cursor));
        SystemMenu.Centered(batch, font, Lang.T("common.no"), NoButton(height), QuestionSize, NoButton(height).Contains(cursor));
    }

    private static void Button(SpriteBatch batch, Texture2D pixel, UiFont font, Rectangle rect, string label, Vector2 cursor) =>
        TextButton.Draw(batch, pixel, font, rect, label, cursor);

    /// <summary>The short German state of the building (soldiers for military buildings).</summary>
    private string StateOf(World world, Building building)
    {
        string status = world.StatusOf(building);
        if (SoldiersPattern().Match(status) is { Success: true } soldiers)
            return Lang.T("building.soldiers", soldiers.Groups[1].Value, soldiers.Groups[2].Value)
                   + (soldiers.Groups[3].Success ? Lang.T("building.soldiersOnTheWay", soldiers.Groups[3].Value) : "")
                   + (DoorPattern().Match(status) is { Success: true } door ? Lang.T("building.door", door.Groups[1].Value) : "");
        foreach (var (contains, state) in _states)
            if (status.Contains(contains, StringComparison.Ordinal))
                return Lang.T("buildingStatus." + state);
        return Lang.T("buildingStatus.active");
    }

    private static List<(Material Material, string Text)> Goods(World world, Building building)
    {
        // A shipyard building a ship: the ship's material, delivered of needed (as on a construction site).
        if (world.Shipping.Building(building) is { } ship)
            return world.Shipping.CostOf(ship).Select(c =>
                (c.Key, $"{Math.Min(world.Shipping.Delivered(building, c.Key), c.Value)}/{c.Value}")).ToList();
        // A war machine works at work: its material likewise.
        if (world.WarMachines.Current(building) is { } work)
            return world.WarMachines.CostOf(building, work).Select(c =>
                (c.Key, $"{Math.Min(world.WarMachines.Delivered(building, c.Key), c.Value)}/{c.Value}")).ToList();
        if (building.State != BuildingState.Finished)
        {
            // Construction piles count their capacity down as material is built in.
            var needed = building.Definition.ConstructionPiles.Where(p => p.Material is not null)
                .GroupBy(p => p.Material!.Value).ToDictionary(g => g.Key, g => g.Sum(p => world.ConstructionCost(building.Player, p)));
            return needed.Select(n =>
            {
                var piles = building.Piles.Where(p => p.Role == PileRole.Request && p.AcceptedMaterial == n.Key).ToList();
                int delivered = n.Value - piles.Sum(p => p.Capacity) + piles.Sum(p => p.Count);
                return (n.Key, $"{Math.Clamp(delivered, 0, n.Value)}/{n.Value}");
            }).ToList();
        }
        return building.Piles.Where(p => (p.Material ?? p.AcceptedMaterial) is not null && (p.Role != PileRole.Storage || p.Count > 0))
            .GroupBy(p => (p.Material ?? p.AcceptedMaterial)!.Value)
            .Select(g => (g.Key, g.Sum(p => p.Count).ToString()))
            .ToList();
    }

    [GeneratedRegex(@"^(\d+)/(\d+) soldiers(?:, (\d+) on the way)?")]
    private static partial Regex SoldiersPattern();

    [GeneratedRegex(@", door (\d+)%")]
    private static partial Regex DoorPattern();
}
