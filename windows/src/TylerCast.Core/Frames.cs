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
    /// <summary>Threads used for one frame (-1: all). Settable for benchmarks.</summary>
    public static int MaxThreads { get; set; } = -1;

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

        // Column lookups depend only on the geometry: worked out once, not per pixel.
        if (_cols is null || _colsFor != (srcW, r.Width))
        {
            _cols = new int[r.Width * 3];
            long sx = ((long)srcW << 16) / r.Width;
            for (var ox = 0; ox < r.Width; ox++)
            {
                var fx = Math.Max(0, ox * sx + sx / 2 - 0x8000);
                var x0 = Math.Min((int)(fx >> 16), srcW - 1);
                _cols[ox * 3] = x0 * 4;
                _cols[ox * 3 + 1] = Math.Min(x0 + 1, srcW - 1) * 4;
                _cols[ox * 3 + 2] = (int)((fx >> 8) & 0xFF);
            }
            _colsFor = (srcW, r.Width);
        }
        var cols = _cols;

        // Pin the spans for the parallel loop (spans can't be captured).
        unsafe
        {
            fixed (byte* src = bgra)
            fixed (byte* dst = nv12)
            fixed (int* cp = cols)
            {
                var s = (nint)src;
                var d = (nint)dst;
                var c = (nint)cp;
                int w = Width, h = Height;
                // Each iteration does one pair of output rows: two luma rows and their chroma row.
                Parallel.For(0, r.Height / 2, new ParallelOptions { MaxDegreeOfParallelism = MaxThreads },
                    pair => ConvertPair((byte*)s, srcH, stride, (byte*)d, w, h, r, (int*)c, pair));
            }
        }
        return r;
    }

    private int[]? _cols;
    private (int, int) _colsFor;

    // Fully optimised from the first frame: tiered compilation would otherwise run the
    // first frames through unoptimised code (measured 43ms/frame on a 2-core runner).
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static unsafe void ConvertPair(byte* src, int srcH, int stride, byte* dst, int w, int h, PixelRect r, int* cols, int pair)
    {
        long sy = ((long)srcH << 16) / r.Height;
        var rw = r.Width;
        // Two rows of (R, G, B) for the chroma average.
        var rgb = stackalloc int[rw * 3 * 2];
        for (var k = 0; k < 2; k++)
        {
            var oy = pair * 2 + k;
            var fy = Math.Max(0, oy * sy + sy / 2 - 0x8000);
            var y0 = Math.Min((int)(fy >> 16), srcH - 1);
            var y1 = Math.Min(y0 + 1, srcH - 1);
            var wy = (int)((fy >> 8) & 0xFF);
            var iwy = 256 - wy;
            var row0 = src + (long)y0 * stride;
            var row1 = src + (long)y1 * stride;
            var yOut = dst + (long)(r.Y + oy) * w + r.X;
            var o = rgb + k * rw * 3;
            for (var ox = 0; ox < rw; ox++)
            {
                int xa = cols[ox * 3], xb = cols[ox * 3 + 1], wx = cols[ox * 3 + 2], iwx = 256 - wx;
                byte* a = row0 + xa, b = row0 + xb, c = row1 + xa, e = row1 + xb;
                var bl = ((a[0] * iwx + b[0] * wx) * iwy + (c[0] * iwx + e[0] * wx) * wy + 32768) >> 16;
                var gr = ((a[1] * iwx + b[1] * wx) * iwy + (c[1] * iwx + e[1] * wx) * wy + 32768) >> 16;
                var rd = ((a[2] * iwx + b[2] * wx) * iwy + (c[2] * iwx + e[2] * wx) * wy + 32768) >> 16;
                var y = (47 * rd + 157 * gr + 16 * bl + 128) / 256 + 16;
                yOut[ox] = (byte)(y > 235 ? 235 : y);
                o[0] = rd; o[1] = gr; o[2] = bl;
                o += 3;
            }
        }
        // Chroma: average of each 2x2 block.
        var uvOut = dst + (long)w * h + (long)(r.Y / 2 + pair) * w + r.X;
        int* t = rgb, u = rgb + rw * 3;
        for (var ox = 0; ox < rw; ox += 2)
        {
            var sr = (t[0] + t[3] + u[0] + u[3] + 2) >> 2;
            var sg = (t[1] + t[4] + u[1] + u[4] + 2) >> 2;
            var sb = (t[2] + t[5] + u[2] + u[5] + 2) >> 2;
            var (_, cb, cr) = ToYCbCr(sr, sg, sb);
            uvOut[ox] = cb;
            uvOut[ox + 1] = cr;
            t += 6;
            u += 6;
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
