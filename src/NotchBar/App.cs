using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using static NotchBar.W;

namespace NotchBar;

/// The palette. One glass tone for every surface, hairline edges; colour means status, plus an accent for
/// "running" and selection. Follows Windows' taskbar theme (light/dark) and accent colour unless the config says otherwise.
static class C
{
    public static uint Glass, Edge, Line, Hover, Sel, Track, Text, Dim, Faint, Ok, Warn, Err,
        Accent, Run,                                         // Run: spinners and progress (the accent, or Text with accent = none)
        Face, Bar, Pane, Well, WellWarn, Dots, Solid, SolidText, Ghost, ChipOn, ChipHot, ChipOff,
        SelFill, SelEdge, DockRing, DockFill, Mark, Hair, OnBar;
    public static bool Light, HasAccent;
    public static int Version;                               // bumps on every theme change: cached card faces are redrawn

    public static uint A(uint c, float a) => (c & 0xFFFFFF) | ((uint)(Math.Clamp(a, 0, 1) * 255) << 24);

    public static void Load(Config cfg)
    {
        Light = cfg.Theme switch { "light" => true, "dark" => false, _ => SystemLight() };
        if (!Light)
        {
            Glass = 0xC4131417; Edge = 0x1FFFFFFF; Line = 0x14FFFFFF; Hover = 0x0EFFFFFF; Sel = 0x17FFFFFF; Track = 0x24FFFFFF;
            Text = 0xFFEDEDEF; Dim = 0xFFA1A1A9; Faint = 0xFF6E6E77; Ok = 0xFF4CC38A; Warn = 0xFFF2A93B; Err = 0xFFEF5B5B;
            Face = 0xFF111215; Bar = 0xFF16171B; Pane = 0xFF131418; Well = 0xFF0D0E11; WellWarn = 0xFF1B1712; Dots = 0xFF2A2B30;
            Solid = 0xF2EDEDEF; SolidText = 0xFF0B0B0C; Ghost = 0xFF16171A; ChipOn = 0xD82C2D32; ChipHot = 0xC41E1F23; ChipOff = 0xB4141518;
            SelFill = 0x15FFFFFF; SelEdge = 0x12FFFFFF; DockRing = 0xFF0D0D0F; DockFill = 0xFF1F2024; Mark = 0x40FFFFFF; Hair = 0x0BFFFFFF;
        }
        else
        {
            Glass = 0xD2F3F3F5; Edge = 0x1C000000; Line = 0x12000000; Hover = 0x0D000000; Sel = 0x14000000; Track = 0x22000000;
            Text = 0xFF19191D; Dim = 0xFF5B5B65; Faint = 0xFF8A8A94; Ok = 0xFF139A5C; Warn = 0xFFC47500; Err = 0xFFD63636;
            Face = 0xFFFCFCFD; Bar = 0xFFF1F1F4; Pane = 0xFFF6F6F8; Well = 0xFFEDEDF0; WellWarn = 0xFFFFF5E3; Dots = 0xFFD5D5DB;
            Solid = 0xF219191D; SolidText = 0xFFFFFFFF; Ghost = 0xFFF5F5F7; ChipOn = 0xF2FFFFFF; ChipHot = 0xE8F6F6F8; ChipOff = 0xDAEDEDF0;
            SelFill = 0x10000000; SelEdge = 0x10000000; DockRing = 0xFFF3F3F5; DockFill = 0xFFE3E3E8; Mark = 0x2E000000; Hair = 0x0C000000;
        }
        OnBar = 0xEEFFFFFF;
        uint? acc = cfg.Accent switch
        {
            "none" or "off" => null,
            ['#', ..] s when s.Length == 7 && uint.TryParse(s.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var rgb) => 0xFF000000 | rgb,
            _ => SystemAccent(Light),
        };
        HasAccent = acc != null;
        Accent = acc ?? Text;
        Run = Accent;
        if (HasAccent)
        {   // tint the selection with the accent
            SelFill = A(Accent, Light ? 0.13f : 0.20f);
            SelEdge = A(Accent, Light ? 0.30f : 0.34f);
        }
        Version++;
    }

    /// The taskbar follows the *system* theme (not the apps' one), and our glass sits behind the taskbar.
    static bool SystemLight()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    /// Windows' accent palette (8 shades, light to dark): a light shade on dark glass, a dark shade on light glass.
    static uint? SystemAccent(bool light)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (k?.GetValue("AccentPalette") is byte[] p && p.Length >= 32)
            {
                int i = (light ? 4 : 1) * 4;   // Dark1 / Light2
                return 0xFF000000 | (uint)p[i] << 16 | (uint)p[i + 1] << 8 | p[i + 2];
            }
            if (k?.GetValue("AccentColorMenu") is int abgr)
                return 0xFF000000 | (uint)(abgr & 0xFF) << 16 | (uint)(abgr & 0xFF00) | (uint)(abgr >> 16 & 0xFF);
        }
        catch { }
        return null;
    }
}

enum Ico { None, Spin, Pulse, Check, Cross, Dot }
enum HK { None, Open, Close, Tab, Row, Server, Repo, Card, Chip, Focus, Allow, Deny, Rerun }
struct Hit { public RectF R; public HK K; public string Arg; }

/// What the collapsed notch says: an icon (or agent glyph), a title, a mono detail, docked agents.
struct Pill
{
    public Ico Icon; public uint IconColor; public string? Agent;
    public string Title, Detail;
    public string? Sub;                            // the second line revealed on hover
    public bool HasProg, Err; public float Prog;   // Prog < 0 = indeterminate
    public int Dock;
    public string? Ask;   // agent key: a PermissionRequest is waiting, show Allow / Deny
}

/// Shape of the island this frame, in monitor DIPs: the taskbar's center box (cb*) with the notch
/// or panel (n*) standing on its top edge.
struct Geo { public float Cb0, Cb1, By0, By1, Nx0, Nx1, Ny0, Nw, Nh, Pcx, Rn; }

static unsafe partial class App
{
    const uint WM_SOCK = 0x8001, WM_GIT = 0x8002, WM_LAYOUT = 0x8003, WM_TICK = 0x8004;

    [DllImport("dwmapi.dll")] static extern int DwmFlush();

    /// Millisecond clock with sub-ms resolution for animation (TickCount64 moves in 15.6 ms steps).
    static long Hr => (long)(System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
    const int T_ANIM = 1, T_POLL = 2, T_OUT = 4, T_TB = 5, T_TBDEB = 6, T_TBAGAIN = 7, T_CLEAR = 8, T_FS = 9;
    static uint _tbCreated;
    const float TabH = 34, BR = 12, FIL = 10, RB = 14, SP = 18;

    static nint _hwnd, _sideL, _sideR, _sock, _hand;
    static Gfx _g = null!; static Gfx? _gL, _gR;
    static float _s = 1;
    static RECT _mon;
    static Config _cfg = Config.Load();

    static float _tFrom, _tTo; static long _aStart; static int _aDur;
    static float _cb0 = -1, _cb1 = -1, _tabW = 160;
    static long _lastFrame;
    static int _animMs, _pollMs, _sig, _pollN, _layerKey;
    static bool _tracking, _hidden;
    static RECT _win;
    static HK _hk; static string? _harg;
    static readonly List<Hit> _hits = new();
    static readonly List<TaskInfo> _tasks = new();
    static readonly List<AgentInfo> _agents = new();
    static readonly List<Repo> _repos = new();

    // ================================================================ bootstrap

    public static void Run()
    {
        Gfx.Startup();
        Dw.Init();
        C.Load(_cfg);
        DarkMenus();
        Model.Pin(_cfg.Repos);
        nint inst = GetModuleHandleW(0);
        _hand = LoadCursorW(0, 32649);
        fixed (char* cls = "NotchBar")
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &WndProc, hInstance = inst,
                hCursor = LoadCursorW(0, 32512), lpszClassName = cls,
            };
            RegisterClassExW(&wc);
            // LAYERED | TOOLWINDOW (no taskbar/alt-tab) | TOPMOST | NOACTIVATE (never steals focus)
            _hwnd = CreateWindowExW(0x80000 | 0x80 | 0x8 | 0x8000000, cls, cls, 0x80000000, 0, 0, 1, 1, 0, 0, inst, 0);
            // the two side boxes also ignore the mouse (TRANSPARENT): the taskbar above them handles clicks
            _sideL = CreateWindowExW(0x80000 | 0x80 | 0x8 | 0x8000000 | 0x20, cls, cls, 0x80000000, 0, 0, 1, 1, 0, 0, inst, 0);
            _sideR = CreateWindowExW(0x80000 | 0x80 | 0x8 | 0x8000000 | 0x20, cls, cls, 0x80000000, 0, 0, 1, 1, 0, 0, inst, 0);
            CreateDeckWindows(inst, cls);
        }
        _monH = MonitorFromPoint(default, 1);
        Place();
        OpenSocket();
        RegisterHotkey();
        AppBar(true);
        SetWinEventHook(3, 3, 0, &OnForeground, 0, 0, 2); // EVENT_SYSTEM_FOREGROUND, out-of-context, skip own
        SetWinEventHook(0x8001, 0x8003, 0, &OnShowHide, 0, 0, 2); // EVENT_OBJECT_DESTROY/SHOW/HIDE: app windows coming and going
        Probe.Poll(_cfg);
        TaskbarProbe.Refresh(_hwnd, WM_LAYOUT, Tray());
        WatchForeground(GetForegroundWindow());
        CheckFullscreen();
        UpdateTuck();
        Frame();
        ShowWindow(_hwnd, _hidden ? 0 : 4); // SW_SHOWNOACTIVATE
        ZOrder(_hwnd);
        SetPoll(4000);
        SetTimer(_hwnd, T_TB, 15000, 0);   // backstop only (pin changes); events drive the rest
        fixed (char* n = "TaskbarCreated") _tbCreated = RegisterWindowMessageW(n);   // sent when Explorer (re)starts
        if (_cfg.ClearTaskbar) TaskbarClear.Launch();

        MSG m;
        while (GetMessageW(&m, 0, 0, 0) > 0) { TranslateMessage(&m); DispatchMessageW(&m); }
    }

    static void Place()
    {
        nint mon = _monH != 0 ? _monH : MonitorFromPoint(default, 1);
        MONITORINFO mi = default; mi.cbSize = (uint)sizeof(MONITORINFO);
        GetMonitorInfoW(mon, &mi);
        uint dx, dy;
        float s = GetDpiForMonitor(mon, 0, &dx, &dy) == 0 ? dx / 96f : 1f;
        _mon = mi.rcMonitor;
        if (_g == null || MathF.Abs(s - _s) > 0.001f)
        {
            _g?.Dispose(); _gL?.Dispose(); _gR?.Dispose();
            _gL = _gR = null;
            _s = s;
            _g = new Gfx(s, 1000, 200);
            EnsureGfx(MathF.Max(640, (_mon.R - _mon.L) / s * 0.45f) + 2 * SP, HH + 60 + 2 * SP);
            _gd?.Dispose(); _gd = null;
        }
    }

    /// Grow the drawing surface when the island needs more room (never shrinks; it's reused).
    static void EnsureGfx(float wDip, float hDip)
    {
        if (_g.W >= (int)MathF.Ceiling(wDip * _s) + 2 && _g.H >= (int)MathF.Ceiling(hDip * _s) + 2) return;
        float w = MathF.Max(wDip + 40, _g.W / _s), h = MathF.Max(hDip + 40, _g.H / _s);
        _g.Dispose();
        _g = new Gfx(_s, w, h);
        _layerKey = 0;
    }

    /// Keep our windows directly beneath the taskbar: above every app window, below the taskbar's icons.
    static void ZOrder(nint h)
    {
        nint tray = Tray();
        SetWindowPos(h, tray != 0 ? tray : -1, 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x200);
    }

    // ================================================================ which screen

    static nint _monH;

    /// The taskbar window on the screen we're on: the main one, or a secondary taskbar on another monitor.
    static nint Tray()
    {
        nint main = FindWindowW("Shell_TrayWnd", null);
        if (_monH == 0 || main == 0 || MonitorFromWindow(main, 2) == _monH) return main;
        for (nint h = 0; (h = FindWindowExW(0, h, "Shell_SecondaryTrayWnd", null)) != 0;)
            if (MonitorFromWindow(h, 2) == _monH) return h;
        return main;
    }

    /// monitor = follow: move to the taskbar of the screen holding the window you're working in.
    static void FollowMonitor(nint fg)
    {
        if (!_cfg.FollowMonitor || fg == 0 || Expanded || _deckShown) return;
        if (CoversMonitor(fg, 0)) return;   // a fullscreen app elsewhere: stay on the taskbar that's still visible
        if (fg == _hwnd || fg == _deck || fg == _scrim) return;
        var cls = stackalloc char[64];
        int n = GetClassNameW(fg, cls, 64);
        var name = new ReadOnlySpan<char>(cls, n);
        // the taskbars, the desktop and Start/Search belong to every screen: don't move for them
        if (name is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW" or "Windows.UI.Core.CoreWindow" or "XamlExplorerHostIslandWindow") return;
        nint mon = MonitorFromWindow(fg, 2);
        if (mon == 0 || mon == _monH) return;
        if (Secondary(mon) == 0 && MonitorFromWindow(FindWindowW("Shell_TrayWnd", null), 2) != mon) return;   // no taskbar on that screen
        _monH = mon;
        Place();
        _cb0 = _cb1 = -1;                  // appear on the new taskbar, don't slide across screens
        TaskbarProbe.Current = null;
        TaskbarProbe.Refresh(_hwnd, WM_LAYOUT, Tray());
        UpdatePlates();
        Frame();
        ZAll();
    }

    static nint Secondary(nint mon)
    {
        for (nint h = 0; (h = FindWindowExW(0, h, "Shell_SecondaryTrayWnd", null)) != 0;)
            if (MonitorFromWindow(h, 2) == mon) return h;
        return 0;
    }

    // ================================================================ hotkey

    const int HOTKEY_ID = 1;
    static bool _viaHotkey;

    static void RegisterHotkey()
    {
        UnregisterHotKey(_hwnd, HOTKEY_ID);
        if (ParseHotkey(_cfg.Hotkey, out uint mods, out uint vk) && RegisterHotKey(_hwnd, HOTKEY_ID, mods | 0x4000, vk) == 0)   // MOD_NOREPEAT
            Log(new Exception($"hotkey '{_cfg.Hotkey}' is taken by another app"));
    }

    /// "win+alt+n", "ctrl+shift+f9", "ctrl+alt+space" -> MOD_* flags and a virtual-key code.
    static bool ParseHotkey(string s, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (s is "" or "off" or "none") return false;
        foreach (var raw in s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw)
            {
                case "alt": mods |= 1; break;
                case "ctrl" or "control": mods |= 2; break;
                case "shift": mods |= 4; break;
                case "win" or "windows": mods |= 8; break;
                case "space": vk = 0x20; break;
                case "tab": vk = 0x09; break;
                case "enter": vk = 0x0D; break;
                case "`" or "backtick": vk = 0xC0; break;
                case ['f', .. var num] when int.TryParse(num, out int fn) && fn is >= 1 and <= 24: vk = (uint)(0x6F + fn); break;
                case [var ch] when char.IsAsciiLetterOrDigit(ch): vk = char.ToUpperInvariant(ch); break;
                default: return false;
            }
        }
        return vk != 0 && mods != 0;
    }

    static void OnHotkey()
    {
        if (_hidden) return;
        if (Expanded) { Collapse(); return; }
        _viaHotkey = true;
        Expand();
    }

    // ================================================================ alerts: chime + flash

    static long _flashAt; static uint _flashCol;

    static void ProcessAlerts()
    {
        if (Model.Alerts.Count == 0) return;
        nint fg = GetForegroundWindow();
        uint fgPid = 0;
        if (fg != 0) GetWindowThreadProcessId(fg, &fgPid);
        Dictionary<uint, uint>? parents = null;
        string? sound = null;
        foreach (var a in Model.Alerts)
        {
            _flashAt = Hr;
            _flashCol = a.Kind switch { "wait" => C.Warn, "fail" => C.Err, _ => C.Ok };
            if (!_cfg.Sound || _hidden) continue;
            // stay quiet when you're already looking at the window it runs in
            parents ??= Probe.ParentMap();
            bool watching = false;
            uint p = a.Pid;
            for (int i = 0; i < 16 && p != 0 && fgPid != 0; i++)
            {
                if (p == fgPid) { watching = true; break; }
                if (!parents.TryGetValue(p, out var pp) || pp == p) break;
                p = pp;
            }
            if (watching) continue;
            // "needs you" outranks "done" when several arrive at once
            string s = a.Kind == "wait" ? "Notification.Reminder" : a.Kind == "fail" ? "SystemHand" : "Notification.Default";
            if (sound == null || a.Kind == "wait") sound = s;
        }
        Model.Alerts.Clear();
        if (sound != null) PlaySoundW(sound, 0, 0x10000 | 0x1 | 0x2 | 0x10);   // SND_ALIAS | SND_ASYNC | SND_NODEFAULT | SND_NOSTOP
    }

    static void ThemeChanged()
    {
        C.Load(_cfg);
        _layerKey = 0;
        FreeCards();
        UpdatePlates();
        Invalidate();
    }

    static void OpenSocket()
    {
        byte* wsa = stackalloc byte[512];
        if (WSAStartup(0x0202, wsa) != 0) return;
        _sock = socket(2, 2, 17);
        if (_sock == -1) return;
        byte* sa = stackalloc byte[16];
        new Span<byte>(sa, 16).Clear();
        sa[0] = 2;                                       // AF_INET
        sa[2] = Proto.Port >> 8; sa[3] = Proto.Port & 0xFF;
        sa[4] = 127; sa[7] = 1;                          // 127.0.0.1 only
        if (bind(_sock, sa, 16) != 0) { _sock = 0; return; }
        WSAAsyncSelect(_sock, _hwnd, WM_SOCK, 1);       // FD_READ -> window message
    }

    static void DarkMenus()
    {
        try
        {   // uxtheme #135 SetPreferredAppMode(ForceDark), #136 FlushMenuThemes
            nint ux = NativeLibrary.Load("uxtheme.dll");
            nint a = GetProcAddress(ux, 135), b = GetProcAddress(ux, 136);
            if (a != 0) ((delegate* unmanaged<int, int>)a)(2);
            if (b != 0) ((delegate* unmanaged<void>)b)();
        }
        catch { }
    }

    // ================================================================ messages

    [UnmanagedCallersOnly]
    static nint WndProc(nint h, uint m, nint w, nint l)
    {
        try { return Handle(h, m, w, l); }
        catch (Exception ex) { Log(ex); return DefWindowProcW(h, m, w, l); }
    }

    static long _lastLog;
    static void Log(Exception ex)
    {
        if (Model.Now - _lastLog < 5000) return; // never spam the disk from a render loop
        _lastLog = Model.Now;
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "notchbar.log"), $"{DateTime.Now:s} {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n"); } catch { }
    }

    [UnmanagedCallersOnly]
    static void OnForeground(nint hook, uint ev, nint hwnd, int obj, int child, uint thread, uint time)
    {
        try { FollowMonitor(hwnd); WatchForeground(hwnd); CheckFullscreen(); UpdateTuck(); LayoutSoon(); } catch { }
    }

    // ---- the front window's size: maximize / restore / F11 don't change which window is in front, so watch
    // its location changes. The hook is limited to that window's process to keep the event traffic tiny.
    static nint _fgWatch, _locHook; static uint _locPid;

    static void WatchForeground(nint fg)
    {
        if (fg == 0 || fg == _hwnd || fg == _deck || fg == _scrim) return;
        _fgWatch = fg;
        uint pid;
        GetWindowThreadProcessId(fg, &pid);
        if (pid == _locPid && _locHook != 0) return;
        if (_locHook != 0) UnhookWinEvent(_locHook);
        _locHook = pid == 0 ? 0 : SetWinEventHook(0x800B, 0x800B, 0, &OnLocation, pid, 0, 2);   // EVENT_OBJECT_LOCATIONCHANGE
        _locPid = pid;
    }

    [UnmanagedCallersOnly]
    static void OnLocation(nint hook, uint ev, nint hwnd, int obj, int child, uint thread, uint time)
    {
        try
        {
            if (hwnd != _fgWatch || obj != 0 || child != 0) return;   // the front window itself, not its parts or the cursor
            SetTimer(_hwnd, T_FS, 120, 0);                          // once it has settled: fullscreen? maximized?
        }
        catch { }
    }

    // ---- tucked: a maximized window is in front, so the notch sinks into the taskbar leaving a slim lip
    static bool _tuckWant;
    static float _tuck;
    const float LipH = 6;

    static void UpdateTuck()
    {
        bool want = _cfg.TuckMaximized && MaximizedHere(GetForegroundWindow());
        if (want == _tuckWant) return;
        _tuckWant = want;
        Invalidate();
    }

    /// A maximized window on our screen whose bottom edge meets a bottom taskbar: the notch would cover it.
    static bool MaximizedHere(nint h)
    {
        if (h == 0 || h == _hwnd || h == _deck || h == _scrim || IsZoomed(h) == 0) return false;
        nint m = MonitorFromWindow(h, 2);
        if (m != _monH && _monH != 0) return false;
        MONITORINFO mi = default; mi.cbSize = (uint)sizeof(MONITORINFO);
        return GetMonitorInfoW(m, &mi) != 0 && mi.rcWork.B < mi.rcMonitor.B;   // the taskbar reserves the bottom
    }

    /// Tuck target for this frame: never while open, hovered or being interacted with.
    static bool StepTuck(float dt)
    {
        float target = _tuckWant && !Expanded && _tTo < 0.5f && _hk == HK.None ? 1 : 0;
        if (_cfg.LowMotion) { bool ch = _tuck != target; _tuck = target; return ch; }
        return ApproachF(ref _tuck, target, 1 - MathF.Exp(-dt * (target > _tuck ? 9 : 16)));   // tuck gently, pop up quickly
    }

    /// The status line on the lip: the notch's state in one stroke.
    static void LipStatus(in Pill p, float x0, float y0, float w, long now, float a)
    {
        var g = _g;
        float l = x0 + 12, r = x0 + w - 12, y = y0 + 2.6f, fa = g.Alpha;
        g.Alpha = fa * a;
        g.Line(l, y, r, y, 2, C.A(C.Text, 0.16f));
        if (p.Icon == Ico.Pulse)
        {
            float br = _cfg.LowMotion ? 1 : 0.55f + 0.45f * MathF.Sin(now / 900f * MathF.Tau);
            g.Line(l, y, r, y, 2, C.A(p.IconColor, br));
        }
        else if (p.HasProg && p.Prog >= 0) g.Line(l, y, l + (r - l) * p.Prog, y, 2, p.Err ? C.Err : C.Run);
        else if (p.Icon == Ico.Spin || p.HasProg)
        {
            float seg = (r - l) * 0.25f, ph = _cfg.LowMotion ? 0.4f : now % 1400 / 1400f, s = l + (r - l + seg) * ph - seg;
            g.Line(MathF.Max(l, s), y, MathF.Min(r, s + seg), y, 2, C.Run);
        }
        else if (p.Icon is Ico.Check or Ico.Cross) g.Line(l, y, r, y, 2, p.IconColor);
        else if (p.IconColor != C.Faint)
        {
            float c = (l + r) / 2, half = (r - l) * 0.12f;
            g.Line(c - half, y, c + half, y, 2, C.A(p.IconColor, 0.75f));
        }
        g.Alpha = fa;
    }

    /// A top-level app window appeared or disappeared: its taskbar button may have too.
    [UnmanagedCallersOnly]
    static void OnShowHide(nint hook, uint ev, nint hwnd, int obj, int child, uint thread, uint time)
    {
        try
        {
            if (obj != 0 || child != 0 || hwnd == 0) return;                                       // OBJID_WINDOW only
            // a destroyed window can't be inspected: react only if it's an app window we saw appear
            if (ev == 0x8001) { if (_appWins.Remove(hwnd)) LayoutSoon(); return; }
            if (GetAncestor(hwnd, 2) != hwnd) return;                                              // GA_ROOT: top-level only
            long ex = GetWindowLongPtrW(hwnd, -20);                                                // GWL_EXSTYLE
            if ((ex & 0x80) != 0 && (ex & 0x40000) == 0) return;                                  // tool window without APPWINDOW
            if ((ex & 0x40000) == 0 && GetWindow(hwnd, 4) != 0) return;                           // owned (dialogs, popups)
            if (ev == 0x8002) _appWins.Add(hwnd); else _appWins.Remove(hwnd);
            LayoutSoon();
        }
        catch { }
    }

    static readonly HashSet<nint> _appWins = new();

    /// Re-read the taskbar half a second after things settle (opening an app fires a burst of events).
    static void LayoutSoon() { if (!_hidden) SetTimer(_hwnd, T_TBDEB, 500, 0); }

    static nint Handle(nint h, uint m, nint w, nint l)
    {
        if (_deck != 0 && (h == _deck || h == _scrim)) return DeckProc(h, m, w, l);
        if (h != _hwnd && _hwnd != 0) return DefWindowProcW(h, m, w, l);   // side boxes: nothing to handle
        if (m == _tbCreated && _tbCreated != 0)
        {   // Explorer restarted: a new taskbar, with its background back. Clear it once it has settled.
            if (_cfg.ClearTaskbar) SetTimer(_hwnd, T_CLEAR, 2500, 0);
            LayoutSoon();
            return 0;
        }
        switch (m)
        {
            case 0x0002: AppBar(false); PostQuitMessage(0); return 0;       // WM_DESTROY
            case WM_APPBAR:                                                  // ABN_FULLSCREENAPP: one opened or closed
                if ((int)w == 2) { CheckFullscreen(); SetTimer(_hwnd, T_FS, 350, 0); }   // and again once it has finished resizing
                return 0;
            case 0x0021: return 3;                                           // WM_MOUSEACTIVATE -> NOACTIVATE
            case 0x0113: OnTimer((int)w); return 0;                         // WM_TIMER
            case 0x0200: OnMouseMove((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF)); return 0;
            case 0x02A3: OnMouseLeave(); return 0;
            case 0x0202: OnClick((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF)); return 0;
            case 0x0205: ShowMenu(); return 0;
            case 0x020A: OnWheel(-(short)((w >> 16) & 0xFFFF), false); return 0;   // WM_MOUSEWHEEL: down = next
            case 0x020E: OnWheel((short)((w >> 16) & 0xFFFF), false); return 0;    // WM_MOUSEHWHEEL: swipe right = next
            case 0x0100: OnKey((int)w); return 0;                                   // WM_KEYDOWN (we're foreground while open)
            case 0x0006: if ((w & 0xFFFF) == 0 && Expanded) Collapse(false); break; // WM_ACTIVATE: clicked elsewhere
            case 0x0020:                                                     // WM_SETCURSOR
                if (_hk != HK.None) { SetCursor(_hand); return 1; }
                break;
            case 0x02E0: case 0x007E:                                        // DPI / display change
                if (m == 0x007E) _monH = MonitorFromPoint(default, 1);       // screens may be gone: back to the main one
                _cb0 = _cb1 = -1; Place(); TaskbarProbe.Refresh(_hwnd, WM_LAYOUT, Tray()); Frame(); return 0;
            case 0x0312: if ((int)w == HOTKEY_ID) OnHotkey(); return 0;      // WM_HOTKEY
            case 0x001A:                                                     // WM_SETTINGCHANGE: light/dark or accent switched
                if (l != 0 && new string((char*)l) == "ImmersiveColorSet") ThemeChanged();
                break;
            case 0x0320: ThemeChanged(); return 0;                           // WM_DWMCOLORIZATIONCOLORCHANGED
            case WM_SOCK: OnSocket(); return 0;
            case WM_GIT: Invalidate(); return 0;
            case WM_LAYOUT: UpdatePlates(); Invalidate(); return 0;
            case WM_TICK: _tickPosted = false; PumpInput(); Frame(); return 0;
            case WM_PREVIEW: OnPreviews(); return 0;
        }
        return DefWindowProcW(h, m, w, l);
    }

    /// Posted messages outrank input in GetMessage, so a chain of WM_TICKs would keep clicks and keys
    /// waiting for as long as an animation runs. Handle any pending input before each animation frame.
    static void PumpInput()
    {
        MSG m;
        for (int i = 0; i < 64; i++)
        {
            if (PeekMessageW(&m, 0, 0x0100, 0x0109, 1) == 0 &&       // WM_KEYFIRST..WM_KEYLAST, PM_REMOVE
                PeekMessageW(&m, 0, 0x0200, 0x020E, 1) == 0) break;  // WM_MOUSEFIRST..WM_MOUSELAST
            TranslateMessage(&m); DispatchMessageW(&m);
        }
    }

    static void OnTimer(int id)
    {
        switch (id)
        {
            case T_ANIM: Frame(); break;
            case T_POLL: Poll(); break;
            case T_TB: if (!_hidden) TaskbarProbe.Refresh(_hwnd, WM_LAYOUT, Tray()); break;
            // the taskbar slides its buttons into place: read once soon, then again after the slide
            case T_TBDEB: KillTimer(_hwnd, T_TBDEB); TaskbarProbe.Refresh(_hwnd, WM_LAYOUT, Tray()); SetTimer(_hwnd, T_TBAGAIN, 1000, 0); break;
            case T_TBAGAIN: KillTimer(_hwnd, T_TBAGAIN); TaskbarProbe.Refresh(_hwnd, WM_LAYOUT, Tray()); break;
            case T_CLEAR: KillTimer(_hwnd, T_CLEAR); TaskbarClear.Launch(); break;
            case T_FS: KillTimer(_hwnd, T_FS); CheckFullscreen(); UpdateTuck(); break;
            // Decide from the real cursor position: redrawing a layered window can emit spurious leave events.
            case T_OUT: KillTimer(_hwnd, T_OUT); if (!CursorOver()) Collapse(); break;
        }
    }

    static void OnSocket()
    {
        if (_sock == 0) return;
        byte* buf = stackalloc byte[4096];
        int n, got = 0;
        while ((n = recvfrom(_sock, buf, 4096, 0, 0, 0)) > 0)
        {
            Model.Apply(Encoding.UTF8.GetString(buf, n));
            got++;
        }
        if (got == 0) return;
        ProcessAlerts();
        if (Expanded) RefreshUsage();
        Invalidate();
    }

    static void Poll()
    {
        long now = Model.Now;
        if (_hidden) { CheckFullscreen(); return; }
        // Collapsed: cheap listener poll, plus a process sweep every 3rd tick (~12 s) for open agent CLIs.
        if (Expanded || ++_pollN % 3 == 0) Probe.Poll(_cfg); else Probe.PollLight(_cfg);
        Model.Expire(now);
        CheckFullscreen();
        UpdateTuck();
        ZAll();
        if (Expanded) { RefreshGit(now); RefreshUsage(); }
        else if (!_deckShown)
        {   // hovered but never opened: drop the prefetched pictures
            if (_prev.Count > 0 && now - _prevAt > 10_000) FreePreviews(_prev);
            if (_scrimPre != 0 && _scrimBusy == 0 && now - _scrimPreAt > 8_000) { nint b = Interlocked.Exchange(ref _scrimPre, 0); if (b != 0) DeleteObject(b); }
        }
        int sig = Sig(now);
        if (Expanded || sig != _sig) { _sig = sig; Invalidate(); }
    }

    static void CheckFullscreen()
    {
        bool fs = false;
        if (_cfg.HideFullscreen && !Expanded && !_deckShown)   // our own scrim mustn't count as a fullscreen app
        {
            int st;
            if (SHQueryUserNotificationState(&st) == 0) fs = st is 2 or 3 or 4; // BUSY, D3D_FULLSCREEN, PRESENTATION
            // ...which misses borderless fullscreen (browser F11, video players, many games): check the window itself
            fs |= CoversMonitor(GetForegroundWindow(), _monH);
        }
        if (fs == _hidden) return;
        _hidden = fs;
        // sink into the screen edge (or rise back out of it) instead of blinking out
        _dropFrom = _drop; _dropAt = Hr;
        if (!fs) ShowWindow(_hwnd, 4);
        UpdatePlates();
        SetPoll(fs ? 8000 : Expanded ? 2000 : 4000);
        Frame();
    }

    /// A window that fills a whole monitor, taskbar area included: fullscreen video, a browser in F11, a borderless
    /// game. (A maximized window stops at the taskbar, so it doesn't count.) `mon` = 0 for any monitor.
    static bool CoversMonitor(nint h, nint mon)
    {
        if (h == 0 || h == _hwnd || h == _deck || h == _scrim || IsWindowVisible(h) == 0) return false;
        var cls = stackalloc char[64];
        var name = new ReadOnlySpan<char>(cls, GetClassNameW(h, cls, 64));
        if (name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;   // the desktop covers it too
        nint m = MonitorFromWindow(h, 2);
        if (m == 0 || (mon != 0 && m != mon)) return false;
        MONITORINFO mi = default; mi.cbSize = (uint)sizeof(MONITORINFO);
        RECT r;
        if (GetMonitorInfoW(m, &mi) == 0 || GetWindowRect(h, &r) == 0) return false;
        return r.L <= mi.rcMonitor.L && r.T <= mi.rcMonitor.T && r.R >= mi.rcMonitor.R && r.B >= mi.rcMonitor.B;
    }

    // ---- sink/rise animation for fullscreen
    static float _drop, _dropFrom; static long _dropAt;
    const int DropMs = 260;

    /// 0 = in place, 1 = fully sunk below the screen edge.
    static float DropAt(long hr)
    {
        float target = _hidden ? 1 : 0, p = _dropAt == 0 ? 1 : Math.Clamp((hr - _dropAt) / (float)DropMs, 0, 1);
        if (_cfg.LowMotion) p = 1;
        float e = _hidden ? p * p * (3 - 2 * p) : 1 - (1 - p) * (1 - p) * (1 - p);   // smooth in going down, ease out coming up
        return _dropFrom + (target - _dropFrom) * e;
    }

    // ---- appbar registration: Explorer then tells us whenever a fullscreen app opens or closes (ABN_FULLSCREENAPP)
    const uint WM_APPBAR = 0x8006;
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct APPBARDATA { public uint cbSize; public nint hWnd; public uint uCallbackMessage, uEdge; public RECT rc; public nint lParam; }
    [System.Runtime.InteropServices.DllImport("shell32.dll")] static extern nuint SHAppBarMessage(uint msg, APPBARDATA* d);
    static bool _appbar;

    static void AppBar(bool on)
    {
        if (on == _appbar) return;
        // registered but never positioned: it reserves no screen space, it only gets notifications
        var d = new APPBARDATA { cbSize = (uint)sizeof(APPBARDATA), hWnd = _hwnd, uCallbackMessage = WM_APPBAR };
        SHAppBarMessage(on ? 0u : 1u, &d);   // ABM_NEW / ABM_REMOVE
        _appbar = on;
    }

    // ================================================================ input

    static bool Expanded => _tTo > 0.5f;

    static void OnMouseMove(int x, int y)
    {
        if (!_tracking)
        {
            var t = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = 2, hwndTrack = _hwnd };
            TrackMouseEvent(&t);
            _tracking = true;
        }
        KillTimer(_hwnd, T_OUT);
        float dx = x / _s, dy = y / _s;
        HK k = HK.None; string? arg = null;
        foreach (var hit in _hits) if (hit.R.Contains(dx, dy)) { k = hit.K; arg = hit.Arg; break; }
        if (k == HK.Open && !Expanded) { Prefetch(); RefreshUsage(); }
        if (k != _hk || arg != _harg) { _hk = k; _harg = arg; Invalidate(); }
    }

    static bool CursorOver()
    {
        POINT p; GetCursorPos(&p);
        return p.X >= _win.L && p.X < _win.R && p.Y >= _win.T && p.Y < _win.B;
    }

    static void OnMouseLeave()
    {
        _tracking = false;
        if (_hk != HK.None) { _hk = HK.None; _harg = null; Invalidate(); }
    }

    static void OnClick(int x, int y)
    {
        // hit-test the click itself (touch and pen taps arrive without a hover first)
        float dx = x / _s, dy = y / _s;
        foreach (var hit in _hits) if (hit.R.Contains(dx, dy)) { _hk = hit.K; _harg = hit.Arg; break; }
        if (_hk is HK.Allow or HK.Deny) { SendVerdict(_harg!, _hk == HK.Allow); return; }
        if (!Expanded) { if (_hk == HK.Open) Expand(); return; }
        switch (_hk)
        {
            case HK.Close: Collapse(); break;
            case HK.Tab: GoTab(int.Parse(_harg!)); break;
        }
        Invalidate();
    }

    static void ShowMenu()
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        bool auto;
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) auto = k?.GetValue("notchbar") != null;
        nint m = CreatePopupMenu();
        AppendMenuW(m, auto ? 0x8u : 0, 1, "Start with Windows");
        AppendMenuW(m, 0, 2, "Edit config");
        AppendMenuW(m, 0, 3, "Reload config");
        AppendMenuW(m, 0x800, 0, null);
        AppendMenuW(m, 0, 9, "Quit notchbar");
        POINT p; GetCursorPos(&p);
        SetForegroundWindow(_hwnd);
        int cmd = TrackPopupMenu(m, 0x100 | 0x2 | 0x80, p.X, p.Y, 0, _hwnd, 0); // RETURNCMD | RIGHTBUTTON | NONOTIFY
        DestroyMenu(m);
        switch (cmd)
        {
            case 1:
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                    if (auto) k.DeleteValue("notchbar", false); else k.SetValue("notchbar", $"\"{Environment.ProcessPath}\"");
                break;
            case 2: Config.EnsureFile(); ShellExecuteW(0, "open", "notepad.exe", $"\"{Config.FilePath}\"", null, 1); break;
            case 3:
                _cfg = Config.Load(); Model.Pin(_cfg.Repos); RegisterHotkey(); ThemeChanged();
                if (!_cfg.FollowMonitor && _monH != MonitorFromPoint(default, 1)) { _monH = MonitorFromPoint(default, 1); _cb0 = _cb1 = -1; Place(); TaskbarProbe.Refresh(_hwnd, WM_LAYOUT, Tray()); }
                _tuckWant = false; CheckFullscreen(); UpdateTuck(); Poll(); break;
            case 9: DestroyWindow(_hwnd); break;
        }
    }

    // ================================================================ animation

    static readonly bool Prof = Environment.GetEnvironmentVariable("NOTCHBAR_PROF") == "1";
    static void P(string what, long t0) { if (Prof) try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "notchbar-prof.txt"), $"{what} {(System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.00}ms" + Environment.NewLine); } catch { } }

    static void Expand()
    {
        if (Expanded) return;
        long p0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _prevFg = GetForegroundWindow();
        SortLists(); BuildTerms();
        RefreshUsage();
        // an agent is waiting for Allow / Deny: open straight onto its card
        for (int i = 0; i < _terms.Count && i < MaxCards; i++)
            if (_terms[i].AskPort > 0 && _terms[i].State == "wait" && _terms[i].Verdict.Length == 0) { _tab = 0; _tabPos = 0; _focus[0] = i; break; }
        if (Model.Now - _prevAt > 3000) CapturePreviews();
        EnsureDeck();
        _fpos = _focus[_tab]; _enterAt = 0;
        ShowScrim();
        P("expand-prep", p0);
        Animate(1, 420);
        // take the keyboard (arrows, Esc) while open, then tuck the island back under the taskbar
        SetForegroundWindow(_hwnd);
        ZAll();
        SetPoll(2000);   // the next poll (process sweep + git) runs after the animation
    }

    /// restoreFocus: give the keyboard back to the window that had it (not when the user picked another one).
    static void Collapse(bool restoreFocus = true)
    {
        if (!Expanded) return;
        _viaHotkey = false;
        _hits.Clear(); _hk = HK.None; _harg = null;
        _dhits.Clear(); _dhk = HK.None; _dharg = null;
        if (restoreFocus && GetForegroundWindow() == _hwnd && _prevFg != 0 && IsWindow(_prevFg) != 0) SetForegroundWindow(_prevFg);
        _prevFg = 0;
        Animate(0, 300);
        SetPoll(4000);
        ZAll();
    }

    static void Animate(float to, int ms)
    {
        long now = Hr;
        _tFrom = T(now); _tTo = to; _aStart = now; _aDur = ms;
        Frame();
    }

    static float T(long now)
    {
        float p = _aDur <= 0 ? 1 : Math.Clamp((now - _aStart) / (float)_aDur, 0, 1);
        float e;
        if (_tTo > _tFrom) { float x = p - 1; const float c1 = 0.9f, c3 = c1 + 1; e = 1 + c3 * x * x * x + c1 * x * x; } // easeOutBack
        else { float x = 1 - p; e = 1 - x * x * x; }                                                                  // easeOutCubic
        return _tFrom + (_tTo - _tFrom) * e;
    }

    static bool Approach(ref float v, float target, float k)
    {
        if (MathF.Abs(target - v) < 0.3f) { v = target; return false; }
        v += (target - v) * k;
        return true;
    }

    static bool ApproachF(ref float v, float target, float k)
    {
        if (MathF.Abs(target - v) < 0.002f) { v = target; return false; }
        v += (target - v) * k;
        return true;
    }

    static void SetAnim(int ms)
    {
        if (ms == _animMs) return;
        if (ms == 0) KillTimer(_hwnd, T_ANIM); else SetTimer(_hwnd, T_ANIM, (uint)ms, 0);
        _animMs = ms;
    }

    static void SetPoll(int ms)
    {
        if (ms == _pollMs) return;
        SetTimer(_hwnd, T_POLL, (uint)ms, 0);
        _pollMs = ms;
    }

    /// Render now unless a fast animation timer is about to anyway.
    static void Invalidate() { if (_animMs == 0 || _animMs > 50) Frame(); }

    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    // ================================================================ geometry

    /// The taskbar's center box (Start + apps), in monitor DIPs. Without a readable taskbar it
    /// is a box just wide enough for the notch, centered at the bottom of the screen.
    static void TargetBox(out float x0, out float x1, out float top, out float bot)
    {
        var l = TaskbarProbe.Current;
        RECT bar = l?.Bar ?? default;
        if (bar.R <= bar.L || bar.B - bar.T < 20 || bar.T >= _mon.B - 6)
            bar = new RECT { L = _mon.L, T = _mon.B - (int)(48 * _s), R = _mon.R, B = _mon.B };
        top = (bar.T - _mon.T) / _s + 2;
        bot = (bar.B - _mon.T) / _s - 2;
        if (l is { HasCenter: true })
        {
            x0 = (l.Center.L - _mon.L) / _s - 6;
            x1 = (l.Center.R - _mon.L) / _s + 6;
        }
        else
        {
            float c = (_mon.R - _mon.L) / _s / 2, hw = MathF.Max(_tabW / 2 + BR + FIL + 12, 170);
            x0 = c - hw; x1 = c + hw;
        }
    }

    static Geo Geom(float t)
    {
        TargetBox(out float tx0, out float tx1, out float top, out float bot);
        float cb0 = _cb0 < 0 ? tx0 : _cb0, cb1 = _cb0 < 0 ? tx1 : _cb1;
        float tc = Math.Clamp(t, 0, 1), cbc = (cb0 + cb1) / 2, monW = (_mon.R - _mon.L) / _s;
        float hw = HdrWidth(), pcx = Math.Clamp(cbc, hw / 2 + 8, monW - hw / 2 - 8);
        float nw = Lerp(_tabW, hw, t), nh = Lerp(Lerp(TabH + _hov * _hovH, LipH, _tuck), HH, t), ncx = Lerp(cbc, pcx, tc);
        return new Geo
        {
            Cb0 = cb0, Cb1 = cb1, By0 = top, By1 = bot,
            Nx0 = ncx - nw / 2, Nx1 = ncx + nw / 2, Ny0 = top - nh, Nw = nw, Nh = nh, Pcx = pcx,
            Rn = Lerp(12, 14, tc),
        };
    }

    /// The island outline: the center box with the notch/panel standing on it, joined by concave
    /// fillets. Each side is worked out on its own, since the panel can overhang the box on one side only.
    static void ShapePath(in Geo q, float ox, float oy)
    {
        var g = _g;
        float bx0 = q.Cb0 + ox, bx1 = q.Cb1 + ox, by0 = q.By0 + oy, by1 = q.By1 + oy;
        float nx0 = q.Nx0 + ox, nx1 = q.Nx1 + ox, ny0 = q.Ny0 + oy;
        float gl = nx0 - bx0, gr = bx1 - nx1;
        // fillets shrink with a low notch (tucked lip): half its height, so the top corners keep a radius too
        float fl = MathF.Min(MathF.Min(FIL, q.Nh / 2), MathF.Abs(gl) / 2), fr = MathF.Min(MathF.Min(FIL, q.Nh / 2), MathF.Abs(gr) / 2);
        float rn = MathF.Max(0, MathF.Min(q.Rn, MathF.Min(q.Nh - MathF.Max(fl, fr), q.Nw / 2)));
        g.PathBegin();
        // left join
        if (gl >= 0)
        {
            float r1 = MathF.Min(BR, MathF.Max(0, gl - fl));
            g.PathArc(bx0 + r1, by0 + r1, r1, 180, 90);          // box top-left
            g.PathArc(nx0 - fl, by0 - fl, fl, 90, -90);          // concave fillet into the notch
        }
        else
        {
            float rb = MathF.Max(0, MathF.Min(RB, MathF.Min(-gl - fl, q.Nh - rn)));
            g.PathArc(bx0 - fl, by0 + fl, fl, 0, -90);           // concave fillet out to the panel
            g.PathArc(nx0 + rb, by0 - rb, rb, 90, 90);           // panel bottom-left
        }
        g.PathArc(nx0 + rn, ny0 + rn, rn, 180, 90);              // notch top-left
        g.PathArc(nx1 - rn, ny0 + rn, rn, 270, 90);              // notch top-right
        // right join
        if (gr >= 0)
        {
            float r1 = MathF.Min(BR, MathF.Max(0, gr - fr));
            g.PathArc(nx1 + fr, by0 - fr, fr, 180, -90);
            g.PathArc(bx1 - r1, by0 + r1, r1, 270, 90);
        }
        else
        {
            float rb = MathF.Max(0, MathF.Min(RB, MathF.Min(-gr - fr, q.Nh - rn)));
            g.PathArc(nx1 - rb, by0 - rb, rb, 0, 90);
            g.PathArc(bx1 + fr, by0 + fr, fr, 270, -90);
        }
        g.PathArc(bx1 - BR, by1 - BR, BR, 0, 90);                // box bottom-right
        g.PathArc(bx0 + BR, by1 - BR, BR, 90, 90);               // box bottom-left
        g.PathClose();
    }

    // ================================================================ side boxes

    static void UpdatePlates()
    {
        var l = TaskbarProbe.Current;
        bool show = l != null && !_hidden && l.Bar.B - l.Bar.T > 20 && l.Bar.T < _mon.B - 6;
        int i2 = (int)(2 * _s), i4 = (int)(4 * _s);
        Plate(_sideL, ref _gL, show && l!.HasLeft, l == null ? default : new RECT { L = l.Left.L - i2, T = l.Bar.T + i2, R = l.Left.R + i2, B = l.Bar.B - i2 });
        Plate(_sideR, ref _gR, show && l!.HasRight, l == null ? default : new RECT { L = l.Right.L - i4, T = l.Bar.T + i2, R = l.Right.R + i4, B = l.Bar.B - i2 });
    }

    static void Plate(nint h, ref Gfx? g, bool show, RECT r)
    {
        int w = r.R - r.L, hh = r.B - r.T;
        if (!show || w <= 4 || hh <= 4) { ShowWindow(h, 0); return; }
        if (g == null || g.W < w + 2 || g.H < hh + 2) { g?.Dispose(); g = new Gfx(_s, w / _s + 4, hh / _s + 4); }
        float wd = w / _s, hd = hh / _s;
        g.Clear(w, hh);
        g.Begin();
        g.FillRR(0, 0, wd, hd, BR, C.Glass);
        g.StrokeRR(0.5f, 0.5f, wd - 1, hd - 1, BR - 0.5f, C.Edge, 1);
        g.Flush();
        Present(h, r.L, r.T, w, hh, g.Dc);
        ShowWindow(h, 4);
        ZOrder(h);
    }

    static void Present(nint h, int x, int y, int w, int hh, nint dc, byte alpha = 255)
    {
        POINT dst = new() { X = x, Y = y }, src = default;
        SIZE sz = new() { W = w, H = hh };
        BLENDFUNCTION bf = new() { SourceConstantAlpha = alpha, AlphaFormat = 1 };
        UpdateLayeredWindow(h, 0, &dst, &sz, dc, &src, 0, &bf, 2);
    }

    // ================================================================ frame

    static void Frame()
    {
        long p0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try { FrameCore(); } finally { if (_tTo > 0.5f || Hr - _aStart < _aDur) P("frame", p0); }
        if (_wantTick && !_tickPosted)
        {   // pace animation frames to the display: wait for the compositor, then draw the next one
            _wantTick = false; _tickPosted = true;
            DwmFlush();
            PostMessageW(_hwnd, WM_TICK, 0, 0);
        }
    }

    static void FrameCore()
    {
        long now = Model.Now, hr = Hr;
        _drop = DropAt(hr);
        if (_hidden && _drop >= 0.999f) { ShowWindow(_hwnd, 0); _win = default; SetAnim(0); return; }
        bool dropping = _drop > 0.001f && _drop < 0.999f;
        float dt = _lastFrame == 0 ? 1 : Math.Clamp((hr - _lastFrame) / 1000f, 0.001f, 0.25f);
        _lastFrame = hr;
        float t = T(hr), tc = Math.Clamp(t, 0, 1);
        var pill = ComputePill(now);
        bool panel = tc > 0 || _tTo > 0;
        if (!panel && _g.HasLayer) _g.FreeLayer();
        if (panel) { SortLists(); BuildTerms(); }

        TargetBox(out float tx0, out float tx1, out _, out _);
        if (_cb0 < 0) { _cb0 = tx0; _cb1 = tx1; }
        float k = 1 - MathF.Exp(-dt * 12);
        bool hovering = Hover(pill, dt) | StepTuck(dt);
        bool settling = Approach(ref _cb0, tx0, k) | Approach(ref _cb1, tx1, k)
                      | Approach(ref _tabW, TabWidth(pill, tx1 - tx0), k) | ApproachF(ref _tabPos, _tab, k) | hovering;
        var q = Geom(t);

        // window = island bounds + room for the shadow; snap the origin to whole pixels
        float L = MathF.Floor((MathF.Min(q.Cb0, q.Nx0) - SP) * _s) / _s, Tp = MathF.Floor((q.Ny0 - SP) * _s) / _s;
        float Rr = MathF.Max(q.Cb1, q.Nx1) + SP, Bt = q.By1 + 1;
        EnsureGfx(Rr - L, Bt - Tp);
        int bw = Math.Min(_g.W, (int)MathF.Ceiling((Rr - L) * _s)), bh = Math.Min(_g.H, (int)MathF.Ceiling((Bt - Tp) * _s));
        float ox = -L, oy = -Tp;

        var g = _g;
        float ca = Math.Clamp(1 - t * 2.6f, 0, 1), ea = Math.Clamp((t - 0.35f) / 0.45f, 0, 1);
        bool steady = ea >= 1 && hr - _aStart >= _aDur && !settling;
        g.Clear(bw, bh);
        g.OffY = _drop * (Bt - Tp + 2);   // fullscreen: everything slides down and is cut off by the window's bottom edge
        g.Begin();
        _hits.Clear();
        if (steady)
        {   // the body (snapshot, tint, outline, shadow) only changes with the geometry: cache it
            var hc = new HashCode(); hc.Add(bw); hc.Add(bh); hc.Add(q.Cb0); hc.Add(q.Cb1); hc.Add(q.Nx0); hc.Add(q.Nw); hc.Add(_s);
            int key = hc.ToHashCode();
            if (key != _layerKey || !g.HasLayer)
            {
                g.BeginLayer();
                Body(q, ox, oy, tc, ea);
                g.EndLayer();
                _layerKey = key;
            }
            g.DrawLayer(0, 0, bw / _s, bh / _s);
        }
        else
        {
            if (ca > 0 && pill.Icon == Ico.Pulse) Halo(q, ox, oy, pill.IconColor, ca, now);
            Body(q, ox, oy, MathF.Max(tc, _hov * 0.55f), ea);
            if (ca > 0 && _hov > 0.01f) { ShapePath(q, ox, oy); g.Stroke(C.A(C.Text, 0.10f * _hov * ca), 1); }   // the rim lights up on hover
        }
        bool flashing = ca > 0 && Flash(q, ox, oy, ca, hr);

        if (ca > 0) { g.Alpha = ca; DrawTab(pill, q.Nx0 + ox, q.Ny0 + oy, q.Nw, q.Nh, now); }
        if (ea > 0)
        {   // the page switcher, laid out at its final width and revealed by the growing shape
            g.Alpha = ea;
            float hw = HdrWidth();
            bool clip = q.Nw < hw - 0.5f;
            if (clip) g.ClipRect(q.Nx0 + ox + 2, q.Ny0 + oy, q.Nw - 4, q.Nh);
            DrawHeader((q.Nx0 + q.Nx1) / 2 - hw / 2 + ox, q.Ny0 + oy, hw, ea > 0.85f && _tTo > 0.5f);
            if (clip) g.Unclip();
        }
        g.Alpha = 1;
        g.Flush();

        int wx = _mon.L + (int)MathF.Round(L * _s), wy = _mon.T + (int)MathF.Round(Tp * _s);
        Present(_hwnd, wx, wy, bw, bh, g.Dc, (byte)(255 * (1 - _drop * _drop * 0.7f)));
        _win = new RECT { L = wx, T = wy, R = wx + bw, B = wy + bh };

        bool deckMoving = DeckFrame(Geom(1), tc, hr, now, 1 - MathF.Exp(-dt * 14));
        bool anim = hr - _aStart < _aDur || settling || deckMoving || flashing || dropping;
        bool live = (ca > 0 && (pill.Icon is Ico.Spin or Ico.Pulse || pill.HasProg && pill.Prog < 0)) || (ea > 0 && PanelLive());
        // open cards with a running clock (a build, a working agent) tick once a second
        bool clocks = ea > 0 && _tTo > 0.5f && (_tab == 0 ? _terms.Exists(x => x.State == "busy") : _tab == 1 && _tasks.Exists(x => x.Running));
        if (anim)
        {   // pace animation frames to the display: wait for the compositor, then draw the next one
            SetAnim(0);
            _wantTick = true;
        }
        else SetAnim(live ? (_cfg.LowMotion ? 1000 : 50) : clocks ? 1000 : 0);
    }

    // ---- hover: the notch lifts a little and reveals a second line (spring, slight overshoot)

    static float _hov, _hovV, _hovH;

    static bool Hover(in Pill p, float dt)
    {
        float target = !Expanded && _tTo < 0.5f && _hk is HK.Open or HK.Allow or HK.Deny ? 1 : 0;
        float h = p.Sub is { Length: > 0 } ? 17 : 4;
        if (target > 0) _hovH = h;
        if (_cfg.LowMotion) { bool ch = _hov != target; _hov = target; _hovV = 0; return ch; }
        if (MathF.Abs(target - _hov) < 0.002f && MathF.Abs(_hovV) < 0.02f) { _hov = target; _hovV = 0; return false; }
        for (float left = dt; left > 0; left -= 1 / 240f)
        {   // small fixed steps keep the spring stable whatever the frame gap
            float step = MathF.Min(left, 1 / 240f);
            float acc = 420 * (target - _hov) - 30 * _hovV;
            _hovV += acc * step; _hov += _hovV * step;
        }
        _hov = Math.Clamp(_hov, -0.1f, 1.15f);
        return true;
    }

    // ---- status glow: amber breathing halo while an agent needs you; a ripple when something finishes

    static void Halo(in Geo q, float ox, float oy, uint col, float ca, long now)
    {
        float br = _cfg.LowMotion ? 0.6f : 0.5f + 0.5f * MathF.Sin(now / 1800f * MathF.Tau);
        for (int i = 1; i <= 4; i++)
        {
            float e = i * 2.4f;
            _g.FillRR(q.Nx0 + ox - e, q.Ny0 + oy - e, q.Nw + 2 * e, q.Nh + e, q.Rn + e, C.A(col, (0.035f + 0.045f * br) * ca));
        }
    }

    static bool Flash(in Geo q, float ox, float oy, float ca, long hr)
    {
        const float D = 1100;
        float p = (hr - _flashAt) / D;
        if (_flashAt == 0 || p >= 1 || p < 0) return false;
        var g = _g;
        float x0 = q.Nx0 + ox, y0 = q.Ny0 + oy, by = q.By0 + oy;
        g.ClipRect(x0 - 24, y0 - 24, q.Nw + 48, by - y0 + 23.5f);   // above the box: the ring hugs the notch only
        float e = _cfg.LowMotion ? 0 : Ease(p) * 9, fade = (1 - p) * (1 - p);
        g.StrokeRR(x0 - e + 0.5f, y0 - e + 0.5f, q.Nw + 2 * e - 1, q.Nh + 2 * e, q.Rn + e, C.A(_flashCol, 0.85f * fade * ca), 1.6f);
        g.StrokeRR(x0 + 0.5f, y0 + 0.5f, q.Nw - 1, q.Nh + 4, q.Rn, C.A(_flashCol, 0.55f * (1 - p) * ca), 1.2f);
        g.Unclip();
        return true;
    }

    /// Glass body of the island: soft shadow while open, the blurred snapshot under the panel, tint, rim.
    static bool _tickPosted, _wantTick;

    static void Body(in Geo q, float ox, float oy, float tc, float ea)
    {
        var g = _g;
        if (tc > 0.02f)   // three wide, faint layers: as soft as six, half the fill cost
            for (int i = 1; i <= 3; i++)
                g.FillRR(q.Nx0 + ox - i * 3.2f, q.Ny0 + oy + 6 - i * 2, q.Nw + i * 6.4f, q.Nh + i * 2.4f, q.Rn + i * 3.2f, C.A(0, 0.055f * tc));
        ShapePath(q, ox, oy);
        g.Fill(C.Glass);
        g.Stroke(C.Edge, 1);
    }

    static bool PanelLive() => MathF.Abs(_tabPos - _tab) > 0.002f;

    // ================================================================ header (the open notch)

    static float HdrWidth()
    {
        float w = 7;
        for (int i = 0; i < Tabs; i++) w += TabW(i) + 2;
        return w + 8 + _g.Measure("← →", _g.FMonoSm) + 10 + 28 + 8;
    }

    static float TabW(int i)
    {
        string c = TabCount(i);
        return 12 + _g.Measure(TabNames[i], _g.FTab) + (c.Length > 0 ? 6 + _g.Measure(c, _g.FMonoSm) : 0) + 12;
    }

    static void DrawHeader(float x0, float y0, float w, bool hit)
    {
        var g = _g;
        float ty = y0 + (HH - 32) / 2, x = x0 + 7;
        Span<float> xs = stackalloc float[Tabs], ws = stackalloc float[Tabs];
        for (int i = 0; i < Tabs; i++) { xs[i] = x; ws[i] = TabW(i); x += ws[i] + 2; }
        int a = Math.Clamp((int)MathF.Floor(_tabPos), 0, Tabs - 1), b = Math.Min(Tabs - 1, a + 1);
        float f = _tabPos - a, hx = Lerp(xs[a], xs[b], f), hw = Lerp(ws[a], ws[b], f);
        g.FillRR(hx, ty, hw, 32, 9, C.SelFill);
        g.StrokeRR(hx + 0.5f, ty + 0.5f, hw - 1, 31, 8.5f, C.SelEdge, 1);
        int nc = _cards.Count;
        if (nc > 1 && _deckShown)
        {   // page dots under the selected tab: which card of this page is in front
            float dcx = hx + hw / 2, dy = ty + 32 + 3.2f, gap = 6.5f, d0 = dcx - (nc - 1) * gap / 2;
            for (int i = 0; i < nc; i++)
            {
                float on = MathF.Max(0, 1 - MathF.Abs(i - _fpos));
                g.FillRR(d0 + i * gap - 1.4f - on * 2.2f, dy - 1.4f, 2.8f + on * 4.4f, 2.8f, 1.4f, C.A(on > 0.5f ? C.Accent : C.Faint, 0.55f + 0.45f * on));
            }
        }
        bool waiting = false; foreach (var t in _terms) if (t.State == "wait") waiting = true;
        for (int i = 0; i < Tabs; i++)
        {
            bool on = i == _tab, hot = !on && Hot(HK.Tab, i.ToString());
            float nw = g.Measure(TabNames[i], g.FTab);
            g.Text(TabNames[i], g.FTab, on ? C.Text : hot ? C.Dim : C.Faint, xs[i] + 12, ty, nw + 2, 32);
            string c = TabCount(i);
            if (c.Length > 0) g.Text(c, g.FMonoSm, i == 0 && waiting ? C.Warn : C.Faint, xs[i] + 12 + nw + 6, ty + 0.5f, 30, 32);
            AddHit(hit, xs[i], ty, ws[i], 32, HK.Tab, i.ToString());
        }
        float rx = x0 + w - 8;
        bool hotX = Hot(HK.Close, "");
        if (hotX) g.FillRR(rx - 28, ty + 2, 28, 28, 8, C.Hover);
        g.Line(rx - 18, ty + 14, rx - 14, ty + 18, 1.6f, hotX ? C.Text : C.Faint);
        g.Line(rx - 14, ty + 18, rx - 10, ty + 14, 1.6f, hotX ? C.Text : C.Faint);
        AddHit(hit, rx - 28, ty + 2, 28, 28, HK.Close, "");
        g.Text("← →", g.FMonoSm, C.Faint, x0, ty, rx - 28 - 10 - x0, 32, 2);
    }

    // ================================================================ collapsed notch

    /// Agents shown as mini glyphs: other busy/waiting hook sessions, then agent CLIs seen running.
    static readonly List<(string Name, uint Col)> _dock = new();
    static int BuildDock(AgentInfo? primary)
    {
        _dock.Clear();
        foreach (var a in Model.Agents)
            if (a != primary && a.State is "busy" or "wait") _dock.Add((a.Name, a.State == "wait" ? C.Warn : C.Text));
        foreach (var n in Probe.AgentNames)
        {
            bool shown = primary != null && primary.Name.Equals(n, StringComparison.OrdinalIgnoreCase);
            foreach (var d in _dock) if (d.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) shown = true;
            if (!shown) _dock.Add((n, C.Dim));
        }
        if (_dock.Count > 3) _dock.RemoveRange(3, _dock.Count - 3);
        return _dock.Count;
    }

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    static (string Ev, string Tool, string Detail) ToolOf(string tool)
    {
        var p = tool.Split('\t');
        return (p[0], p.Length > 1 ? p[1] : "", p.Length > 2 ? p[2] : "");
    }

    /// "Editing billing.ts", "Running npm test", ... from the agent's current tool call.
    static string Doing(AgentInfo a) => Doing(a.Tool);

    static string Doing(string tool)
    {
        var (ev, tn, td) = ToolOf(tool);
        if (ev != "PreToolUse" || tn.Length == 0) return "Thinking";
        return tn switch
        {
            "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => "Editing " + Fmt.Dir(td),
            "Read" => "Reading " + Fmt.Dir(td),
            "Bash" => "Running " + FirstWords(td, 3),
            "Grep" or "Glob" => "Searching the code",
            "WebFetch" or "WebSearch" => "Browsing the web",
            "Task" => "Running a subagent",
            "TodoWrite" => "Planning",
            _ => "Using " + tn,
        };
    }

    static string FirstWords(string s, int n)
    {
        int i = 0, seen = 0;
        for (; i < s.Length; i++) if (s[i] == ' ' && ++seen == n) break;
        return s[..i];
    }

    static Pill ComputePill(long now)
    {
        AgentInfo? wait = null, busy = null, done = null;
        foreach (var a in Model.Agents)
        {
            if (a.State == "wait" && (wait == null || a.Updated > wait.Updated)) wait = a;
            else if (a.State == "busy" && (busy == null || a.Updated > busy.Updated)) busy = a;
            else if (a.State == "done" && (done == null || a.Since > done.Since)) done = a;
        }
        TaskInfo? run = null, fin = null; int nRun = 0;
        foreach (var t in Model.Tasks)
        {
            if (t.Running) { nRun++; if (run == null || t.Start > run.Start) run = t; }
            else if (fin == null || t.End > fin.End) fin = t;
        }

        if (wait != null)
        {
            const string perm = "Claude needs your permission to use ";
            var (_, tn, td) = ToolOf(wait.Tool);
            bool asks = wait.Msg.StartsWith(perm, StringComparison.Ordinal);
            string tool = asks ? wait.Msg[perm.Length..] : "";
            return new Pill { Dock = wait.AskPort > 0 ? 0 : BuildDock(wait), Ask = wait.AskPort > 0 ? wait.Key : null, Icon = Ico.Pulse, IconColor = C.Warn, Agent = wait.Name,
                Title = asks ? tool : wait.Msg.Length > 0 ? wait.Msg : wait.Name + " needs you",
                Detail = asks && tn == tool && td.Length > 0 ? td : Fmt.Dir(wait.Cwd),
                Sub = AgentTitle(wait.Name) + " in " + Fmt.Dir(wait.Cwd) + (wait.AskPort > 0 ? "  ·  " + HotkeyHint("open, then A / D") : "") };
        }
        if (run != null)
        {
            long eta = Model.EtaFor(run.Label);
            return new Pill { Dock = BuildDock(null), Icon = Ico.Spin, IconColor = C.Run, Title = run.Label,
                Detail = (nRun > 1 ? $"+{nRun - 1} · " : "") + Fmt.Clock(now - run.Start) + (eta > 0 ? " / ~" + Fmt.Clock(eta) : ""),
                HasProg = true, Prog = eta > 0 ? Math.Clamp((now - run.Start) / (float)eta, 0, 1) : -1,
                Sub = ShortPath(run.Cwd) + (eta > 0 ? $"  ·  {Math.Clamp((now - run.Start) * 100 / eta, 0, 99)}% of the usual time" : "  ·  first run") };
        }
        if (fin != null && now - fin.End < (fin.Code == 0 ? 10_000 : 30_000))
        {
            bool ok = fin.Code == 0;
            return new Pill { Dock = BuildDock(null), Icon = ok ? Ico.Check : Ico.Cross, IconColor = ok ? C.Ok : C.Err,
                Title = ok ? fin.Label : fin.Label + " failed", Detail = ResultText(fin), HasProg = !ok, Prog = 1, Err = !ok,
                Sub = ShortPath(fin.Cwd) + "  ·  " + (ok ? "passed in " + Fmt.Dur(fin.End - fin.Start) : "click to see it, Enter to run again") };
        }
        if (done != null && now - done.Since < 8_000)
            return new Pill { Dock = BuildDock(done), Icon = Ico.Check, IconColor = C.Ok, Agent = done.Name, Title = done.Name + " finished", Detail = Fmt.Dir(done.Cwd),
                Sub = Quote(done.Prompt) };
        if (now - Model.NoteAt < 6_000 && Model.Note.Length > 0)
            return new Pill { Icon = Ico.Dot, IconColor = C.Text, Title = Model.Note, Detail = "" };
        if (busy != null)
            return new Pill { Dock = BuildDock(busy), Icon = Ico.Spin, IconColor = C.Run, Agent = busy.Name, Title = Doing(busy), Detail = Fmt.Clock(now - busy.Since),
                Sub = Quote(busy.Prompt) is { Length: > 0 } q ? q : AgentTitle(busy.Name) + " in " + Fmt.Dir(busy.Cwd) };
        string ports = Probe.Servers.Count switch { 0 => "", 1 => ":" + Probe.Servers[0].Port, var n => $"{n} ports" };
        if (BuildDock(null) > 0)
        {
            int n = _dock.Count;
            var (n0, _) = _dock[0];
            return new Pill { Dock = n, Icon = Ico.Dot, IconColor = C.Ok, Title = n == 1 ? AgentTitle(n0) + " open" : $"{n} agents open", Detail = ports,
                Sub = HotkeyHint("see them") };
        }
        if (ports.Length > 0)
            return new Pill { Icon = Ico.Dot, IconColor = C.Ok, Title = ports, Detail = Probe.Servers[0].Proc,
                Sub = string.Join("  ", Probe.Servers.Take(5).Select(s => ":" + s.Port + " " + s.Proc)) };
        return new Pill { Icon = Ico.Dot, IconColor = C.Faint, Title = "All quiet", Detail = "", Sub = HotkeyHint("agents, builds, ports, git") };
    }

    static string Quote(string prompt) => prompt.Length == 0 ? "" : "“" + (prompt.Length > 90 ? prompt[..89] + "…" : prompt) + "”";

    /// "Win+Alt+N  ·  see them" (or just the words when there's no shortcut).
    static string HotkeyHint(string what)
    {
        if (!ParseHotkey(_cfg.Hotkey, out _, out _)) return "Click: " + what;
        var parts = _cfg.Hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join("+", parts.Select(Cap)) + ": " + what;
    }

    static string ResultText(TaskInfo t) =>
        t.Code == 0 ? Fmt.Dur(t.End - t.Start) : t.Code == TaskInfo.Lost ? "lost" : "exit " + t.Code;

    static float TabWidth(in Pill p, float boxW)
    {
        float w = 8 + 22 + 9 + MathF.Min(_g.Measure(p.Title, _g.FSemi), 220)
                + (p.Detail.Length > 0 ? 8 + MathF.Min(_g.Measure(p.Detail, _g.FMonoSm), 220) : 0)
                + (p.Dock > 0 ? p.Dock * 14 + 12 : 0) + (p.Ask != null ? 112 : 0) + 14;
        if (_hov > 0.01f && p.Sub is { Length: > 0 })   // room for the hover line, and a slight lift either way
            w = Lerp(w, MathF.Max(w, MathF.Min(_g.Measure(p.Sub, _g.FSmall), 380) + 32), Math.Clamp(_hov, 0, 1));
        w += 8 * Math.Clamp(_hov, 0, 1);
        float max = MathF.Max(120, boxW - 2 * (BR + FIL) - 4);
        return Math.Clamp(MathF.Ceiling(w), 120, MathF.Min(p.Ask != null ? 560 : 460, max));
    }

    static void DrawTab(in Pill p, float x0, float y0, float w, float h, long now)
    {
        var g = _g;
        float yTop = y0, hFull = h;
        float tk = Math.Clamp(_tuck, 0, 1);
        if (tk > 0.01f) LipStatus(p, x0, yTop, w, now, Math.Clamp((tk - 0.4f) / 0.6f, 0, 1));
        if (tk > 0.6f)
        {   // tucked: only the lip, plus an all-but-invisible strip above it so it's easy to hover
            // (a layered window lets the mouse through where alpha is 0, not where it is 1)
            g.FillRect(x0, yTop - 6, w, 6, 0x01000000);
            AddHit(true, x0, yTop - 6, w, hFull + 6, HK.Open, "");
            return;
        }
        bool clipRow = tk > 0.01f;
        if (clipRow) { g.ClipRect(x0, yTop, w, hFull); g.Alpha *= 1 - tk / 0.6f; }
        // hover: the notch grows upward and the second line appears above the (unmoving) main row
        if (h > TabH + 6 && p.Sub is { Length: > 0 } sub)
        {
            float a = g.Alpha;
            g.Alpha = a * Math.Clamp((h - TabH - 6) / 10f, 0, 1);
            g.Text(sub, g.FSmall, C.Dim, x0 + 14, y0 + 5, w - 28, 16, 1);
            g.Alpha = a;
        }
        y0 += h - TabH; h = TabH;
        float cy = y0 + h / 2, cx = x0 + 8 + 11;
        if (p.Agent != null)
        {
            if (p.Icon == Ico.Spin)
            {
                g.Arc(cx, cy, 9.5f, 0, 360, 1.2f, C.Track);
                g.Arc(cx, cy, 9.5f, _cfg.LowMotion ? -90 : now % 1000 / 1000f * 360f, 80, 1.5f, C.Run);
            }
            else if (p.Icon == Ico.Pulse)
            {
                if (!_cfg.LowMotion) { float ph = now % 1600 / 1600f; g.Arc(cx, cy, 9.5f + ph * 4.5f, 0, 360, 1.3f, C.A(p.IconColor, (1 - ph) * 0.7f)); }
                g.Arc(cx, cy, 9.5f, 0, 360, 1.4f, p.IconColor);
            }
            else g.Arc(cx, cy, 9.5f, 0, 360, 1.2f, C.A(p.IconColor, 0.6f));
            Glyph(p.Agent, cx, cy, 4.4f, C.Text);
        }
        else Icon(p.Icon, cx, cy, p.IconColor, now);

        float x = x0 + 8 + 22 + 9, xr = x0 + w - 12;
        if (p.Ask != null)
        {   // Allow / Deny (answers the hook that's holding the agent's permission prompt)
            float bx = x0 + w - 6;
            foreach (var (label, k, solid) in new[] { ("Allow", HK.Allow, true), ("Deny", HK.Deny, false) })
            {
                float bw = g.Measure(label, g.FBtn) + 20; bx -= bw;
                bool hot = _hk == k;
                if (solid) g.FillRR(bx, cy - 11, bw, 22, 11, hot ? C.A(C.Solid, 1) : C.Solid);
                else { if (hot) g.FillRR(bx, cy - 11, bw, 22, 11, C.Hover); g.StrokeRR(bx + 0.5f, cy - 10.5f, bw - 1, 21, 10.5f, C.Track, 1); }
                g.Text(label, g.FBtn, solid ? C.SolidText : hot ? C.Text : C.Dim, bx, cy - 11, bw, 22, 1);
                AddHit(true, bx, cy - 11, bw, 22, k, p.Ask);
                bx -= 6;
            }
            xr = bx - 6;
        }
        if (p.Dock > 0)
        {   // overlapping stack of the other agents
            float sx = xr - 9 - (p.Dock - 1) * 14;
            for (int i = 0; i < p.Dock && i < _dock.Count; i++)
            {
                var (n, c) = _dock[i];
                float gx = sx + i * 14;
                g.Circle(gx, cy, 10, C.DockRing);
                g.Circle(gx, cy, 9, C.DockFill);
                Glyph(n, gx, cy, 3.6f, c);
            }
            xr = sx - 9 - 10;
        }
        float tw = MathF.Min(g.Measure(p.Title, g.FSemi), MathF.Max(0, xr - x));
        g.Text(p.Title, g.FSemi, C.Text, x, y0, tw + 2, h);
        float dx = x + tw + 8;
        if (p.Detail.Length > 0 && xr - dx > 16) g.Text(p.Detail, g.FMonoSm, C.Faint, dx, y0 + 0.5f, xr - dx, h);
        if (p.HasProg)
        {   // hairline progress along the notch's bottom edge
            float r0 = x0 + 16, r1 = x0 + w - 16, ry = y0 + h - 2;
            g.Line(r0, ry, r1, ry, 1.6f, C.Track);
            if (p.Prog >= 0) g.Line(r0, ry, r0 + (r1 - r0) * p.Prog, ry, 1.6f, p.Err ? C.Err : C.Run);
            else Scanner(r0, r1, ry, now);
        }
        if (clipRow) g.Unclip();
        AddHit(true, x0, yTop, w, hFull, HK.Open, "");   // last: the buttons above win
    }

    static void Scanner(float x0, float x1, float y, long now)
    {
        float seg = (x1 - x0) * 0.22f, p = _cfg.LowMotion ? 0.4f : now % 1500 / 1500f;
        float a = x0 + (x1 - x0 + seg) * p - seg;
        _g.Line(MathF.Max(x0, a), y, MathF.Min(x1, a + seg), y, 1.6f, C.Run);
    }

    static void Icon(Ico k, float cx, float cy, uint c, long now, Gfx? gg = null)
    {
        var g = gg ?? _g;
        switch (k)
        {
            case Ico.Spin:
                g.Arc(cx, cy, 6.5f, 0, 360, 1.4f, C.Track);
                g.Arc(cx, cy, 6.5f, _cfg.LowMotion ? -90 : now % 900 / 900f * 360f, 100, 1.7f, c);
                break;
            case Ico.Pulse:
                if (!_cfg.LowMotion) { float ph = now % 1400 / 1400f; g.Circle(cx, cy, 3.5f + ph * 5.5f, C.A(c, (1 - ph) * 0.4f)); }
                g.Circle(cx, cy, 4f, c);
                break;
            case Ico.Check:
                g.Line(cx - 4.5f, cy + 0.2f, cx - 1.4f, cy + 3.4f, 1.8f, c);
                g.Line(cx - 1.4f, cy + 3.4f, cx + 4.6f, cy - 3.6f, 1.8f, c);
                break;
            case Ico.Cross:
                g.Line(cx - 3.6f, cy - 3.6f, cx + 3.6f, cy + 3.6f, 1.8f, c);
                g.Line(cx + 3.6f, cy - 3.6f, cx - 3.6f, cy + 3.6f, 1.8f, c);
                break;
            case Ico.Dot:
                g.Circle(cx, cy, 3.2f, c);
                break;
        }
    }

    // ================================================================ panel

    static readonly string[] TabNames = { "Agents", "Builds", "Ports", "Git" };
    const int Tabs = 4;
    static int _tab, _wheelAcc;
    static float _tabPos;
    static long _lastStep;

    static void OnWheel(int delta, bool cards)
    {
        if (!Expanded) return;
        POINT cp; GetCursorPos(&cp);
        if (!cards && cp.Y < _win.T + 20 * _s) cards = true;   // the wheel message went to the focused island
        _wheelAcc += delta;
        if (Math.Abs(_wheelAcc) < 100) return;
        int dir = Math.Sign(_wheelAcc);
        _wheelAcc = 0;
        long now = Model.Now;
        if (now - _lastStep < 220) return;   // one page per flick, even from fast touchpads
        _lastStep = now;
        if (cards) Focus(_focus[_tab] + dir); else GoTab(_tab + dir);
    }

    static void GoTab(int t)
    {
        t = Math.Clamp(t, 0, Tabs - 1);
        if (t == _tab) return;
        _tab = t;
        _hk = HK.None; _harg = null; _dhk = HK.None; _dharg = null;
        _fpos = _focus[t]; _enterAt = Hr;
        Invalidate();
    }

    static void SortLists()
    {
        _tasks.Clear(); _tasks.AddRange(Model.Tasks);
        _tasks.Sort((a, b) => a.Running != b.Running ? (a.Running ? -1 : 1) : a.Running ? b.Start.CompareTo(a.Start) : b.End.CompareTo(a.End));
        _agents.Clear(); _agents.AddRange(Model.Agents);
        _agents.Sort((a, b) => Rank(a) != Rank(b) ? Rank(a) - Rank(b) : b.Updated.CompareTo(a.Updated));
        _repos.Clear(); _repos.AddRange(Model.Repos);
        _repos.Sort((a, b) => b.Used.CompareTo(a.Used));
        static int Rank(AgentInfo a) => a.State switch { "wait" => 0, "busy" => 1, _ => 2 };
    }

    /// Context meter: re-read the sessions' transcripts while the cards are open or about to be
    /// (each read parses only the lines added since the last one).
    static void RefreshUsage()
    {
        foreach (var a in Model.Agents)
            if (a.Transcript.Length > 0) Usage.Refresh(a.Transcript, _hwnd, WM_GIT);
    }

    static void RefreshGit(long now)
    {
        for (int i = 0; i < Math.Min(3, Model.Repos.Count); i++)
        {
            Repo? best = null;
            foreach (var r in Model.Repos) if (now - r.Last > 4000 && (best == null || r.Used > best.Used)) best = r;
            if (best == null) break;
            Git.Refresh(best, _hwnd, WM_GIT);
        }
    }

    static void AddHit(bool on, float x, float y, float w, float h, HK k, string arg)
    {
        if (on) _hits.Add(new Hit { R = new RectF(x, y, w, h), K = k, Arg = arg });
    }

    static bool Hot(HK k, string arg) => _hk == k && _harg == arg;

    static string TabCount(int i) => i switch
    {
        0 => _terms.Count > 0 ? _terms.Count.ToString() : "",
        1 => _tasks.Exists(t => t.Running) ? _tasks.FindAll(t => t.Running).Count.ToString() : "",
        2 => Probe.Servers.Count > 0 ? Probe.Servers.Count.ToString() : "",
        _ => _repos.FindAll(r => r.St is { } s && s.Changed + s.Untracked > 0) is { Count: > 0 } d ? d.Count.ToString() : "",
    };

    // ---------------------------------------------------------------- Agents

    struct Term
    {
        public string Name, Host, Cwd, State, Prompt, Tool, Msg, Key, Verdict, Transcript;
        public long Since, PromptAt; public bool Hooked; public List<LogEntry>? Log;
        public uint Pid; public int AskPort;
    }
    static readonly List<Term> _terms = new();

    /// One entry per agent terminal: every hook-reported session, plus agent CLIs seen running
    /// that no session accounts for (no hooks connected, or an agent without hook support).
    static void BuildTerms()
    {
        _terms.Clear();
        var procs = Probe.Agents;
        Span<bool> used = stackalloc bool[procs.Count];
        foreach (var a in _agents)
        {
            string host = a.Host;
            uint pid = a.Pid;
            int m = -1;   // the agent process this session runs in: by pid when the hook sent one, else by name
            for (int i = 0; i < procs.Count && m < 0; i++) if (!used[i] && pid != 0 && procs[i].Pid == pid) m = i;
            for (int i = 0; i < procs.Count && m < 0; i++) if (!used[i] && procs[i].Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase)) m = i;
            if (m >= 0) { used[m] = true; if (host.Length == 0) host = procs[m].Host; if (pid == 0) pid = procs[m].Pid; }
            _terms.Add(new Term { Name = a.Name, Host = host.Length > 0 ? host : "Terminal", Cwd = a.Cwd, State = a.State, Prompt = a.Prompt,
                Tool = a.Tool, Msg = a.Msg, Key = a.Key, Since = a.Since, PromptAt = a.PromptAt, Hooked = true, Log = a.Log,
                Pid = pid, AskPort = a.AskPort, Verdict = a.Verdict, Transcript = a.Transcript });
        }
        for (int i = 0; i < procs.Count; i++)
            if (!used[i])
                _terms.Add(new Term { Name = procs[i].Name, Host = procs[i].Host, Cwd = "", State = "open", Prompt = "", Tool = "", Msg = "", Verdict = "", Transcript = "",
                    Key = "p" + procs[i].Pid, Pid = procs[i].Pid });
    }

    static (string, uint) StateWord(in Term t) => t.State switch
    {
        "wait" => (t.Msg.StartsWith("Claude needs your permission", StringComparison.Ordinal) ? "Needs approval" : "Needs you", C.Warn),
        "busy" => ("Working", C.Text),
        "done" => ("Done", C.Ok),
        _ => ("Open", C.Faint),
    };

    static string BranchFor(string cwd)
    {
        if (cwd.Length == 0) return "";
        foreach (var r in Model.Repos)
            if (cwd.StartsWith(r.Root, StringComparison.OrdinalIgnoreCase)) return r.St?.Branch ?? "";
        return "";
    }

    static string ShortPath(string p)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 0 && p.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + p[home.Length..].Replace('\\', '/') : p;
    }

    static string AgentTitle(string name) => name.ToLowerInvariant() switch
    {
        "claude" => "Claude Code", "codex" => "Codex", "gemini" => "Gemini CLI", "aider" => "Aider",
        "opencode" => "OpenCode", "cursor-agent" => "Cursor Agent", "amp" => "Amp", "goose" => "Goose",
        "qwen" => "Qwen Code", "droid" => "Droid", "crush" => "Crush", "kiro-cli" => "Kiro",
        _ => Cap(name),
    };

    static int Sig(long now)
    {
        var hc = new HashCode();
        foreach (var s in Probe.Servers) { hc.Add(s.Port); hc.Add(s.Proc); }
        hc.Add(Probe.Shells); hc.Add(Probe.AgentProcs); hc.Add(Probe.Active); hc.Add(Probe.AgentNames.Count);
        var p = ComputePill(now);
        hc.Add(p.Icon); hc.Add(p.Title); hc.Add(p.Detail); hc.Add(p.Dock);
        hc.Add(Model.Tasks.Count); hc.Add(Model.Agents.Count);
        return hc.ToHashCode();
    }

    // ---------------------------------------------------------------- agent glyphs
    //
    // One simple geometric mark per agent CLI (not brand logos), so claude vs codex vs gemini is
    // recognisable at a glance. s = radius.

    static void Glyph(string name, float cx, float cy, float s, uint c, Gfx? gg = null)
    {
        var g = gg ?? _g;
        float w = MathF.Max(1.1f, s * 0.18f);
        string n = name.ToLowerInvariant();
        Span<float> p = stackalloc float[16];
        if (n.Contains("claude"))
        {   // spark: eight rays, alternating length
            for (int i = 0; i < 8; i++)
            {
                float a = i * MathF.PI / 4 - MathF.PI / 2, r = i % 2 == 0 ? s : s * 0.68f;
                g.Line(cx + MathF.Cos(a) * s * 0.22f, cy + MathF.Sin(a) * s * 0.22f, cx + MathF.Cos(a) * r, cy + MathF.Sin(a) * r, w, c);
            }
        }
        else if (n.Contains("codex"))
        {   // hexagon with a core
            for (int i = 0; i < 6; i++) { float a = i * MathF.PI / 3 - MathF.PI / 2; p[i * 2] = cx + MathF.Cos(a) * s; p[i * 2 + 1] = cy + MathF.Sin(a) * s; }
            g.Poly(p[..12], w, c);
            g.Circle(cx, cy, s * 0.24f, c);
        }
        else if (n.Contains("gemini") || n.Contains("qwen"))
        {   // four-point star
            float k = s * 0.14f;
            g.Bezier(cx, cy - s, cx + k, cy - k, cx + k, cy - k, cx + s, cy, w, c);
            g.Bezier(cx + s, cy, cx + k, cy + k, cx + k, cy + k, cx, cy + s, w, c);
            g.Bezier(cx, cy + s, cx - k, cy + k, cx - k, cy + k, cx - s, cy, w, c);
            g.Bezier(cx - s, cy, cx - k, cy - k, cx - k, cy - k, cx, cy - s, w, c);
        }
        else if (n.Contains("cursor"))
        {   // triangle
            for (int i = 0; i < 3; i++) { float a = i * 2 * MathF.PI / 3 - MathF.PI / 2; p[i * 2] = cx + MathF.Cos(a) * s; p[i * 2 + 1] = cy + MathF.Sin(a) * s * 0.95f + s * 0.1f; }
            g.Poly(p[..6], w, c);
        }
        else if (n.Contains("aider"))
        {   // double chevron
            for (int i = 0; i < 2; i++)
            {
                float ox = cx - s * 0.55f + i * s * 0.7f;
                g.Line(ox, cy - s * 0.7f, ox + s * 0.5f, cy, w, c);
                g.Line(ox + s * 0.5f, cy, ox, cy + s * 0.7f, w, c);
            }
        }
        else if (n.Contains("opencode") || n.Contains("crush"))
        {   // brackets
            float bx = s * 0.75f, by = s * 0.85f, t = s * 0.35f;
            g.Line(cx - bx + t, cy - by, cx - bx, cy - by, w, c); g.Line(cx - bx, cy - by, cx - bx, cy + by, w, c); g.Line(cx - bx, cy + by, cx - bx + t, cy + by, w, c);
            g.Line(cx + bx - t, cy - by, cx + bx, cy - by, w, c); g.Line(cx + bx, cy - by, cx + bx, cy + by, w, c); g.Line(cx + bx, cy + by, cx + bx - t, cy + by, w, c);
        }
        else if (n.Contains("amp") || n.Contains("droid"))
        {   // bolt
            p[0] = cx + s * 0.25f; p[1] = cy - s; p[2] = cx - s * 0.45f; p[3] = cy + s * 0.1f; p[4] = cx + s * 0.1f; p[5] = cy + s * 0.1f;
            p[6] = cx - s * 0.25f; p[7] = cy + s; p[8] = cx + s * 0.45f; p[9] = cy - s * 0.1f; p[10] = cx - s * 0.1f; p[11] = cy - s * 0.1f;
            g.Poly(p[..12], w, c);
        }
        else
        {   // generic terminal: window with a prompt
            float bw = s * 1.05f, bh = s * 0.85f;
            g.StrokeRR(cx - bw, cy - bh, bw * 2, bh * 2, s * 0.3f, c, w);
            g.Line(cx - bw * 0.55f, cy - bh * 0.35f, cx - bw * 0.15f, cy, w, c);
            g.Line(cx - bw * 0.15f, cy, cx - bw * 0.55f, cy + bh * 0.35f, w, c);
            g.Line(cx + bw * 0.05f, cy + bh * 0.4f, cx + bw * 0.5f, cy + bh * 0.4f, w, c);
        }
    }
}

static class Fmt
{
    public static string Dir(string p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        var n = Path.GetFileName(p.TrimEnd('\\', '/'));
        return n.Length > 0 ? n : p;
    }

    public static string Clock(long ms)
    {
        long s = Math.Max(0, ms) / 1000;
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    public static string Dur(long ms)
    {
        if (ms < 10_000) return $"{ms / 1000.0:0.0}s";
        long s = ms / 1000;
        if (s < 60) return $"{s}s";
        if (s < 3600) return $"{s / 60}m {s % 60}s";
        return $"{s / 3600}h {s / 60 % 60}m";
    }

    public static string Ago(long ms)
    {
        long s = ms / 1000;
        if (s < 45) return "now";
        if (s < 3600) return $"{(s + 30) / 60}m ago";
        if (s < 86400) return $"{s / 3600}h ago";
        return $"{s / 86400}d ago";
    }
}
