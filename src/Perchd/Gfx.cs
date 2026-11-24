using static Perch.W;

namespace Perch;

/// Thin GDI+ wrapper drawing into a 32bpp premultiplied DIB that is handed to
/// UpdateLayeredWindow. One brush, one pen, one path — reused for everything.
unsafe sealed class Gfx : IDisposable
{
    const int PARGB = 0x000E200B, UnitPixel = 2, UnitWorld = 0;

    public readonly int W, H;
    public readonly float S;
    public float Alpha = 1;
    /// Vertical offset (DIP) of everything drawn on the main surface: the island floats below the screen edge.
    public float OffY;
    public nint Dc => _dc;
    public readonly nint FUi, FSemi, FSmall, FLabel, FBtn, FMono, FMonoSm, FMonoXs, FNum, FTitle, FBody, FCap, FMonoLg, FMonoXl, FMonoHuge, FTitleLg, FTab;

    readonly nint _dc, _dib, _old, _bmp, _main, _brush, _pen, _path, _fmtL, _fmtC, _fmtR, _fmtW;
    nint _g, _layerBmp, _layerG;   // _g = current target (main surface or the cache layer)

    // ---------------------------------------------------------------- cache layer
    // Static panel chrome (glows, grid, tile frames) is rendered once into this layer and
    // blitted each frame; only live instruments are redrawn. Freed when the panel closes.

    public bool HasLayer => _layerG != 0;

    public void BeginLayer()
    {
        if (_layerG == 0)
        {
            nint b, g;
            GdipCreateBitmapFromScan0(W, H, 0, PARGB, null, &b); _layerBmp = b;
            GdipGetImageGraphicsContext(b, &g); _layerG = g;
            GdipSetSmoothingMode(g, 4); GdipSetPixelOffsetMode(g, 4); GdipSetTextRenderingHint(g, 4);
        }
        _g = _layerG; _tdc = 0;
        GdipGraphicsClear(_g, 0);
        Begin();
        GdipResetWorldTransform(_g);
        GdipScaleWorldTransform(_g, S, S, 0);
    }

    public void EndLayer() { GdipFlush(_layerG, 1); _g = _main; _tdc = _dc; }

    public void DrawLayer(float x, float y, float wDip, float hDip)
    {
        GdipSetInterpolationMode(_g, 5); // nearest: it's a 1:1 pixel copy
        GdipDrawImageRectRect(_g, _layerBmp, x, y, wDip, hDip, 0, 0, wDip * S, hDip * S, 2, 0, 0, 0);
    }

    /// Copy a region of the cached layer back onto the surface (no blending): erases a tile
    /// to its static background so only that tile needs redrawing.
    public void RestoreFromLayer(float x, float y, float w, float h, float ox, float oy)
    {
        GdipSetCompositingMode(_g, 1); // SourceCopy
        GdipSetInterpolationMode(_g, 5);
        GdipDrawImageRectRect(_g, _layerBmp, x, y, w, h, (x - ox) * S, (y - oy) * S, w * S, h * S, 2, 0, 0, 0);
        GdipSetCompositingMode(_g, 0);
    }

    // ---------------------------------------------------------------- frosted backdrop
    // Snapshot of the screen under the panel, taken once when it opens (while only the small
    // island is on screen), shrunk ~10x with HALFTONE averaging, box-blurred and scaled back up
    // with bilinear filtering. Far cheaper than live blur, and the panel is only open briefly.

    nint _bd;
    public bool HasBackdrop => _bd != 0;

    public void CaptureBackdrop(int sx, int sy, int w, int h) => AdoptBackdrop(Snapshot(sx, sy, w, h), w, h);

    int _bdW, _bdH;
    public void AdoptBackdrop(nint bmp, int w, int h) { FreeBackdrop(); _bd = bmp; _bdW = w; _bdH = h; }

    /// 1:1 pixel copies (nearest, unscaled): with a clip set to the island shape this is one cheap
    /// blit, where a texture-brush fill of the same area costs tens of milliseconds in GDI+.
    public void DrawBackdrop(float x, float y) => Blit(_bd, _bdW, _bdH, x, y);
    public void DrawContent(float x, float y) => Blit(_cBmp, _cW, _cH, x, y);

    void Blit(nint bmp, int w, int h, float x, float y)
    {
        if (bmp == 0) return;
        GdipSetInterpolationMode(_g, 5);
        GdipDrawImageRectRect(_g, bmp, x, y, w / S, h / S, 0, 0, w, h, 2, 0, 0, 0);
    }

    public static void DisposeImage(nint img)
    {
        if (img == 0) return;
        GdipDisposeImage(img);
        if (_imgDibs.Remove(img, out var d)) { SelectObject(d.Dc, d.Old); DeleteObject(d.Dib); DeleteDC(d.Dc); }
    }

    // ---------------------------------------------------------------- offscreen images (deck cards)

    nint _offG; float _offY;
    static readonly Dictionary<nint, (nint Dc, nint Dib, nint Old)> _imgDibs = new();

    /// Start drawing into a new bitmap of the given DIP size; everything drawn goes there until EndImage.
    /// The caller owns the returned image. `opaque`: ClearType text (the card face is solid).
    public nint BeginImage(float wDip, float hDip, bool opaque)
    {
        int w = Math.Max(1, (int)MathF.Ceiling(wDip * S)), h = Math.Max(1, (int)MathF.Ceiling(hDip * S));
        nint b, g;
        // backed by a DIB section with its own DC, so DirectWrite text can be drawn into it as well
        nint dc = CreateCompatibleDC(0);
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        void* bits;
        nint dib = CreateDIBSection(dc, &bi, 0, &bits, 0, 0);
        nint old = SelectObject(dc, dib);
        GdipCreateBitmapFromScan0(w, h, w * 4, PARGB, (byte*)bits, &b);
        _imgDibs[b] = (dc, dib, old);
        GdipGetImageGraphicsContext(b, &g);
        GdipSetSmoothingMode(g, 4); GdipSetPixelOffsetMode(g, 4); GdipSetTextRenderingHint(g, opaque ? 5 : 4);
        _offG = g; _g = g; _offY = OffY; OffY = 0;
        _tdc = dc; _tw = w; _th = h;
        Begin();
        return b;
    }

    public void EndImage() { GdipFlush(_offG, 1); GdipDeleteGraphics(_offG); _offG = 0; _g = _main; OffY = _offY; _tdc = _dc; _tw = W; _th = H; }

    /// Draw part of an image (source in its pixels) into a DIP rectangle. interp: 5 nearest, 3 bilinear, 6 HQ bilinear.
    /// No ImageAttributes: any attributes object knocks GDI+ off its fast blit path (~15 ms vs ~0.3 ms for a card).
    public void Image(nint img, float dx, float dy, float dw, float dh, float sx, float sy, float sw, float sh, int interp)
    {
        if (img == 0) return;
        GdipSetInterpolationMode(_g, interp);
        GdipDrawImageRectRect(_g, img, dx, dy, dw, dh, sx, sy, sw, sh, UnitPixel, 0, 0, 0);
    }

    /// An image as a card turned about its vertical axis, centred on (cx, cy), with true perspective
    /// (GDI+'s transforms are affine only). angle > 0 pushes the right edge away. Returns the four
    /// projected corners (TL, TR, BR, BL) in q. Drawn straight into the frame buffer by Warp: GDI+'s own
    /// bilinear scaling costs ~11 ms a card here, which made the deck animate at ~25 fps.
    public void Turned(nint img, int iw, int ih, float cx, float cy, float w, float h, float angleDeg, Span<float> q)
    {
        const float P = 1700;
        float a = angleDeg * MathF.PI / 180, ca = MathF.Cos(a), sa = MathF.Sin(a);
        float X(float u, out float p) { float x0 = (u - 0.5f) * w; p = P / (P + x0 * sa); return cx + x0 * ca * p; }
        float xl = X(0, out float pl), xr = X(1, out float pr);
        q[0] = xl; q[1] = cy - h * pl / 2; q[6] = xl; q[7] = cy + h * pl / 2;
        q[2] = xr; q[3] = cy - h * pr / 2; q[4] = xr; q[5] = cy + h * pr / 2;
        if (img == 0 || iw < 2 || ih < 2 || h <= 0 || xr - xl < 0.5f || _g != _main) return;

        GdipFlush(_g, 1);   // everything GDI+ has drawn so far is in _bits before we write over it
        var rect = new GpRect { W = iw, H = ih };
        GpBitmapData bd;
        if (GdipBitmapLockBits(img, &rect, 1, PARGB, &bd) != 0) return;   // ImageLockModeRead
        try { Warp((uint*)bd.Scan0, bd.Stride / 4, iw, ih, cx, cy, w, h, ca, sa, xl * S, xr * S); }
        finally { GdipBitmapUnlockBits(img, &bd); }
    }

    /// Perspective-map a premultiplied image onto the frame buffer, column by column: each destination
    /// column is one source column u (inverse projection), scaled vertically by its depth. Bilinear
    /// sampling, anti-aliased edges (fractional coverage), premultiplied source-over.
    void Warp(uint* src, int sstr, int iw, int ih, float cx, float cy, float w, float h, float ca, float sa, float XL, float XR)
    {
        const float P = 1700;
        float s = S, pcy = (cy + OffY) * s;
        uint* dst = (uint*)_bits;
        int x0 = Math.Max(0, (int)MathF.Floor(XL)), x1 = Math.Min(W, (int)MathF.Ceiling(XR));
        for (int px = x0; px < x1; px++)
        {
            float covx = MathF.Min(px + 1, XR) - MathF.Max(px, XL);
            if (covx <= 0) continue;
            // inverse of X(u): screen x -> position on the card (DIP from its centre) -> u, depth p
            float dx = (px + 0.5f) / s - cx, xc = dx * P / (ca * P - dx * sa);
            float u = Math.Clamp(xc / w + 0.5f, 0, 1), p = P / (P + xc * sa);
            float sxf = Math.Clamp(u * iw - 0.5f, 0, iw - 1.001f);
            int ix = (int)sxf; uint fx = (uint)((sxf - ix) * 256);
            float half = h * p * 0.5f * s, top = pcy - half, bot = pcy + half, k = ih / (bot - top);
            int y0 = Math.Max(0, (int)MathF.Floor(top)), y1 = Math.Min(H, (int)MathF.Ceiling(bot));
            uint* col = src + ix;
            uint* d = dst + y0 * W + px;
            for (int py = y0; py < y1; py++, d += W)
            {
                float cov = covx * (MathF.Min(py + 1, bot) - MathF.Max(py, top));
                if (cov <= 0) continue;
                float syf = Math.Clamp((py + 0.5f - top) * k - 0.5f, 0, ih - 1.001f);
                int iy = (int)syf; uint fy = (uint)((syf - iy) * 256);
                uint* r = col + iy * sstr;
                uint c = Mix(Mix(r[0], r[1], fx), Mix(r[sstr], r[sstr + 1], fx), fy);
                if (cov < 0.996f) c = Mix(0, c, (uint)(cov * 256));
                uint ia = 256 - (c >> 24);
                *d = c + Mix(0, *d, ia);
            }
        }
    }

    /// Per-channel a + (b - a) * f / 256 on packed 8-bit ARGB, f in 0..256.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    static uint Mix(uint a, uint b, uint f)
    {
        uint g = 256 - f;
        uint rb = (((a & 0xFF00FF) * g + (b & 0xFF00FF) * f) >> 8) & 0xFF00FF;
        uint ag = (((a >> 8) & 0xFF00FF) * g + ((b >> 8) & 0xFF00FF) * f) & 0xFF00FF00;
        return rb | ag;
    }

    /// Fill a closed polygon given as x,y pairs.
    public void FillPoly(ReadOnlySpan<float> pts, uint c)
    {
        GdipResetPath(_path);
        fixed (float* p = pts) GdipAddPathLine2(_path, p, pts.Length / 2);
        GdipClosePathFigure(_path);
        Fill(c);
    }

    public void FillRect(float x, float y, float w, float h, uint c)
    {
        GdipResetPath(_path);
        GdipAddPathRectangle(_path, x, y, w, h);
        Fill(c);
    }

    /// Blurred, darkened full-size copy of a screen rectangle as a GDI bitmap (for the scrim window,
    /// which just BitBlts it). Darker towards the bottom, where the cards float. Worker-thread safe.
    public static nint ScrimBitmap(int sx, int sy, int w, int h)
    {
        if (w < 8 || h < 8) return 0;
        const int D = 12;
        int sw = Math.Max(3, w / D), sh = Math.Max(3, h / D);
        nint scr = GetDC(0);
        if (scr == 0) return 0;
        nint mdc = CreateCompatibleDC(scr);
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = sw, biHeight = -sh, biPlanes = 1, biBitCount = 32 };
        void* bits;
        nint dib = CreateDIBSection(mdc, &bi, 0, &bits, 0, 0);
        nint old = SelectObject(mdc, dib);
        SetStretchBltMode(mdc, 4);
        SetBrushOrgEx(mdc, 0, 0, null);
        bool ok = StretchBlt(mdc, 0, 0, sw, sh, scr, sx, sy, w, h, 0x00CC0020) != 0;
        GdiFlush();
        nint result = 0;
        if (ok)
        {
            uint* px = (uint*)bits;
            BoxBlur(px, sw, sh); BoxBlur(px, sw, sh); BoxBlur(px, sw, sh);
            for (int y = 0; y < sh; y++)
            {
                float k = 0.70f - 0.32f * y / sh;   // light dim at the top, darker behind the cards
                for (int x = 0; x < sw; x++)
                {
                    uint c = px[y * sw + x];
                    uint r = (uint)((c >> 16 & 255) * k) + 3, g = (uint)((c >> 8 & 255) * k) + 4, b = (uint)((c & 255) * k) + 7;
                    px[y * sw + x] = 0xFF000000 | r << 16 | g << 8 | b;
                }
            }
            var bb = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            void* bigBits;
            nint big = CreateDIBSection(scr, &bb, 0, &bigBits, 0, 0);
            if (big != 0)
            {
                nint small, gb, g;
                GdipCreateBitmapFromScan0(sw, sh, sw * 4, PARGB, (byte*)bits, &small);
                GdipCreateBitmapFromScan0(w, h, w * 4, PARGB, (byte*)bigBits, &gb);
                GdipGetImageGraphicsContext(gb, &g);
                GdipSetInterpolationMode(g, 6);
                GdipSetPixelOffsetMode(g, 4);
                GdipDrawImageRectRect(g, small, -w / (float)sw, -h / (float)sh, w + 2 * w / (float)sw, h + 2 * h / (float)sh, 0, 0, sw, sh, UnitPixel, 0, 0, 0);
                GdipDeleteGraphics(g); GdipDisposeImage(gb); GdipDisposeImage(small);
                result = big;
            }
        }
        ReleaseDC(0, scr);
        SelectObject(mdc, old); DeleteObject(dib); DeleteDC(mdc);
        return result;
    }

    /// Picture of a window (even when covered) shrunk to w x h pixels, cropped to fill. 0 if it can't
    /// be captured (minimized, gone). Worker-thread safe.
    public static nint CaptureWindow(nint hwnd, int w, int h)
    {
        RECT r;
        if (IsIconic(hwnd) != 0 || GetWindowRect(hwnd, &r) == 0) return 0;
        int ww = r.R - r.L, wh = r.B - r.T;
        if (ww < 50 || wh < 50 || w < 4 || h < 4) return 0;
        nint scr = GetDC(0);
        nint wdc = CreateCompatibleDC(scr), sdc = CreateCompatibleDC(scr);
        nint wbm = CreateCompatibleBitmap(scr, ww, wh), sbm = CreateCompatibleBitmap(scr, w, h);
        nint o1 = SelectObject(wdc, wbm), o2 = SelectObject(sdc, sbm);
        nint result = 0;
        if (PrintWindow(hwnd, wdc, 2) != 0)   // PW_RENDERFULLCONTENT: works for GPU-drawn apps too
        {
            // crop: keep the bottom-left of the window at the card's aspect ratio, where a terminal's
            // newest output and an editor's terminal panel are
            float k = MathF.Max(w / (float)ww, h / (float)wh);
            int cw = (int)(w / k), ch = (int)(h / k);
            SetStretchBltMode(sdc, 4); SetBrushOrgEx(sdc, 0, 0, null);
            StretchBlt(sdc, 0, 0, w, h, wdc, 0, Math.Max(0, wh - ch), cw, ch, 0x00CC0020);
            GdiFlush();
            SelectObject(sdc, o2); o2 = 0;
            nint bmp;
            if (GdipCreateBitmapFromHBITMAP(sbm, 0, &bmp) == 0) result = bmp;
        }
        if (o2 != 0) SelectObject(sdc, o2);
        SelectObject(wdc, o1);
        DeleteObject(wbm); DeleteObject(sbm); DeleteDC(wdc); DeleteDC(sdc);
        ReleaseDC(0, scr);
        return result;
    }

    /// Blurred snapshot of a screen rectangle as a GDI+ bitmap (0 on failure). Touches no shared
    /// state, so it can run on a worker thread.
    public static nint Snapshot(int sx, int sy, int w, int h)
    {
        if (w < 8 || h < 8) return 0;
        nint result = 0;
        const int D = 10;
        int sw = Math.Max(3, w / D), sh = Math.Max(3, h / D);
        nint scr = GetDC(0);
        if (scr == 0) return 0;
        nint mdc = CreateCompatibleDC(scr);
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = sw, biHeight = -sh, biPlanes = 1, biBitCount = 32 };
        void* bits;
        nint dib = CreateDIBSection(mdc, &bi, 0, &bits, 0, 0);
        nint old = SelectObject(mdc, dib);
        SetStretchBltMode(mdc, 4); // HALFTONE: area-averaged downscale
        SetBrushOrgEx(mdc, 0, 0, null);
        bool ok = StretchBlt(mdc, 0, 0, sw, sh, scr, sx, sy, w, h, 0x00CC0020) != 0;
        GdiFlush();
        ReleaseDC(0, scr);
        if (ok)
        {
            uint* px = (uint*)bits;
            BoxBlur(px, sw, sh); BoxBlur(px, sw, sh);
            for (int i = 0; i < sw * sh; i++) px[i] |= 0xFF000000;
            nint small, big, bg;
            GdipCreateBitmapFromScan0(sw, sh, sw * 4, PARGB, (byte*)bits, &small);
            GdipCreateBitmapFromScan0(w, h, 0, PARGB, null, &big);
            GdipGetImageGraphicsContext(big, &bg);
            GdipSetInterpolationMode(bg, 6); // high-quality bilinear
            GdipDrawImageRectRect(bg, small, -w / (float)sw, -h / (float)sh, w + 2 * w / (float)sw, h + 2 * h / (float)sh, 0, 0, sw, sh, UnitPixel, 0, 0, 0);
            GdipDeleteGraphics(bg); GdipDisposeImage(small);
            result = big;
        }
        SelectObject(mdc, old); DeleteObject(dib); DeleteDC(mdc);
        return result;
    }

    static void BoxBlur(uint* p, int w, int h)
    {
        var t = new uint[w * h];
        for (int pass = 0; pass < 2; pass++)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    uint r = 0, gg = 0, b = 0, n = 0;
                    for (int d = -1; d <= 1; d++)
                    {
                        int xx = pass == 0 ? Math.Clamp(x + d, 0, w - 1) : x, yy = pass == 1 ? Math.Clamp(y + d, 0, h - 1) : y;
                        uint c = pass == 0 ? p[yy * w + xx] : t[yy * w + xx];
                        r += c >> 16 & 255; gg += c >> 8 & 255; b += c & 255; n++;
                    }
                    uint o = (r / n) << 16 | (gg / n) << 8 | (b / n);
                    if (pass == 0) t[y * w + x] = o; else p[y * w + x] = o;
                }
    }

    /// Fill the current path with the backdrop, its top-left pixel at (x, y) in DIPs.
    public void FillBackdrop(float x, float y) => FillTex(_bd, x, y);

    /// Fill the current path with a device-pixel bitmap placed at (x, y) DIP. One textured fill
    /// is far cheaper than drawing the same content through a clip region.
    void FillTex(nint bmp, float x, float y)
    {
        if (bmp == 0) return;
        nint b;
        GdipCreateTexture(bmp, 4, &b);   // WrapModeClamp: nothing outside the bitmap
        GdipScaleTextureTransform(b, 1 / S, 1 / S, 1);
        GdipTranslateTextureTransform(b, x, y, 1);
        GdipFillPath(_g, b, _path);
        GdipDeleteBrush(b);
    }

    // ---------------------------------------------------------------- content snapshot
    // The panel's content rendered once when it opens/closes; during the morph the growing shape
    // is filled with it instead of redrawing everything through a clip every frame.

    nint _cBmp, _cG; int _cW, _cH;
    public bool HasContent => _cG != 0;

    public void BeginContent(float wDip, float hDip)
    {
        int w = (int)MathF.Ceiling(wDip * S), h = (int)MathF.Ceiling(hDip * S);
        if (_cG == 0 || w != _cW || h != _cH)
        {
            FreeContent();
            nint b, g;
            GdipCreateBitmapFromScan0(w, h, 0, PARGB, null, &b); _cBmp = b;
            GdipGetImageGraphicsContext(b, &g); _cG = g;
            GdipSetSmoothingMode(g, 4); GdipSetPixelOffsetMode(g, 4); GdipSetTextRenderingHint(g, 4);
            _cW = w; _cH = h;
        }
        _g = _cG; _tdc = 0;
        GdipGraphicsClear(_g, 0);
        Begin();
        GdipResetWorldTransform(_g);
        GdipScaleWorldTransform(_g, S, S, 0);
    }

    public void EndContent() { GdipFlush(_cG, 1); _g = _main; _tdc = _dc; }

    public void FillContent(float x, float y) => FillTex(_cBmp, x, y);

    public void FreeContent()
    {
        if (_cG == 0) return;
        GdipDeleteGraphics(_cG); GdipDisposeImage(_cBmp);
        _cG = _cBmp = 0;
    }

    public void FreeBackdrop()
    {
        if (_bd == 0) return;
        GdipDisposeImage(_bd); _bd = 0;
    }

    public void FreeLayer()
    {
        if (_layerG == 0) return;
        GdipDeleteGraphics(_layerG); GdipDisposeImage(_layerBmp);
        _layerG = _layerBmp = 0;
    }
    readonly byte* _bits;
    nint _tdc; int _tw, _th;                              // where DirectWrite text goes (0: GDI+ text)
    bool _clipShape, _clipRect; RectF _clip;
    readonly Dictionary<nint, (nint Fmt, nint Sign)> _dwf = new();
    readonly List<nint> _fams = new(), _fonts = new();
    readonly Dictionary<(string, nint), float> _measure = new();

    static nint _token;

    public static void Startup()
    {
        GpInput i = new() { Version = 1, NoThread = 1 };
        GpOutput o;
        nint tok;
        GdiplusStartup(&tok, &i, &o);
        _token = tok;
        // With NoThread=1 we must call the notification hook ourselves.
        if (o.Hook != 0) { nint t; ((delegate* unmanaged[Stdcall]<nint*, int>)o.Hook)(&t); }
    }

    public Gfx(float scale, float wDip, float hDip)
    {
        S = scale;
        W = (int)MathF.Ceiling(wDip * scale);
        H = (int)MathF.Ceiling(hDip * scale);

        _dc = CreateCompatibleDC(0); _tdc = _dc; _tw = W; _th = H;
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = W, biHeight = -H, biPlanes = 1, biBitCount = 32 };
        void* bits;
        _dib = CreateDIBSection(_dc, &bi, 0, &bits, 0, 0);
        _bits = (byte*)bits;
        _old = SelectObject(_dc, _dib);

        nint v;
        GdipCreateBitmapFromScan0(W, H, W * 4, PARGB, _bits, &v); _bmp = v;
        GdipGetImageGraphicsContext(_bmp, &v); _g = _main = v;
        GdipSetSmoothingMode(_g, 4);       // AntiAlias
        GdipSetPixelOffsetMode(_g, 4);     // Half
        GdipSetTextRenderingHint(_g, 4);   // AntiAlias (grayscale: correct on alpha)

        GdipCreateSolidFill(0, &v); _brush = v;
        GdipCreatePen1(0xFFFFFFFF, 1, UnitWorld, &v); _pen = v;
        GdipSetPenStartCap(_pen, 2); GdipSetPenEndCap(_pen, 2);
        GdipCreatePath(0, &v); _path = v;

        _fmtL = Fmt(0); _fmtC = Fmt(1); _fmtR = Fmt(2);
        {   // word-wrapped, top-aligned, ellipsized at the last whole word that fits
            nint gen, f;
            GdipStringFormatGetGenericTypographic(&gen);
            GdipCloneStringFormat(gen, &f);
            GdipSetStringFormatFlags(f, 0x0800 | 0x2000);
            GdipSetStringFormatTrimming(f, 4);
            _fmtW = f;
        }


        FUi = Font("Segoe UI", 12.5f, 0, dw: (Ui, 400));
        FSemi = Font("Segoe UI Semibold", 12.5f, 0, "Segoe UI", 1, (Ui, 600));
        FSmall = Font("Segoe UI", 11.5f, 0, dw: (Ui, 400));
        FLabel = Font("Segoe UI", 11f, 0, dw: (Ui, 400));
        FBtn = Font("Segoe UI Semibold", 11f, 0, "Segoe UI", 1, (Ui, 600));
        FMono = Font("Cascadia Mono", 11.5f, 0, "Consolas", 0, (Mono, 400));
        FMonoSm = Font("Cascadia Mono", 10.5f, 0, "Consolas", 0, (Mono, 400));
        FMonoXs = Font("Cascadia Mono", 9f, 0, "Consolas", 0, (Mono, 400));
        FNum = Font("Segoe UI Light", 22f, 0, "Segoe UI", 0, (Disp, 300));
        FTitle = Font("Segoe UI Semibold", 17f, 0, "Segoe UI", 1, (Disp, 600));
        FBody = Font("Segoe UI", 13.5f, 0, dw: (Ui, 400));
        FCap = Font("Segoe UI Semibold", 9.5f, 0, "Segoe UI", 1, (Ui, 600));
        FMonoLg = Font("Cascadia Mono", 13.5f, 0, "Consolas", 0, (Mono, 400));
        FMonoXl = Font("Cascadia Mono", 20f, 0, "Consolas", 0, (Mono, 400));
        FMonoHuge = Font("Cascadia Mono", 40f, 0, "Consolas", 0, (Mono, 350));
        FTitleLg = Font("Segoe UI Semibold", 21f, 0, "Segoe UI", 1, (Disp, 600));
        FTab = Font("Segoe UI", 13f, 0, dw: (Ui, 500));
    }

    nint Fmt(int align)
    {
        nint gen, f;
        GdipStringFormatGetGenericTypographic(&gen);
        GdipCloneStringFormat(gen, &f);
        GdipSetStringFormatFlags(f, 0x1000 | 0x0800 | 0x2000); // NoWrap | MeasureTrailingSpaces | LineLimit
        GdipSetStringFormatAlign(f, align);
        GdipSetStringFormatLineAlign(f, 1);
        GdipSetStringFormatTrimming(f, 3); // EllipsisCharacter
        return f;
    }

    static readonly string[] Ui = { "Segoe UI Variable Text", "Segoe UI" }, Disp = { "Segoe UI Variable Display", "Segoe UI" },
        Mono = { "Cascadia Mono", "Consolas" };

    nint Font(string family, float px, int style, string? fallback = null, int fallbackStyle = 0, (string[] Fams, int Weight)? dw = null)
    {
        nint fam = 0, font;
        fixed (char* p = family)
            if (GdipCreateFontFamilyFromName(p, 0, &fam) != 0 && fallback != null)
            {
                style = fallbackStyle;
                fixed (char* q = fallback) GdipCreateFontFamilyFromName(q, 0, &fam);
            }
        _fams.Add(fam);
        GdipCreateFont(fam, px, style, UnitPixel, &font);
        _fonts.Add(font);
        if (dw is { } d && Dw.On) { var f = Dw.Format(d.Fams, d.Weight, px); if (f.Fmt != 0) _dwf[font] = f; }
        return font;
    }

    // ---------------------------------------------------------------- frame

    public void Clear(int wpx, int hpx)
    {
        int stride = W * 4;
        for (int y = 0; y < hpx; y++) new Span<byte>(_bits + y * stride, wpx * 4).Clear();
    }

    public void Begin()
    {
        GdipResetClip(_g);
        _clipShape = _clipRect = false;
        GdipResetWorldTransform(_g);
        GdipScaleWorldTransform(_g, S, S, 0);
        if (OffY != 0) GdipTranslateWorldTransform(_g, 0, OffY, 0);
        Alpha = 1;
    }

    public void Flush() => GdipFlush(_g, 1);

    uint Mul(uint c)
    {
        if (Alpha >= 1) return c;
        uint a = (uint)((c >> 24) * Math.Clamp(Alpha, 0, 1));
        return (c & 0xFFFFFF) | (a << 24);
    }

    // ---------------------------------------------------------------- shapes

    public void RR(float x, float y, float w, float h, float r)
    {
        r = MathF.Max(0.1f, MathF.Min(r, MathF.Min(w, h) / 2));
        float d = r * 2;
        GdipResetPath(_path);
        GdipAddPathArc(_path, x, y, d, d, 180, 90);
        GdipAddPathArc(_path, x + w - d, y, d, d, 270, 90);
        GdipAddPathArc(_path, x + w - d, y + h - d, d, d, 0, 90);
        GdipAddPathArc(_path, x, y + h - d, d, d, 90, 90);
        GdipClosePathFigure(_path);
    }

    public void Fill(uint c) { GdipSetSolidFillColor(_brush, Mul(c)); GdipFillPath(_g, _brush, _path); }
    public void FillRR(float x, float y, float w, float h, float r, uint c) { RR(x, y, w, h, r); Fill(c); }
    public void StrokeRR(float x, float y, float w, float h, float r, uint c, float width)
    {
        RR(x, y, w, h, r);
        GdipSetPenColor(_pen, Mul(c)); GdipSetPenWidth(_pen, width);
        GdipDrawPath(_g, _pen, _path);
    }
    /// Rounded rect with a vertical gradient (lighter top, like a lit card).
    public void FillRRGrad(float x, float y, float w, float h, float r, uint top, uint bottom)
    {
        RR(x, y, w, h, r);
        var rc = new RectF(x, y - 1, w, h + 2);
        nint b;
        GdipCreateLineBrushFromRect(&rc, Mul(top), Mul(bottom), 1, 3, &b);
        GdipFillPath(_g, b, _path);
        GdipDeleteBrush(b);
    }

    /// Area chart: polygon under `pts` (x,y pairs) down to `baseY`, gradient-filled, then stroked.
    public void Area(ReadOnlySpan<float> pts, float baseY, uint fillTop, uint line, float width)
    {
        int n = pts.Length / 2;
        if (n < 2) return;
        float* buf = stackalloc float[(n + 2) * 2];
        pts.CopyTo(new Span<float>(buf, n * 2));
        buf[n * 2] = pts[^2]; buf[n * 2 + 1] = baseY;
        buf[n * 2 + 2] = pts[0]; buf[n * 2 + 3] = baseY;
        float top = baseY;
        for (int i = 1; i < pts.Length; i += 2) top = MathF.Min(top, pts[i]);
        GdipResetPath(_path);
        GdipAddPathLine2(_path, buf, n + 2);
        GdipClosePathFigure(_path);
        var rc = new RectF(pts[0], top - 1, pts[^2] - pts[0], baseY - top + 2);
        nint b;
        GdipCreateLineBrushFromRect(&rc, Mul(fillTop), Mul(fillTop & 0x00FFFFFF), 1, 3, &b);
        GdipFillPath(_g, b, _path);
        GdipDeleteBrush(b);
        GdipSetPenColor(_pen, Mul(line)); GdipSetPenWidth(_pen, width);
        GdipDrawLines(_g, _pen, buf, n);
    }

    /// Soft radial glow (bell falloff to transparent).
    /// Implemented as stacked translucent ellipses: solid scanline fills are ~50x cheaper
    /// in GDI+ than a path-gradient brush, and at these alphas the steps are invisible.
    public void Glow(float cx, float cy, float rx, float ry, uint c)
    {
        const int steps = 7;
        float a = (c >> 24) / 255f / steps * 1.6f;
        uint layer = C.A(c, a);
        for (int i = 0; i < steps; i++)
        {
            float k = 1 - i / (float)steps;
            Circle2(cx, cy, rx * k, ry * k, layer);
        }
    }

    void Circle2(float cx, float cy, float rx, float ry, uint c)
    {
        GdipSetSolidFillColor(_brush, Mul(c));
        GdipFillEllipse(_g, _brush, cx - rx, cy - ry, 2 * rx, 2 * ry);
    }

    public void Bezier(float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4, float width, uint c)
    {
        GdipSetPenColor(_pen, Mul(c)); GdipSetPenWidth(_pen, width);
        GdipDrawBezier(_g, _pen, x1, y1, x2, y2, x3, y3, x4, y4);
    }

    /// Fill the current path with a vertical gradient between y0 and y1.
    public void FillGrad(float y0, float y1, uint top, uint bottom)
    {
        var rc = new RectF(0, y0 - 1, 10, y1 - y0 + 2);
        nint b;
        GdipCreateLineBrushFromRect(&rc, Mul(top), Mul(bottom), 1, 3, &b);
        GdipFillPath(_g, b, _path);
        GdipDeleteBrush(b);
    }

    public void FillRectGrad(float x, float y, float w, float h, uint top, uint bottom)
    {
        GdipResetPath(_path);
        GdipAddPathRectangle(_path, x, y, w, h);
        FillGrad(y, y + h, top, bottom);
    }

    /// Open polyline through x,y pairs.
    public void Lines(ReadOnlySpan<float> pts, float width, uint c)
    {
        GdipSetPenColor(_pen, Mul(c)); GdipSetPenWidth(_pen, width);
        fixed (float* p = pts) GdipDrawLines(_g, _pen, p, pts.Length / 2);
    }

    // ---------------------------------------------------------------- free-form path

    public void PathBegin() => GdipResetPath(_path);

    /// Arc around (cx, cy); consecutive arcs are joined by straight lines. Angles in degrees, 0 = east, 90 = down.
    public void PathArc(float cx, float cy, float r, float start, float sweep)
    {
        r = MathF.Max(r, 0.05f);
        GdipAddPathArc(_path, cx - r, cy - r, 2 * r, 2 * r, start, sweep);
    }

    public void PathClose() => GdipClosePathFigure(_path);

    public void Stroke(uint c, float width)
    {
        GdipSetPenColor(_pen, Mul(c)); GdipSetPenWidth(_pen, width);
        GdipDrawPath(_g, _pen, _path);
    }

    /// Closed polyline outline through x,y pairs.
    public void Poly(ReadOnlySpan<float> pts, float width, uint c)
    {
        GdipResetPath(_path);
        fixed (float* p = pts) GdipAddPathLine2(_path, p, pts.Length / 2);
        GdipClosePathFigure(_path);
        GdipSetPenColor(_pen, Mul(c)); GdipSetPenWidth(_pen, width);
        GdipSetPenLineJoin(_pen, 2);
        GdipDrawPath(_g, _pen, _path);
    }

    public void Clip() { GdipSetClipPath(_g, _path, 0); _clipShape = true; }
    /// Rectangular clip (DIP), honoured by DirectWrite text too.
    public void ClipRect(float x, float y, float w, float h) { GdipSetClipRect(_g, x, y, w, h, 0); _clipRect = true; _clip = new RectF(x, y, w, h); }
    public void Unclip() { GdipResetClip(_g); _clipShape = _clipRect = false; }

    public void Circle(float cx, float cy, float r, uint c)
    {
        GdipSetSolidFillColor(_brush, Mul(c));
        GdipFillEllipse(_g, _brush, cx - r, cy - r, 2 * r, 2 * r);
    }

    public void Line(float x1, float y1, float x2, float y2, float width, uint c)
    {
        GdipSetPenColor(_pen, Mul(c)); GdipSetPenWidth(_pen, width);
        GdipDrawLine(_g, _pen, x1, y1, x2, y2);
    }

    public void Arc(float cx, float cy, float r, float start, float sweep, float width, uint c)
    {
        GdipSetPenColor(_pen, Mul(c)); GdipSetPenWidth(_pen, width);
        GdipDrawArc(_g, _pen, cx - r, cy - r, 2 * r, 2 * r, start, sweep);
    }

    // ---------------------------------------------------------------- text

    /// align: 0 left, 1 center, 2 right. Single line, vertically centered, ellipsized.
    public void Text(string s, nint font, uint c, float x, float y, float w, float h, int align = 0)
    {
        if (s.Length == 0 || w <= 1) return;
        if (DwText(s, font, c, x, y, w, h, align, false)) return;
        var rc = new RectF(x, y, w, h);
        GdipSetSolidFillColor(_brush, Mul(c));
        fixed (char* p = s) GdipDrawString(_g, p, s.Length, font, &rc, align == 0 ? _fmtL : align == 1 ? _fmtC : _fmtR, _brush);
    }

    /// Multi-line text wrapped at word boundaries inside the box.
    public void TextWrap(string s, nint font, uint c, float x, float y, float w, float h)
    {
        if (s.Length == 0 || w <= 1) return;
        if (DwText(s, font, c, x, y, w, h, 0, true)) return;
        var rc = new RectF(x, y, w, h);
        GdipSetSolidFillColor(_brush, Mul(c));
        fixed (char* p = s) GdipDrawString(_g, p, s.Length, font, &rc, _fmtW, _brush);
    }

    bool DwText(string s, nint font, uint c, float x, float y, float w, float h, int align, bool wrap)
    {
        if (_tdc == 0 || _clipShape || !_dwf.TryGetValue(font, out var f)) return false;
        GdipFlush(_g, 1);
        var clip = new RectF(_clip.X, _clip.Y + OffY, _clip.W, _clip.H);
        Dw.Draw(_tdc, _tw, _th, S, s, f.Fmt, f.Sign, Mul(c), x, y + OffY, w, h, align, wrap, clip, _clipRect);
        return true;
    }

    public float Measure(string s, nint font)
    {
        if (s.Length == 0) return 0;
        if (_measure.TryGetValue((s, font), out var w)) return w;
        if (_dwf.TryGetValue(font, out var f)) w = Dw.Measure(s, f.Fmt);
        else
        {
            var rc = new RectF(0, 0, 10000, 100);
            RectF box; int a, b;
            fixed (char* p = s) GdipMeasureString(_g, p, s.Length, font, &rc, _fmtL, &box, &a, &b);
            w = box.W;
        }
        if (_measure.Count > 512) _measure.Clear();
        _measure[(s, font)] = w;
        return w;
    }

    public void Dispose()
    {
        foreach (var f in _fonts) GdipDeleteFont(f);
        foreach (var f in _fams) GdipDeleteFontFamily(f);
        GdipDeleteStringFormat(_fmtL); GdipDeleteStringFormat(_fmtC); GdipDeleteStringFormat(_fmtR); GdipDeleteStringFormat(_fmtW);
        GdipDeletePath(_path); GdipDeletePen(_pen); GdipDeleteBrush(_brush);
        FreeLayer(); FreeBackdrop(); FreeContent(); GdipDeleteGraphics(_main); GdipDisposeImage(_bmp);
        SelectObject(_dc, _old); DeleteObject(_dib); DeleteDC(_dc);
    }
}
