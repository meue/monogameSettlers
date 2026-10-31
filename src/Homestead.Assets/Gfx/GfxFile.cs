using System.Buffers.Binary;

namespace Homestead.Assets.Gfx;

/// <summary>
/// Reader for a single GFX <c>.dat</c> file of the original game.
/// </summary>
/// <remarks>
/// Layout (all values little endian):
/// <list type="bullet">
/// <item>Fixed header, color masks at offset 33, file length at 48, then a table of
/// eight offsets pointing to index blocks.</item>
/// <item>Index block: <c>u32 type, u16 byteCount, u16 count, u32[count] offsets</c>.</item>
/// <item>Settler, torso and shadow entries point to a sequence header
/// (<c>02 14 00 00 08 00 00, u8 frameCount, u32[frameCount]</c> relative offsets).
/// Landscape and GUI entries point directly to a bitmap.</item>
/// <item>Bitmaps are run-length encoded rows, see <see cref="DecodeRows"/>.</item>
/// </list>
/// Format knowledge cross-checked against JSettlers (MIT), see THIRD_PARTY_NOTICES.md.
/// </remarks>
public sealed class GfxFile
{
    private const uint Signature = 0x00041304;
    private const int HeaderTableOffset = 52;
    private const int HeaderTableCount = 8;

    private static readonly byte[] SequenceMagic = [0x02, 0x14, 0x00, 0x00, 0x08, 0x00, 0x00];

    private readonly byte[] _data;
    private readonly Dictionary<GfxIndexType, int[]> _indices = new();

    private GfxFile(string path, byte[] data)
    {
        Path = path;
        _data = data;
        ColorFormat = ReadHeader();
        (_torsoForSettler, _shadowForSettler) = AlignLayers(SettlerCount, TorsoCount, ShadowCount);
    }

    private readonly int[] _torsoForSettler;
    private readonly int[] _shadowForSettler;

    public string Path { get; }

    public GfxColorFormat ColorFormat { get; }

    public int LandscapeCount => Index(GfxIndexType.Landscape).Length;
    public int SettlerCount => Index(GfxIndexType.Settler).Length;
    public int TorsoCount => Index(GfxIndexType.Torso).Length;
    public int ShadowCount => Index(GfxIndexType.Shadow).Length;
    public int GuiCount => Index(GfxIndexType.Gui).Length;

    /// <summary>Raw index tables by type, including types we do not decode yet.</summary>
    public IReadOnlyDictionary<GfxIndexType, int[]> Indices => _indices;

    public static GfxFile Open(string path) => new(path, File.ReadAllBytes(path));

    public static bool HasGfxSignature(string path)
    {
        Span<byte> head = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        return stream.ReadAtLeast(head, 4, throwOnEndOfStream: false) == 4
               && BinaryPrimitives.ReadUInt32LittleEndian(head) == Signature;
    }

    public int GetSettlerFrameCount(int sequence) => SequenceFrameCount(GfxIndexType.Settler, sequence);
    public int GetTorsoFrameCount(int sequence) => SequenceFrameCount(GfxIndexType.Torso, sequence);
    public int GetShadowFrameCount(int sequence) => SequenceFrameCount(GfxIndexType.Shadow, sequence);

    public GfxBitmap ReadSettler(int sequence, int frame) =>
        ReadDisplaced(SequenceFrameOffset(GfxIndexType.Settler, sequence, frame), PixelKind.Color);

    public GfxBitmap ReadTorso(int sequence, int frame) =>
        ReadDisplaced(SequenceFrameOffset(GfxIndexType.Torso, sequence, frame), PixelKind.Torso);

    public GfxBitmap ReadShadow(int sequence, int frame) =>
        ReadDisplaced(SequenceFrameOffset(GfxIndexType.Shadow, sequence, frame), PixelKind.Shadow);

    /// <summary>Torso sequence belonging to a settler sequence, or -1 if it has none.</summary>
    public int TorsoSequenceFor(int settlerSequence) => _torsoForSettler[settlerSequence];

    /// <summary>Shadow sequence belonging to a settler sequence, or -1 if it has none.</summary>
    public int ShadowSequenceFor(int settlerSequence) => _shadowForSettler[settlerSequence];

    /// <summary>Torso frame matching a settler frame, or null if there is none.</summary>
    public GfxBitmap? ReadSettlerTorso(int settlerSequence, int frame)
    {
        int torso = TorsoSequenceFor(settlerSequence);
        return torso >= 0 && frame < GetTorsoFrameCount(torso) ? ReadTorso(torso, frame) : null;
    }

    /// <summary>Shadow frame matching a settler frame, or null if there is none.</summary>
    public GfxBitmap? ReadSettlerShadow(int settlerSequence, int frame)
    {
        int shadow = ShadowSequenceFor(settlerSequence);
        return shadow >= 0 && frame < GetShadowFrameCount(shadow) ? ReadShadow(shadow, frame) : null;
    }

    public GfxBitmap ReadLandscape(int index)
    {
        // Header: u16 width, u16 height, u16 unknown.
        int pos = Index(GfxIndexType.Landscape)[index];
        int width = U16(pos);
        int height = U16(pos + 2);
        return DecodeRows(Align(pos + 6), width, height, 0, 0, PixelKind.Color);
    }

    public GfxBitmap ReadGui(int index)
    {
        // Header: u16 width, u16 height, u16 unknown, u16 unknown.
        int pos = Index(GfxIndexType.Gui)[index];
        int width = U16(pos);
        int height = U16(pos + 2);
        return DecodeRows(Align(pos + 8), width, height, 0, 0, PixelKind.Color);
    }

    private GfxColorFormat ReadHeader()
    {
        if (_data.Length < HeaderTableOffset + HeaderTableCount * 4 || U32(0) != Signature)
            throw new InvalidDataException($"{Path}: not a GFX file.");

        GfxColorFormat format = (_data[33], _data[37]) switch
        {
            (0x7c, 0x03) => GfxColorFormat.Rgb555,
            (0xf8, 0x07) => GfxColorFormat.Rgb565,
            var masks => throw new InvalidDataException($"{Path}: unknown color masks {masks}."),
        };

        if (U32(48) != _data.Length)
            throw new InvalidDataException($"{Path}: stored length {U32(48)} does not match file length {_data.Length}.");

        for (int i = 0; i < HeaderTableCount; i++)
        {
            int blockPos = (int)U32(HeaderTableOffset + i * 4);
            var type = (GfxIndexType)U32(blockPos);
            int byteCount = U16(blockPos + 4);
            int count = U16(blockPos + 6);
            if (byteCount != count * 4 + 8)
                continue; // not a plain offset table (header terminator, palette, ...)

            var offsets = new int[count];
            for (int j = 0; j < count; j++)
                offsets[j] = (int)U32(blockPos + 8 + j * 4);
            _indices[type] = offsets;
        }

        return format;
    }

    /// <summary>
    /// The text table of files 07–09 (block <c>0x1904</c>, see <c>docs/formate/gfx.md</c>): per language (0 German,
    /// 1 English, 2 Italian, 3 French, 4 Polish, 5 Spanish, …) all its texts, null-terminated, in the language's Windows code page (1252; Polish 1250). Empty if the file has none or the language is missing.
    /// </summary>
    public IReadOnlyList<string> ReadTexts(int language, int codePage = 1252)
    {
        for (int i = 0; i < HeaderTableCount; i++)
        {
            int blockPos = (int)U32(HeaderTableOffset + i * 4);
            if (blockPos + 12 > _data.Length || U32(blockPos) != TextTable)
                continue;
            int count = U16(blockPos + 8), languages = U16(blockPos + 10);
            if (language < 0 || language >= languages || blockPos + 12 + count * languages * 4 > _data.Length)
                return [];
            var encoding = Encoding(codePage);
            var texts = new string[count];
            for (int entry = 0; entry < count; entry++)
            {
                int start = (int)U32(blockPos + 12 + (language * count + entry) * 4);
                int end = start < _data.Length ? Array.IndexOf(_data, (byte)0, start) : -1;
                texts[entry] = end < 0 ? "" : encoding.GetString(_data, start, end - start);
            }
            return texts;
        }
        return [];
    }

    private const uint TextTable = 0x1904;

    private static System.Text.Encoding Encoding(int codePage)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        return System.Text.Encoding.GetEncoding(codePage);
    }

    /// <summary>
    /// Torso and shadow tables are often shorter than the settler table, and the entries are not
    /// matched by index. Rules (including the per-file special cases, recognized by table sizes)
    /// taken from JSettlers' AdvancedDatFileReader (MIT).
    /// </summary>
    private static (int[] Torso, int[] Shadow) AlignLayers(int settlers, int torsos, int shadows)
    {
        // Torsos: missing entries are at the start.
        var torso = new int[settlers];
        int torsoGap = Math.Max(0, settlers - torsos);
        for (int i = 0; i < settlers; i++)
            torso[i] = i - torsoGap < torsos ? i - torsoGap : -1;

        var shadow = new int[settlers];
        int shadowGap = settlers - shadows;
        if (shadowGap is 7 or 8)
        {
            // Missing entries at the start.
            for (int i = 0; i < settlers; i++)
                shadow[i] = i >= shadowGap ? i - shadowGap : -1;
        }
        else
        {
            // Missing entries at the end.
            for (int i = 0; i < settlers; i++)
                shadow[i] = i < shadows ? i : -1;
        }

        if (shadowGap == 26 && torsos == 0)
        {
            // Amazon buildings (43): in order up to the market place, whose shadow is part of its image;
            // the buildings after it use the shadow one earlier, the shipyard and the harbor the last two.
            // Our own pairing (a shadow fits snugly around its body without covering it); JSettlers treats
            // 43 like 13, which puts the weaponsmith's large shadow under the goldmelt.
            shadow[33] = -1; // market place
            for (int i = 34; i <= 38; i++)
                shadow[i] = i - 1;
            for (int i = 39; i < settlers; i++)
                shadow[i] = -1;
            shadow[47] = 38; // dockyard
            shadow[48] = 39; // harbor
        }
        else if (shadowGap == 11 && settlers is 48 or 50)
        {
            // Egyptian (23) and Asian (33) buildings: in order up to the market place, whose shadow is
            // part of its image; the buildings after it use the shadow one earlier, the shipyard and the
            // harbor the last two. Our own pairing (body and shadow outlines compared); JSettlers keeps them in order.
            int market = settlers == 48 ? 28 : 31, last = settlers == 48 ? 35 : 37;
            shadow[market] = -1;
            for (int i = market + 1; i <= last; i++)
                shadow[i] = i - 1;
            for (int i = last + 1; i < settlers; i++)
                shadow[i] = -1;
            shadow[last + 10] = last; // dockyard
            shadow[last + 11] = last + 1; // harbor
        }
        else if (shadowGap == 26)
        {
            // Roman buildings (13).
            for (int i = 0; i < 27; i++)
                shadow[i] = shadow[i + 3];
            for (int i = 27; i < 36; i++)
                shadow[i] = shadow[i + 2];
            shadow[27] = 30;
            shadow[28] = -1; // market place has its shadow baked in
            shadow[44] = shadow[38]; // dockyard
            shadow[45] = shadow[39]; // harbor
            for (int i = 36; i < 44; i++)
                shadow[i] = -1;
            for (int i = 46; i < settlers; i++)
                shadow[i] = -1;
        }
        else if (settlers == 35 && shadows == 7 || settlers == 10 && shadows == 2)
        {
            // Ships (36; 46 the Amazons'): four sequences per ship (deck, an empty one, the parts in front of
            // the deck, the whole ship without sail), then the sails; shadow k belongs to ship k. The front
            // part is drawn over the deck and gets none.
            for (int i = 0; i < settlers; i++)
                shadow[i] = i < 4 * shadows && i % 4 is 0 or 3 ? i / 4 : -1;
        }
        else if (settlers == 140 && shadows == 107)
        {
            // Map objects (1): in order up to the wine; the water sparkles (26) have none, the decoration,
            // wrecks and stones after them use the shadow one earlier. Further on the order is broken
            // (unused). Our own pairing: frame counts and outlines match.
            shadow[26] = -1;
            for (int i = 27; i <= 31; i++)
                shadow[i] = i - 1;
            for (int i = 32; i < settlers; i++)
                shadow[i] = -1;
        }
        else if (shadowGap == 0 && settlers == 239)
        {
            // Roman specialists (11).
            for (int i = 171; i >= 13; i--)
                shadow[i] = shadow[i - 13];
        }
        else if (shadows == 26 && settlers == 39)
        {
            // Egyptian soldiers (22; JSettlers' ShadowMapping22).
            for (int i = 0; i < settlers; i++)
                shadow[i] = i is 1 or (>= 8 and <= 13) or > 32 ? -1 : i == 0 ? 19 : i < 14 ? i + 18 : i - 14;
        }
        else if (shadows == 26 && settlers == 34)
        {
            // Roman (12) and Asian (32) soldiers, laid out like the Egyptian ones: the soldiers from 9 on
            // (frame counts match shadow for shadow), the sequences 0 and 2–7 at the end. Our own pairing;
            // JSettlers shifts the soldiers by 8, which gives bowmen the shadows of their neighbors.
            // The infantry in tower windows (the last six, also in 22 and 42) have no shadow.
            for (int i = 0; i < settlers; i++)
                shadow[i] = i is 1 or 8 or > 27 ? -1 : i == 0 ? 19 : i < 8 ? i + 18 : i - 9;
        }
        else if (shadows == 26 && settlers == 32)
        {
            // Amazon soldiers (42): the soldiers from 7 on, the sequences 0–6 at the end. Our own pairing
            // (frame counts match); JSettlers' ShadowMapping42 shifts the soldiers by 6.
            for (int i = 0; i < settlers; i++)
                shadow[i] = i > 25 ? -1 : i <= 6 ? i + 19 : i - 7;
        }

        for (int i = 0; i < settlers; i++)
            if (shadow[i] >= shadows)
                shadow[i] = -1;
        return (torso, shadow);
    }

    private int[] Index(GfxIndexType type) => _indices.TryGetValue(type, out var offsets) ? offsets : [];

    private int SequenceFrameCount(GfxIndexType type, int sequence)
    {
        int pos = Index(type)[sequence];
        ExpectSequenceMagic(pos);
        return _data[pos + 7];
    }

    private int SequenceFrameOffset(GfxIndexType type, int sequence, int frame)
    {
        int pos = Index(type)[sequence];
        ExpectSequenceMagic(pos);
        int frameCount = _data[pos + 7];
        if ((uint)frame >= (uint)frameCount)
            throw new ArgumentOutOfRangeException(nameof(frame), $"Sequence {sequence} has {frameCount} frames.");
        return pos + (int)U32(pos + 8 + frame * 4);
    }

    private void ExpectSequenceMagic(int pos)
    {
        if (!_data.AsSpan(pos, SequenceMagic.Length).SequenceEqual(SequenceMagic))
            throw new InvalidDataException($"{Path}: no sequence header at 0x{pos:x}.");
    }

    private GfxBitmap ReadDisplaced(int pos, PixelKind kind)
    {
        // Header: u32 0x0c, u16 width, u16 height, s16 offsetX, s16 offsetY.
        if (U32(pos) != 0x0c)
            throw new InvalidDataException($"{Path}: no bitmap header at 0x{pos:x}.");
        int width = U16(pos + 4);
        int height = U16(pos + 6);
        int offsetX = (short)U16(pos + 8);
        int offsetY = (short)U16(pos + 10);
        return DecodeRows(Align(pos + 12), width, height, offsetX, offsetY, kind);
    }

    /// <summary>
    /// Each row is a series of runs. A run starts with a u16 control word:
    /// bits 0-7 = number of pixels that follow, bits 8-14 = transparent pixels to skip first,
    /// bit 15 = last run of this row. Color pixels are u16, torso pixels u8, shadow runs carry no data.
    /// </summary>
    private GfxBitmap DecodeRows(int pos, int width, int height, int offsetX, int offsetY, PixelKind kind)
    {
        var pixels = new uint[width * height];
        for (int y = 0; y < height; y++)
        {
            int x = 0;
            int row = y * width;
            bool lastRun;
            do
            {
                int control = U16(pos);
                pos += 2;
                int length = control & 0xff;
                x += (control >> 8) & 0x7f;
                lastRun = (control & 0x8000) != 0;

                if (x + length > width)
                    throw new InvalidDataException($"{Path}: run exceeds image width in row {y} (data at 0x{pos:x}).");

                for (int i = 0; i < length; i++, x++)
                {
                    switch (kind)
                    {
                        case PixelKind.Color:
                            pixels[row + x] = GfxColor.ToRgba(U16(pos), ColorFormat);
                            pos += 2;
                            break;
                        case PixelKind.Torso:
                            pixels[row + x] = GfxColor.TorsoToRgba(_data[pos]);
                            pos += 1;
                            break;
                        case PixelKind.Shadow:
                            pixels[row + x] = GfxColor.Shadow;
                            break;
                    }
                }
            } while (!lastRun);
        }
        return new GfxBitmap(width, height, offsetX, offsetY, pixels);
    }

    private static int Align(int pos) => (pos + 1) & ~1;

    private ushort U16(int pos) => BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(pos, 2));

    private uint U32(int pos) => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(pos, 4));

    private enum PixelKind
    {
        Color,
        Torso,
        Shadow,
    }
}

public enum GfxIndexType : uint
{
    Settler = 0x106,
    Landscape = 0x2412,
    Palette = 0x2607,
    Torso = 0x3112,
    Shadow = 0x5982,
    Gui = 0x11306,
    Animation = 0x21702,
}
