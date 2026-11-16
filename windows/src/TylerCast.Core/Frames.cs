namespace TylerCast.Core;

/// <summary>
/// Screen pixels (BGRA, as Windows captures them) to the encoder's NV12 frame:
/// scaled to fit (aspect kept), centred in black, inside the margin, BT.709
/// video range. Bilinear sampling; rows run in parallel. Portable, so it is
/// tested on the Mac; on Windows it replaces what ScreenCaptureKit did on the GPU.
/// </summary>
public sealed class FrameConverter
{
    public int Width { get; }
    public int Height { get; }
    public int FrameBytes => Width * Height * 3 / 2;

    public FrameConverter(int width, int height)
    {
        if (width % 2 != 0 || height % 2 != 0) throw new ArgumentException("NV12 needs even sizes");
        Width = width;
        Height = height;
    }

    /// <summary>BT.709 limited range from 8-bit RGB, fixed point (x256).</summary>
    public static (byte Y, byte Cb, byte Cr) ToYCbCr(int r, int g, int b)
    {
        // Y = 16 + 219·(0.2126R + 0.7152G + 0.0722B)/255; Cb, Cr = 128 + 224·(…)/255
        var y = (47 * r + 157 * g + 16 * b + 128) / 256 + 16;
        var cb = (-26 * r - 87 * g + 112 * b + 128) / 256 + 128;
        var cr = (112 * r - 102 * g - 10 * b + 128) / 256 + 128;
        return ((byte)Math.Clamp(y, 16, 235), (byte)Math.Clamp(cb, 16, 240), (byte)Math.Clamp(cr, 16, 240));
    }

    /// <summary>
    /// Converts one BGRA image (<paramref name="stride"/> bytes per row) into <paramref name="nv12"/>.
    /// Returns where the picture went (the rest is black).
    /// </summary>
    public PixelRect Convert(ReadOnlySpan<byte> bgra, int srcW, int srcH, int stride, Span<byte> nv12, double marginPercent)
    {
        if (nv12.Length < FrameBytes) throw new ArgumentException("output too small");
        var r = Geometry.Fit(srcW, srcH, Width, Height, marginPercent);
        nv12[..(Width * Height)].Fill(16);
        nv12[(Width * Height)..FrameBytes].Fill(128);
        if (r.Width <= 0 || r.Height <= 0) return r;

        // Pin the spans as arrays-of-bytes for the parallel loop (spans can't be captured).
        unsafe
        {
            fixed (byte* src = bgra)
            fixed (byte* dst = nv12)
            {
                var s = (nint)src;
                var d = (nint)dst;
                int w = Width, h = Height;
                // Each iteration does one pair of output rows: two luma rows and their chroma row.
                Parallel.For(0, r.Height / 2, pair => ConvertPair((byte*)s, srcW, srcH, stride, (byte*)d, w, h, r, pair));
            }
        }
        return r;
    }

    private static unsafe void ConvertPair(byte* src, int srcW, int srcH, int stride, byte* dst, int w, int h, PixelRect r, int pair)
    {
        // Fixed-point (16.16) source position of each output pixel centre.
        long sx = ((long)srcW << 16) / r.Width, sy = ((long)srcH << 16) / r.Height;
        Span<int> rgb = stackalloc int[r.Width * 3 * 2];
        for (var k = 0; k < 2; k++)
        {
            var oy = pair * 2 + k;
            var fy = (oy * sy + sy / 2 - 0x8000);
            if (fy < 0) fy = 0;
            var y0 = (int)(fy >> 16);
            var wy = (int)((fy >> 8) & 0xFF);
            var y1 = Math.Min(y0 + 1, srcH - 1);
            y0 = Math.Min(y0, srcH - 1);
            var row0 = src + (long)y0 * stride;
            var row1 = src + (long)y1 * stride;
            var yOut = dst + (long)(r.Y + oy) * w + r.X;
            for (var ox = 0; ox < r.Width; ox++)
            {
                var fx = (ox * sx + sx / 2 - 0x8000);
                if (fx < 0) fx = 0;
                var x0 = (int)(fx >> 16);
                var wx = (int)((fx >> 8) & 0xFF);
                var x1 = Math.Min(x0 + 1, srcW - 1);
                x0 = Math.Min(x0, srcW - 1);
                byte* a = row0 + x0 * 4, b = row0 + x1 * 4, c = row1 + x0 * 4, e = row1 + x1 * 4;
                int Lerp(int i)
                {
                    var top = a[i] * (256 - wx) + b[i] * wx;
                    var bottom = c[i] * (256 - wx) + e[i] * wx;
                    return (top * (256 - wy) + bottom * wy + (1 << 15)) >> 16;
                }
                int bl = Lerp(0), gr = Lerp(1), rd = Lerp(2);
                var (Y, _, _) = ToYCbCr(rd, gr, bl);
                yOut[ox] = Y;
                var o = (k * r.Width + ox) * 3;
                rgb[o] = rd; rgb[o + 1] = gr; rgb[o + 2] = bl;
            }
        }
        // Chroma: average of each 2x2 block.
        var uvOut = dst + (long)w * h + (long)(r.Y / 2 + pair) * w + r.X;
        for (var ox = 0; ox < r.Width; ox += 2)
        {
            int sr = 0, sg = 0, sb = 0;
            for (var k = 0; k < 2; k++)
                for (var j = 0; j < 2; j++)
                {
                    var o = (k * r.Width + ox + j) * 3;
                    sr += rgb[o]; sg += rgb[o + 1]; sb += rgb[o + 2];
                }
            var (_, cb, cr) = ToYCbCr((sr + 2) / 4, (sg + 2) / 4, (sb + 2) / 4);
            uvOut[ox] = cb;
            uvOut[ox + 1] = cr;
        }
    }
}

/// <summary>
/// Draws the mouse pointer into a captured BGRA image. Desktop Duplication hands
/// the pointer over separately, in one of three shapes (the DXGI_OUTDUPL_POINTER_SHAPE_TYPE values).
/// </summary>
public static class CursorPainter
{
    public enum ShapeType { Monochrome = 1, Color = 2, MaskedColor = 4 }

    /// <summary>
    /// Draws <paramref name="shape"/> with its top-left at (<paramref name="x"/>, <paramref name="y"/>) (already
    /// hotspot-adjusted), clipped to the image. For <see cref="ShapeType.Monochrome"/>,
    /// <paramref name="shapeH"/> is the full mask height (AND mask on top of XOR mask: twice the pointer).
    /// </summary>
    public static void Draw(Span<byte> bgra, int w, int h, int stride, ShapeType type, ReadOnlySpan<byte> shape,
        int shapeW, int shapeH, int pitch, int x, int y)
    {
        var rows = type == ShapeType.Monochrome ? shapeH / 2 : shapeH;
        for (var row = 0; row < rows; row++)
        {
            var py = y + row;
            if (py < 0 || py >= h) continue;
            for (var col = 0; col < shapeW; col++)
            {
                var px = x + col;
                if (px < 0 || px >= w) continue;
                var d = bgra.Slice(py * stride + px * 4, 4);
                switch (type)
                {
                    case ShapeType.Color:
                    {
                        var s = shape.Slice(row * pitch + col * 4, 4);
                        int a = s[3];
                        for (var i = 0; i < 3; i++) d[i] = (byte)((s[i] * a + d[i] * (255 - a) + 127) / 255);
                        break;
                    }
                    case ShapeType.MaskedColor:
                    {
                        var s = shape.Slice(row * pitch + col * 4, 4);
                        if (s[3] == 0) { d[0] = s[0]; d[1] = s[1]; d[2] = s[2]; }
                        else { d[0] ^= s[0]; d[1] ^= s[1]; d[2] ^= s[2]; }
                        break;
                    }
                    default:
                    {
                        var bit = (byte)(0x80 >> (col % 8));
                        var and = (shape[row * pitch + col / 8] & bit) != 0;
                        var xor = (shape[(row + rows) * pitch + col / 8] & bit) != 0;
                        for (var i = 0; i < 3; i++)
                        {
                            var v = and ? d[i] : (byte)0;
                            d[i] = xor ? (byte)(v ^ 0xFF) : v;
                        }
                        break;
                    }
                }
            }
        }
    }
}
