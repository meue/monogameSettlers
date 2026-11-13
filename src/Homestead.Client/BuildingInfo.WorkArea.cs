using Homestead.Simulation;
using Homestead.Simulation.Commands;
using Homestead.Simulation.Buildings;
using Microsoft.Xna.Framework;

namespace Homestead.Client;

/// <summary>
/// The work-area button of a finished own building with a work area (as in the original, centered between
/// the pause and the tear-down icon): after it, the next left click on the map moves the work area there
/// (see <see cref="World.SetWorkCenter"/>); a right click or Esc cancels. The marks of the moved area
/// show as always (see MapGame's work-area marks).
/// </summary>
public sealed partial class BuildingInfo
{
    /// <summary>The building whose work area the next left click on the map moves, if any.</summary>
    private Building? _workAiming;

    /// <summary>Whether the next left click on the map moves the work area of <paramref name="building"/>.</summary>
    public bool AimsWorkCenter(Building? building) => building is not null && _workAiming == building;

    /// <summary>No work area to move any more (right click, Esc).</summary>
    public void CancelWorkCenter() => _workAiming = null;

    private static bool OffersWorkArea(Building building) => building.HasWorkArea && building.State == BuildingState.Finished;

    /// <summary>A click on the panel (panel space): arms or disarms moving the work area. Returns a message, or null if it hit nothing.</summary>
    public string? WorkAreaClick(Vector2 panel, World world, Building building, int ownPlayer)
    {
        if (building.Player != ownPlayer || !OffersWorkArea(building) || _confirming == building || !WorkAreaButton(0).Contains(panel))
            return null;
        if (AimsWorkCenter(building))
        {
            _workAiming = null;
            return Lang.T("building.workAreaUndone");
        }
        _workAiming = building;
        UiSounds.Toggle();
        return Lang.T("building.workAreaArmed");
    }

    /// <summary>
    /// A left click on the map while aiming (<see cref="AimsWorkCenter"/>), and the right click on the selected
    /// building: the work area moves there (at most <see cref="World.MaxWorkCenterDistance"/> fields away).
    /// </summary>
    public void WorkCenterCommand(World world, Building building, GridPoint clicked) =>
        Issue!(new SetWorkCenter(building.Player, building.Id, clicked), result =>
        {
            if (result is not GridPoint center)
                return null;
            if (_workAiming == building)
                _workAiming = null;
            UiSounds.WorkArea();
            return building.HasGatheringPoint ? Lang.T("building.gatheringPointMoved", center.X, center.Y)
                : center == clicked ? Lang.T("building.workAreaMoved", center.X, center.Y)
                : Lang.T("building.workAreaMovedCapped", center.X, center.Y, World.MaxWorkCenterDistance);
        });
}
