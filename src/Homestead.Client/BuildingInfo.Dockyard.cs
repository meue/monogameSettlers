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
/// A finished own shipyard's panel as in the original: at the left the ferry and the trade ship, at the right the hammer
/// that sets the dock – after it, the next left click into the water puts the pier at the best place near it at once (see
/// <see cref="Shipping"/>). With a dock a ship button starts that ship: it stays pressed and neither button takes a click
/// until the ship is launched; meanwhile its material shows at the bottom as on a construction site.
/// </summary>
public sealed partial class BuildingInfo
{
    private Texture2D? _dockIcon;

    /// <summary>Per people: ferry, ferry pressed, trade ship, trade ship pressed (GUI images of the people's file).</summary>
    private readonly Dictionary<Civilization, Texture2D[]> _shipIcons = new();

    /// <summary>The shipyard whose dock the next left click on the map sets, if any.</summary>
    private Building? _dockAiming;

    /// <summary>Whether the next left click on the map sets the dock of <paramref name="dockyard"/>.</summary>
    public bool AimsDock(Building? dockyard) => dockyard is not null && _dockAiming == dockyard;

    /// <summary>No dock to set any more (right click, Esc).</summary>
    public void CancelDock() => _dockAiming = null;

    private void LoadDockyardIcons(GraphicsDevice device, GfxDirectory gfx, GfxFile gui)
    {
        static Texture2D Load(GraphicsDevice device, GfxFile file, int index)
        {
            var bitmap = file.ReadGui(index);
            var texture = new Texture2D(device, bitmap.Width, bitmap.Height);
            texture.SetData(bitmap.Pixels);
            return texture;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(BuildingCatalog.DefaultDataDirectory, "ui.json")));
        var dockyard = document.RootElement.GetProperty("dockyard");
        _dockIcon = Load(device, gui, dockyard.GetProperty("dock").GetInt32());
        var steps = dockyard.GetProperty("shipSteps").EnumerateArray().Select(s => s.GetInt32()).ToArray();
        foreach (var ships in dockyard.GetProperty("ships").EnumerateObject())
        {
            var file = gfx.Open(ships.Value[0].GetInt32());
            int first = ships.Value[1].GetInt32();
            _shipIcons[Enum.Parse<Civilization>(ships.Name)] = steps.Select(s => Load(device, file, first + s)).ToArray();
        }
    }

    // Layout (panel space): below the name (itself below the icons) the ships at the left, the hammer at the right, the image beside the ships.
    private static int DockyardTop => HeadBottom + 19 + 4;
    private static Rectangle FerryButton => new(Left, DockyardTop, 60, 40);
    private static Rectangle TradeShipButton => new(Left, DockyardTop + 44, 60, 40);
    private static Rectangle DockButton => new(Left + Inner - 30, DockyardTop + 5, 30, 30);

    /// <summary>Where the building's image goes next to the ships.</summary>
    private static int DockyardImageLeft => FerryButton.Right + 4;

    private static int DockyardImageTop => DockButton.Bottom + 4;

    private bool OffersDockyard(World world, Building building) =>
        _dockIcon is not null && building.State == BuildingState.Finished && world.Shipping.IsDockyard(building);

    /// <summary>Whether the ship buttons take a click: a dock and no ship on the stocks.</summary>
    private static bool CanOrderShip(World world, Building building) =>
        world.Shipping.DockOf(building) is not null && world.Shipping.Building(building) is null;

    private void DrawDockyard(SpriteBatch batch, Texture2D pixel, World world, Building building, Civilization people, Vector2 cursor)
    {
        var onStocks = world.Shipping.Building(building);
        bool open = CanOrderShip(world, building);
        if (_shipIcons.TryGetValue(people, out var ships) || _shipIcons.TryGetValue(Civilization.Roman, out ships))
        {
            batch.Draw(ships[onStocks == ShipKind.Ferry ? 1 : 0], FerryButton, Color.White);
            batch.Draw(ships[onStocks == ShipKind.TradeShip ? 3 : 2], TradeShipButton, Color.White);
            if (open && FerryButton.Contains(cursor))
                batch.Draw(pixel, FerryButton, HoverColor);
            if (open && TradeShipButton.Contains(cursor))
                batch.Draw(pixel, TradeShipButton, HoverColor);
        }
        batch.Draw(_dockIcon!, DockButton, Color.White);
        if (AimsDock(building))
            Frame(batch, pixel, DockButton, Color.White);
        else if (DockButton.Contains(cursor))
            batch.Draw(pixel, DockButton, HoverColor);
    }

    /// <summary>A click on a shipyard's panel (panel space). Returns a message, or null if it hit nothing.</summary>
    public string? DockyardClick(Vector2 panel, World world, Building building, int ownPlayer)
    {
        if (building.Player != ownPlayer || !OffersDockyard(world, building) || _confirming == building)
            return null;
        if (DockButton.Contains(panel) && world.Shipping.Building(building) is null)
        {
            _dockAiming = building;
            UiSounds.Toggle();
            return "click into the water: the dock goes to the best place near it (right click: cancel)";
        }
        if (FerryButton.Contains(panel) || TradeShipButton.Contains(panel))
        {
            var kind = FerryButton.Contains(panel) ? ShipKind.Ferry : ShipKind.TradeShip;
            if (world.Shipping.DockOf(building) is null)
                return "set a dock first (the hammer)";
            if (world.Shipping.Building(building) is { } onStocks)
                return $"{onStocks} on the stocks: {Math.Round(world.Shipping.Progress(building) * 100 ?? 0)}% built";
            Issue!(new OrderShip(ownPlayer, building.Id, kind));
            UiSounds.Toggle();
            return $"{kind} ordered: carriers bring its material";
        }
        return null;
    }

    /// <summary>What the shipyard's button under the point does (hover text), or null.</summary>
    public string? DockyardHover(Vector2 panel, World world, Building building, int ownPlayer)
    {
        if (building.Player != ownPlayer || !OffersDockyard(world, building) || _confirming == building)
            return null;
        if (DockButton.Contains(panel) && world.Shipping.Building(building) is not null)
            return "the dock stays while a ship is built";
        if (DockButton.Contains(panel))
            return world.Shipping.DockOf(building) is { } dock
                ? $"dock at {dock.Land.X},{dock.Land.Y}: set it elsewhere"
                : "set the dock (then click into the water)";
        if ((FerryButton.Contains(panel) || TradeShipButton.Contains(panel)) && world.Shipping.Building(building) is { } onStocks)
            return $"{onStocks} on the stocks: {Math.Round(world.Shipping.Progress(building) * 100 ?? 0)}% built";
        if (FerryButton.Contains(panel))
            return "build a ferry";
        if (TradeShipButton.Contains(panel))
            return "build a trade ship";
        return null;
    }

    /// <summary>A left click on the map while aiming (<see cref="AimsDock"/>): puts the dock at the best place near it. Returns a message.</summary>
    public void DockCommand(World world, Building dockyard, GridPoint clicked) =>
        Issue!(new PlaceDock(dockyard.Player, dockyard.Id, clicked), result =>
        {
            if (result is not true)
                return "no place for a dock there (open water on own land near the building)";
            if (_dockAiming == dockyard)
                _dockAiming = null;
            UiSounds.WorkArea();
            var dock = world.Shipping.DockOf(dockyard)!.Value;
            return $"dock at {dock.Land.X},{dock.Land.Y}";
        });

    /// <summary>
    /// The selected ships in the panel: per kind its button image, its name and how many, for ferries also how many are
    /// on board, for trade ships their cargo; at the bottom „Schiff entladen“ while a selected ferry carries anybody.
    /// </summary>
    public void DrawShips(SpriteBatch batch, Texture2D pixel, UiFont font, World world, IReadOnlyList<Ship> ships, Vector2 cursor)
    {
        int y = Top;
        foreach (var group in ships.GroupBy(s => (s.People, s.Kind)).OrderBy(g => g.Key.Kind))
        {
            if ((_shipIcons.TryGetValue(group.Key.People, out var icons) || _shipIcons.TryGetValue(Civilization.Roman, out icons)))
                batch.Draw(icons[group.Key.Kind == ShipKind.Ferry ? 0 : 2], new Rectangle(Left, y, 60, 40), Color.White);
            Write(batch, font, Lang.T(group.Key.Kind == ShipKind.Ferry ? "ships.ferry" : "ships.tradeShip"), new Vector2(Left + 66, y + 3), Text);
            string count = group.Count().ToString();
            if (group.Sum(s => world.Shipping.Capacity(s)) is var capacity and > 0)
                count += "   " + Lang.T("ships.passengers", group.Sum(s => s.Passengers.Count), capacity);
            else if (group.Key.Kind == ShipKind.TradeShip)
                count += "   " + Lang.T("ships.cargo", group.Sum(s => world.Shipping.CargoOf(s).Sum(c => c.Count)));
            Write(batch, font, count, new Vector2(Left + 66, y + 21), Text);
            y += 46;
        }
        if (OffersUnload(ships))
            Button(batch, pixel, font, UnloadButton, Lang.T("ships.unload"), cursor);
    }

    /// <summary>A ship's button image in the box (the group icons).</summary>
    public void DrawShipIcon(SpriteBatch batch, Ship ship, Rectangle box)
    {
        if (_shipIcons.TryGetValue(ship.People, out var icons) || _shipIcons.TryGetValue(Civilization.Roman, out icons))
            batch.Draw(icons[ship.Kind == ShipKind.Ferry ? 0 : 2], box, Color.White);
    }

    /// <summary>„Schiff entladen“ at the bottom, as the tear-down question's buttons.</summary>
    private static Rectangle UnloadButton => new(Left, Bottom - 28, Inner, 28);

    private static bool OffersUnload(IReadOnlyList<Ship> ships) => ships.Any(s => s.Passengers.Count > 0);

    /// <summary>Whether the panel point is on „Schiff entladen“ of the selected ships.</summary>
    public static bool UnloadAt(Vector2 panel, IReadOnlyList<Ship> ships) => OffersUnload(ships) && UnloadButton.Contains(panel);
}
