using System.Runtime.InteropServices;
using static Perch.W;

namespace Perch;

/// DirectWrite text drawn through a Direct2D DC render target straight into our 32bpp premultiplied DIBs.
/// Sharper, properly hinted grayscale text than GDI+ (which also can't draw colour emoji). Each call binds
/// the render target to just the text's pixel rectangle, so it costs tens of microseconds.
/// Raw COM vtable calls keep it AOT-friendly. If anything fails to initialise, Gfx keeps using GDI+ text.
static unsafe class Dw
{
    public static bool On { get; private set; }
    static nint _dw, _d2d, _rt, _brush, _sys, _params;
    static readonly Dictionary<(string, int, float), (nint Fmt, nint Sign)> _fmts = new();
    static readonly Dictionary<string, bool> _has = new(StringComparer.OrdinalIgnoreCase);

    [DllImport("dwrite.dll")] static extern int DWriteCreateFactory(int type, Guid* iid, nint* factory);
    [DllImport("d2d1.dll")] static extern int D2D1CreateFactory(int type, Guid* iid, void* opts, nint* factory);

    static nint* VT(nint o) => *(nint**)o;
    static void Release(nint o) { if (o != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)VT(o)[2])(o); }

    [StructLayout(LayoutKind.Sequential)]
    struct RTProps { public int Type, Format, Alpha; public float DpiX, DpiY; public int Usage, MinLevel; }
    [StructLayout(LayoutKind.Sequential)] struct ColorF { public float R, G, B, A; }
    [StructLayout(LayoutKind.Sequential)] struct Mat { public float M11, M12, M21, M22, Dx, Dy; }
    [StructLayout(LayoutKind.Sequential)] struct RectD { public float L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct Trim { public int Granularity; public uint Delim, DelimCount; }
    [StructLayout(LayoutKind.Sequential)]
    struct Metrics { public float Left, Top, Width, WidthTrailing, Height, LayoutW, LayoutH; public uint Depth, Lines; }

    public static void Init()
    {
        if (Environment.GetEnvironmentVariable("PERCH_GDIPLUS_TEXT") == "1") return;
        try
        {
            Guid iidDw = new("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
            nint dw;
            if (DWriteCreateFactory(0, &iidDw, &dw) != 0 || dw == 0) return;
            _dw = dw;
            nint sys;
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int, int>)VT(_dw)[3])(_dw, &sys, 0) != 0) return;   // GetSystemFontCollection
            _sys = sys;
            Guid iidF = new("06152247-6f50-465a-9245-118bfd3b6007");
            nint f;
            if (D2D1CreateFactory(0, &iidF, null, &f) != 0 || f == 0) return;
            _d2d = f;
            if (!MakeTarget()) return;
            On = true;
        }
        catch { On = false; }
    }

    static bool MakeTarget()
    {
        Release(_brush); Release(_rt); _brush = _rt = 0;
        // software target (we draw into CPU memory), BGRA premultiplied, 96 dpi: one unit = one pixel
        var p = new RTProps { Type = 1, Format = 87, Alpha = 1, DpiX = 96, DpiY = 96 };
        nint rt;
        if (((delegate* unmanaged[Stdcall]<nint, RTProps*, nint*, int>)VT(_d2d)[16])(_d2d, &p, &rt) != 0 || rt == 0) return false;   // CreateDCRenderTarget
        _rt = rt;
        var c = new ColorF { A = 1 };
        nint br;
        if (((delegate* unmanaged[Stdcall]<nint, ColorF*, void*, nint*, int>)VT(_rt)[8])(_rt, &c, null, &br) != 0) return false;   // CreateSolidColorBrush
        _brush = br;
        ((delegate* unmanaged[Stdcall]<nint, int, void>)VT(_rt)[34])(_rt, 2);   // SetTextAntialiasMode(GRAYSCALE): correct on alpha
        if (_params == 0)
        {   // natural symmetric, a touch more contrast than the default: crisp light-on-dark text
            nint rp;
            if (((delegate* unmanaged[Stdcall]<nint, float, float, float, int, int, nint*, int>)VT(_dw)[12])(_dw, 1.8f, 0.75f, 0f, 0, 5, &rp) == 0) _params = rp;   // CreateCustomRenderingParams
        }
        if (_params != 0) ((delegate* unmanaged[Stdcall]<nint, nint, void>)VT(_rt)[36])(_rt, _params);   // SetTextRenderingParams
        return true;
    }

    public static bool HasFamily(string name)
    {
        if (_sys == 0) return false;
        if (_has.TryGetValue(name, out var b)) return b;
        uint idx; int exists = 0;
        fixed (char* n = name) ((delegate* unmanaged[Stdcall]<nint, char*, uint*, int*, int>)VT(_sys)[5])(_sys, n, &idx, &exists);   // FindFamilyName
        return _has[name] = exists != 0;
    }

    /// A text format for (first installed family of `families`, weight, size in DIP). Cached for the process.
    public static (nint Fmt, nint Sign) Format(string[] families, int weight, float size)
    {
        string fam = families[^1];
        foreach (var f in families) if (HasFamily(f)) { fam = f; break; }
        if (_fmts.TryGetValue((fam, weight, size), out var v)) return v;
        nint fmt = 0, sign = 0;
        fixed (char* n = fam, loc = "en-us")
            ((delegate* unmanaged[Stdcall]<nint, char*, nint, int, int, int, float, char*, nint*, int>)VT(_dw)[15])(_dw, n, 0, weight, 0, 5, size, loc, &fmt);   // CreateTextFormat
        if (fmt != 0) ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)VT(_dw)[20])(_dw, fmt, &sign);   // CreateEllipsisTrimmingSign
        return _fmts[(fam, weight, size)] = (fmt, sign);
    }

    static nint Layout(string s, nint fmt, nint sign, float w, float h, int align, bool wrap)
    {
        nint lay = 0;
        fixed (char* p = s)
            ((delegate* unmanaged[Stdcall]<nint, char*, uint, nint, float, float, nint*, int>)VT(_dw)[18])(_dw, p, (uint)s.Length, fmt, w, h, &lay);   // CreateTextLayout
        if (lay == 0) return 0;
        var vt = VT(lay);
        ((delegate* unmanaged[Stdcall]<nint, int, int>)vt[3])(lay, align == 0 ? 0 : align == 1 ? 2 : 1);   // SetTextAlignment: leading / center / trailing
        ((delegate* unmanaged[Stdcall]<nint, int, int>)vt[4])(lay, wrap ? 0 : 2);                          // SetParagraphAlignment: near / center
        ((delegate* unmanaged[Stdcall]<nint, int, int>)vt[5])(lay, wrap ? 0 : 1);                          // SetWordWrapping: wrap / none
        var t = new Trim { Granularity = wrap ? 2 : 1 };                                                       // word / character
        ((delegate* unmanaged[Stdcall]<nint, Trim*, nint, int>)vt[9])(lay, &t, sign);                       // SetTrimming (ellipsis)
        return lay;
    }

    public static float Measure(string s, nint fmt)
    {
        nint lay = Layout(s, fmt, 0, 100000, 1000, 0, false);
        if (lay == 0) return 0;
        Metrics m;
        ((delegate* unmanaged[Stdcall]<nint, Metrics*, int>)VT(lay)[60])(lay, &m);   // GetMetrics
        Release(lay);
        return m.WidthTrailing;
    }

    /// Draw into `dc` (a 32bpp premultiplied DIB of sw x sh pixels). Box and clip in DIP; s = pixels per DIP.
    public static void Draw(nint dc, int sw, int sh, float s, string str, nint fmt, nint sign, uint argb,
        float x, float y, float w, float h, int align, bool wrap, in RectF clip, bool clipped)
    {
        // the pixels the text can touch: its box plus room for overhanging glyphs, cut to the surface and clip
        float bx0 = x - 3, by0 = y - 4, bx1 = x + w + 3, by1 = y + h + 4;
        if (clipped) { bx0 = MathF.Max(bx0, clip.X); by0 = MathF.Max(by0, clip.Y); bx1 = MathF.Min(bx1, clip.X + clip.W); by1 = MathF.Min(by1, clip.Y + clip.H); }
        var r = new RECT
        {
            L = Math.Max(0, (int)MathF.Floor(bx0 * s)), T = Math.Max(0, (int)MathF.Floor(by0 * s)),
            R = Math.Min(sw, (int)MathF.Ceiling(bx1 * s)), B = Math.Min(sh, (int)MathF.Ceiling(by1 * s)),
        };
        if (r.R <= r.L || r.B <= r.T) return;
        nint lay = Layout(str, fmt, sign, w, h, align, wrap);
        if (lay == 0) return;
        try
        {
            var vt = VT(_rt);
            if (((delegate* unmanaged[Stdcall]<nint, nint, RECT*, int>)vt[57])(_rt, dc, &r) != 0) return;   // BindDC
            ((delegate* unmanaged[Stdcall]<nint, void>)vt[48])(_rt);                                         // BeginDraw
            var m = new Mat { M11 = s, M22 = s, Dx = -r.L, Dy = -r.T };
            ((delegate* unmanaged[Stdcall]<nint, Mat*, void>)vt[30])(_rt, &m);                              // SetTransform
            var c = new ColorF { A = (argb >> 24) / 255f, R = (argb >> 16 & 255) / 255f, G = (argb >> 8 & 255) / 255f, B = (argb & 255) / 255f };
            ((delegate* unmanaged[Stdcall]<nint, ColorF*, void>)VT(_brush)[8])(_brush, &c);                  // SetColor
            ulong origin = (ulong)BitConverter.SingleToUInt32Bits(x) | (ulong)BitConverter.SingleToUInt32Bits(y) << 32;
            // ENABLE_COLOR_FONT (emoji) | CLIP for wrapped text (whole box)
            ((delegate* unmanaged[Stdcall]<nint, ulong, nint, nint, int, void>)vt[28])(_rt, origin, lay, _brush, 4 | (wrap ? 2 : 0));   // DrawTextLayout
            int hr = ((delegate* unmanaged[Stdcall]<nint, ulong*, ulong*, int>)vt[49])(_rt, null, null);     // EndDraw
            if (hr == unchecked((int)0x8899000C)) MakeTarget();                                               // D2DERR_RECREATE_TARGET
            GdiFlush();
        }
        finally { Release(lay); }
    }
}
