using System.Text.Json;
using Homestead.Assets.Gfx;
using Homestead.Simulation;
using Homestead.Simulation.Commands;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Military;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Homestead.Client;

/// <summary>
/// A finished own war machine works' panel: at the left the machine and its ammunition (the gong has none), each click orders
/// one more (Ctrl+click takes back the last one not started), how many are ordered beside it; the one being made is pressed.
/// The image beside them, below the loads of ammunition lying at the works; the material of the current work at the bottom
/// as on a construction site.
/// </summary>
public sealed partial class BuildingInfo
{
    /// <summary>Per people: machine, machine pressed, ammunition, ammunition pressed (GUI images of the people's file).</summary>
    private readonly Dictionary<Civilization, Texture2D[]> _warIcons = new();

    private void LoadWarWorksIcons(GraphicsDevice device, GfxDirectory gfx)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(BuildingCatalog.DefaultDataDirectory, "ui.json")));
        if (!document.RootElement.TryGetProperty("warMachines", out var machines))
            return;
        foreach (var people in machines.EnumerateObject().Where(p => !p.Name.StartsWith('_')))
        {
            var icons = people.Value.GetProperty("icons");
            var file = gfx.Open(icons[0].GetInt32());
            int first = icons[1].GetInt32();
            // Three sizes each: the middle one.
            _warIcons[Enum.Parse<Civilization>(people.Name)] = new[] { 1, 4, 7, 10 }.Select(step =>
            {
                var bitmap = file.ReadGui(first + step);
                var texture = new Texture2D(device, bitmap.Width, bitmap.Height);
                texture.SetData(bitmap.Pixels);
                return texture;
            }).ToArray();
        }
    }

    private static int WarWorksTop => HeadBottom + 19 + 4; // below the name, itself below the icons
    private static Rectangle MachineButton => new(Left, WarWorksTop, 48, 48);
    private static Rectangle AmmoButton => new(Left, WarWorksTop + 54, 48, 48);

    /// <summary>Where the building's image goes next to the buttons (right of their counts).</summary>
    private static int WarWorksImageLeft => MachineButton.Right + 30;

    private static bool OffersWarWorks(World world, Building building) =>
        building.State == BuildingState.Finished && world.WarMachines.IsWorks(building);

    private static bool UsesAmmo(World world, Building building) =>
        world.WarMachines.KindOf(building.Definition.Civilization) is { Shots: > 0 };

    private void DrawWarWorks(SpriteBatch batch, Texture2D pixel, UiFont font, World world, Building building, Vector2 cursor)
    {
        var machines = world.WarMachines;
        var current = machines.Current(building);
        if (!_warIcons.TryGetValue(building.Definition.Civilization, out var icons))
            return;
        void Order(Rectangle button, WarWork work, int icon)
        {
            batch.Draw(icons[current == work ? icon + 1 : icon], button, Color.White);
            if (button.Contains(cursor))
                batch.Draw(pixel, button, HoverColor);
            if (machines.Ordered(building, work) is > 0 and var ordered)
                Write(batch, font, $"×{ordered}", new Vector2(button.Right + 3, button.Y + 15), Text);
        }
        Order(MachineButton, WarWork.Machine, 0);
        if (UsesAmmo(world, building))
        {
            Order(AmmoButton, WarWork.Ammo, 2);
            Write(batch, font, Lang.T("building.ammoLoads", machines.Loads(building)), new Vector2(Left, AmmoButton.Bottom + 4), Text);
        }
    }

    /// <summary>Below the buttons (and the loads): where the state starts.</summary>
    private static int WarWorksBottom(World world, Building building) => (UsesAmmo(world, building) ? AmmoButton.Bottom + 21 : MachineButton.Bottom) + 4;

    /// <summary>A click on a war machine works' panel (panel space). Returns a message, or null if it hit nothing.</summary>
    public string? WarWorksClick(Vector2 panel, World world, Building building, int ownPlayer, bool takeBack)
    {
        if (building.Player != ownPlayer || !OffersWarWorks(world, building) || _confirming == building)
            return null;
        WarWork? work = MachineButton.Contains(panel) ? WarWork.Machine : AmmoButton.Contains(panel) && UsesAmmo(world, building) ? WarWork.Ammo : null;
        if (work is not { } w)
            return null;
        string what = w == WarWork.Machine ? "war machine" : "load of ammunition";
        Issue!(new OrderWarWork(ownPlayer, building.Id, w, takeBack), result =>
        {
            if (result is not true)
                return takeBack ? $"no {what} to take back (the one being made stays)" : $"no {what} here";
            UiSounds.Step(more: !takeBack);
            return takeBack ? $"{what} taken back: {world.WarMachines.Ordered(building, w)} ordered"
                : $"{what} ordered ({world.WarMachines.Ordered(building, w)}): carriers bring its material";
        });
        return null;
    }

    /// <summary>What the works' button under the point does (hover text), or null.</summary>
    public string? WarWorksHover(Vector2 panel, World world, Building building, int ownPlayer)
    {
        if (building.Player != ownPlayer || !OffersWarWorks(world, building) || _confirming == building)
            return null;
        string Cost(WarWork work) => string.Join(", ", world.WarMachines.CostOf(building, work).Select(c => $"{c.Value} {c.Key}"));
        if (MachineButton.Contains(panel))
            return $"build a war machine ({Cost(WarWork.Machine)}); Ctrl+click: take one back";
        if (AmmoButton.Contains(panel) && UsesAmmo(world, building))
            return $"make a load of ammunition ({Cost(WarWork.Ammo)}); Ctrl+click: take one back";
        return null;
    }
}
