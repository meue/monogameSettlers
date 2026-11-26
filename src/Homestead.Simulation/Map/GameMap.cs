using Homestead.Assets.Maps;

namespace Homestead.Simulation.Map;

/// <summary>
/// The playing field: a square grid of points with terrain, height and an optional object.
/// Points are addressed by <see cref="GridPoint"/> or by index (<c>y * Size + x</c>).
/// </summary>
public sealed class GameMap
{
    private readonly TerrainType[] _terrain;
    private readonly byte[] _height;
    /// <summary>Object per point as original object code (0 = none, see <see cref="MapObjectCodes"/>).</summary>
    private readonly byte[] _objects;

    /// <summary>How many object footprints cover a point (objects can block their neighbors).</summary>
    private readonly byte[] _blockers;

    private readonly bool[] _walkable;

    /// <summary>Resource under a point (fish in water, ores in mountains) and its amount (0-15 in original maps).</summary>
    /// <summary>Owner of each point: player index, or <see cref="NoOwner"/>.</summary>
    private readonly byte[] _owner;

    private readonly ResourceType[] _resourceType;
    private readonly byte[] _resourceAmount;

    /// <summary>What lies on the ground of a point (farmland, trail); see <see cref="GroundCover"/>.</summary>
    private readonly GroundCover[] _cover;

    /// <summary>What levelled points were before, where it was not grass (they go back to it, see <see cref="Unlevel"/>).</summary>
    private readonly Dictionary<int, TerrainType> _levelledFrom = new();

    public GameMap(int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 2);
        Size = size;
        _terrain = new TerrainType[size * size];
        _height = new byte[size * size];
        _objects = new byte[size * size];
        _blockers = new byte[size * size];
        _walkable = new bool[size * size];
        Array.Fill(_walkable, true);
        _owner = new byte[size * size];
        Array.Fill(_owner, NoOwner);
        _resourceType = new ResourceType[size * size];
        _resourceAmount = new byte[size * size];
        _cover = new GroundCover[size * size];
    }

    public int Size { get; }

    /// <summary>Owner value of land that belongs to nobody (as in the original maps).</summary>
    public const byte NoOwner = 255;

    public int PointCount => Size * Size;

    /// <summary>Incremented whenever walkability changes, so caches (e.g. path search regions) can notice.</summary>
    public int WalkabilityVersion { get; private set; }

    /// <summary>The points whose walkability changed, in order; entry i is the change to version <see cref="_changesFrom"/> + i + 1.</summary>
    private readonly List<int> _walkabilityChanges = new();
    private int _changesFrom;

    /// <summary>Changes kept for catching up; a cache further behind starts over.</summary>
    private const int MaxLoggedChanges = 4096;

    /// <summary>
    /// The points whose walkability changed since <paramref name="version"/> (a point may appear more
    /// than once), so a cache can update just those. False if they are no longer known.
    /// </summary>
    public bool WalkabilityChangesSince(int version, out ReadOnlySpan<int> changes)
    {
        changes = default;
        if (version < _changesFrom || version > WalkabilityVersion)
            return false;
        changes = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_walkabilityChanges)[(version - _changesFrom)..];
        return true;
    }

    public static GameMap FromOriginal(MapArea area)
    {
        var map = new GameMap(area.Size);
        for (int i = 0; i < map.PointCount; i++)
        {
            map._terrain[i] = TerrainTypes.FromOriginal(area.Landscape[i]);
            map._height[i] = area.Height[i];
            map._owner[i] = area.Owner[i];
            // High nibble type, low nibble amount; 0 means nothing.
            byte resource = area.Resources[i];
            if (resource != 0 && resource >> 4 <= (int)ResourceType.Sulfur)
            {
                map._resourceType[i] = (ResourceType)(resource >> 4);
                map._resourceAmount[i] = (byte)(resource & 0x0f);
            }
        }
        for (int i = 0; i < map.PointCount; i++)
            map.SetObjectCode(map.Point(i), area.Object[i]);
        for (int i = 0; i < map.PointCount; i++)
            map._walkable[i] = map.ComputeWalkable(i);
        map.WalkabilityVersion = 0;
        map._walkabilityChanges.Clear();
        map._changesFrom = 0;
        return map;
    }

    public bool Contains(GridPoint p) => (uint)p.X < (uint)Size && (uint)p.Y < (uint)Size;

    public int Index(GridPoint p) => p.Y * Size + p.X;

    public GridPoint Point(int index) => new(index % Size, index / Size);

    public TerrainType TerrainAt(GridPoint p) => _terrain[Index(p)];

    public int HeightAt(GridPoint p) => _height[Index(p)];

    public MapObjectKind ObjectAt(GridPoint p) => MapObjectKinds.FromOriginal(_objects[Index(p)]);

    /// <summary>The object as original object code; decides both rules and graphics.</summary>
    public byte ObjectCodeAt(GridPoint p) => _objects[Index(p)];

    public byte ObjectCodeAt(int index) => _objects[index];

    /// <summary>The player owning the point, or null.</summary>
    public int? OwnerAt(GridPoint p) => _owner[Index(p)] is var o && o != NoOwner ? o : null;

    /// <summary>Owner by point index; <see cref="NoOwner"/> for nobody.</summary>
    public byte OwnerAt(int index) => _owner[index];

    /// <summary>Changes whenever an owner changes (pathfinding keeps land regions per version).</summary>
    public int OwnerVersion { get; private set; }

    public void SetOwner(GridPoint p, int? player)
    {
        byte value = player is { } o ? (byte)o : NoOwner;
        if (_owner[Index(p)] == value)
            return;
        _owner[Index(p)] = value;
        OwnerVersion++;
    }

    /// <summary>Resource under the point; amount 0 means there is nothing (left).</summary>
    public (ResourceType Type, int Amount) ResourceAt(GridPoint p) => (_resourceType[Index(p)], _resourceAmount[Index(p)]);

    public (ResourceType Type, int Amount) ResourceAt(int index) => (_resourceType[index], _resourceAmount[index]);

    /// <summary>Amount of <paramref name="type"/> under the point, 0 if there is another or no resource.</summary>
    public int ResourceAmount(GridPoint p, ResourceType type) =>
        _resourceType[Index(p)] == type ? _resourceAmount[Index(p)] : 0;

    public void SetResource(GridPoint p, ResourceType type, int amount)
    {
        _resourceType[Index(p)] = type;
        _resourceAmount[Index(p)] = (byte)Math.Clamp(amount, 0, 255);
    }

    public bool IsWalkable(GridPoint p) => Contains(p) && _walkable[Index(p)];

    public bool IsWalkable(int index) => _walkable[index];

    /// <summary>Raised when terrain or height of a point changes after loading (for redrawing).</summary>
    public event Action<GridPoint>? PointChanged;

    public void SetTerrain(GridPoint p, TerrainType terrain)
    {
        int index = Index(p);
        if (terrain != TerrainType.Flattened)
            _levelledFrom.Remove(index);
        else if (_terrain[index] is not (TerrainType.Grass or TerrainType.Flattened))
            _levelledFrom[index] = _terrain[index];
        _terrain[index] = terrain;
        // Farmland and trails lie on grass only (levelled for a building: gone).
        if (terrain != TerrainType.Grass)
            _cover[Index(p)] = GroundCover.None;
        UpdateWalkable(Index(p));
        PointChanged?.Invoke(p);
    }

    /// <summary>Farmland or trail on the point (walkable as before, only the look and the walking speed change).</summary>
    public GroundCover CoverAt(GridPoint p) => _cover[Index(p)];

    public GroundCover CoverAt(int index) => _cover[index];

    public TerrainType TerrainAt(int index) => _terrain[index];

    /// <summary>Levelled ground nothing stands on (a building that came down, or levelled on the original map).</summary>
    public bool IsBareLevelled(GridPoint p) => Contains(p) && _terrain[Index(p)] == TerrainType.Flattened && _blockers[Index(p)] == 0;

    /// <summary>The terrain of the point, for levelled ground what it was before (grass unless known otherwise).</summary>
    public TerrainType Unlevelled(GridPoint p) =>
        _terrain[Index(p)] == TerrainType.Flattened ? _levelledFrom.GetValueOrDefault(Index(p), TerrainType.Grass) : _terrain[Index(p)];

    /// <summary>Levelled ground goes back to what it was (grass grows over a demolished building's ground, farmland takes it).</summary>
    public void Unlevel(GridPoint p)
    {
        if (TerrainAt(p) == TerrainType.Flattened)
            SetTerrain(p, Unlevelled(p));
    }

    /// <summary>Bare levelled grass, which sowing takes back (<see cref="Unlevel"/>).</summary>
    private bool IsReclaimable(GridPoint p) => IsBareLevelled(p) && Unlevelled(p) == TerrainType.Grass;

    /// <summary>
    /// Whether <paramref name="cover"/> may lie on the grass point <paramref name="p"/>. Farmland and trails draw
    /// with the original's tiles for earth and sand, whose transitions would join them with rivers, swamps,
    /// deserts and each other: one point of grass stays between them and any other ground (trails may touch
    /// levelled ground). Farmland may touch trails, since sowing cuts them back (<see cref="Trails.CutAround"/>), and may
    /// take bare levelled grass, which sowing turns back into grass (<see cref="IsReclaimable"/>), on and around the point.
    /// </summary>
    public bool CoverFits(GridPoint p, GroundCover cover)
    {
        if (!Contains(p) || TerrainAt(p) != TerrainType.Grass && !(cover == GroundCover.Farmland && IsReclaimable(p)))
            return false;
        for (int d = 0; d < DirectionExtensions.Count; d++)
        {
            var q = p.Neighbor((Direction)d);
            if (!Contains(q))
                continue;
            if (TerrainAt(q) != TerrainType.Grass
                && !(cover == GroundCover.Trail ? TerrainAt(q) == TerrainType.Flattened : IsReclaimable(q)))
                return false;
            var other = CoverAt(q);
            if (other != GroundCover.None && other != cover && !(cover == GroundCover.Farmland && other == GroundCover.Trail))
                return false;
        }
        return true;
    }

    public void SetCover(GridPoint p, GroundCover cover)
    {
        if (_cover[Index(p)] == cover)
            return;
        _cover[Index(p)] = cover;
        PointChanged?.Invoke(p);
    }

    public void SetHeight(GridPoint p, int height)
    {
        _height[Index(p)] = (byte)Math.Clamp(height, 0, 255);
        PointChanged?.Invoke(p);
    }

    /// <summary>Sets an object by kind, using a representative code (tests, tools).</summary>
    public void SetObject(GridPoint p, MapObjectKind kind) => SetObjectCode(p, kind switch
    {
        MapObjectKind.None => MapObjectCodes.None,
        MapObjectKind.Tree => MapObjectCodes.FirstTree,
        MapObjectKind.ResourceStone => MapObjectCodes.FullStone,
        MapObjectKind.Decoration => MapObjectCodes.FirstPlant,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    });

    public void SetObjectCode(GridPoint p, byte code)
    {
        int index = Index(p);
        var oldKind = MapObjectKinds.FromOriginal(_objects[index]);
        var newKind = MapObjectKinds.FromOriginal(code);
        bool pier = _objects[index] == MapObjectCodes.Dock || code == MapObjectCodes.Dock;
        _objects[index] = code;
        if (oldKind != newKind)
        {
            ChangeFootprint(p, oldKind, -1);
            ChangeFootprint(p, newKind, +1);
        }
        if (pier)
            UpdateWalkable(index);
        PointChanged?.Invoke(p);
    }

    /// <summary>Marks a point as blocked by something other than a map object (e.g. a building). Counted.</summary>
    public void AddBlocker(GridPoint p) => Cover(p, +1);

    public void RemoveBlocker(GridPoint p) => Cover(p, -1);

    private void ChangeFootprint(GridPoint p, MapObjectKind kind, int delta)
    {
        if (!kind.BlocksWalking())
            return;
        Cover(p, delta);
        if (kind.BlocksNeighbors())
            for (int d = 0; d < DirectionExtensions.Count; d++)
                Cover(p.Neighbor((Direction)d), delta);
    }

    private void Cover(GridPoint p, int delta)
    {
        if (!Contains(p))
            return;
        int index = Index(p);
        _blockers[index] = (byte)(_blockers[index] + delta);
        UpdateWalkable(index);
    }

    /// <summary>Works out every point's walkability again (after loading a game saved by another build).</summary>
    public void RefreshWalkable()
    {
        for (int i = 0; i < PointCount; i++)
            UpdateWalkable(i);
    }

    private void UpdateWalkable(int index)
    {
        bool walkable = ComputeWalkable(index);
        if (walkable == _walkable[index])
            return;
        _walkable[index] = walkable;
        if (_walkabilityChanges.Count >= MaxLoggedChanges)
        {
            _walkabilityChanges.Clear();
            _changesFrom = WalkabilityVersion;
        }
        _walkabilityChanges.Add(index);
        WalkabilityVersion++;
    }

    /// <summary>Walkable ground, or a pier over the water (<see cref="MapObjectCodes.Dock"/>), with nothing on it.</summary>
    private bool ComputeWalkable(int index) =>
        (_terrain[index].IsWalkable() || _objects[index] == MapObjectCodes.Dock) && _blockers[index] == 0;
}
