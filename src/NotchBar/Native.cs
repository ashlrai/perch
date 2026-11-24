using System.Runtime.InteropServices;

namespace NotchBar;

[StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
[StructLayout(LayoutKind.Sequential)] struct SIZE { public int W, H; }
[StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
[StructLayout(LayoutKind.Sequential)] struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public POINT pt; public uint priv; }
[StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
[StructLayout(LayoutKind.Sequential)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
[StructLayout(LayoutKind.Sequential)] struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public nint hwndTrack; public uint dwHoverTime; }
[StructLayout(LayoutKind.Sequential)]
unsafe struct PAINTSTRUCT { public nint hdc; public int fErase; public RECT rc; public int fRestore, fIncUpdate; public fixed byte rgb[32]; }
[StructLayout(LayoutKind.Sequential)]
struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong a, b, c, d, e, f, g; }
[StructLayout(LayoutKind.Sequential)]
struct BITMAPINFOHEADER
{
    public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
    public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
}
[StructLayout(LayoutKind.Sequential)]
unsafe struct WNDCLASSEXW
{
    public uint cbSize, style;
    public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
    public int cbClsExtra, cbWndExtra;
    public nint hInstance, hIcon, hCursor, hbrBackground;
    public char* lpszMenuName, lpszClassName;
    public nint hIconSm;
}
[StructLayout(LayoutKind.Sequential)]
unsafe struct PROCESSENTRY32W
{
    public uint dwSize, cntUsage, th32ProcessID;
    public nuint th32DefaultHeapID;
    public uint th32ModuleID, cntThreads, th32ParentProcessID;
    public int pcPriClassBase;
    public uint dwFlags;
    public fixed char szExeFile[260];
}
[StructLayout(LayoutKind.Sequential)]
struct RectF
{
    public float X, Y, W, H;
    public RectF(float x, float y, float w, float h) { X = x; Y = y; W = w; H = h; }
    public readonly bool Contains(float px, float py) => px >= X && py >= Y && px < X + W && py < Y + H;
}

static unsafe class W
{
    const string U = "user32.dll", K = "kernel32.dll", G = "gdi32.dll", P = "gdiplus.dll", WS = "ws2_32.dll";

    // ---- user32
    [DllImport(U)] public static extern ushort RegisterClassExW(WNDCLASSEXW* wc);
    [DllImport(U)] public static extern nint CreateWindowExW(uint ex, char* cls, char* name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport(U)] public static extern nint DefWindowProcW(nint h, uint m, nint w, nint l);
    [DllImport(U)] public static extern int GetMessageW(MSG* m, nint h, uint a, uint b);
    [DllImport(U)] public static extern int PeekMessageW(MSG* m, nint h, uint a, uint b, uint remove);
    [DllImport(U)] public static extern int TranslateMessage(MSG* m);
    [DllImport(U)] public static extern nint DispatchMessageW(MSG* m);
    [DllImport(U)] public static extern void PostQuitMessage(int c);
    [DllImport(U)] public static extern int PostMessageW(nint h, uint m, nint w, nint l);
    [DllImport(U, CharSet = CharSet.Unicode)] public static extern nint FindWindowW(string cls, string? name);
    [DllImport(U)] public static extern int GetWindowRect(nint h, RECT* r);
    [DllImport(U)] public static extern nint GetWindowLongPtrW(nint h, int index);
    [DllImport(U)] public static extern nint GetWindow(nint h, uint cmd);
    [DllImport(U)] public static extern nint GetAncestor(nint h, uint flags);
    [DllImport(U)] public static extern nint SetTimer(nint h, nint id, uint ms, nint fn);
    [DllImport(U)] public static extern int KillTimer(nint h, nint id);
    [DllImport(U)] public static extern int UpdateLayeredWindow(nint h, nint dst, POINT* pt, SIZE* sz, nint src, POINT* spt, uint key, BLENDFUNCTION* bf, uint flags);
    [DllImport(U)] public static extern int ShowWindow(nint h, int cmd);
    [DllImport(U)] public static extern int SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint f);
    [DllImport(U)] public static extern int DestroyWindow(nint h);
    [DllImport(U)] public static extern int TrackMouseEvent(TRACKMOUSEEVENT* t);
    [DllImport(U)] public static extern nint MonitorFromPoint(POINT p, uint f);
    [DllImport(U)] public static extern int GetMonitorInfoW(nint m, MONITORINFO* mi);
    [DllImport(U)] public static extern nint LoadCursorW(nint inst, nint id);
    [DllImport(U)] public static extern nint SetCursor(nint c);
    [DllImport(U)] public static extern int GetCursorPos(POINT* p);
    [DllImport(U)] public static extern int SetForegroundWindow(nint h);
    [DllImport(U)] public static extern nint CreatePopupMenu();
    [DllImport(U, CharSet = CharSet.Unicode)] public static extern int AppendMenuW(nint m, uint f, nint id, string? s);
    [DllImport(U)] public static extern int TrackPopupMenu(nint m, uint f, int x, int y, int r, nint h, nint rc);
    [DllImport(U)] public static extern int DestroyMenu(nint m);
    [DllImport(U)] public static extern int SetProcessDpiAwarenessContext(nint v);
    [DllImport(U)] public static extern nint SetWinEventHook(uint min, uint max, nint mod, delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> fn, uint pid, uint tid, uint flags);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(nint m, int type, uint* x, uint* y);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern nint ShellExecuteW(nint h, string? op, string file, string? args, string? dir, int show);
    [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(int* s);

    // ---- kernel32
    [DllImport(K)] public static extern nint GetModuleHandleW(nint name);
    [DllImport(K)] public static extern nint GetProcAddress(nint mod, nint ordinal);
    [DllImport(K, CharSet = CharSet.Unicode, SetLastError = true)] public static extern nint CreateMutexW(nint attr, int owner, string name);
    [DllImport(K)] public static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport(K)] public static extern int Process32FirstW(nint snap, PROCESSENTRY32W* e);
    [DllImport(K)] public static extern int Process32NextW(nint snap, PROCESSENTRY32W* e);
    [DllImport(K)] public static extern int CloseHandle(nint h);
    [DllImport(K)] public static extern nint OpenProcess(uint access, int inherit, uint pid);
    [DllImport(K)] public static extern uint WaitForSingleObject(nint h, uint ms);
    [DllImport(K)] public static extern int QueryFullProcessImageNameW(nint h, uint flags, char* buf, int* len);
    [DllImport(K)] public static extern int GetSystemTimes(long* idle, long* kernel, long* user);
    [DllImport(K)] public static extern int GlobalMemoryStatusEx(MEMORYSTATUSEX* m);
    [DllImport("iphlpapi.dll")] public static extern uint GetExtendedTcpTable(nint buf, int* size, int order, int af, int cls, uint res);

    // ---- winsock (async-select: datagrams arrive as window messages, no extra threads)
    [DllImport(WS)] public static extern int WSAStartup(ushort ver, byte* data);
    [DllImport(WS)] public static extern nint socket(int af, int type, int proto);
    [DllImport(WS)] public static extern int bind(nint s, byte* addr, int len);
    [DllImport(WS)] public static extern int WSAAsyncSelect(nint s, nint hwnd, uint msg, int events);
    [DllImport(WS)] public static extern int recvfrom(nint s, byte* buf, int len, int flags, nint from, nint fromlen);

    // ---- gdi32
    [DllImport(G)] public static extern nint CreateCompatibleDC(nint dc);
    [DllImport(G)] public static extern nint CreateDIBSection(nint dc, BITMAPINFOHEADER* bmi, uint usage, void** bits, nint sec, uint off);
    [DllImport(G)] public static extern nint SelectObject(nint dc, nint obj);
    [DllImport(G)] public static extern int DeleteObject(nint obj);
    [DllImport(G)] public static extern int DeleteDC(nint dc);
    [DllImport(G)] public static extern int StretchBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, int sw, int sh, uint rop);
    [DllImport(G)] public static extern int SetStretchBltMode(nint dc, int mode);
    [DllImport(G)] public static extern int SetBrushOrgEx(nint dc, int x, int y, POINT* old);
    [DllImport(G)] public static extern int GdiFlush();
    [DllImport(G)] public static extern nint CreateCompatibleBitmap(nint dc, int w, int h);
    [DllImport(G)] public static extern int BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [DllImport(G)] public static extern nint CreateSolidBrush(uint color);
    [DllImport(U)] public static extern int FillRect(nint dc, RECT* r, nint brush);
    [DllImport(U)] public static extern nint BeginPaint(nint h, PAINTSTRUCT* ps);
    [DllImport(U)] public static extern int EndPaint(nint h, PAINTSTRUCT* ps);
    [DllImport(U)] public static extern int UpdateWindow(nint h);
    [DllImport(U)] public static extern uint RegisterWindowMessageW(char* name);
    [DllImport(U)] public static extern int InvalidateRect(nint h, RECT* r, int erase);
    [DllImport(U)] public static extern int SetLayeredWindowAttributes(nint h, uint key, byte alpha, uint flags);
    [DllImport(U)] public static extern int PrintWindow(nint h, nint dc, uint flags);
    [DllImport(U)] public static extern int IsIconic(nint h);
    [DllImport(U)] public static extern int IsZoomed(nint h);
    [DllImport(U)] public static extern int UnhookWinEvent(nint hook);
    [DllImport(U)] public static extern int IsWindowVisible(nint h);
    [DllImport(U)] public static extern int IsWindow(nint h);
    [DllImport(U)] public static extern int GetWindowTextLengthW(nint h);
    [DllImport(U)] public static extern uint GetWindowThreadProcessId(nint h, uint* pid);
    [DllImport(U)] public static extern int EnumWindows(delegate* unmanaged<nint, nint, int> fn, nint l);
    [DllImport(U)] public static extern nint GetForegroundWindow();
    [DllImport(U)] public static extern nint SetFocus(nint h);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint h, int attr, void* v, int size);
    [DllImport(WS)] public static extern int sendto(nint s, byte* buf, int len, int flags, byte* to, int tolen);
    [DllImport(U)] public static extern nint GetDC(nint hwnd);
    [DllImport(U)] public static extern int ReleaseDC(nint hwnd, nint dc);

    // ---- gdiplus flat API
    [StructLayout(LayoutKind.Sequential)] public struct GpInput { public uint Version; public nint Debug; public int NoThread, NoCodecs; }
    [StructLayout(LayoutKind.Sequential)] public struct GpOutput { public nint Hook, Unhook; }
    [DllImport(P)] public static extern int GdiplusStartup(nint* token, GpInput* input, GpOutput* output);
    [DllImport(P)] public static extern int GdipCreateBitmapFromScan0(int w, int h, int stride, int fmt, byte* scan0, nint* bmp);
    [DllImport(P)] public static extern int GdipGetImageGraphicsContext(nint img, nint* g);
    [DllImport(P)] public static extern int GdipDisposeImage(nint img);
    [DllImport(P)] public static extern int GdipDeleteGraphics(nint g);
    [DllImport(P)] public static extern int GdipFlush(nint g, int intention);
    [StructLayout(LayoutKind.Sequential)] public struct GpRect { public int X, Y, W, H; }
    [StructLayout(LayoutKind.Sequential)] public struct GpBitmapData { public uint Width, Height; public int Stride, PixelFormat; public nint Scan0; public nint Reserved; }
    [DllImport(P)] public static extern int GdipBitmapLockBits(nint bmp, GpRect* rect, uint flags, int fmt, GpBitmapData* data);
    [DllImport(P)] public static extern int GdipBitmapUnlockBits(nint bmp, GpBitmapData* data);
    [DllImport(P)] public static extern int GdipSetSmoothingMode(nint g, int m);
    [DllImport(P)] public static extern int GdipSetPixelOffsetMode(nint g, int m);
    [DllImport(P)] public static extern int GdipSetTextRenderingHint(nint g, int m);
    [DllImport(P)] public static extern int GdipResetWorldTransform(nint g);
    [DllImport(P)] public static extern int GdipScaleWorldTransform(nint g, float sx, float sy, int order);
    [DllImport(P)] public static extern int GdipSetClipPath(nint g, nint path, int mode);
    [DllImport(P)] public static extern int GdipSetClipRect(nint g, float x, float y, float w, float h, int mode);
    [DllImport(U)] public static extern int RegisterHotKey(nint h, int id, uint mods, uint vk);
    [DllImport(U)] public static extern int UnregisterHotKey(nint h, int id);
    [DllImport(U)] public static extern nint MonitorFromWindow(nint h, uint flags);
    [DllImport(U)] public static extern int GetClassNameW(nint h, char* buf, int max);
    [DllImport(U, CharSet = CharSet.Unicode)] public static extern nint FindWindowExW(nint parent, nint after, string cls, string? name);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern int PlaySoundW(string? sound, nint mod, uint flags);
    [DllImport(P)] public static extern int GdipResetClip(nint g);
    [DllImport(P)] public static extern int GdipCreatePath(int fill, nint* path);
    [DllImport(P)] public static extern int GdipDeletePath(nint path);
    [DllImport(P)] public static extern int GdipResetPath(nint path);
    [DllImport(P)] public static extern int GdipAddPathArc(nint path, float x, float y, float w, float h, float start, float sweep);
    [DllImport(P)] public static extern int GdipClosePathFigure(nint path);
    [DllImport(P)] public static extern int GdipCreateSolidFill(uint argb, nint* brush);
    [DllImport(P)] public static extern int GdipSetSolidFillColor(nint brush, uint argb);
    [DllImport(P)] public static extern int GdipDeleteBrush(nint brush);
    [DllImport(P)] public static extern int GdipFillPath(nint g, nint brush, nint path);
    [DllImport(P)] public static extern int GdipFillEllipse(nint g, nint brush, float x, float y, float w, float h);
    [DllImport(P)] public static extern int GdipCreatePen1(uint argb, float width, int unit, nint* pen);
    [DllImport(P)] public static extern int GdipSetPenColor(nint pen, uint argb);
    [DllImport(P)] public static extern int GdipSetPenWidth(nint pen, float w);
    [DllImport(P)] public static extern int GdipSetPenStartCap(nint pen, int cap);
    [DllImport(P)] public static extern int GdipSetPenLineJoin(nint pen, int join);
    [DllImport(P)] public static extern int GdipSetPenEndCap(nint pen, int cap);
    [DllImport(P)] public static extern int GdipDeletePen(nint pen);
    [DllImport(P)] public static extern int GdipDrawPath(nint g, nint pen, nint path);
    [DllImport(P)] public static extern int GdipDrawArc(nint g, nint pen, float x, float y, float w, float h, float start, float sweep);
    [DllImport(P)] public static extern int GdipDrawLine(nint g, nint pen, float x1, float y1, float x2, float y2);
    [DllImport(P)] public static extern int GdipGraphicsClear(nint g, uint c);
    [DllImport(P)] public static extern int GdipSetInterpolationMode(nint g, int m);
    [DllImport(P)] public static extern int GdipSetCompositingMode(nint g, int m);
    [DllImport(P)] public static extern int GdipDrawImageRectRect(nint g, nint img, float dx, float dy, float dw, float dh, float sx, float sy, float sw, float sh, int unit, nint attr, nint cb, nint data);
    [DllImport(P)] public static extern int GdipAddPathLine2(nint path, float* pts, int n);
    [DllImport(P)] public static extern int GdipDrawLines(nint g, nint pen, float* pts, int n);
    [DllImport(P)] public static extern int GdipAddPathRectangle(nint path, float x, float y, float w, float h);
    [DllImport(P)] public static extern int GdipDrawBezier(nint g, nint pen, float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4);
    [DllImport(P)] public static extern int GdipAddPathEllipse(nint path, float x, float y, float w, float h);
    [DllImport(P)] public static extern int GdipCreateLineBrushFromRect(RectF* rc, uint c1, uint c2, int mode, int wrap, nint* brush);
    [DllImport(P)] public static extern int GdipCreatePathGradientFromPath(nint path, nint* brush);
    [DllImport(P)] public static extern int GdipSetPathGradientCenterColor(nint brush, uint c);
    [DllImport(P)] public static extern int GdipSetPathGradientSurroundColorsWithCount(nint brush, uint* c, int* n);
    [DllImport(P)] public static extern int GdipSetPathGradientSigmaBlend(nint brush, float focus, float scale);
    [DllImport(P)] public static extern int GdipCreateFromHDC(nint dc, nint* g);
    [DllImport(P)] public static extern int GdipCreateBitmapFromHBITMAP(nint hbm, nint pal, nint* bmp);
    [DllImport(P)] public static extern int GdipCreateImageAttributes(nint* attr);
    [DllImport(P)] public static extern int GdipSetImageAttributesWrapMode(nint attr, int wrap, uint argb, int clamp);
    [DllImport(P)] public static extern int GdipDisposeImageAttributes(nint attr);
    [DllImport(P)] public static extern int GdipCreateTexture(nint img, int wrap, nint* brush);
    [DllImport(P)] public static extern int GdipScaleTextureTransform(nint brush, float sx, float sy, int order);
    [DllImport(P)] public static extern int GdipTranslateTextureTransform(nint brush, float dx, float dy, int order);
    [DllImport(P)] public static extern int GdipTranslateWorldTransform(nint g, float dx, float dy, int order);
    [DllImport(P)] public static extern int GdipCreateFontFamilyFromName(char* name, nint coll, nint* fam);
    [DllImport(P)] public static extern int GdipDeleteFontFamily(nint fam);
    [DllImport(P)] public static extern int GdipCreateFont(nint fam, float size, int style, int unit, nint* font);
    [DllImport(P)] public static extern int GdipDeleteFont(nint font);
    [DllImport(P)] public static extern int GdipStringFormatGetGenericTypographic(nint* fmt);
    [DllImport(P)] public static extern int GdipCloneStringFormat(nint fmt, nint* clone);
    [DllImport(P)] public static extern int GdipDeleteStringFormat(nint fmt);
    [DllImport(P)] public static extern int GdipSetStringFormatFlags(nint fmt, int flags);
    [DllImport(P)] public static extern int GdipSetStringFormatAlign(nint fmt, int align);
    [DllImport(P)] public static extern int GdipSetStringFormatLineAlign(nint fmt, int align);
    [DllImport(P)] public static extern int GdipSetStringFormatTrimming(nint fmt, int trim);
    [DllImport(P)] public static extern int GdipDrawString(nint g, char* s, int len, nint font, RectF* rc, nint fmt, nint brush);
    [DllImport(P)] public static extern int GdipMeasureString(nint g, char* s, int len, nint font, RectF* rc, nint fmt, RectF* box, int* fitted, int* lines);
}
