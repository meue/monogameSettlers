using Homestead.Simulation;
using Homestead.Simulation.Buildings;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Homestead.Client;

/// <summary>
/// The selected units in the blue panel: one portrait per kind (soldier type and level, geologist, priest,
/// scout, pioneer) with how many are selected; at the bottom the player's combat strength away from own land,
/// above it the orders of the selection (<see cref="ActionAt"/>): „Position halten“ for soldiers, „Waren stehlen“
/// for scouts, „Land einnehmen“ for pioneers. Below the spell menu when priests are selected.
/// </summary>
public sealed class SelectionPanel
{
    private const int Columns = 3;
    private const int CellHeight = 80;

    /// <summary>The portraits sit a little below the top of their cell (clear of the bar above).</summary>
    private const int PortraitDrop = 6;

    private const int ActionHeight = 22;

    /// <summary>Whether a war machine takes ammunition (the gong does not): „Munition laden“ is offered for it.</summary>
    public static Func<Settler, bool> UsesAmmo { get; set; } = _ => true;

    /// <summary>The orders offered for the selection, bottom up.</summary>
    private static List<SelectionAction> Actions(IEnumerable<Settler> selection)
    {
        var actions = new List<SelectionAction>();
        if (selection.Any(s => s.Profession == Profession.Soldier && !s.IsDead))
            actions.Add(SelectionAction.Hold);
        if (selection.Any(s => s.Profession == Profession.Scout && !s.IsDead))
            actions.Add(SelectionAction.Steal);
        if (selection.Any(s => s.Profession == Profession.Pioneer && !s.IsDead))
            actions.Add(SelectionAction.Claim);
        if (selection.Any(s => s.Profession == Profession.WarMachine && !s.IsDead && UsesAmmo(s)))
            actions.Add(SelectionAction.Reload);
        return actions;
    }

    private static Rectangle ActionRect(int i) =>
        new(BuildMenu.Content.X + 2, BuildMenu.Content.Bottom - 44 - i * ActionHeight, BuildMenu.Content.Width - 4, 20);

    private static string TextOf(SelectionAction action) => Lang.T(action switch
    {
        SelectionAction.Steal => "selection.steal",
        SelectionAction.Claim => "selection.claim",
        SelectionAction.Reload => "selection.reload",
        _ => "selection.hold",
    });

    /// <summary>The order text under the point (panel space), or null.</summary>
    public static SelectionAction? ActionAt(IEnumerable<Settler> selection, Vector2 panel)
    {
        var actions = Actions(selection);
        for (int i = 0; i < actions.Count; i++)
            if (ActionRect(i).Contains(panel))
                return actions[i];
        return null;
    }

    /// <param name="portraits">The portraits of a people (won-over soldiers keep their people's look).</param>
    /// <param name="own">The own people: its kinds come first among those of the same type.</param>
    /// <param name="player">The local player (the portraits' color).</param>
    /// <param name="top">Where the portraits start (panel space).</param>
    /// <param name="cursor">Mouse in panel space (highlights the order under it).</param>
    public void Draw(SpriteBatch batch, UiFont font, SettlerRenderer settlers, Func<Civilization, Portraits> portraits, Civilization own, int player,
        IReadOnlyCollection<Settler> selection, float combatStrength, int top, Vector2 cursor)
    {
        var actions = Actions(selection);
        var content = BuildMenu.Content;
        // One portrait per kind and people.
        var kinds = selection.Where(s => !s.IsDead).GroupBy(s => (Type: SettlerRenderer.TypeOf(s), People: settlers.PeopleOf(s)))
            .Where(g => g.Key.Type is not null)
            .OrderBy(g => g.Key.Type).ThenBy(g => g.Key.People != own).ThenBy(g => g.Key.People).ToList();
        var (columns, scale) = Layout(kinds.Count, content.Bottom - 22 - actions.Count * ActionHeight - top, content.Width);
        int cellWidth = content.Width / columns, cellHeight = (int)(CellHeight * scale);
        int textSize = Math.Max(9, (int)MathF.Round(14 * scale));
        for (int i = 0; i < kinds.Count; i++)
        {
            var cell = new Rectangle(content.X + i % columns * cellWidth, top + i / columns * cellHeight, cellWidth, cellHeight);
            var portrait = new Rectangle(cell.X, cell.Y + (int)(PortraitDrop * scale), cell.Width, (int)(Portraits.Size.Y * scale));
            var (type, people) = kinds[i].Key;
            portraits(people).Draw(batch, type!, portrait, settlers, people, PlayerColors.Of(player), scale);
            string count = kinds[i].Count().ToString();
            var size = font.Measure(count, textSize);
            Shadowed(batch, font, count, new Vector2(cell.Center.X - size.X / 2, portrait.Bottom), textSize);
        }
        for (int i = 0; i < actions.Count; i++)
        {
            var rect = ActionRect(i);
            Shadowed(batch, font, TextOf(actions[i]), rect.Location.ToVector2(), 14, rect.Contains(cursor) ? new Color(255, 240, 160) : Color.White);
        }
        Shadowed(batch, font, Lang.T("selection.combatStrength", $"{combatStrength * 100:0}"), new Vector2(content.X + 2, content.Bottom - 20), 14);
    }

    /// <summary>
    /// How the kinds fit into the space: three columns at full size if they do, else squeezed – more columns
    /// or smaller portraits, whichever keeps them larger.
    /// </summary>
    private static (int Columns, float Scale) Layout(int kinds, int height, int width)
    {
        var best = (Columns: Columns, Scale: 0f);
        for (int columns = Columns; columns <= 2 * Columns; columns++)
        {
            int rows = Math.Max(1, (kinds + columns - 1) / columns);
            float scale = Math.Min(1, Math.Min(height / (float)(rows * CellHeight), (width / columns - 2) / (float)Portraits.Size.X));
            if (scale > best.Scale + 0.001f)
                best = (columns, scale);
        }
        return (best.Columns, Math.Max(0.2f, best.Scale));
    }

    /// <summary>White text with a dark shadow, readable on the blue panel.</summary>
    internal static void Shadowed(SpriteBatch batch, UiFont font, string text, Vector2 position, int size, Color? color = null)
    {
        font.Draw(batch, text, position + new Vector2(1, 1), Color.Black * 0.8f, size);
        font.Draw(batch, text, position, color ?? Color.White, size);
    }
}

/// <summary>Orders in <see cref="SelectionPanel"/>.</summary>
public enum SelectionAction
{
    /// <summary>„Waren stehlen“: scouts steal the nearest goods.</summary>
    Steal,

    /// <summary>„Land einnehmen“: pioneers take land around them.</summary>
    Claim,

    /// <summary>„Position halten“: soldiers stay where they stand and fight only enemies within reach.</summary>
    Hold,

    /// <summary>„Munition laden“: war machines drive to their works and load.</summary>
    Reload,
}
