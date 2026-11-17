using Homestead.Client.Terrain;
using Homestead.Simulation;
using Homestead.Simulation.Map;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Economy;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Homestead.Client;

/// <summary>Draws buildings and piles of goods.</summary>
public sealed class EconomyRenderer
{
    private const int PileFile = 1;

    // Flags and work area marks (JSettlers' MapObjectDrawer, MIT): waving flag in the player color,
    // on the roof of an occupied workplace or at the door of buildings without a worker.
    private const int FlagFile = 13;
    private const int DoorFlagSequence = 63;
    private const int RoofFlagSequence = 64;
    private const float FlagFramesPerSecond = 10;
    private const int BorderPostSequence = 65; // file 13, in the player color
    private const int WorkAreaFile = 1;
    private const int WorkAreaSequence = 91; // 4 posts, green (inner ring) to red (outer ring)
    private const int SignFile = 1;            // geologist signs (JSettlers' MapObjectDrawer, MIT)
    private const int SitePostSequence = 92;   // file 1: posts around a construction site
    private const int SiteSignSequence = 93;   // file 1: the sign on a construction site
    private const int MaxResourceAmount = 15;  // amount per field in original maps
    private const int DustFile = 13;           // a building coming down (JSettlers' MapObjectDrawer, MIT)
    private const int DustSequence = 38;
    private const int FlameFile = 13;          // a medium flame, looping
    private const int FlameSequence = 40;
    private const float FlameFramesPerSecond = 24;
    private const int GhostFile = 12;          // a settler's ghost rising in the player color (JSettlers' MapObjectDrawer, MIT)
    private const int GhostSequence = 27;

    private readonly SpriteLibrary _library;

    public EconomyRenderer(SpriteLibrary library)
    {
        _library = library;
    }

    /// <summary>Grid row used for drawing order: the lowest row the building covers.</summary>
    public static int DepthRow(Building building) => building.Position.Y + building.Definition.BottomRow;

    /// <summary>
    /// A vertical slice of a building's picture, <see cref="Left"/> to <see cref="Right"/> pixels from its anchor,
    /// drawn in grid row <see cref="Row"/> (offset from the building's): the front row of the footprint below it.
    /// What stands beside a tall building, in front of its side but behind its front row, would be painted over
    /// if the whole picture were drawn in the front row.
    /// </summary>
    public readonly record struct Strip(int Row, int Left, int Right);

    private const int StripWidth = 8;   // half a field: the footprint's fields lie on columns 8 px apart
    private const int Unbounded = 1 << 20;

    private readonly Dictionary<BuildingDefinition, Strip[]> _strips = new();

    /// <summary>The slices of a building, left to right; the outermost reach to either side without end.</summary>
    public IReadOnlyList<Strip> StripsOf(BuildingDefinition definition)
    {
        if (_strips.TryGetValue(definition, out var strips))
            return strips;
        var fields = definition.Blocked.Select(o => (X: o.X * 16 - o.Y * 8, Row: o.Y)).ToList();
        if (fields.Count == 0)
            return _strips[definition] = [new Strip(0, -Unbounded, Unbounded)];
        int first = fields.Min(f => f.X), last = fields.Max(f => f.X);
        var list = new List<Strip>();
        for (int x = first; x <= last; x += StripWidth)
        {
            // The front-most field reaching over this column (a field is 16 px wide).
            int row = fields.Where(f => Math.Abs(f.X - x) <= StripWidth).Select(f => f.Row).DefaultIfEmpty(int.MinValue).Max();
            if (row == int.MinValue)
                row = fields.MinBy(f => Math.Abs(f.X - x)).Row;
            int left = x == first ? -Unbounded : x - StripWidth / 2;
            if (list.Count > 0 && list[^1].Row == row)
                list[^1] = list[^1] with { Right = x + StripWidth / 2 };
            else
                list.Add(new Strip(row, left, x + StripWidth / 2));
        }
        list[^1] = list[^1] with { Right = Unbounded };
        return _strips[definition] = list.ToArray();
    }

    /// <summary>A construction site's sign and the posts around it, from the start of the site.</summary>
    public void DrawSiteMarks(SpriteBatch batch, Building building, TerrainMesh terrain, Color playerColor)
    {
        if (building.State == BuildingState.Finished)
            return;
        Sprites(SignFile, SiteSignSequence).Draw(batch, 0, Anchor(building.Position, terrain), 1, playerColor);
        foreach (var mark in building.Definition.BuildMarks)
            Sprites(SignFile, SitePostSequence).Draw(batch, 0, Anchor(building.Position + mark, terrain), 1, playerColor);
    }

    /// <summary>
    /// One slice of a building. Finished buildings show their first final layer, the soldiers in its places and
    /// its further layers (window frames and parapets in front of the soldiers, so that only their heads and
    /// shoulders show as in the original). Construction sites show the construction image, growing from the
    /// bottom with progress; while levelling there is nothing to see yet.
    /// </summary>
    public void DrawStrip(SpriteBatch batch, Building building, Strip strip, IReadOnlyList<Settler?> occupants, SettlerRenderer settlers,
        TerrainMesh terrain, Color playerColor, long tick, float tickAlpha)
    {
        var anchor = Anchor(building.Position, terrain);
        var clip = new Rectangle((int)Math.Round(anchor.X) + strip.Left, -Unbounded,
            Math.Min(strip.Right - strip.Left, 2 * Unbounded), 2 * Unbounded);
        switch (building.State)
        {
            case BuildingState.Finished:
                var layers = building.Definition.FinalImages;
                if (layers.Count > 0)
                    Sprites(layers[0].File, layers[0].Sequence).Draw(batch, layers[0].Image, anchor, 1, playerColor, clip: clip);
                DrawOccupants(batch, building, occupants, settlers, terrain, tick, tickAlpha, clip);
                foreach (var image in layers.Skip(1))
                    Sprites(image.File, image.Sequence).Draw(batch, image.Image, anchor, 1, playerColor, clip: clip);
                break;
            case BuildingState.Constructing when building.Definition.ConstructionImage is { } image:
                // A sliver at the start, so a fresh site is visible at all.
                float visible = 0.05f + 0.95f * building.ConstructionProgress;
                Sprites(image.File, image.Sequence).Draw(batch, image.Image, anchor, 1, playerColor, visible, clip: clip);
                break;
        }
    }

    /// <summary>
    /// World-space bounds of the finished building's image (also for sites, so the frame does not grow
    /// while it is built), or a small box at the anchor if it has none.
    /// </summary>
    public Rectangle BuildingBounds(Building building, TerrainMesh terrain)
    {
        var anchor = Anchor(building.Position, terrain);
        var bounds = building.Definition.FinalImages.Select(i => Sprites(i.File, i.Sequence).Bounds(i.Image)).OfType<Rectangle>().ToList();
        var local = bounds.Count > 0 ? bounds.Aggregate(Rectangle.Union) : new Rectangle(-8, -16, 16, 16);
        local.Offset((int)anchor.X, (int)anchor.Y);
        return local;
    }

    /// <summary>The building's flag: on the roof once its worker has arrived; at the door if it has no worker.</summary>
    public void DrawFlag(SpriteBatch batch, Building building, bool hasWorker, TerrainMesh terrain, Color playerColor, TimeSpan time)
    {
        if (building.State != BuildingState.Finished || building.Definition.Flag is not { } offset)
            return;
        bool workplace = building.Definition.Worker is not null;
        if (workplace && !hasWorker)
            return;
        var sprites = Sprites(FlagFile, workplace ? RoofFlagSequence : DoorFlagSequence);
        if (sprites.FrameCount == 0)
            return;
        int frame = (int)(time.TotalSeconds * FlagFramesPerSecond + building.Id) % sprites.FrameCount;
        sprites.Draw(batch, frame, Anchor(building.Position + offset, terrain), 1, playerColor);
    }

    /// <summary>The waving flag at a gathering point (the door flag of buildings without a worker).</summary>
    public void DrawGatheringFlag(SpriteBatch batch, GridPoint p, TerrainMesh terrain, Color playerColor, TimeSpan time)
    {
        var sprites = Sprites(FlagFile, DoorFlagSequence);
        if (sprites.FrameCount == 0)
            return;
        int frame = (int)(time.TotalSeconds * FlagFramesPerSecond) % sprites.FrameCount;
        sprites.Draw(batch, frame, Anchor(p, terrain), 1, playerColor);
    }

    /// <summary>A border post of the player's land.</summary>
    public void DrawBorderPost(SpriteBatch batch, GridPoint p, int player, TerrainMesh terrain)
    {
        var sprites = Sprites(FlagFile, BorderPostSequence);
        if (sprites.FrameCount > 0)
            sprites.Draw(batch, 0, Anchor(p, terrain), 1, PlayerColors.Of(player));
    }

    /// <summary>
    /// Soldiers in the places of a military building, standing on it at the places' pixel offsets
    /// (y up, from the building anchor), facing south-east or south-west.
    /// </summary>
    private static void DrawOccupants(SpriteBatch batch, Building building, IReadOnlyList<Settler?> occupants, SettlerRenderer settlers,
        TerrainMesh terrain, long tick, float tickAlpha, Rectangle clip)
    {
        for (int i = 0; i < occupants.Count; i++)
        {
            if (occupants[i] is not { } soldier)
                continue;
            var place = building.Definition.Occupiers[i];
            settlers.DrawOccupant(batch, soldier, OccupantAnchor(building, i, terrain),
                place.LooksRight ? Direction.SouthEast : Direction.SouthWest, PlayerColors.Of(soldier.Player), tick, tickAlpha, clip);
        }
    }

    /// <summary>Where the soldier of a place stands on the building, in world pixels.</summary>
    public static Vector2 OccupantAnchor(Building building, int place, TerrainMesh terrain)
    {
        var pixel = building.Definition.Occupiers[place].Pixel;
        return Anchor(building.Position, terrain) + new Vector2(pixel.X, -pixel.Y);
    }

    private const int ArrowFile = 1;
    private const int ArrowSequence = 100;

    /// <summary>
    /// An arrow at a world position, flying in a screen direction (SW, W, NW, NE, E, SE: sequences
    /// 100-105 of the object file); frame 0 rising, 1 level, 2 falling.
    /// </summary>
    public void DrawArrow(SpriteBatch batch, Vector2 position, int direction, int frame)
    {
        var sprites = Sprites(ArrowFile, ArrowSequence + direction);
        if (sprites.FrameCount > 0)
            sprites.Draw(batch, Math.Min(frame, sprites.FrameCount - 1), position, 1, Color.White);
    }

    /// <summary>Swamp bubbles in the object file: growing, sinking back, growing on and bursting.</summary>
    public const int BubbleRises = 106, BubbleSinks = 107, BubbleBursts = 108;

    /// <summary>A frame of a swamp bubble (<see cref="BubbleRises"/>, …) at a world position.</summary>
    public void DrawBubble(SpriteBatch batch, Vector2 position, int sequence, int frame)
    {
        var sprites = Sprites(ArrowFile, sequence);
        if (frame < sprites.FrameCount)
            sprites.Draw(batch, frame, position, 1, Color.White);
    }

    /// <summary>One post of a work area marking; <paramref name="ring"/> 0 (inner) to 3 (outer).</summary>
    public void DrawWorkAreaMark(SpriteBatch batch, GridPoint p, int ring, TerrainMesh terrain, Color? tint = null)
    {
        var sprites = Sprites(WorkAreaFile, WorkAreaSequence);
        if (sprites.FrameCount > 0)
            sprites.Draw(batch, Math.Min(ring, sprites.FrameCount - 1), Anchor(p, terrain), 1, Color.White, tint: tint);
    }

    /// <summary>
    /// A geologist's sign: one sequence per resource (94 coal, 95 gems, 96 gold, 97 iron, 98 sulfur,
    /// 99 nothing), its frames from little to much.
    /// </summary>
    public void DrawResourceSign(SpriteBatch batch, GridPoint p, ResourceSign sign, TerrainMesh terrain)
    {
        int sequence = sign.Resource switch
        {
            ResourceType.Coal => 94,
            ResourceType.Gems => 95,
            ResourceType.GoldOre => 96,
            ResourceType.IronOre => 97,
            ResourceType.Sulfur => 98,
            _ => 99,
        };
        var sprites = Sprites(SignFile, sequence);
        if (sprites.FrameCount == 0)
            return;
        float progress = Math.Clamp(sign.Amount / (float)MaxResourceAmount, 0, 1);
        sprites.Draw(batch, Math.Min((int)(progress * sprites.FrameCount), sprites.FrameCount - 1), Anchor(p, terrain), 1, Color.White);
    }

    /// <summary>The dust cloud of a building coming down, <paramref name="progress"/> 0..1 through it.</summary>
    public void DrawDustCloud(SpriteBatch batch, GridPoint p, float progress, TerrainMesh terrain)
    {
        var sprites = Sprites(DustFile, DustSequence);
        if (sprites.FrameCount == 0 || progress >= 1)
            return;
        int frame = Math.Clamp((int)(progress * sprites.FrameCount), 0, sprites.FrameCount - 1);
        sprites.Draw(batch, frame, Anchor(p, terrain), 1, Color.White);
    }

    /// <summary>A flame over a pile summoned by a spell, <paramref name="seconds"/> after it lit.</summary>
    public void DrawFlame(SpriteBatch batch, GridPoint p, float seconds, TerrainMesh terrain)
    {
        var sprites = Sprites(FlameFile, FlameSequence);
        if (sprites.FrameCount == 0)
            return;
        int frame = (int)(seconds * FlameFramesPerSecond) % sprites.FrameCount;
        sprites.Draw(batch, frame, Anchor(p, terrain), 1, Color.White);
    }

    /// <summary>The ghost rising where a settler died, in its player's color, <paramref name="progress"/> 0..1 through it.</summary>
    public void DrawGhost(SpriteBatch batch, GridPoint p, float progress, TerrainMesh terrain, Color playerColor)
    {
        var sprites = Sprites(GhostFile, GhostSequence);
        if (sprites.FrameCount == 0 || progress >= 1)
            return;
        int frame = Math.Clamp((int)(progress * sprites.FrameCount), 0, sprites.FrameCount - 1);
        sprites.Draw(batch, frame, Anchor(p, terrain), 1, playerColor);
    }

    /// <summary>Semi-transparent preview of a building at a position, green if it fits, red if not.</summary>
    /// <summary>The finished building, see-through, where it would be placed.</summary>
    public void DrawPreview(SpriteBatch batch, BuildingDefinition definition, GridPoint position, TerrainMesh terrain, Color playerColor)
    {
        var anchor = Anchor(position, terrain);
        var tint = Color.White * 0.75f;
        foreach (var image in definition.FinalImages)
            Sprites(image.File, image.Sequence).Draw(batch, image.Image, anchor, 1, playerColor, tint: tint);
    }

    private static Vector2 Anchor(GridPoint p, TerrainMesh terrain) =>
        ScreenProjection.ToWorld(p.X, p.Y, terrain.HeightAt(p.X, p.Y));

    public void DrawPile(SpriteBatch batch, Pile pile, TerrainMesh terrain)
    {
        if (pile.Material is { } material)
            DrawStack(batch, material, pile.Count, ScreenProjection.ToWorld(pile.Position.X, pile.Position.Y,
                terrain.HeightAt(pile.Position.X, pile.Position.Y)));
    }

    /// <summary>A pile of <paramref name="count"/> units of a good at a world position (e.g. on a ship's deck).</summary>
    public void DrawStack(SpriteBatch batch, Material material, int count, Vector2 anchor)
    {
        if (count == 0 || StackSequence(material) is not { } sequence)
            return;
        var sprites = Sprites(PileFile, sequence);
        sprites.Draw(batch, Math.Min(count, sprites.FrameCount) - 1, anchor, 1, Color.White);
    }

    /// <summary>
    /// The finished building as in the game, standing on the bottom of the box, its body centered and scaled
    /// down to fit body and shadow - so never cut off at the sides and the top.
    /// </summary>
    public void DrawBuildingImage(SpriteBatch batch, BuildingDefinition definition, Rectangle box, Color playerColor)
    {
        var layers = definition.FinalImages.Select(i => (Sprites: Sprites(i.File, i.Sequence), Frame: i.Image)).ToList();
        var bounds = layers.Select(l => Extent(l.Sprites, l.Frame)).OfType<Rectangle>().ToList();
        if (bounds.Count == 0)
            return;
        var all = bounds.Aggregate(Rectangle.Union);
        // What the eye reads as centered is the building, not body and shadow together: a shadow reaching
        // one side would tilt the body off the middle. The fit still covers the full extent - centered on
        // the body, its wider side beyond the body sets the scale.
        var bodies = layers.Select(l => l.Sprites.Bounds(l.Frame)).OfType<Rectangle>().ToList();
        var body = bodies.Count > 0 ? bodies.Aggregate(Rectangle.Union) : all;
        float middle = body.X + body.Width / 2f;
        float overhang = Math.Max(middle - all.X, all.X + all.Width - middle);
        // The contain-fit leaves this much of the box free, so body and shadow never touch its edges.
        const float FitMargin = 2f;
        float scale = Math.Min(1f, Math.Min((box.Width / 2f - FitMargin) / overhang, (box.Height - 2 * FitMargin) / all.Height));
        var anchor = new Vector2(box.Center.X - middle * scale, box.Bottom - (all.Y + all.Height) * scale);
        foreach (var (sprites, frame) in layers)
            sprites.Draw(batch, frame, anchor, scale, playerColor);
    }

    /// <summary>Body and shadow of a frame together relative to the anchor - the shadow may reach past the body.</summary>
    private static Rectangle? Extent(SettlerSprites sprites, int frame)
    {
        var body = sprites.Bounds(frame);
        var shadow = sprites.ShadowBounds(frame);
        return body is null ? shadow : shadow is null ? body : Rectangle.Union(body.Value, shadow.Value);
    }

    /// <summary>A small pile of one unit of a good fitted into a box (info panel).</summary>
    public void DrawMaterialIcon(SpriteBatch batch, Material material, Rectangle box)
    {
        if (StackSequence(material) is not { } sequence)
            return;
        var sprites = Sprites(PileFile, sequence);
        if (sprites.Bounds(0) is not { } bounds)
            return;
        var (anchor, scale) = SettlerSprites.Fit(bounds, box, 1.5f);
        sprites.Draw(batch, 0, anchor, scale, Color.White);
    }

    /// <summary>Pile graphics in GFX file 01 (one image per pile size). Table from JSettlers' EMaterialType (MIT).</summary>
    private static int? StackSequence(Material material) => material switch
    {
        Material.Axe => 46,
        Material.Bow => 63,
        Material.Blade => 55,
        Material.Bread => 49,
        Material.Coal => 34,
        Material.Crop => 50,
        Material.Fish => 47,
        Material.FishingRod => 66,
        Material.Flour => 48,
        Material.Gold => 37,
        Material.GoldOre => 36,
        Material.Hammer => 51,
        Material.Iron => 42,
        Material.IronOre => 39,
        Material.Meat => 52,
        Material.Pick => 53,
        Material.Pig => 73,
        Material.Plank => 33,
        Material.Saw => 54,
        Material.Scythe => 56,
        Material.Spear => 60,
        Material.Stone => 43,
        Material.Sword => 59,
        Material.Trunk => 41,
        Material.Water => 77,
        Material.Wine => 69,
        Material.Mead => 90,
        Material.Honey => 89,
        Material.Gems => 79,
        Material.Sulfur => 80,
        Material.Rice => 78,
        Material.Keg => 70,
        Material.Liquor => 71,
        Material.GunPowder => 82,
        _ => null,
    };

    private SettlerSprites Sprites(int file, int sequence) => _library.Sequence(file, sequence);
}
