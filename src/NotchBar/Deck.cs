using System.Runtime.InteropServices;
using System.Text;
using static NotchBar.W;

namespace NotchBar;

/// The open state: the notch turns into a slim page switcher and each page's items float above the
/// taskbar as cards (agent windows, builds, servers, repos), over a dimmed, blurred copy of the desktop.
///
/// Three windows: the island (header), the deck (cards + chips, per-pixel alpha) and the scrim
/// (a plain layered window painted once and faded with its window alpha, so it costs nothing per frame).
/// Card faces are rendered once into bitmaps; animation frames only place them.
static unsafe partial class App
{
    const uint WM_PREVIEW = 0x8005;
    const float CW = 560, CH = 350, HH = 44, ChipH = 28, SideW = 236, Spread = 420, Spread2 = 220;
    const int MaxCards = 8;

    static nint _deck, _scrim;
    static Gfx? _gd;
    static bool _deckShown;
    static RECT _deckWin, _scrimR;
    static nint _scrimBmp;
    static byte _scrimA;
    static readonly List<Hit> _dhits = new();
    static HK _dhk; static string? _dharg;
    static bool _dtracking;
    static nint _prevFg;
    static long _enterAt;
    static float _fpos;
    static readonly int[] _focus = new int[4];

    sealed class Card
    {
        public string Key = "", Chip = "", Arg = "";
        public nint Img; public int Sig; public uint ChipCol; public HK Act;
        public readonly List<Hit> Hits = new();
    }
    static readonly List<Card> _cards = new();

    // ================================================================ windows

    static void CreateDeckWindows(nint inst, char* cls)
    {
        _deck = CreateWindowExW(0x80000 | 0x80 | 0x8 | 0x8000000, cls, cls, 0x80000000, 0, 0, 1, 1, 0, 0, inst, 0);
        _scrim = CreateWindowExW(0x80000 | 0x80 | 0x8 | 0x8000000, cls, cls, 0x80000000, 0, 0, 1, 1, 0, 0, inst, 0);
        SetLayeredWindowAttributes(_scrim, 0, 0, 2);   // LWA_ALPHA
    }

    /// Our windows sit directly under the taskbar, top to bottom: island, side boxes, deck, scrim.
    static void ZAll()
    {
        if (_deckShown) { ZOrder(_scrim); ZOrder(_deck); }
        ZOrder(_sideL); ZOrder(_sideR); ZOrder(_hwnd);
    }

    static nint DeckProc(nint h, uint m, nint w, nint l)
    {
        if (h == _scrim)
        {
            switch (m)
            {
                case 0x000F: PaintScrim(); return 0;                         // WM_PAINT
                case 0x0014: return 1;                                        // WM_ERASEBKGND
                case 0x0021: return 3;                                        // MA_NOACTIVATE
                case 0x0202: case 0x0205: Collapse(); return 0;              // click outside the cards closes
                case 0x020A: OnWheel(-(short)((w >> 16) & 0xFFFF), true); return 0;
            }
            return DefWindowProcW(h, m, w, l);
        }
        switch (m)
        {
            case 0x0021: return 3;
            case 0x0200: DeckMove((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF)); return 0;
            case 0x02A3: _dtracking = false; if (_dhk != HK.None) { _dhk = HK.None; _dharg = null; Invalidate(); } return 0;
            case 0x0202: DeckClick((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF)); return 0;
            case 0x020A: OnWheel(-(short)((w >> 16) & 0xFFFF), true); return 0;
            case 0x020E: OnWheel((short)((w >> 16) & 0xFFFF), true); return 0;
            case 0x0020: if (_dhk != HK.None) { SetCursor(_hand); return 1; } break;
        }
        return DefWindowProcW(h, m, w, l);
    }

    /// The screen above the taskbar, in physical pixels. Never the whole monitor: Windows would take a
    /// monitor-sized topmost window for a fullscreen app and drop the taskbar (and us) out of topmost.
    static RECT ScrimRect()
    {
        var bar = TaskbarProbe.Current?.Bar ?? default;
        int top = bar.B > bar.T && bar.T > _mon.T + 100 && bar.T < _mon.B ? bar.T : _mon.B - (int)(48 * _s);
        return new RECT { L = _mon.L, T = _mon.T, R = _mon.R, B = Math.Min(top, _mon.B - 1) };
    }

    static void PaintScrim()
    {
        PAINTSTRUCT ps;
        nint dc = BeginPaint(_scrim, &ps);
        int w = _scrimR.R - _scrimR.L, h = _scrimR.B - _scrimR.T;
        if (_scrimBmp != 0)
        {
            nint m = CreateCompatibleDC(dc), o = SelectObject(m, _scrimBmp);
            BitBlt(dc, 0, 0, w, h, m, 0, 0, 0x00CC0020);
            SelectObject(m, o); DeleteDC(m);
        }
        else
        {
            RECT r = new() { R = w, B = h };
            nint br = CreateSolidBrush(0x000A0605);
            FillRect(dc, &r, br); DeleteObject(br);
        }
        EndPaint(_scrim, &ps);
    }

    // ---- prefetch: the blurred desktop and the agents' window pictures are made on worker threads
    // while the cursor is on the notch, so the click opens without waiting for them.

    static nint _scrimPre; static RECT _scrimPreR; static long _scrimPreAt; static int _scrimBusy;

    static void Prefetch()
    {
        if (Model.Now - _prevAt > 3000) { SortLists(); BuildTerms(); CapturePreviews(); }
        if (!_cfg.Glass || _scrimBusy == 1 || (_scrimPre != 0 && Model.Now - _scrimPreAt < 1500)) return;
        var r = ScrimRect();
        _scrimBusy = 1;
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            nint b = 0;
            try { b = Gfx.ScrimBitmap(r.L, r.T, r.R - r.L, r.B - r.T); } catch { }
            nint old = Interlocked.Exchange(ref _scrimPre, b);
            if (old != 0) DeleteObject(old);
            _scrimPreR = r; _scrimPreAt = Model.Now;
            _scrimBusy = 0;
        }, null);
    }

    static void ShowScrim()
    {
        var r = ScrimRect();
        if (_cfg.Glass)
        {   // a click usually follows a hover: wait a moment for an in-flight capture rather than go without
            for (int i = 0; i < 120 && _scrimBusy == 1; i++) Thread.Sleep(1);
            nint b = Model.Now - _scrimPreAt < 4000 ? Interlocked.Exchange(ref _scrimPre, 0) : 0;
            if (b != 0 && (_scrimPreR.L != r.L || _scrimPreR.T != r.T || _scrimPreR.R != r.R || _scrimPreR.B != r.B)) { DeleteObject(b); b = 0; }
            if (_scrimBmp != 0) DeleteObject(_scrimBmp);
            _scrimBmp = b;
        }
        _scrimR = r;
        _scrimA = 0;
        SetLayeredWindowAttributes(_scrim, 0, 0, 2);
        SetWindowPos(_scrim, 0, r.L, r.T, r.R - r.L, r.B - r.T, 0x10 | 0x4);   // NOACTIVATE | NOZORDER
        ShowWindow(_scrim, 4);
        UpdateWindow(_scrim);   // paint now: posted animation ticks would starve WM_PAINT
    }

    // ---- live pictures of the agents' editor/terminal windows

    static Dictionary<string, (nint Hwnd, nint Img)> _prev = new();
    static Dictionary<string, (nint, nint)>? _prevNew;
    static int _prevBusy; static long _prevAt = -100_000;

    static void CapturePreviews()
    {
        if (Interlocked.Exchange(ref _prevBusy, 1) == 1) return;
        _prevAt = Model.Now;
        var want = new List<(string Key, uint Pid)>();
        foreach (var t in _terms) if (t.Pid != 0) want.Add((t.Key, t.Pid));
        int pw = (int)((CW - SideW) * _s), ph = (int)((CH - 53) * _s);
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            var res = new Dictionary<string, (nint, nint)>();
            try
            {
                if (want.Count > 0)
                {
                    var wins = TopWindows();
                    var parents = Probe.ParentMap();
                    var shots = new Dictionary<nint, nint>();
                    foreach (var (key, pid) in want)
                    {
                        nint h = 0; uint p = pid;
                        for (int i = 0; i < 16 && p != 0; i++)
                        {   // walk up from the agent process to the first one that owns a window
                            if (wins.TryGetValue(p, out h)) break;
                            if (!parents.TryGetValue(p, out var pp) || pp == p) break;
                            p = pp;
                        }
                        if (h == 0) continue;
                        if (!shots.TryGetValue(h, out var img)) shots[h] = img = Gfx.CaptureWindow(h, pw, ph);
                        else img = 0;   // same window as another agent: share the picture (owned by the first)
                        res[key] = (h, img);
                    }
                    foreach (var k in res.Keys.ToList())
                        if (res[k].Item2 == 0 && shots.TryGetValue(res[k].Item1, out var s)) res[k] = (res[k].Item1, s);
                }
            }
            catch { }
            var old = Interlocked.Exchange(ref _prevNew, res);
            if (old != null) FreePreviews(old);
            _prevBusy = 0;
            PostMessageW(_hwnd, WM_PREVIEW, 0, 0);
        }, null);
    }

    static void FreePreviews(Dictionary<string, (nint, nint)> d)
    {
        var seen = new HashSet<nint>();
        foreach (var (_, img) in d.Values) if (img != 0 && seen.Add(img)) Gfx.DisposeImage(img);
        d.Clear();
    }

    static void OnPreviews()
    {
        var n = Interlocked.Exchange(ref _prevNew, null);
        if (n == null) return;
        FreePreviews(_prev);
        _prev = n;
        Invalidate();
    }

    [ThreadStatic] static Dictionary<uint, nint>? _enumOut;

    /// Visible, unowned app windows by process id (front-most first).
    static Dictionary<uint, nint> TopWindows()
    {
        _enumOut = new Dictionary<uint, nint>();
        EnumWindows(&EnumProc, 0);
        var d = _enumOut; _enumOut = null;
        return d;
    }

    [UnmanagedCallersOnly]
    static int EnumProc(nint h, nint l)
    {
        try
        {
            if (IsWindowVisible(h) == 0 || GetWindow(h, 4) != 0 || GetWindowTextLengthW(h) == 0) return 1;
            long ex = GetWindowLongPtrW(h, -20);
            if ((ex & 0x80) != 0 && (ex & 0x40000) == 0) return 1;
            int cloaked = 0;
            if (DwmGetWindowAttribute(h, 14, &cloaked, 4) == 0 && cloaked != 0) return 1;   // other desktop / suspended
            uint pid;
            GetWindowThreadProcessId(h, &pid);
            _enumOut!.TryAdd(pid, h);
        }
        catch { }
        return 1;
    }

    // ================================================================ cards

    static void SyncCards(long now)
    {
        var want = new List<(string Key, int Sig, int Idx)>();
        switch (_tab)
        {
            case 0:
                for (int i = 0; i < _terms.Count && want.Count < MaxCards; i++)
                {
                    var t = _terms[i];
                    var hc = new HashCode();
                    hc.Add(t.State); hc.Add(t.Prompt); hc.Add(t.Tool); hc.Add(t.Msg); hc.Add(t.Host); hc.Add(t.Cwd); hc.Add(t.AskPort); hc.Add(t.Verdict);
                    hc.Add(StatusText(t, now)); hc.Add(BranchFor(t.Cwd)); hc.Add(t.Log?.Count ?? 0);
                    hc.Add(_prev.TryGetValue(t.Key, out var pv) ? pv.Img : 0);
                    hc.Add(Usage.Get(t.Transcript));
                    if (t.Log is { Count: > 0 } lg) hc.Add(lg[^1].Done);
                    want.Add(("a" + t.Key, hc.ToHashCode(), i));
                }
                break;
            case 1:
                BuildJobs();
                for (int i = 0; i < _jobs.Count && want.Count < MaxCards; i++)
                {
                    var j = _jobs[i];
                    var last = j.Runs.Count > 0 ? j.Runs[^1] : null;
                    want.Add(("b" + j.Key, HashCode.Combine(j.Label, j.Live?.Running, j.Live?.Code, j.Runs.Count, last?.EndUtc,
                        j.Live is { Running: true } lt ? Fmt.Clock(now - lt.Start) : last != null ? Fmt.Ago(WallAgo(last)) : ""), i));
                }
                break;
            case 2:
                for (int i = 0; i < Probe.Servers.Count && want.Count < MaxCards; i++)
                    want.Add(("p" + Probe.Servers[i].Port, HashCode.Combine(Probe.Servers[i].Proc), i));
                break;
            default:
                for (int i = 0; i < _repos.Count && want.Count < MaxCards; i++)
                    want.Add(("g" + _repos[i].Root, HashCode.Combine(_repos[i].St, _repos[i].Name), i));
                break;
        }
        if (want.Count == 0) want.Add(("e" + _tab, _tab, -1));

        var old = new Dictionary<string, Card>();
        foreach (var c in _cards) old[c.Key] = c;
        _cards.Clear();
        foreach (var (key, sig, idx) in want)
        {
            if (!old.Remove(key, out var c)) c = new Card { Key = key, Sig = sig ^ 1 };
            if (c.Sig != sig || c.Img == 0)
            {
                Gfx.DisposeImage(c.Img);
                c.Hits.Clear();
                c.Img = _gd!.BeginImage(CW, CH, true);
                try { RenderCard(c, idx, now); } finally { _gd.EndImage(); }
                c.Sig = sig;
            }
            _cards.Add(c);
        }
        foreach (var c in old.Values) Gfx.DisposeImage(c.Img);
        _focus[_tab] = Math.Clamp(_focus[_tab], 0, _cards.Count - 1);
    }

    static void FreeCards()
    {
        foreach (var c in _cards) Gfx.DisposeImage(c.Img);
        _cards.Clear();
    }

    static void RenderCard(Card c, int idx, long now)
    {
        if (idx < 0) { EmptyCard(c); return; }
        switch (_tab)
        {
            case 0: AgentCard(c, _terms[idx], now); break;
            case 1: BuildCard(c, _jobs[idx], now); break;
            case 2: PortCard(c, Probe.Servers[idx]); break;
            default: GitCard(c, _repos[idx]); break;
        }
    }

    // ---- shared pieces

    static Gfx G => _gd!;

    static void Face() => G.FillRR(0, 0, CW, CH, 12, C.Face);
    static void FaceEdge() => G.StrokeRR(0.5f, 0.5f, CW - 1, CH - 1, 11.5f, C.Edge, 1);

    static void TitleBar(string title, bool url)
    {
        var g = G;
        g.RR(0, 0, CW, CH, 12); g.Clip();
        g.FillRect(0, 0, CW, 30, C.Bar);
        g.Unclip();
        g.Line(0, 30.5f, CW, 30.5f, 1, C.Line);
        for (int i = 0; i < 3; i++) g.Circle(15 + i * 14, 15, 4.5f, C.Dots);
        if (url)
        {
            float w = MathF.Min(g.Measure(title, g.FMonoSm) + 80, CW - 140);
            g.FillRR(CW / 2 - w / 2, 6.5f, w, 18, 6, C.Well);
            g.StrokeRR(CW / 2 - w / 2 + 0.5f, 7, w - 1, 17, 5.5f, C.Line, 1);
            g.Text(title, g.FMonoSm, C.Dim, CW / 2 - w / 2, 6.5f, w, 18, 1);
        }
        else g.Text(title, g.FSmall, C.Faint, 90, 0, CW - 180, 30, 1);
    }

    /// A button; `key` adds a small keycap with its keyboard shortcut.
    static float Btn(Card c, float xRight, float y, string text, bool solid, HK k, string arg, string? key = null)
    {
        var g = G;
        float tw = g.Measure(text, g.FBtn), kw = key != null ? MathF.Max(15, g.Measure(key, g.FMonoXs) + 8) : 0;
        float w = tw + 22 + (key != null ? kw + 7 : 0), x = xRight - w;
        uint fg = solid ? C.SolidText : C.Text;
        if (solid) g.FillRR(x, y, w, 25, 7, C.Solid);
        else { g.FillRR(x, y, w, 25, 7, C.Ghost); g.StrokeRR(x + 0.5f, y + 0.5f, w - 1, 24, 6.5f, C.Track, 1); }
        g.Text(text, g.FBtn, fg, x + 11, y, tw + 2, 25);
        if (key != null)
        {
            float kx = x + 11 + tw + 7;
            g.StrokeRR(kx + 0.5f, y + 5.5f, kw - 1, 14, 3.5f, C.A(fg, 0.32f), 1);
            g.Text(key, g.FMonoXs, C.A(fg, 0.75f), kx, y + 5, kw, 15, 1);
        }
        c.Hits.Add(new Hit { R = new RectF(x, y, w, 25), K = k, Arg = arg });
        return w;
    }

    static void EmptyCard(Card c)
    {
        var g = G;
        Face();
        (string t, string s) = _tab switch
        {
            0 => ("No agent sessions", "Claude Code, Codex, Gemini and other agent CLIs show up here while they run. Run  nb hooks  to connect Claude Code."),
            1 => ("Nothing built yet", "Prefix a command with  nb run  to follow it here, with a progress bar learned from earlier runs."),
            2 => ("No dev servers running", "Local listeners from node, bun, python, docker and others show up here."),
            _ => ("No repositories yet", "Repos show up once you  nb run  in them or an agent works in them, or add  repo = <path>  to the config."),
        };
        g.Text(t, g.FTitle, C.Dim, 0, CH / 2 - 40, CW, 26, 1);
        g.TextWrap(s, g.FUi, C.Faint, 90, CH / 2 - 6, CW - 180, 60);
        FaceEdge();
        c.Chip = _tab switch { 0 => "Agents", 1 => "Builds", 2 => "Ports", _ => "Git" };
        c.ChipCol = C.Faint; c.Act = HK.None;
    }

    // ---- agents: the session's window on the left, the agent's side pane on the right

    static string StatusText(in Term t, long now) =>
        t.Verdict == "allow" ? "Allowed from the notch" : t.Verdict == "deny" ? "Denied from the notch"
        : t.State == "wait" ? (t.AskPort > 0 ? "Waiting for you" : "Answer in " + t.Host)
        : t.State == "busy" ? "Running · " + Fmt.Clock(now - t.Since)
        : t.State == "done" ? "Done · " + Fmt.Ago(now - t.Since)
        : "Running in " + t.Host;

    static uint HostBar(string host) => host switch
    {
        "VS Code" or "VS Code Insiders" => 0xFF1F6FEB,
        "Visual Studio" => 0xFF68217A,
        _ => C.Light ? 0xFF3B3C44 : 0xFF1B1C20,
    };

    static void AgentCard(Card c, in Term t, long now)
    {
        var g = G;
        string project = t.Cwd.Length > 0 ? Fmt.Dir(t.Cwd) : AgentTitle(t.Name);
        Face();
        TitleBar(project + " — " + t.Host, false);
        float lw = CW - SideW, top = 31, lh = CH - 31 - 22;

        // left: a picture of the real window, or the activity log when there is none
        if (_prev.TryGetValue(t.Key, out var pv) && pv.Img != 0)
            g.Image(pv.Img, 0, top, lw, lh, 0, 0, (int)(lw * _s), (int)((CH - 53) * _s), 5);
        else
        {
            float y = top + 14;
            var log = t.Log;
            if (log != null && log.Count > 0)
            {
                int start = Math.Max(0, log.Count - 9);
                for (int i = start; i < log.Count; i++)
                {
                    var e = log[i];
                    string line = e.Tool == "Bash" ? "$ " + e.Detail : e.Tool + "  " + (e.Detail.Contains('\\') || e.Detail.Contains('/') ? Fmt.Dir(e.Detail) : e.Detail);
                    if (e.Done) Icon(Ico.Check, 22, y + 9, C.Ok, now, g); else g.Circle(22, y + 9, 2.5f, t.State == "wait" ? C.Warn : C.Text);
                    g.Text(line, g.FMonoSm, e.Done ? C.Dim : C.Text, 36, y, lw - 50, 18);
                    y += 26;
                }
            }
            else
            {
                Glyph(t.Name, lw / 2, top + lh / 2 - 16, 16, C.Mark, g);
                g.Text(t.Hooked ? "No tool calls yet" : AgentTitle(t.Name) + " in " + t.Host, g.FSmall, C.Faint, 0, top + lh / 2 + 12, lw, 18, 1);
            }
        }

        // right: the agent pane
        g.FillRect(lw, top, SideW, lh, C.Pane);
        g.Line(lw + 0.5f, top, lw + 0.5f, top + lh, 1, C.Line);
        float sx = lw + 14, sw = SideW - 28, sy = top + 13;
        Glyph(t.Name, sx + 5, sy + 6, 4.6f, C.Dim, g);
        g.Text(AgentTitle(t.Name).ToUpperInvariant(), g.FCap, C.Faint, sx + 16, sy, sw - 16, 13);
        sy += 22;

        // context meter: how full the session's context window is, and its token use so far
        var u = Usage.Get(t.Transcript);
        if (u != null && u.Context > 0)
        {
            long win = Usage.Window(u.Model, u.Context, _cfg.ContextWindow);
            float f = Math.Clamp(u.Context / (float)win, 0, 1);
            uint mc = f > 0.85f ? C.Err : f > 0.6f ? C.Warn : C.Run;
            g.Text($"Context {f * 100:0}%", g.FLabel, f > 0.6f ? mc : C.Dim, sx, sy, sw / 2, 15);
            g.Text(Usage.Tok(u.Context) + " / " + Usage.Tok(win), g.FMonoXs, C.Faint, sx + sw / 2, sy, sw / 2, 15, 2);
            sy += 18;
            g.FillRR(sx, sy, sw, 3, 1.5f, C.Track);
            g.FillRR(sx, sy, MathF.Max(3, sw * f), 3, 1.5f, mc);
            sy += 8;
            g.Text("Session " + Usage.Tok(u.Total) + " tokens", g.FMonoXs, C.Faint, sx, sy, sw, 14);
            sy += 22;
        }
        else sy += 2;

        if (t.Hooked && t.Prompt.Length > 0)
        {
            g.Text("You", g.FSemi, C.Text, sx, sy, sw, 18); sy += 19;
            float ph = u != null && u.Context > 0 ? 34 : 52;   // two lines when the meter takes room, else three
            g.TextWrap(t.Prompt, g.FUi, C.Text, sx, sy, sw, ph); sy += ph + 6;
        }
        else if (!t.Hooked)
        {
            g.TextWrap(t.Name.Contains("claude", StringComparison.OrdinalIgnoreCase)
                ? "Run  nb hooks  and add it to your Claude Code settings to see prompts, tool calls and approvals here."
                : "Live prompts and tool calls are shown for Claude Code sessions.", g.FLabel, C.Faint, sx, sy, sw, 64);
            sy += 70;
        }
        string note = t.State switch { "busy" => Doing(t.Tool) + "…", "wait" => t.Msg, "done" => "Finished. Your turn.", _ => "" };
        if (note.Length > 0) { g.TextWrap(note, g.FLabel, C.Faint, sx, sy, sw, 30); sy += note.Length > 34 ? 32 : 18; }

        // the command box, pinned to the bottom of the pane
        var (_, tn, td) = ToolOf(t.Tool);
        string cmd = tn == "Bash" ? "$ " + td : tn.Length > 0 ? tn + "  " + Fmt.Dir(td) : "";
        bool ask = t.AskPort > 0 && t.State == "wait" && t.Verdict.Length == 0;
        float bh = (cmd.Length > 0 ? 50 : 30) + (ask ? 34 : 0), by = CH - 22 - 12 - bh, bx = sx - 2, bw = sw + 4;

        // the last few tool calls, newest at the bottom, in whatever room is left above the command box
        // (the window picture on the left hides the full log, so it's repeated here in short)
        var recent = t.Log;
        if (recent != null && recent.Count > 0 && _prev.TryGetValue(t.Key, out var pv2) && pv2.Img != 0)
        {
            int fit = Math.Clamp((int)((by - 6 - sy) / 17), 0, 3);
            int skip = Math.Max(0, recent.Count - fit);
            float ly0 = by - 6 - fit * 17;
            for (int i = 0; i < fit && skip + i < recent.Count; i++)
            {
                var e = recent[skip + i];
                float yy = ly0 + i * 17;
                string line = e.Tool == "Bash" ? e.Detail : e.Tool + " " + (e.Detail.Contains('\\') || e.Detail.Contains('/') ? Fmt.Dir(e.Detail) : e.Detail);
                if (e.Done) Icon(Ico.Check, sx + 3, yy + 8, C.A(C.Ok, 0.8f), now, g);
                else g.Circle(sx + 3, yy + 8, 2.2f, t.State == "wait" ? C.Warn : C.Run);
                g.Text(line, g.FMonoXs, e.Done ? C.Faint : C.Dim, sx + 13, yy, sw - 13, 16);
            }
        }
        bool amber = t.State == "wait" && t.Verdict.Length == 0;
        g.FillRR(bx, by, bw, bh, 9, amber ? C.WellWarn : C.Well);
        g.StrokeRR(bx + 0.5f, by + 0.5f, bw - 1, bh - 1, 8.5f, amber ? C.A(C.Warn, 0.42f) : C.Line, 1);
        float ly = by + 8;
        if (cmd.Length > 0) { g.Text(cmd, g.FMonoSm, C.Text, bx + 10, ly, bw - 20, 16); ly += 21; }
        string st = StatusText(t, now);
        uint col; Ico ic;
        if (t.Verdict == "allow") { col = C.Ok; ic = Ico.Check; }
        else if (t.Verdict == "deny") { col = C.Err; ic = Ico.Cross; }
        else (col, ic) = t.State switch { "wait" => (C.Warn, Ico.Dot), "busy" => (C.Run, Ico.Spin), "done" => (C.Ok, Ico.Check), _ => (C.Faint, Ico.Dot) };
        float iy = ly + 7.5f;
        if (ic == Ico.Spin) { g.Arc(bx + 15, iy, 4.5f, 0, 360, 1.3f, C.Track); g.Arc(bx + 15, iy, 4.5f, -90, 100, 1.5f, col); }
        else if (ic == Ico.Dot) g.Circle(bx + 15, iy, 3, col);
        else Icon(ic, bx + 15, iy, col, now, g);
        g.Text(st, g.FSmall, col, bx + 26, ly, bw - 34, 15);
        if (ask)
        {
            float xr = bx + bw - 10, yb = by + bh - 33;
            xr -= Btn(c, xr, yb, "Allow", true, HK.Allow, t.Key, "A") + 6;
            Btn(c, xr, yb, "Deny", false, HK.Deny, t.Key, "D");
        }

        // status bar in the host's colour
        g.RR(0, 0, CW, CH, 12); g.Clip();
        g.FillRect(0, CH - 22, CW, 22, HostBar(t.Host));
        g.Unclip();
        string br = BranchFor(t.Cwd);
        g.Text(br.Length > 0 ? br : project, g.FMonoXs, C.OnBar, 12, CH - 22, CW / 2, 22);
        g.Text(t.Host, g.FMonoXs, C.OnBar, CW / 2, CH - 22, CW / 2 - 12, 22, 2);
        FaceEdge();

        bool hasWin = _prev.TryGetValue(t.Key, out var pw) && pw.Hwnd != 0;
        if (hasWin) Btn(c, lw - 10, top + 10, "Open in " + t.Host + " ↗", true, HK.Focus, t.Key, "↵");
        c.Act = hasWin ? HK.Focus : HK.None; c.Arg = t.Key;
        c.Chip = t.Host;
        foreach (var o in _terms) if (o.Key != t.Key && o.Host == t.Host) { c.Chip = t.Host + " · " + project; break; }
        c.ChipCol = t.Verdict == "allow" ? C.Text : StateWord(t).Item2;
    }

    // ---- builds

    // A "job" is one command in one folder: its live run (if any) plus its finished runs from the history.
    sealed class Job
    {
        public string Key = "", Label = "", Cmd = "", Cwd = "";
        public TaskInfo? Live;
        public readonly List<Run> Runs = new();   // oldest first
    }
    static readonly List<Job> _jobs = new();

    static string JobKey(string cwd, string cmd, string label) => cwd.ToLowerInvariant() + "\n" + (cmd.Length > 0 ? cmd : label);
    static long WallAgo(Run r) => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - r.EndUtc;

    /// Running commands first, then everything run before, most recent first.
    static void BuildJobs()
    {
        _jobs.Clear();
        var by = new Dictionary<string, Job>();
        Job Get(string key, string label, string cmd, string cwd)
        {
            if (!by.TryGetValue(key, out var j)) { by[key] = j = new Job { Key = key, Label = label, Cmd = cmd, Cwd = cwd }; _jobs.Add(j); }
            return j;
        }
        foreach (var t in _tasks)
            if (t.Running || t.Code == TaskInfo.Lost)
            {
                var j = Get(JobKey(t.Cwd, t.Cmd, t.Label), t.Label, t.Cmd, t.Cwd);
                if (j.Live == null || t.Running) j.Live = t;
            }
        var runs = Model.Runs;
        for (int i = runs.Count - 1; i >= 0; i--)
        {
            var r = runs[i];
            Get(r.Job, r.Label, r.Cmd, r.Cwd).Runs.Insert(0, r);
        }
        if (_jobs.Count > MaxCards) _jobs.RemoveRange(MaxCards, _jobs.Count - MaxCards);
    }

    static void BuildCard(Card c, Job j, long now)
    {
        var g = G;
        Face();
        float x = 26, w = CW - 52, y = 24;
        var t = j.Live;
        var last = j.Runs.Count > 0 ? j.Runs[^1] : null;
        bool running = t is { Running: true }, lost = !running && t != null && t.Code == TaskInfo.Lost && (last == null || t.End > 0);
        int code = last?.Code ?? 0;
        bool ok = !lost && code == 0;
        string word; uint col;
        if (running) { word = "Running"; col = C.Run; g.Arc(x + 6, y + 9, 5.5f, 0, 360, 1.3f, C.Track); g.Arc(x + 6, y + 9, 5.5f, -90, 100, 1.6f, C.Run); }
        else if (lost) { word = "Lost"; col = C.Faint; g.Circle(x + 6, y + 9, 3, col); }
        else if (ok) { word = "Passed"; col = C.Ok; Icon(Ico.Check, x + 6, y + 9, col, now, g); }
        else { word = "Failed · exit " + code; col = C.Err; Icon(Ico.Cross, x + 6, y + 9, col, now, g); }
        g.Text(word, g.FUi, col, x + 20, y, 300, 18);
        g.Text(running ? "now" : last != null ? Fmt.Ago(WallAgo(last)) : "", g.FMonoSm, C.Faint, x, y, w, 18, 2);
        y += 32;
        g.Text(j.Label, g.FMonoXl, C.Text, x, y, w, 30);
        y += 32;
        string where = (j.Cwd.Length > 0 ? ShortPath(j.Cwd) + " · " : "") + (j.Cmd.Length > 0 && j.Cmd != j.Label ? j.Cmd : "via nb run");
        g.Text(where, g.FSmall, C.Faint, x, y, w, 18);
        y += 32;
        long eta = Model.EtaFor(j.Label);
        g.FillRR(x, y, w, 3, 1.5f, C.Track);
        float frac = running ? (eta > 0 ? Math.Clamp((now - t!.Start) / (float)eta, 0, 1) : 0) : lost ? 0 : 1;
        if (frac > 0) g.FillRR(x, y, MathF.Max(3, w * frac), 3, 1.5f, running ? C.Run : ok ? C.Ok : C.Err);
        else if (running) g.FillRR(x + w * (now % 1500 / 1500f) * 0.8f, y, w * 0.2f, 3, 1.5f, C.Run);
        y += 12;
        string left = running ? Fmt.Clock(now - t!.Start) + (eta > 0 ? " / ~" + Fmt.Clock(eta) : "") : last != null ? Fmt.Dur(last.Ms) : "";
        g.Text(left, g.FMono, C.Dim, x, y, w / 2, 18);
        string right = running ? (eta > 0 ? $"{frac * 100:0}%" : "first run") : lost ? "lost" : last == null ? "" : ok ? "exit 0" : "exit " + code;
        g.Text(right, g.FMono, running || ok ? C.Dim : col, x + w / 2, y, w / 2, 18, 2);
        y += 34;

        // history: one bar per earlier run, height = how long it took, colour = how it ended
        var hist = j.Runs;
        if (hist.Count > 0)
        {
            int n = Math.Min(hist.Count, 24);
            long max = 1;
            for (int i = hist.Count - n; i < hist.Count; i++) max = Math.Max(max, hist[i].Ms);
            int okN = 0; for (int i = hist.Count - n; i < hist.Count; i++) if (hist[i].Code == 0) okN++;
            g.Text($"Last {n} run{(n == 1 ? "" : "s")}", g.FLabel, C.Faint, x, y, w / 2, 15);
            g.Text($"{okN}/{n} passed" + (eta > 0 ? " · usually ~" + Fmt.Clock(eta) : ""), g.FLabel, C.Faint, x + w / 2, y, w / 2, 15, 2);
            y += 20;
            const float BH = 30, BW = 9, GAP = 4;
            for (int i = 0; i < n; i++)
            {
                var r = hist[hist.Count - n + i];
                float bh = MathF.Max(3, BH * r.Ms / max), bx = x + i * (BW + GAP);
                uint bc = r.Code == 0 ? C.Ok : C.Err;
                g.FillRR(bx, y + BH - bh, BW, bh, 2, C.A(bc, i == n - 1 ? 0.95f : 0.45f));
            }
        }
        float by = CH - 24 - 25, xr = CW - 26;
        if (j.Cwd.Length > 0) xr -= Btn(c, xr, by, "Terminal ↗", false, HK.Repo, j.Cwd) + 8;
        if (!running && (j.Cmd.Length > 0 || j.Label.Length > 0)) Btn(c, xr, by, "Run again", true, HK.Rerun, j.Key, "↵");
        FaceEdge();
        c.Act = !running ? HK.Rerun : j.Cwd.Length > 0 ? HK.Repo : HK.None;
        c.Arg = c.Act == HK.Rerun ? j.Key : j.Cwd;
        c.Chip = j.Label.Length > 26 ? j.Label[..25] + "…" : j.Label;
        c.ChipCol = running ? C.Run : col;
    }

    /// Run a job's command again, in a new terminal tab in its folder, through nb so it shows up here.
    static void Rerun(string key)
    {
        var j = _jobs.Find(x => x.Key == key);
        if (j == null) return;
        string cmd = j.Cmd.Length > 0 ? j.Cmd : j.Label;
        string nb = Path.Combine(AppContext.BaseDirectory, "nb.exe");
        string inner = $"\"{nb}\" run" + (j.Label != cmd ? $" -n \"{j.Label.Replace("\"", "'")}\"" : "") + " -- " + cmd;
        string dir = Directory.Exists(j.Cwd) ? j.Cwd : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Collapse(false);
        // Windows Terminal reads ';' as "new tab": escape it. cmd /s /k "..." strips exactly the outer quotes.
        string wtArgs = $"-w 0 nt -d \"{dir}\" cmd /s /k \"{inner.Replace(";", "\\;")}\"";
        if (ShellExecuteW(0, "open", "wt.exe", wtArgs, null, 1) <= 32)
            ShellExecuteW(0, "open", Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", $"/s /k \"{inner}\"", dir, 1);
    }

    // ---- ports

    static void PortCard(Card c, Probe.Server s)
    {
        var g = G;
        string port = s.Port.ToString();
        Face();
        TitleBar("localhost:" + port, true);
        float x = 28, w = CW - 56, y = 31 + 22;
        g.Circle(x + 3, y + 9, 3, C.Ok);
        g.Text("Listening", g.FSmall, C.Ok, x + 12, y, 200, 18);
        y += 28;
        g.Text(":" + port, g.FMonoHuge, C.Text, x - 3, y, w, 52);
        y += 56;
        g.Text(s.Proc, g.FUi, C.Dim, x, y, w, 20);
        y += 38;
        foreach (var (k, v) in new[] { ("Process", s.Proc), ("Address", "127.0.0.1:" + port), ("URL", "http://localhost:" + port) })
        {
            g.Text(k, g.FUi, C.Faint, x, y, 90, 20);
            g.Text(v, k == "Process" ? g.FUi : g.FMono, C.Text, x + 96, y, w - 96, 20);
            y += 27;
        }
        Btn(c, CW - 26, CH - 24 - 25, "Open ↗", true, HK.Server, port);
        FaceEdge();
        c.Act = HK.Server; c.Arg = port;
        c.Chip = ":" + port; c.ChipCol = C.Ok;
    }

    // ---- git

    static void GitCard(Card c, Repo r)
    {
        var g = G;
        var st = r.St;
        Face();
        float x = 26, w = CW - 52, y = 24;
        string br = st?.Branch ?? "…";
        float bw = g.Measure(br, g.FMonoSm) + 18;
        g.StrokeRR(x + 0.5f, y + 0.5f, bw - 1, 21, 6, C.Track, 1);
        g.Text(br, g.FMonoSm, C.Dim, x, y, bw, 22, 1);
        if (st != null) g.Text((st.Ahead > 0 ? "↑" + st.Ahead + "  " : "") + (st.Behind > 0 ? "↓" + st.Behind : ""), g.FMonoSm, C.Dim, x, y, w, 22, 2);
        y += 36;
        g.Text(r.Name, g.FTitleLg, C.Text, x, y, w, 30);
        y += 32;
        uint col;
        if (st == null) { g.Text("Reading status…", g.FSmall, C.Faint, x, y, w, 18); col = C.Faint; }
        else if (st.Changed + st.Untracked == 0) { g.Text("Clean · nothing to commit", g.FSmall, C.Ok, x, y, w, 18); col = C.Ok; }
        else
        {
            string ch = (st.Changed > 0 ? $"{st.Changed} modified" : "") + (st.Changed > 0 && st.Untracked > 0 ? " · " : "") + (st.Untracked > 0 ? $"{st.Untracked} new" : "");
            g.Text(ch, g.FSmall, C.Warn, x, y, w, 18); col = C.Warn;
        }
        y += 30;
        if (st != null)
            for (int i = 0; i < st.Files.Count && i < 5; i++)
            {
                var (code, path) = st.Files[i];
                uint cc = code switch { 'A' => C.Ok, 'D' => C.Err, 'U' => C.Err, '?' => C.Faint, 'R' => C.Dim, _ => C.Warn };
                g.Text(code == '?' ? "??" : code.ToString(), g.FMonoSm, cc, x, y, 24, 26);
                g.Text(path, g.FMonoSm, C.Dim, x + 28, y, w - 28, 26);
                g.Line(x, y + 26.5f, x + w, y + 26.5f, 1, C.Hair);
                y += 27;
            }
        g.Text(ShortPath(r.Root), g.FLabel, C.Faint, x, CH - 24 - 25, w - 120, 25);
        Btn(c, CW - 26, CH - 24 - 25, "Terminal ↗", false, HK.Repo, r.Root);
        FaceEdge();
        c.Act = HK.Repo; c.Arg = r.Root;
        c.Chip = r.Name; c.ChipCol = col;
    }

    // ================================================================ frame

    static void EnsureDeck()
    {
        float monW = (_mon.R - _mon.L) / _s;
        if (_gd != null && MathF.Abs(_gd.S - _s) < 0.001f) return;
        _gd?.Dispose();
        _gd = new Gfx(_s, MathF.Min(monW, 2 * (Spread + Spread2 + CW * 0.45f) + 40) + 4, CH + 150);
    }

    static void HideDeck()
    {
        ShowWindow(_deck, 0); ShowWindow(_scrim, 0);
        _deckShown = false;
        FreeCards();
        _gd?.Dispose(); _gd = null;
        if (_scrimBmp != 0) { DeleteObject(_scrimBmp); _scrimBmp = 0; }
        FreePreviews(_prev);
        _prevAt = -100_000;
        _dhits.Clear(); _dhk = HK.None; _dharg = null;
    }

    static float Ease(float x) { x = Math.Clamp(x, 0, 1); float y = 1 - x; return 1 - y * y * y; }

    /// Draw the deck and fade the scrim for island progress tc. Returns true while something is still moving.
    static bool DeckFrame(in Geo q, float tc, long hr, long now, float k)
    {
        bool open = tc > 0.001f || _tTo > 0.5f;
        if (!open) { if (_deckShown) HideDeck(); return false; }
        EnsureDeck();
        var g = _gd!;
        SyncCards(now);
        int f = _focus[_tab];
        bool sliding = ApproachF(ref _fpos, f, k);

        // layout (monitor DIP): header on the box, chips above it, cards above the chips
        float monW = (_mon.R - _mon.L) / _s, cx = q.Pcx;
        float hy0 = q.By0 - HH, chipB = hy0 - 14, chipT = chipB - ChipH, cardB = chipT - 18;
        float span = MathF.Min(monW, g.W / _s - 4);
        float L = MathF.Floor(Math.Clamp(cx - span / 2, 0, MathF.Max(0, monW - span)) * _s) / _s;
        float T = MathF.Floor((cardB - CH - 30) * _s) / _s;
        int bw = Math.Min(g.W, (int)MathF.Ceiling(span * _s)), bh = Math.Min(g.H, (int)MathF.Ceiling((chipB + 6 - T) * _s));
        float ox = -L, oy = -T;

        g.Clear(bw, bh);
        g.Begin();
        _dhits.Clear();
        // _enterAt == 0 means no page switch is playing; never treat that as entering, or the deck
        // animates forever and the posted frame ticks starve mouse/keyboard input
        bool entering = _enterAt != 0 && (hr - _enterAt) / 380f < 1.5f;
        if (!entering) _enterAt = 0;

        // farthest first, so the focused card is drawn on top
        Span<float> quad = stackalloc float[8];
        var order = new List<int>(_cards.Count);
        for (int i = 0; i < _cards.Count; i++) if (MathF.Abs(i - _fpos) < 2.6f) order.Add(i);
        order.Sort((a, b) => MathF.Abs(b - _fpos).CompareTo(MathF.Abs(a - _fpos)));
        bool settled = !sliding && tc >= 0.999f && !entering;
        foreach (int i in order)
        {
            var c = _cards[i];
            float d = i - _fpos, ad = MathF.Abs(d), s = MathF.Sign(d);
            float kOpen = Math.Clamp((tc - MathF.Min(ad, 2) * 0.08f) / (1 - 0.16f), 0, 1);
            float kEnt = _enterAt == 0 ? 1 : Ease((hr - _enterAt - ad * 45) / 380f);
            float kc = MathF.Min(kOpen, kEnt);
            float x = s * (ad <= 1 ? ad * Spread : Spread + (ad - 1) * Spread2) * Lerp(0.4f, 1, kc);
            float sc = (1 - MathF.Min(ad, 2) * 0.15f) * Lerp(0.86f, 1, kc);
            float ry = -s * MathF.Min(ad, 2) * 11;
            float ccx = cx + x + ox, bot = cardB + (1 - kc) * 70 + oy, ccy = bot - CH * sc / 2;
            g.Glow(ccx, bot + 4, CW * sc * 0.42f, 16, C.A(0, 0.75f * kc));
            int iw = (int)MathF.Ceiling(CW * _s), ih = (int)MathF.Ceiling(CH * _s);
            if (ad < 0.0005f && MathF.Abs(sc - 1) < 0.0005f)
            {
                float x0 = MathF.Round((ccx - CW / 2) * _s) / _s, y0 = MathF.Round((ccy - CH / 2) * _s) / _s;
                g.Image(c.Img, x0, y0, CW, CH, 0, 0, iw, ih, 5);
                if (settled)
                {
                    foreach (var h in c.Hits) _dhits.Add(new Hit { R = new RectF(h.R.X + x0, h.R.Y + y0, h.R.W, h.R.H), K = h.K, Arg = h.Arg });
                    if (c.Act != HK.None) _dhits.Add(new Hit { R = new RectF(x0, y0, CW, CH), K = c.Act, Arg = c.Arg });
                }
            }
            else
            {
                g.Turned(c.Img, iw, ih, ccx, ccy, CW * sc, CH * sc, ry, quad);
                float dim = ad < 1 ? ad * 0.47f : MathF.Min(0.62f, 0.32f + ad * 0.15f);
                g.FillPoly(quad, C.A(0x05060A, dim));
                if (ad > 0.5f) _dhits.Add(new Hit { R = new RectF(quad[0], quad[1], quad[2] - quad[0], quad[5] - quad[1]), K = HK.Card, Arg = i.ToString() });
            }
        }
        // the hit list is searched front to back: focused card's buttons first, side cards last
        _dhits.Reverse();
        ReorderHits();

        // chips
        float total = 0;
        Span<float> cw = stackalloc float[_cards.Count];
        for (int i = 0; i < _cards.Count; i++) { cw[i] = 10 + 6 + 7 + g.Measure(_cards[i].Chip, g.FUi) + 12; total += cw[i] + (i > 0 ? 8 : 0); }
        float chx = cx - total / 2 + ox, chy = chipT + oy;
        for (int i = 0; i < _cards.Count; i++)
        {
            var c = _cards[i];
            bool on = i == f, hot = _dhk == HK.Chip && _dharg == i.ToString();
            g.FillRR(chx, chy, cw[i], ChipH, ChipH / 2, on ? C.ChipOn : hot ? C.ChipHot : C.ChipOff);
            g.StrokeRR(chx + 0.5f, chy + 0.5f, cw[i] - 1, ChipH - 1, ChipH / 2 - 0.5f, on ? (C.HasAccent ? C.A(C.Accent, 0.6f) : C.Track) : C.Line, 1);
            g.Circle(chx + 13, chy + ChipH / 2, 3, c.ChipCol);
            g.Text(c.Chip, g.FUi, on || hot ? C.Text : C.Dim, chx + 23, chy, cw[i] - 23, ChipH);
            _dhits.Add(new Hit { R = new RectF(chx, chy, cw[i], ChipH), K = HK.Chip, Arg = i.ToString() });
            chx += cw[i] + 8;
        }
        g.Flush();

        int wx = _mon.L + (int)MathF.Round(L * _s), wy = _mon.T + (int)MathF.Round(T * _s);
        byte da = (byte)(Math.Clamp(tc * 1.7f, 0, 1) * 255);
        Present(_deck, wx, wy, bw, bh, g.Dc, da);
        _deckWin = new RECT { L = wx, T = wy, R = wx + bw, B = wy + bh };
        if (!_deckShown) { ShowWindow(_deck, 4); _deckShown = true; ZAll(); }

        byte sa = (byte)(Math.Clamp(tc * 1.5f, 0, 1) * (_cfg.Glass ? 255 : 160));
        if (sa != _scrimA) { _scrimA = sa; SetLayeredWindowAttributes(_scrim, 0, sa, 2); }
        return sliding || entering;
    }

    /// Buttons beat the card body they sit on; the body beats side cards.
    static void ReorderHits()
    {
        _dhits.Sort((a, b) => Rank(a.K).CompareTo(Rank(b.K)));
        static int Rank(HK k) => k switch { HK.Allow or HK.Deny => 0, HK.Card => 3, HK.Chip => 0, _ when k is HK.Focus or HK.Server or HK.Repo or HK.Rerun => 1, _ => 2 };
    }

    // ================================================================ input

    static void DeckMove(int x, int y)
    {
        if (!_dtracking)
        {
            var t = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = 2, hwndTrack = _deck };
            TrackMouseEvent(&t);
            _dtracking = true;
        }
        var (k, arg) = DeckHit(x, y);
        if (k != _dhk || arg != _dharg) { _dhk = k; _dharg = arg; Invalidate(); }
    }

    static (HK, string?) DeckHit(int x, int y)
    {
        float dx = x / _s, dy = y / _s;
        foreach (var h in _dhits) if (h.R.Contains(dx, dy)) return (h.K, h.Arg);
        return (HK.None, null);
    }

    static void DeckClick(int x, int y)
    {
        var (k, arg) = DeckHit(x, y);
        if (k == HK.None) { Collapse(); return; }   // a click in the deck's empty space closes, like the scrim
        CardAction(k, arg!);
    }

    static void CardAction(HK k, string arg)
    {
        switch (k)
        {
            case HK.Card: case HK.Chip: Focus(int.Parse(arg)); break;
            case HK.Allow: case HK.Deny: SendVerdict(arg, k == HK.Allow); break;
            case HK.Focus:
                if (_prev.TryGetValue(arg, out var pv) && pv.Hwnd != 0 && IsWindow(pv.Hwnd) != 0)
                {
                    Collapse(false);
                    if (IsIconic(pv.Hwnd) != 0) ShowWindow(pv.Hwnd, 9);   // SW_RESTORE
                    SetForegroundWindow(pv.Hwnd);
                }
                break;
            case HK.Rerun: Rerun(arg); break;
            case HK.Server: Collapse(false); ShellExecuteW(0, "open", "http://localhost:" + arg, null, null, 1); break;
            case HK.Repo:
                Collapse(false);
                if (ShellExecuteW(0, "open", "wt.exe", $"-d \"{arg}\"", null, 1) <= 32) ShellExecuteW(0, "open", arg, null, null, 1);
                break;
        }
    }

    static void Focus(int i)
    {
        if (_cards.Count == 0) return;
        i = Math.Clamp(i, 0, _cards.Count - 1);
        if (i == _focus[_tab]) return;
        _focus[_tab] = i;
        _dhk = HK.None; _dharg = null;
        Invalidate();
    }

    static void OnKey(int vk)
    {
        if (!Expanded) return;
        switch (vk)
        {
            case 0x1B: Collapse(); break;                                       // Esc
            case 0x25: Focus(_focus[_tab] - 1); break;                          // ←
            case 0x27: Focus(_focus[_tab] + 1); break;                          // →
            case 0x26: GoTab(_tab - 1); break;                                  // ↑
            case 0x28: GoTab(_tab + 1); break;                                  // ↓
            case 0x09: GoTab((_tab + 1) % Tabs); break;                         // Tab
            case 0x0D:                                                           // Enter: the focused card's main action
                if (_focus[_tab] < _cards.Count && _cards[_focus[_tab]] is { Act: not HK.None } c) CardAction(c.Act, c.Arg);
                break;
            case 0x41: case 0x44: case 0x59: case 0x4E:                         // A / D (or Y / N): answer the focused agent's permission request
                if (_tab == 0 && _focus[0] < _terms.Count && _terms[_focus[0]] is { AskPort: > 0, State: "wait" } t && t.Verdict.Length == 0)
                {
                    bool allow = vk is 0x41 or 0x59;
                    SendVerdict(t.Key, allow);
                    // opened with the shortcut just to answer: close again once nothing else is waiting
                    if (_viaHotkey && !Model.Agents.Exists(a => a.AskPort > 0 && a.State == "wait")) Collapse();
                }
                break;
            case 0x52:                                                           // R: run the focused build again
                if (_tab == 1 && _focus[1] < _cards.Count && _cards[_focus[1]] is { Act: HK.Rerun } rc) Rerun(rc.Arg);
                break;
        }
    }

    /// Answer a waiting PermissionRequest hook (nb listens on the port it reported).
    static void SendVerdict(string key, bool allow)
    {
        var a = Model.Agents.Find(x => x.Key == key);
        if (a == null || a.AskPort <= 0 || _sock == 0) return;
        byte* sa = stackalloc byte[16];
        new Span<byte>(sa, 16).Clear();
        sa[0] = 2; sa[2] = (byte)(a.AskPort >> 8); sa[3] = (byte)(a.AskPort & 0xFF); sa[4] = 127; sa[7] = 1;
        var b = Encoding.ASCII.GetBytes(allow ? "allow" : "deny");
        fixed (byte* p = b) sendto(_sock, p, b.Length, 0, sa, 16);
        a.Verdict = allow ? "allow" : "deny";
        a.AskPort = 0;
        if (allow) { a.State = "busy"; a.Since = Model.Now; }
        Invalidate();
    }
}
