using Homestead.Assets.Gfx;
using Homestead.Simulation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Homestead.Client;

/// <summary>
/// The original's side panel at the left edge: the minimap in its frame on top, the blue panel below.
/// Graphics from the installation (large size, as the original at 1024 × 768), scaled together to the
/// full height of the window. The menus inside (build menu, building info, …) draw in panel space:
/// unscaled, origin at the top left of the blue panel (<see cref="Begin"/>, <see cref="ToPanel"/>).
/// </summary>
public sealed class SidePanel : IDisposable
{
    /// <summary>GFX file 04, frame 2 (large) of sequence 0 (frame with the minimap), 1 (its right edge over the game view), 2 (panel).</summary>
    private const int File = 4;
    private const int Frame = 2;

    /// <summary>Unscaled width of the panel (and of the menus inside).</summary>
    public const int Width = 216;

    /// <summary>Unscaled height of the minimap frame above the panel.</summary>
    public const int Top = 224;

    /// <summary>Unscaled height of the blue panel: the room for the menus inside.</summary>
    public const int ContentHeight = 544;

    private const int FullHeight = Top + ContentHeight;

    // The minimap's parallelogram (unscaled, measured on the frame): rows slant half a pixel per pixel,
    // left edge x = 113 - y/2, right edge x = 321 - y/2, from y = 6 to 214.
    private const float MapTop = 6, MapBottom = 214, MapLeftAtTop = 113 - MapTop / 2, MapWidth = 208;

    private readonly GraphicsDevice _device;
    private readonly Texture2D _frame;
    private readonly Texture2D _frameRight;
    private readonly Texture2D _panel;

    /// <summary>The slim gold edge with a vine right of the blue panel (sequence 3).</summary>
    private readonly Texture2D _panelRight;
    private readonly BasicEffect _mapEffect;

    /// <summary>Black behind the minimap, a little lower and wider than it: the frame leaves thin gaps below and right of the map.</summary>
    private readonly Texture2D _black;
    private const float BackingBelow = 3, BackingRight = 3;

    public SidePanel(GraphicsDevice device, GfxDirectory gfx)
    {
        _device = device;
        var file = gfx.Open(File);
        _frame = ToTexture(file.ReadSettler(0, Frame));
        _frameRight = ToTexture(file.ReadSettler(1, Frame));
        _panel = ToTexture(file.ReadSettler(2, Frame));
        _panelRight = ToTexture(file.ReadSettler(3, Frame));
        _mapEffect = new BasicEffect(device) { TextureEnabled = true, VertexColorEnabled = false, LightingEnabled = false };
        _black = new Texture2D(device, 1, 1);
        _black.SetData([Color.Black]);
    }

    private Texture2D ToTexture(GfxBitmap bitmap)
    {
        var texture = new Texture2D(_device, bitmap.Width, bitmap.Height);
        texture.SetData(bitmap.Pixels);
        return texture;
    }

    /// <summary>Scale from unscaled panel pixels to the screen: frame and panel fill the window height.</summary>
    public static float Scale(int viewportHeight) => viewportHeight / (float)FullHeight;

    /// <summary>Whether a screen point is on the panel (minimap included; clicks there do not reach the map).</summary>
    public static bool Contains(Vector2 screen, int viewportHeight) => screen.X < Width * Scale(viewportHeight);

    /// <summary>A screen point in panel space (see the class remarks).</summary>
    public static Vector2 ToPanel(Vector2 screen, int viewportHeight) => screen / Scale(viewportHeight) - new Vector2(0, Top);

    /// <summary>A panel point back on the screen (the inverse of <see cref="ToPanel"/>).</summary>
    public static Vector2 ToScreen(Vector2 panel, int viewportHeight) => (panel + new Vector2(0, Top)) * Scale(viewportHeight);

    /// <summary>Starts a sprite batch drawing in panel space.</summary>
    public static void Begin(SpriteBatch batch, int viewportHeight) =>
        batch.Begin(samplerState: SamplerState.LinearClamp, transformMatrix: PanelTransform(viewportHeight));

    private static Matrix PanelTransform(int viewportHeight) =>
        Matrix.CreateTranslation(0, Top, 0) * Matrix.CreateScale(Scale(viewportHeight));

    /// <summary>
    /// Where a world position (<see cref="ScreenProjection.ToWorld(int, int, int)"/>, heights left out) lies on the
    /// minimap, unscaled. The minimap slants like the screen, so this is a plain scaling of both axes.
    /// </summary>
    private static Vector2 OnMinimap(Vector2 world, int mapSize) => new(
        MapLeftAtTop + world.X * MapWidth / (ScreenProjection.TileWidth * mapSize),
        MapTop + world.Y * (MapBottom - MapTop) / (ScreenProjection.RowHeight * mapSize));

    /// <summary>
    /// The grid point under a screen point on the minimap, or <c>null</c> off the minimap; with
    /// <paramref name="clamp"/> the nearest point on the map instead (dragging beyond the edge).
    /// </summary>
    public static GridPoint? MinimapAt(Vector2 screen, int viewportHeight, int mapSize, bool clamp = false)
    {
        var p = screen / Scale(viewportHeight);
        float v = (p.Y - MapTop) / (MapBottom - MapTop);
        float u = (p.X - MapLeftAtTop + v * (MapBottom - MapTop) / 2) / MapWidth;
        if (clamp)
        {
            v = Math.Clamp(v, 0, 0.9999f);
            u = Math.Clamp(u, 0, 0.9999f);
        }
        else if (u is < 0 or >= 1 || v is < 0 or >= 1)
            return null;
        return new GridPoint((int)(u * mapSize), (int)(v * mapSize));
    }

    /// <summary>
    /// Draws the frame with the minimap (<paramref name="minimap"/>: one texel per grid point of a map of
    /// <paramref name="mapSize"/>), the part of the world on screen (<paramref name="view"/>) as a frame on it,
    /// units and military buildings as dots (<paramref name="dots"/>) and the empty panel.
    /// </summary>
    public void Draw(SpriteBatch batch, Texture2D pixel, Texture2D? minimap, int mapSize, RectangleF view,
        IReadOnlyList<MinimapDot>? dots = null)
    {
        var viewport = _device.Viewport;
        float scale = Scale(viewport.Height);
        if (minimap is not null)
            DrawMinimap(minimap, scale);
        batch.Begin(samplerState: SamplerState.LinearClamp, transformMatrix: Matrix.CreateScale(scale));
        // Before the frame: where the view reaches beyond the map, the frame covers it.
        if (minimap is not null)
        {
            if (dots is not null)
                DrawDots(batch, pixel, mapSize, dots);
            DrawViewFrame(batch, pixel, mapSize, view);
        }
        batch.Draw(_frame, Vector2.Zero, Color.White);
        batch.Draw(_frameRight, new Vector2(Width, 0), Color.White);
        batch.Draw(_panel, new Vector2(0, Top), Color.White);
        batch.Draw(_panelRight, new Vector2(Width, Top), Color.White);
        batch.End();
    }

    /// <summary>Unscaled size of a unit's dot on the minimap; a building's is a bigger black square (an occupied military building's with its color inside).</summary>
    private const float DotSize = 2, BuildingDotSize = 4;

    private static void DrawDots(SpriteBatch batch, Texture2D pixel, int mapSize, IReadOnlyList<MinimapDot> dots)
    {
        foreach (var dot in dots)
        {
            var at = OnMinimap(ScreenProjection.ToWorld(dot.At), mapSize);
            if (dot.Building)
            {
                Square(at, BuildingDotSize + 1, Color.Black);
                if (dot.Color != Color.Black)
                    Square(at, BuildingDotSize - 1, dot.Color);
            }
            else
                Square(at, DotSize, dot.Color);
        }

        void Square(Vector2 center, float size, Color color) =>
            batch.Draw(pixel, center - new Vector2(size / 2), null, color, 0, Vector2.Zero, new Vector2(size), SpriteEffects.None, 0);
    }

    private static void DrawViewFrame(SpriteBatch batch, Texture2D pixel, int mapSize, RectangleF view)
    {
        var a = OnMinimap(new Vector2(view.X, view.Y), mapSize);
        var b = OnMinimap(new Vector2(view.X + view.Width, view.Y + view.Height), mapSize);
        // Kept within the minimap's bounding box, the rest would lie on the game view.
        float left = Math.Max(a.X, MapLeftAtTop - (MapBottom - MapTop) / 2), right = Math.Min(b.X, MapLeftAtTop + MapWidth);
        float top = Math.Max(a.Y, MapTop), bottom = Math.Min(b.Y, MapBottom);
        if (right <= left || bottom <= top)
            return;
        void Line(float x, float y, float width, float height) =>
            batch.Draw(pixel, new Vector2(x, y), null, Color.White, 0, Vector2.Zero, new Vector2(width, height), SpriteEffects.None, 0);
        Line(left, top, right - left, 1);
        Line(left, bottom - 1, right - left, 1);
        Line(left, top, 1, bottom - top);
        Line(right - 1, top, 1, bottom - top);
    }

    /// <summary>The map texture on the parallelogram: row y of the map shifts left by half a point per row, as on screen.</summary>
    private void DrawMinimap(Texture2D minimap, float scale)
    {
        var viewport = _device.Viewport;
        _mapEffect.World = Matrix.CreateScale(scale);
        _mapEffect.View = Matrix.Identity;
        _mapEffect.Projection = Matrix.CreateOrthographicOffCenter(0, viewport.Width, viewport.Height, 0, -1, 1);
        DrawParallelogram(_black, MapBottom + BackingBelow, MapWidth + BackingRight);
        DrawParallelogram(minimap, MapBottom, MapWidth);
    }

    /// <summary>A texture on the minimap's parallelogram, from its top down to <paramref name="bottom"/>, <paramref name="width"/> wide (same slant).</summary>
    private void DrawParallelogram(Texture2D texture, float bottom, float width)
    {
        _mapEffect.Texture = texture;
        float shift = (bottom - MapTop) / 2;
        var topLeft = new Vector3(MapLeftAtTop, MapTop, 0);
        var topRight = new Vector3(MapLeftAtTop + width, MapTop, 0);
        var bottomLeft = new Vector3(MapLeftAtTop - shift, bottom, 0);
        var bottomRight = new Vector3(MapLeftAtTop - shift + width, bottom, 0);
        VertexPositionTexture[] quad =
        [
            new(topLeft, new Vector2(0, 0)), new(topRight, new Vector2(1, 0)), new(bottomLeft, new Vector2(0, 1)),
            new(topRight, new Vector2(1, 0)), new(bottomRight, new Vector2(1, 1)), new(bottomLeft, new Vector2(0, 1)),
        ];
        _device.BlendState = BlendState.Opaque;
        _device.RasterizerState = RasterizerState.CullNone;
        _device.DepthStencilState = DepthStencilState.None;
        _device.SamplerStates[0] = SamplerState.LinearClamp;
        foreach (var pass in _mapEffect.CurrentTechnique.Passes)
        {
            pass.Apply();
            _device.DrawUserPrimitives(PrimitiveType.TriangleList, quad, 0, 2);
        }
    }

    public void Dispose()
    {
        _frame.Dispose();
        _frameRight.Dispose();
        _panel.Dispose();
        _panelRight.Dispose();
        _mapEffect.Dispose();
        _black.Dispose();
    }
}

/// <summary>A dot on the minimap: a unit or (<paramref name="Building"/>) a building, black unless an occupied military building.</summary>
public readonly record struct MinimapDot(GridPoint At, Color Color, bool Building = false);
