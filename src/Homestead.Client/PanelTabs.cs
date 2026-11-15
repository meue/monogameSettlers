using System.Text.Json;
using Homestead.Assets.Gfx;
using Homestead.Simulation.Buildings;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Homestead.Client;

public enum PanelTab
{
    Build,
    Goods,
    Settlers,

    /// <summary>The computer button's menu (save, quit): no tab pressed, no sub-menus.</summary>
    System,

    /// <summary>The text of the event looked at last (event buttons, space): no tab pressed, no sub-menus.</summary>
    Event,

    /// <summary>Nothing (as in the original after a click into the empty landscape): no tab pressed, an empty panel.</summary>
    None,
}

/// <summary>
/// The main tabs of the blue panel (build, goods, settlers), drawn with the original's buttons over the
/// panel's own, the active one pressed; and for goods and settlers their sub-menus as buttons in the dark
/// bar below, like the build menu's categories (the build tab is <see cref="BuildMenu"/>). Configured in
/// <c>data/ui.json</c> (<c>panelTabs</c>); the sub-menus show placeholders for now. Panel space.
/// </summary>
public sealed class PanelTabs : IDisposable
{
    /// <summary>Main tab buttons: GUI images of file 03.</summary>
    private const int File = 3;

    /// <summary>Where the main tab buttons cover the panel's own (measured on the panel image), 55 × 55 each.</summary>
    private static readonly Point[] TabPositions = [new(10, 9), new(80, 9), new(150, 9)];

    private readonly List<Tab> _tabs = new();

    public PanelTabs(GraphicsDevice device, GfxDirectory gfx)
    {
        var file = gfx.Open(File);
        Texture2D Image(JsonElement images, int i)
        {
            var bitmap = file.ReadGui(images[i].GetInt32());
            var texture = new Texture2D(device, bitmap.Width, bitmap.Height);
            texture.SetData(bitmap.Pixels);
            return texture;
        }

        using var document = JsonDocument.Parse(System.IO.File.ReadAllText(Path.Combine(BuildingCatalog.DefaultDataDirectory, "ui.json")));
        foreach (var tab in document.RootElement.GetProperty("panelTabs").EnumerateArray())
        {
            var images = tab.GetProperty("tab");
            var menus = tab.TryGetProperty("menus", out var list)
                ? list.EnumerateArray().Select(m => new Menu(m.GetProperty("id").GetString()!, Lang.T("panel.menus." + m.GetProperty("id").GetString()),
                    Image(m.GetProperty("tab"), 0), Image(m.GetProperty("tab"), 1))).ToList()
                : [];
            _tabs.Add(new Tab(Lang.T("panel.tabs." + tab.GetProperty("id").GetString()), Image(images, 0), Image(images, 1), menus));
        }
    }

    public PanelTab Current { get; set; } = PanelTab.Build;

    /// <summary>The sub-menu shown per main tab (index into its menus).</summary>
    private readonly int[] _menu = new int[3];

    /// <summary>The id of the sub-menu shown in the current tab (null for the build tab).</summary>
    public string? CurrentMenu => HasTab && _tabs[(int)Current].Menus is { Count: > 0 } menus ? menus[_menu[(int)Current]].Id : null;

    /// <summary>Whether one of the main tabs is shown (not the computer menu, an event or nothing).</summary>
    private bool HasTab => Current is not (PanelTab.System or PanelTab.Event or PanelTab.None);

    private static Rectangle TabRect(int i) => new(TabPositions[i].X, TabPositions[i].Y, 55, 55);

    /// <summary>The main tab under the point (panel space).</summary>
    public PanelTab? TabAt(Vector2 panel)
    {
        for (int i = 0; i < _tabs.Count && i < TabPositions.Length; i++)
            if (TabRect(i).Contains(panel))
                return (PanelTab)i;
        return null;
    }

    /// <summary>The sub-menu button of the current tab under the point.</summary>
    public int? MenuAt(Vector2 panel)
    {
        if (!HasTab)
            return null;
        var menus = _tabs[(int)Current].Menus;
        for (int i = 0; i < menus.Count; i++)
            if (MenuRect(menus, i).Contains(panel))
                return i;
        return null;
    }

    /// <summary>The name of the main tab or sub-menu under the point (hover text).</summary>
    public string? NameAt(Vector2 panel) =>
        TabAt(panel) is { } tab ? _tabs[(int)tab].Name
        : MenuAt(panel) is { } menu ? _tabs[(int)Current].Menus[menu].Name
        : null;

    /// <summary>Shows the sub-menu of the current tab.</summary>
    public void ShowMenu(int index)
    {
        if (HasTab)
            _menu[(int)Current] = index;
    }

    /// <summary>Shows the sub-menu of the current tab with that id, if it has one.</summary>
    public void ShowMenu(string id)
    {
        if (!HasTab)
            return;
        int index = _tabs[(int)Current].Menus.FindIndex(m => m.Id == id);
        if (index >= 0)
            ShowMenu(index);
    }

    /// <summary>Sub-menu button <paramref name="i"/>: the bar split evenly, the button centered in its part.</summary>
    private static Rectangle MenuRect(List<Menu> menus, int i)
    {
        var bar = BuildMenu.TabBar;
        int part = bar.Width / menus.Count;
        var image = menus[i].Normal;
        return new Rectangle(bar.X + i * part + (part - image.Width) / 2, bar.Y + (bar.Height - image.Height) / 2, image.Width, image.Height);
    }

    /// <summary>The main tabs, the current one pressed.</summary>
    public void DrawTabs(SpriteBatch batch)
    {
        for (int i = 0; i < _tabs.Count && i < TabPositions.Length; i++)
            batch.Draw((PanelTab)i == Current ? _tabs[i].Pressed : _tabs[i].Normal, TabRect(i), Color.White);
    }

    /// <summary>The sub-menus of the current goods or settlers tab, the chosen one's title (unless the menu has headings of its own) and (if it has no content yet) a placeholder.</summary>
    public void DrawContent(SpriteBatch batch, UiFont font, bool placeholder, bool title = true)
    {
        if (!HasTab)
            return;
        var tab = _tabs[(int)Current];
        if (tab.Menus.Count == 0)
            return;
        int chosen = _menu[(int)Current];
        for (int i = 0; i < tab.Menus.Count; i++)
            batch.Draw(i == chosen ? tab.Menus[i].Pressed : tab.Menus[i].Normal, MenuRect(tab.Menus, i), Color.White);
        var content = BuildMenu.Content;
        int y = content.Y;
        if (!title)
            return;
        foreach (string line in font.Wrap(tab.Menus[chosen].Name, content.Width - 8, 16))
        {
            SelectionPanel.Shadowed(batch, font, line, new Vector2(content.X + 4, y), 16);
            y += 19;
        }
        if (placeholder)
            SelectionPanel.Shadowed(batch, font, Lang.T("panel.comingSoon"), new Vector2(content.X + 4, y + 6), 13, new Color(190, 165, 120));
    }

    public void Dispose()
    {
        foreach (var tab in _tabs)
        {
            tab.Normal.Dispose();
            tab.Pressed.Dispose();
            foreach (var menu in tab.Menus)
            {
                menu.Normal.Dispose();
                menu.Pressed.Dispose();
            }
        }
    }

    private sealed record Menu(string Id, string Name, Texture2D Normal, Texture2D Pressed);

    private sealed record Tab(string Name, Texture2D Normal, Texture2D Pressed, List<Menu> Menus);
}

/// <summary>
/// The side panel's sub-menu ids of <c>data/ui.json</c> (<c>panelTabs</c>): the values
/// <see cref="PanelTabs.CurrentMenu"/> returns and the panel menus are keyed under, so the spellings must stay as they are.
/// </summary>
internal static class PanelMenuIds
{
    public const string Stock = "stock";
    public const string Tools = "tools";
    public const string Distribution = "distribution";
    public const string Transport = "transport";
    public const string Overview = "overview";
    public const string Professions = "professions";
    public const string Soldiers = "soldiers";
    public const string Statistics = "statistics";
}
