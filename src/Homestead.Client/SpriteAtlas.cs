using System.Collections.Concurrent;
using Homestead.Assets.Gfx;
using Homestead.Client.Neural;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Homestead.Client;

/// <summary>An image packed into an atlas page: the page texture and the image's area on it.</summary>
public readonly record struct AtlasImage(Texture2D Texture, Rectangle Source);

/// <summary>
/// Shelf packing over square pages: the placement math of <see cref="SpriteAtlas"/> without any MonoGame
/// type, so it is testable without a graphics device. One instance per atlas, stateful.
/// </summary>
public sealed class AtlasLayout
{
    private int _x, _y, _shelfHeight;

    public AtlasLayout(int padding = 1) => Padding = padding;

    /// <summary>Transparent border around every image, so sampling never picks up a neighbor.</summary>
    public int Padding { get; }

    public int PageCount { get; private set; }

    /// <summary>
    /// Where an image of this size lands next (its area on a page, the border around it excluded), or null if
    /// it does not fit a page at all.
    /// </summary>
    public Placement? Place(int width, int height)
    {
        int paddedWidth = width + 2 * Padding, paddedHeight = height + 2 * Padding;
        if (paddedWidth > SpriteAtlas.PageSize || paddedHeight > SpriteAtlas.PageSize)
            return null;
        if (_x + paddedWidth > SpriteAtlas.PageSize)
        {
            _x = 0;
            _y += _shelfHeight;
            _shelfHeight = 0;
        }
        if (PageCount == 0 || _y + paddedHeight > SpriteAtlas.PageSize)
        {
            PageCount++;
            _x = _y = _shelfHeight = 0;
        }
        var placement = new Placement(PageCount - 1, _x + Padding, _y + Padding, width, height);
        _x += paddedWidth;
        _shelfHeight = Math.Max(_shelfHeight, paddedHeight);
        return placement;
    }

    /// <summary>An image's area on a page.</summary>
    public readonly record struct Placement(int Page, int X, int Y, int Width, int Height);
}

/// <summary>
/// Packs many small images into a few large textures, so SpriteBatch can draw sprites of different
/// images in one draw call (it starts a new one whenever the texture changes). Images are added
/// as they are first needed, shelf by shelf; a full page starts the next one.
/// </summary>
public sealed class SpriteAtlas : IDisposable
{
    public const int PageSize = 4096;

    private readonly GraphicsDevice _device;
    private readonly List<Texture2D> _pages = new();
    private readonly List<Texture2D> _single = new();
    private readonly AtlasLayout _layout;

    public SpriteAtlas(GraphicsDevice device, int padding = 1)
    {
        _device = device;
        _layout = new AtlasLayout(padding);
    }

    public int PageCount => _pages.Count;

    public AtlasImage Add(GfxBitmap bitmap)
    {
        if (_layout.Place(bitmap.Width, bitmap.Height) is not { } placement)
        {
            // Larger than a page (does not occur in the original's sprites): a texture of its own.
            var texture = new Texture2D(_device, bitmap.Width, bitmap.Height);
            texture.SetData(bitmap.Pixels);
            _single.Add(texture);
            return new AtlasImage(texture, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        }
        while (_pages.Count < _layout.PageCount)
            NewPage();
        var page = _pages[placement.Page];
        var source = new Rectangle(placement.X, placement.Y, placement.Width, placement.Height);
        page.SetData(0, source, bitmap.Pixels, 0, bitmap.Pixels.Length);
        return new AtlasImage(page, source);
    }

    private void NewPage()
    {
        // Cleared explicitly: the padding must be transparent, new GL textures are undefined.
        var page = new Texture2D(_device, PageSize, PageSize);
        page.SetData(new uint[PageSize * PageSize]);
        _pages.Add(page);
    }

    public void Dispose()
    {
        foreach (var texture in _pages.Concat(_single))
            texture.Dispose();
    }
}

/// <summary>
/// The sprite sequences of the GFX files, loaded on first use into one shared <see cref="SpriteAtlas"/>.
/// Shared by all renderers, so each sequence is loaded once and sprites of all kinds batch together.
/// With neural upscaling on, a background worker builds 2x images into a second atlas; <see cref="Pump"/>
/// uploads them on the game's thread, where alone textures may be created.
/// </summary>
public sealed class SpriteLibrary : IDisposable
{
    /// <summary>Per-frame cap on 2x uploads: each one is a texture SetData on the game's thread, a hitch guard.</summary>
    private const int UploadsPerFrame = 16;

    private readonly GraphicsDevice _device;
    private readonly SpriteAtlas _atlas;
    private readonly Dictionary<(int File, int Sequence), SettlerSprites> _sequences = new();
    private readonly SpriteUpscaleState _upscale = new();
    private readonly Func<IUpscaler> _upscalerFactory;
    private readonly ConcurrentQueue<(GfxFile File, int Number, int Sequence)> _pending = new();
    private readonly ConcurrentQueue<(int File, int Sequence, int Frame, SpriteLayerKind Layer, GfxBitmap Bitmap)> _completed = new();
    private readonly HashSet<(int File, int Sequence)> _attempted = new();
    private Thread? _worker;
    private volatile bool _workerRunning;
    private SpriteAtlas? _hiResAtlas;

    public SpriteLibrary(GraphicsDevice device, GfxDirectory gfx) : this(device, gfx, static () => new OnnxUpscaler())
    {
    }

    /// <summary>The upscaler is created on the worker thread, whose model session belongs there: a factory.</summary>
    public SpriteLibrary(GraphicsDevice device, GfxDirectory gfx, Func<IUpscaler> upscalerFactory)
    {
        _device = device;
        _atlas = new SpriteAtlas(device);
        _upscalerFactory = upscalerFactory;
        Gfx = gfx;
    }

    public GfxDirectory Gfx { get; }

    public int PageCount => _atlas.PageCount;

    /// <summary>The session's effective neural-upscale mode: the setting at the start, F4 afterwards.</summary>
    public bool NeuralUpscaleEnabled => _upscale.Enabled;

    /// <summary>
    /// Turns the 2x sprites on or off for the session. On: the sequences loaded so far are queued, new ones
    /// follow as they load, and the background worker builds their 2x images. Off: every layer draws its 1x
    /// image again at once (the attached 2x images stay for the next turn-on).
    /// </summary>
    public void SetNeuralUpscale(bool enabled)
    {
        if (_upscale.Enabled == enabled)
            return;
        _upscale.Enabled = enabled;
        if (!enabled)
            return;
        _hiResAtlas ??= new SpriteAtlas(_device, padding: 2);
        StartWorker();
        foreach (var (file, sequence) in _sequences.Keys)
            QueueForUpscale(file, sequence);
    }

    public SettlerSprites Sequence(int file, int sequence)
    {
        if (!_sequences.TryGetValue((file, sequence), out var sprites))
        {
            _sequences[(file, sequence)] = sprites = new SettlerSprites(_atlas, Gfx.Open(file), sequence, _upscale);
            QueueForUpscale(file, sequence);
        }
        return sprites;
    }

    /// <summary>
    /// Uploads finished 2x images into the 2x atlas and attaches them to their layers. Only the game's (GL)
    /// thread may call this: texture creation and SetData are theirs alone. Returns the number of uploads.
    /// </summary>
    public int Pump(int maxUploadsPerFrame = UploadsPerFrame)
    {
        if (_hiResAtlas is not { } atlas)
            return 0;
        int uploaded = 0;
        while (uploaded < maxUploadsPerFrame && _completed.TryDequeue(out var result))
        {
            if (_sequences.TryGetValue((result.File, result.Sequence), out var sprites))
                sprites.AttachHiRes(result.Frame, result.Layer, atlas.Add(result.Bitmap));
            uploaded++;
        }
        return uploaded;
    }

    public void Dispose()
    {
        if (_worker is not null)
        {
            _workerRunning = false;
            _worker.Join(TimeSpan.FromSeconds(2));
            _worker = null;
        }
        _atlas.Dispose();
        _hiResAtlas?.Dispose();
    }

    /// <summary>A sequence is queued only while the mode is on, and only once: the first attempt's outcome
    /// (attached or skipped) stands, so toggling does not repeat the work.</summary>
    public static bool ShouldQueueUpscale(bool modeEnabled, bool alreadyAttempted) => modeEnabled && !alreadyAttempted;

    private void QueueForUpscale(int file, int sequence)
    {
        if (ShouldQueueUpscale(_upscale.Enabled, _attempted.Contains((file, sequence)))
            && _attempted.Add((file, sequence)))
            _pending.Enqueue((Gfx.Open(file), file, sequence));
    }

    private void StartWorker()
    {
        if (_worker is not null)
            return;
        _workerRunning = true;
        _worker = new Thread(RunWorker) { IsBackground = true, Name = "neural upscale" };
        _worker.Start();
    }

    private void RunWorker()
    {
        using var upscaler = _upscalerFactory();
        while (_workerRunning)
        {
            if (!_pending.TryDequeue(out var job))
            {
                Thread.Sleep(1);
                continue;
            }
            try
            {
                int frames = job.File.GetSettlerFrameCount(job.Sequence);
                for (int frame = 0; frame < frames && _workerRunning; frame++)
                {
                    Upscale(upscaler, job, frame, SpriteLayerKind.Body, job.File.ReadSettler(job.Sequence, frame));
                    Upscale(upscaler, job, frame, SpriteLayerKind.Torso, job.File.ReadSettlerTorso(job.Sequence, frame));
                    Upscale(upscaler, job, frame, SpriteLayerKind.Shadow, job.File.ReadSettlerShadow(job.Sequence, frame));
                }
            }
            catch (Exception)
            {
                // A sequence that cannot be read stays 1x; the worker lives on.
            }
        }
    }

    private void Upscale(IUpscaler upscaler, (GfxFile File, int Number, int Sequence) job, int frame,
        SpriteLayerKind kind, GfxBitmap? bitmap)
    {
        if (bitmap is not { Width: > 0, Height: > 0 })
            return;
        if (upscaler.Upscale(bitmap) is { } high)
            _completed.Enqueue((job.Number, job.Sequence, frame, kind, high));
    }
}
