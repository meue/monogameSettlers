using System.Text.Json;
using Homestead.Assets.Gfx;
using Homestead.Simulation;
using Homestead.Simulation.Commands;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Magic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Homestead.Client;

/// <summary>
/// Siedler › Krieger in the blue panel, like the original's: the combat strength ("Offensiv"), the divine
/// upgrade (progress of the mana earned towards the next one, one button per soldier type – its weapon plain
/// or upgraded, dark while it cannot be upgraded), the switch between automatic and manual recruitment with
/// the carriers free to become soldiers, and only for manual recruitment per soldier type the weapons in
/// stock and the soldiers planned (arrows: 5 or 1 more or fewer). Graphics in <c>data/ui.json</c>
/// (<c>soldiersMenu</c>). Panel space.
/// </summary>
public sealed class SoldiersMenu : IDisposable
{
    private static readonly SoldierType[] Types = [SoldierType.Swordsman, SoldierType.Bowman, SoldierType.Pikeman];
    private static readonly int[] ArrowSteps = [5, 1, -1, -5];
    private static readonly Color Text = new(240, 225, 190);
    private static readonly Color Label = new(190, 165, 120);

    private readonly Dictionary<SoldierType, Texture2D[]> _upgrades = new();
    private readonly Texture2D[] _arrows;
    private readonly Dictionary<SoldierType, Texture2D> _weapons = new();

    public SoldiersMenu(GraphicsDevice device, GfxDirectory gfx, Civilization people)
    {
        Texture2D Load(GfxFile file, int index)
        {
            var bitmap = file.ReadGui(index);
            var texture = new Texture2D(device, bitmap.Width, bitmap.Height);
            texture.SetData(bitmap.Pixels);
            return texture;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(BuildingCatalog.DefaultDataDirectory, "ui.json")));
        var menu = document.RootElement.GetProperty("soldiersMenu");
        var gui = gfx.Open(3);
        foreach (var type in Types)
            _upgrades[type] = menu.GetProperty("upgrades").GetProperty(Key(type)).EnumerateArray().Select(i => Load(gui, i.GetInt32())).ToArray();
        _arrows = menu.GetProperty("arrows").EnumerateArray().Select(i => Load(gui, i.GetInt32())).ToArray();
        var weapons = menu.GetProperty("weapons").GetProperty(people.ToString());
        var own = gfx.Open(weapons.GetProperty("file").GetInt32());
        foreach (var type in Types)
            _weapons[type] = Load(own, weapons.GetProperty(Key(type)).GetInt32());
    }

    private static string Key(SoldierType type) => type.ToString().ToUpperInvariant();

    // Layout (panel space), top down in the content area.
    private static int Left => BuildMenu.Content.X + 4;
    private static int Width => BuildMenu.Content.Width - 8;
    private static int Top => BuildMenu.Content.Y + 22; // below the title
    private static int OffensiveY => Top;
    private static int DivineY => Top + 20;
    private static Rectangle UpgradeButton(int i) => new(Left + i * (Width / 3) + (Width / 3 - 60) / 2, Top + 42, 60, 40);
    private static int SwitchY => Top + 92;
    private static Rectangle SwitchButton => new(Left + 70, SwitchY + 16, Width - 70, 28);
    private static int RecruitY => SwitchY + 70;
    private const int RowHeight = 34;
    private static Rectangle WeaponIcon(int row) => new(Left, RecruitY + 20 + row * RowHeight, 30, 30);

    /// <summary>The arrows of a row: double up, up, down, double down; the numbers between them.</summary>
    private static Rectangle Arrow(int row, int i)
    {
        int x = Left + 64 + i * 22 + (i >= 2 ? 32 : 0);
        return new Rectangle(x, RecruitY + 20 + row * RowHeight, 20, 30);
    }

    public void Draw(SpriteBatch batch, Texture2D pixel, UiFont font, World world, int player, Portraits portraits, SettlerRenderer settlers,
        Civilization people, Vector2 cursor)
    {
        var military = world.Military;
        var magic = world.Magic;
        Write(batch, font, Lang.T("soldiersMenu.offensive", $"{military.CombatStrength(player) * 100:0}"), new Vector2(Left, OffensiveY), Text, 15);

        // Divine upgrade: how far the mana earned is on the way to the next upgrade.
        int earned = magic.ManaEarned(player);
        int progress = magic.UpgradeThreshold(player) is { } next && next > 0 ? Math.Min(100, earned * 100 / next) : 100;
        Write(batch, font, Lang.T("soldiersMenu.divine", progress), new Vector2(Left, DivineY), Text, 13);
        for (int i = 0; i < Types.Length; i++)
        {
            var type = Types[i];
            bool upgraded = magic.SoldierLevel(player, type) >= 2;
            bool possible = CanUpgrade(world, player, type);
            var images = _upgrades[type];
            var rect = UpgradeButton(i);
            batch.Draw(images[(upgraded ? 2 : 0) + (possible ? 0 : 1)], rect, Color.White);
            if (possible && rect.Contains(cursor))
                batch.Draw(pixel, rect, Color.White * 0.12f);
        }

        // The switch: the carriers free to become soldiers, automatic or manual.
        var switchButton = SwitchButton;
        portraits.Draw(batch, "BEARER", new Rectangle(Left, SwitchY, 40, 60), settlers, people, PlayerColors.Of(player));
        Write(batch, font, world.Recruitment.Convertible(player).ToString(), new Vector2(Left + 44, SwitchY + 20), Text, 15);
        TextButton.Draw(batch, pixel, font, switchButton, Lang.T(military.IsManualRecruitment(player) ? "soldiersMenu.manual" : "soldiersMenu.automatic"), cursor);
        if (!military.IsManualRecruitment(player))
            return;

        // Manual recruitment: per type the weapons in stock, arrows, the soldiers planned.
        string recruitment = Lang.T("soldiersMenu.recruitment");
        var heading = font.Measure(recruitment, 13);
        Write(batch, font, recruitment, new Vector2(Left + (Width - heading.X) / 2, RecruitY), Label, 13);
        for (int row = 0; row < Types.Length; row++)
        {
            var type = Types[row];
            var icon = WeaponIcon(row);
            batch.Draw(_weapons[type], icon, Color.White);
            Write(batch, font, military.WeaponsInStock(player, type).ToString(), new Vector2(icon.Right + 6, icon.Y + 6), Text);
            for (int i = 0; i < _arrows.Length; i++)
            {
                var arrow = Arrow(row, i);
                batch.Draw(_arrows[i], arrow, Color.White);
                if (arrow.Contains(cursor))
                    batch.Draw(pixel, arrow, Color.White * 0.15f);
            }
            string planned = military.Planned(player, type).ToString();
            var plannedSize = font.Measure(planned);
            float between = (Arrow(row, 1).Right + Arrow(row, 2).X) / 2f;
            Write(batch, font, planned, new Vector2(between - plannedSize.X / 2, icon.Y + 6), Text);
        }
    }

    private static bool CanUpgrade(World world, int player, SoldierType type) =>
        world.Magic.UpgradeOrder(player, type) == UpgradeResult.Done && world.Magic.CanAffordUpgrade(player)
        && world.Buildings.Any(b => b.Exists && b.Player == player && b.Definition.Name == world.Magic.Config.Temple && b.State == BuildingState.Finished);

    /// <summary>A click (panel space): upgrades, switches or plans; returns a message, or null if it hit nothing.</summary>
    public string? Click(Vector2 panel, World world, int player, BuildingInfo names, IssueCommand issue)
    {
        var military = world.Military;
        for (int i = 0; i < Types.Length; i++)
            if (UpgradeButton(i).Contains(panel))
            {
                var type = Types[i];
                string name = names.SoldierName(type);
                issue(new UpgradeSoldiers(player, type), result => (UpgradeResult)result! switch
                {
                    UpgradeResult.Done => $"{name}: new ones become {names.LevelName(world.Magic.SoldierLevel(player, type))}",
                    UpgradeResult.OthersFirst => "all soldier types to level 2 first",
                    UpgradeResult.NoMana => "not enough mana earned yet",
                    UpgradeResult.Highest => $"{name}: highest level already",
                    _ => "a finished small temple is needed",
                });
                return null;
            }
        if (SwitchButton.Contains(panel))
        {
            bool manual = !military.IsManualRecruitment(player);
            issue(new SetManualRecruitment(player, manual));
            UiSounds.Toggle();
            return manual ? "manual recruitment: barracks train only the soldiers planned" : "automatic recruitment";
        }
        if (!military.IsManualRecruitment(player))
            return null;
        for (int row = 0; row < Types.Length; row++)
            for (int i = 0; i < ArrowSteps.Length; i++)
                if (Arrow(row, i).Contains(panel))
                {
                    var type = Types[row];
                    issue(new ChangePlanned(player, type, ArrowSteps[i]), _ => $"{names.SoldierName(type)}: {military.Planned(player, type)} planned");
                    UiSounds.Step(ArrowSteps[i] > 0);
                    return null;
                }
        return null;
    }

    /// <summary>What the element under the point does (hover text), or null.</summary>
    public string? Hover(Vector2 panel, World world, int player, BuildingInfo names)
    {
        var magic = world.Magic;
        for (int i = 0; i < Types.Length; i++)
            if (UpgradeButton(i).Contains(panel))
            {
                var type = Types[i];
                string rank = names.LevelName(magic.SoldierLevel(player, type));
                return magic.UpgradeOrder(player, type) switch
                {
                    UpgradeResult.Highest => $"{names.SoldierName(type)} ({rank}): highest level",
                    UpgradeResult.OthersFirst => $"{names.SoldierName(type)} ({rank}): the others first",
                    _ => $"{names.SoldierName(type)} ({rank}): upgrade at {magic.UpgradeThreshold(player)} mana earned ({magic.ManaEarned(player)} so far)",
                };
            }
        if (SwitchButton.Contains(panel))
            return "switch between automatic and manual recruitment";
        if (world.Military.IsManualRecruitment(player))
            for (int row = 0; row < Types.Length; row++)
                for (int i = 0; i < ArrowSteps.Length; i++)
                    if (Arrow(row, i).Contains(panel))
                        return $"{names.SoldierName(Types[row])}: {ArrowSteps[i]:+0;-0} planned";
        return null;
    }

    private static void Write(SpriteBatch batch, UiFont font, string text, Vector2 position, Color color, int size = 14) =>
        SelectionPanel.Shadowed(batch, font, text, position, size, color);

    public void Dispose()
    {
        foreach (var texture in _upgrades.Values.SelectMany(t => t).Concat(_arrows).Concat(_weapons.Values))
            texture.Dispose();
    }
}
