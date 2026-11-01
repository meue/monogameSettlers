using Homestead.Simulation;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Economy;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Homestead.Client;

/// <summary>
/// Groups 1-9 of selected settlers or ships (Ctrl+number stores the selection, number or a click on its icon
/// selects it again; while the selection is exactly a group, what Shift adds joins it). Each group that has members
/// shows as an icon at the top right: the portrait of its first member (as in the selection panel; a ship's button
/// image), the number and how many are left. All icons
/// are as high as a portrait; wider images (war machines, ships) make theirs wider.
/// </summary>
public sealed class ControlGroups
{
    public const int Count = 9;
    private const int Gap = 4;
    private const int Margin = 8;

    /// <summary>The local player: only its settlers and ships stay in groups, portraits in its color.</summary>
    private readonly int _player;

    public ControlGroups(int player) => _player = player;

    /// <summary>The size of a ship's button image, its icon's proportions.</summary>
    private static readonly Point ShipSize = new(60, 40);

    /// <summary>The size of a settler's portrait image (war machines: wider than the others).</summary>
    public static Func<Settler, Point> PortraitSize { get; set; } = _ => Portraits.Size;

    /// <summary>Draws a ship's button image into the box.</summary>
    public static Action<SpriteBatch, Ship, Rectangle>? DrawShip { get; set; }

    private readonly List<Settler>[] _groups = Enumerable.Range(0, Count).Select(_ => new List<Settler>()).ToArray();
    private readonly List<Ship>[] _shipGroups = Enumerable.Range(0, Count).Select(_ => new List<Ship>()).ToArray();

    /// <summary>
    /// Stores the selection as group <paramref name="index"/> (an empty selection clears it). A settler or ship is in one
    /// group at most: the members leave their other groups.
    /// </summary>
    public void Store(int index, IEnumerable<Settler> selection, IEnumerable<Ship>? ships = null)
    {
        var members = selection.ToList();
        var moved = members.ToHashSet();
        foreach (var group in _groups)
            group.RemoveAll(moved.Contains);
        var shipMembers = (ships ?? []).ToList();
        var movedShips = shipMembers.ToHashSet();
        foreach (var group in _shipGroups)
            group.RemoveAll(movedShips.Contains);
        _groups[index].Clear();
        _groups[index].AddRange(members);
        _shipGroups[index].Clear();
        _shipGroups[index].AddRange(shipMembers);
    }

    /// <summary>The living members of a group.</summary>
    public IReadOnlyList<Settler> Members(int index) => _groups[index];

    /// <summary>The ships of a group.</summary>
    public IReadOnlyList<Ship> Ships(int index) => _shipGroups[index];

    private int Size(int index) => _groups[index].Count + _shipGroups[index].Count;

    /// <summary>The group with exactly these members (none for an empty selection).</summary>
    public int? Matching(IReadOnlyCollection<Settler> selection, IReadOnlyCollection<Ship> ships)
    {
        if (selection.Count + ships.Count == 0)
            return null;
        for (int i = 0; i < Count; i++)
            if (_groups[i].ToHashSet().SetEquals(selection) && _shipGroups[i].ToHashSet().SetEquals(ships))
                return i;
        return null;
    }

    /// <summary>
    /// The groups for a saved game: the members' ids (ships with an 's' before), groups separated by ';' (empty ones too).
    /// </summary>
    public string Saved() => string.Join(';', Enumerable.Range(0, Count)
        .Select(i => string.Join(',', _groups[i].Select(s => s.Id.ToString()).Concat(_shipGroups[i].Select(s => $"s{s.Id}")))));

    /// <summary>The groups of a saved game (<see cref="Saved"/>); members no longer there are left out.</summary>
    public static ControlGroups Restore(string? saved, int player, IEnumerable<Settler> settlers, IEnumerable<Ship>? ships = null)
    {
        var groups = new ControlGroups(player);
        if (string.IsNullOrEmpty(saved))
            return groups;
        var byId = settlers.ToDictionary(s => s.Id);
        var shipsById = (ships ?? []).ToDictionary(s => s.Id);
        var parts = saved.Split(';');
        for (int i = 0; i < Math.Min(parts.Length, Count); i++)
            foreach (var id in parts[i].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (id.StartsWith('s') && int.TryParse(id[1..], out int s) && shipsById.TryGetValue(s, out var ship) && ship.Player == player)
                    groups._shipGroups[i].Add(ship);
                else if (int.TryParse(id, out int n) && byId.TryGetValue(n, out var settler))
                    groups._groups[i].Add(settler);
            }
        groups.Update();
        return groups;
    }

    /// <summary>The dead, soldiers who enter a building and those won over by a spell leave their groups.</summary>
    public void Update()
    {
        foreach (var group in _groups)
            group.RemoveAll(s => s.IsDead || s.IsInside || s.Player != _player);
    }

    /// <summary>The group whose icon is under the screen point.</summary>
    public int? GroupAt(Vector2 screen, int width, float scale)
    {
        foreach (var (index, rect) in Icons(width, scale))
            if (rect.Contains(screen))
                return index;
        return null;
    }

    /// <param name="scale">The side panel's scale: the icons are as large as the portraits there.</param>
    public void Draw(SpriteBatch batch, Texture2D pixel, int width, float scale, UiFont font, SettlerRenderer settlers,
        Func<Civilization, Portraits> portraits, IReadOnlyCollection<Settler> selection, IReadOnlyCollection<Ship> selectedShips, Vector2 cursor)
    {
        int textSize = (int)MathF.Round(13 * scale);
        foreach (var (index, rect) in Icons(width, scale))
        {
            var group = _groups[index];
            var ships = _shipGroups[index];
            // The selected group: a yellow frame around its portrait.
            if (selection.Count + selectedShips.Count > 0 && group.All(selection.Contains) && ships.All(selectedShips.Contains))
            {
                var frame = rect;
                frame.Inflate(2, 2);
                batch.Draw(pixel, frame, Color.Yellow);
                batch.Draw(pixel, rect, Color.Black);
            }
            if (ships.Count > 0)
                DrawShip?.Invoke(batch, ships[0], rect);
            else if (SettlerRenderer.TypeOf(group[0]) is { } type)
            {
                var people = settlers.PeopleOf(group[0]);
                // As high as the other portraits.
                portraits(people).Draw(batch, type, rect, settlers, people, PlayerColors.Of(_player), scale * Portraits.Size.Y / PortraitSize(group[0]).Y);
            }
            if (rect.Contains(cursor))
                batch.Draw(pixel, rect, Color.White * 0.15f);
            SelectionPanel.Shadowed(batch, font, (index + 1).ToString(), new Vector2(rect.X + 4 * scale, rect.Y + 2 * scale), textSize);
            string count = Size(index).ToString();
            var size = font.Measure(count, textSize);
            SelectionPanel.Shadowed(batch, font, count, new Vector2(rect.Right - size.X - 4 * scale, rect.Bottom - size.Y - 3 * scale), textSize);
        }
    }

    /// <summary>Where the icons end (the cheat menu goes below them).</summary>
    public static int Bottom(float scale) => Margin + (int)MathF.Round(Portraits.Size.Y * scale);

    /// <summary>Icons of the groups with members, in number order, ending at the right edge.</summary>
    private IEnumerable<(int Index, Rectangle Rect)> Icons(int width, float scale)
    {
        int height = (int)MathF.Round(Portraits.Size.Y * scale);
        var used = Enumerable.Range(0, Count).Where(i => Size(i) > 0)
            .Select(i => (Index: i, Width: (int)MathF.Round(height * Aspect(i)))).ToList();
        int x = width - Margin - used.Sum(u => u.Width + Gap) + Gap;
        foreach (var (index, iconWidth) in used)
        {
            yield return (index, new Rectangle(x, Margin, iconWidth, height));
            x += iconWidth + Gap;
        }
    }

    /// <summary>Width by height of a group's image: its first ship's button, else its first member's portrait.</summary>
    private float Aspect(int index)
    {
        var size = _shipGroups[index].Count > 0 ? ShipSize : PortraitSize(_groups[index][0]);
        return size.X / (float)size.Y;
    }
}
