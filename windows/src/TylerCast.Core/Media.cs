namespace TylerCast.Core;

/// <summary>A pixel rectangle (integer, even-aligned where it matters for 4:2:0).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public double MidX => X + Width / 2.0;
    public double MidY => Y + Height / 2.0;
}

public static class Geometry
{
    /// <summary>
    /// Where the screen goes inside the output frame: aspect kept, centred,
    /// inside a <paramref name="marginPercent"/> black border on each edge, even-sized for 4:2:0.
    /// A 3:2 2940x1912 screen in 1920x1080 is 1660x1080 at x=130 (side bars),
    /// never 1920x1248, which is over H.264 level 4.1's frame size.
    /// </summary>
    public static PixelRect Fit(double sourceWidth, double sourceHeight, int width, int height, double marginPercent)
    {
        var m = Math.Clamp(marginPercent, 0, 20) / 100;
        double boxW = width * (1 - 2 * m), boxH = height * (1 - 2 * m);
        if (sourceWidth <= 0 || sourceHeight <= 0) return new PixelRect(0, 0, width, height);
        var s = Math.Min(boxW / sourceWidth, boxH / sourceHeight);
        int w = (int)(sourceWidth * s) / 2 * 2, h = (int)(sourceHeight * s) / 2 * 2;
        return new PixelRect((width - w) / 4 * 2, (height - h) / 4 * 2, w, h);
    }
}

public sealed record VideoSettings
{
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public int Fps { get; init; } = 25;
    public int Bitrate { get; init; } = 6_000_000;
    /// <summary>Caps bursts (keyframes): at most bitrate·window bits in any window seconds.</summary>
    public double RateWindow { get; init; } = 0.5;
    public double KeyframeSeconds { get; init; } = 2.0;
}

public static class H264
{
    /// <summary>
    /// Length-prefixed NAL units (AVCC/MP4 form) to Annex B start codes, dropping
    /// any access unit delimiter (NAL type 9): the muxer's caller adds its own.
    /// </summary>
    public static byte[] AnnexBFromAvcc(ReadOnlySpan<byte> data, int lengthSize = 4)
    {
        var output = new List<byte>(data.Length + 16);
        var i = 0;
        while (i + lengthSize <= data.Length)
        {
            var n = 0;
            for (var k = 0; k < lengthSize; k++) n = (n << 8) | data[i + k];
            i += lengthSize;
            if (n <= 0 || i + n > data.Length) break;
            if ((data[i] & 0x1F) != 9)
            {
                output.AddRange([0, 0, 0, 1]);
                output.AddRange(data.Slice(i, n));
            }
            i += n;
        }
        return [.. output];
    }
}

public static class Adts
{
    /// <summary>7-byte ADTS header (no CRC): MPEG-4, AAC-LC, 48 kHz, stereo.</summary>
    public static byte[] Header(int payloadBytes)
    {
        var len = payloadBytes + 7;
        const byte profile = 1;   // AAC-LC (object type 2) − 1
        const byte freqIndex = 3; // 48000
        const byte channels = 2;
        return [0xFF, 0xF1, // sync, MPEG-4, layer 0, no CRC
                (byte)((profile << 6) | (freqIndex << 2) | (channels >> 2)),
                (byte)(((channels & 3) << 6) | ((len >> 11) & 0x03)),
                (byte)((len >> 3) & 0xFF),
                (byte)(((len & 0x07) << 5) | 0x1F),
                0xFC]; // buffer fullness 0x7FF (VBR), 1 raw block
    }
}

/// <summary>
/// Colour bars with a moving box, as NV12 (Y plane, then interleaved CbCr),
/// BT.709 video range: tests encode/mux/DLNA with no capture. The same picture
/// as the Swift app's test pattern.
/// </summary>
public sealed class TestPattern
{
    public int Width { get; }
    public int Height { get; }
    private readonly byte[] _template;

    /// <summary>75% bars, BT.709 video range (Y, Cb, Cr).</summary>
    internal static readonly (byte Y, byte Cb, byte Cr)[] Bars =
    [
        (180, 128, 128), (168, 44, 136), (145, 147, 44), (134, 63, 52),
        (63, 193, 204), (51, 109, 212), (28, 212, 120), (16, 128, 128),
    ];

    public TestPattern(int width, int height)
    {
        Width = width;
        Height = height;
        _template = new byte[width * height * 3 / 2];
        Array.Fill(_template, (byte)16, 0, width * height);
        Array.Fill(_template, (byte)128, width * height, width * height / 2);
        for (var y = 0; y < height * 2 / 3; y++)
            for (var x = 0; x < width; x++)
                _template[y * width + x] = Bars[x * 8 / width].Y;
        var uv = width * height;
        for (var y = 0; y < height / 3; y++)
            for (var x = 0; x < width; x += 2)
            {
                var b = Bars[x * 8 / width];
                _template[uv + y * width + x] = b.Cb;
                _template[uv + y * width + x + 1] = b.Cr;
            }
    }

    /// <summary>Frame <paramref name="index"/> into <paramref name="nv12"/> (Width·Height·3/2 bytes, stride = Width).</summary>
    public void Render(int index, Span<byte> nv12)
    {
        _template.CopyTo(nv12);
        // A white box crossing the bottom third once every 4 seconds (100 frames at 25fps).
        var box = Height / 6;
        var x0 = (index % 100) * (Width - box) / 100 / 2 * 2;
        var y0 = Height * 3 / 4 - box / 2;
        for (var y = y0; y < y0 + box; y++) nv12.Slice(y * Width + x0, box).Fill(235);
    }

    public byte[] Frame(int index)
    {
        var f = new byte[_template.Length];
        Render(index, f);
        return f;
    }
}
