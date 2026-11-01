using Homestead.Assets.Maps;
using Homestead.Simulation.Map;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Homestead.Client;

/// <summary>
/// The minimap's picture: one texel per grid point, landscape colors (<see cref="MapOverview"/>) where
/// the local player has explored, black elsewhere; territory borders white for it, turquoise for its allies, red for everyone else (as the dots). Units and military
/// buildings are dots on top (<see cref="SidePanel"/>).
/// </summary>
public sealed class Minimap : IDisposable
{
    private readonly uint[] _landscape;
    private readonly uint[] _pixels;

    public Minimap(GraphicsDevice device, MapArea area)
    {
        _landscape = MapOverview.Pixels(area);
        _pixels = new uint[_landscape.Length];
        Texture = new Texture2D(device, area.Size, area.Size);
    }

    public Texture2D Texture { get; }

    /// <summary>
    /// Shows what <paramref name="vision"/> sees now, darker what it has only explored (everything with
    /// <paramref name="all"/>), with the borders of <paramref name="map"/>'s territories: both fields of every
    /// neighbor pair with different owners (two texels, one is lost when the map is scaled down), in the color of
    /// the owned one – white for the vision's player and turquoise for its allies (<paramref name="allied"/>) where explored, red for
    /// everyone else only where seen now.
    /// </summary>
    public void Update(Vision vision, bool all, GameMap map, Func<int, bool> allied)
    {
        int local = vision.Player;
        for (int i = 0; i < _pixels.Length; i++)
            _pixels[i] = all || vision.IsVisible(i) ? _landscape[i] : vision.IsExplored(i) ? Darker(_landscape[i]) : 0xff000000;
        int size = map.Size;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = y * size + x;
                byte owner = map.OwnerAt(i);
                // Three of the six neighbors: the other three see this field as their neighbor.
                if (x + 1 < size)
                    Border(i, owner, i + 1);
                if (y + 1 < size)
                    Border(i, owner, i + size);
                if (x + 1 < size && y + 1 < size)
                    Border(i, owner, i + size + 1);
            }
        Texture.SetData(_pixels);

        void Border(int a, byte ownerA, int b)
        {
            byte ownerB = map.OwnerAt(b);
            if (ownerA == ownerB)
                return;
            Mark(a, ownerA != GameMap.NoOwner ? ownerA : ownerB);
            Mark(b, ownerB != GameMap.NoOwner ? ownerB : ownerA);
        }

        void Mark(int i, int player)
        {
            bool ally = player != local && allied(player);
            if (all || (player == local || ally ? vision.IsExplored(i) : vision.IsVisible(i)))
                _pixels[i] = (player == local ? Color.White : ally ? AllyColor : Color.Red).PackedValue;
        }
    }

    /// <summary>Allies of the local player on the minimap: borders and dots.</summary>
    public static readonly Color AllyColor = new(64, 224, 208);

    /// <summary>Half the brightness of a packed color (ABGR), alpha kept.</summary>
    private static uint Darker(uint color) => 0xff000000 | ((color >> 1) & 0x7f7f7f);

    public void Dispose() => Texture.Dispose();
}
