using System.Runtime.InteropServices;
using System.Text;
using static Perch.W;

namespace Perch;

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

    // ---- which window each agent runs in (for "Open in …" and quick replies). No pictures are taken.

    static Dictionary<string, (nint Hwnd, nint Img)> _prev = new();
    static Dictionary<string, (nint, nint)>? _prevNew;
    static int _prevBusy; static long _prevAt = -100_000;

    static void CapturePreviews()
    {
        if (Interlocked.Exchange(ref _prevBusy, 1) == 1) return;
        _prevAt = Model.Now;
        var want = new List<(string Key, uint Pid)>();
        foreach (var t in _terms) if (t.Pid != 0) want.Add((t.Key, t.Pid));
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            var res = new Dictionary<string, (nint, nint)>();
            try
            {
                if (want.Count > 0)
                {
                    var wins = TopWindows();
                    var parents = Probe.ParentMap();
                    foreach (var (key, pid) in want)
                    {
                        // walk up from the agent to the terminal/editor (or console) that owns its window; stop at
                        // anything else, so a browser or an unrelated app's window is never picked
                        nint h = 0; uint p = pid;
                        for (int i = 0; i < 16 && p != 0; i++)
                        {
                            var kind = Probe.KindOf(p);
                            if (kind is Probe.Kind.Host or Probe.Kind.Shell && wins.TryGetValue(p, out h)) break;
                            h = 0;
                            if (i > 0 && kind is not (Probe.Kind.Shell or Probe.Kind.Host or Probe.Kind.Agent)) break;
                            if (!parents.TryGetValue(p, out var pp) || pp == p) break;
                            p = pp;
                        }
                        if (h != 0) res[key] = (h, 0);
                    }
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
                    hc.Add(_prev.TryGetValue(t.Key, out var pv) ? pv.Hwnd : 0);
                    hc.Add(Usage.Get(t.Transcript));
                    if (t.Log is { Count: > 0 } lg) hc.Add(lg[^1].Done);
                    hc.Add(_paneView); hc.Add(_tlScroll); hc.Add(t.AutoN); hc.Add(t.Changes); hc.Add(t.Since);
                    hc.Add(_confirm == "undo:" + t.Key && Model.Now - _confirmAt < 4000);
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
                        j.Live is { Running: true } lt ? Fmt.Clock(now - lt.Start) : last != null ? Fmt.Ago(WallAgo(last)) : "",
                        HashCode.Combine(FailOutput(j), Fixable(j) != null)), i));
                }
                break;
            case 2:
                for (int i = 0; i < Probe.Servers.Count && want.Count < MaxCards; i++)
                {
                    var s = Probe.Servers[i];
                    var pi = ProcInfo.Get(s.Pid);
                    want.Add(("p" + s.Port, HashCode.Combine(s.Proc, s.Pid, pi, pi != null ? Uptime(pi.StartUtc) : "",
                        _confirm == "stop:" + s.Port && Model.Now - _confirmAt < 4000, ProcInfo.LaunchedByCached(s.Pid) != null), i));
                }
                break;
            default:
                for (int i = 0; i < _repos.Count && want.Count < MaxCards; i++)
                    want.Add(("g" + _repos[i].Root, HashCode.Combine(_repos[i].St, _repos[i].Name, _repos[i].Gh), i));
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
            0 => ("No agent sessions", "Claude Code, Codex, Gemini and other agent CLIs show up here while they run. Run  perch hooks  to connect Claude Code."),
            1 => ("Nothing built yet", "Prefix a command with  perch run  to follow it here, with a progress bar learned from earlier runs."),
            2 => ("No dev servers running", "Local listeners from node, bun, python, docker and others show up here."),
            _ => ("No repositories yet", "Repos show up once you  perch run  in them or an agent works in them, or add  repo = <path>  to the config."),
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

    /// The session at a glance: the agent's mark in its state's colour, the task (your last prompt), what it's
    /// doing now or what it said when it finished, a tally of the turn ("6 reads · 3 edits · 4 commands") and
    /// its last few steps. Built from the transcript (or, without one, the hook log).
    static void StoryPane(Gfx g, in Term t, Usage.Snap? u, float x, float y, float w, float h, long now)
    {
        const float PAD = 18;
        bool tl = u != null && u.Events.Length > 0;
        float cx = x + PAD, cw = w - 2 * PAD, yy = y + (tl ? 46 : 20), bottom = y + h - 4;
        var (word, col) = StateWord(t);
        uint mark = col == C.Faint ? C.Dim : col;

        // the mark, in a ring of the state's colour
        float gx = cx + 18, gy = yy + 18;
        g.Circle(gx, gy, 18, C.A(mark, 0.12f));
        g.Arc(gx, gy, 17.5f, 0, 360, 1.4f, C.A(mark, t.State is "busy" or "wait" ? 0.7f : 0.35f));
        Glyph(t.Name, gx, gy, 9, mark, g);
        g.Text(AgentTitle(t.Name), g.FTitle, C.Text, cx + 46, yy - 1, cw - 46, 22);
        string when = t.State is "busy" or "wait" ? " · " + Fmt.Clock(now - t.Since) : t.State == "done" ? " · " + Fmt.Ago(now - t.Since) : "";
        g.Text(word + when + "  ·  " + t.Host, g.FSmall, col == C.Faint ? C.Dim : col, cx + 46, yy + 21, cw - 46, 16);
        yy += 50;

        if (!t.Hooked)
        {
            g.TextWrap(t.Name.Contains("claude", StringComparison.OrdinalIgnoreCase)
                ? "Run  perch hooks  and add it to your Claude Code settings to see what this session is working on."
                : "Live summaries are shown for Claude Code sessions.", g.FSmall, C.Faint, cx, yy, cw, 50);
            return;
        }

        // this turn's events: everything after your last prompt
        var evs = u?.Events ?? Array.Empty<Usage.Ev>();
        int from = Array.FindLastIndex(evs, e => e.Kind == 'p');
        var turn = from >= 0 ? evs[(from + 1)..] : evs;
        string task = t.Prompt.Length > 0 ? t.Prompt : from >= 0 ? evs[from].Text : "";
        if (task.Length > 0) { g.TextWrap(task, g.FUi, C.Text, cx, yy, cw, 36); yy += 42; }

        // now / needs you / what it said at the end
        string say = t.State switch
        {
            "busy" => Doing(t.Tool) + "…",
            "wait" => t.Msg.Length > 0 ? t.Msg : "Waiting for you",
            "done" => Array.FindLast(turn, e => e.Kind == 'r')?.Text ?? "Finished. Your turn.",
            _ => "",
        };
        if (say.Length > 0)
        {
            uint sc = t.State == "busy" ? C.Run : t.State == "wait" ? C.Warn : C.Dim;
            g.Line(cx + 0.5f, yy + 2, cx + 0.5f, yy + 40, 2, C.A(sc, 0.6f));   // a margin rule, like a quote
            g.TextWrap(say, g.FSmall, t.State == "done" ? C.Dim : sc, cx + 10, yy, cw - 10, t.State == "done" ? 46 : 32);
            yy += t.State == "done" ? 52 : 38;
        }

        // the tally of this turn's tool calls
        var tools = turn.Where(e => e.Kind == 't').ToList();
        var names = tools.Count > 0 ? tools.Select(e => e.Text.Split(' ', 2)[0]).ToList()
                  : (t.Log ?? new()).Select(e => e.Tool).ToList();
        int reads = names.Count(n => n is "Read" or "Grep" or "Glob" or "LS" or "NotebookRead");
        int edits = names.Count(n => n is "Edit" or "Write" or "MultiEdit" or "NotebookEdit");
        int cmds = names.Count(n => n is "Bash" or "PowerShell" or "BashOutput");
        int web = names.Count(n => n is "WebFetch" or "WebSearch");
        int other = names.Count - reads - edits - cmds - web;
        var parts = new List<string>();
        void P(int n, string one, string many) { if (n > 0) parts.Add($"{n} {(n == 1 ? one : many)}"); }
        P(reads, "read", "reads"); P(edits, "edit", "edits"); P(cmds, "command", "commands"); P(web, "web lookup", "web lookups"); P(other, "other step", "other steps");
        if (parts.Count > 0 && yy + 16 <= bottom) { g.Text(string.Join("  ·  ", parts), g.FLabel, C.Faint, cx, yy, cw, 16); yy += 22; }

        // the last few steps, as many as fit
        int fit = Math.Max(0, (int)((bottom - yy) / 16));
        if (fit == 0) return;
        if (tools.Count > 0)
            foreach (var e in tools.Skip(Math.Max(0, tools.Count - fit)))
            {
                if (e.Dur < 0) g.Circle(cx + 3, yy + 8, 2.4f, C.Run); else Icon(Ico.Check, cx + 3, yy + 8, C.A(C.Ok, 0.7f), now, g);
                g.Text(e.Text, g.FMonoXs, e.Dur < 0 ? C.Dim : C.Faint, cx + 13, yy, cw - 13, 16);
                yy += 16;
            }
        else if (t.Log is { Count: > 0 } log)
            foreach (var e in log.Skip(Math.Max(0, log.Count - fit)))
            {
                if (!e.Done) g.Circle(cx + 3, yy + 8, 2.4f, C.Run); else Icon(Ico.Check, cx + 3, yy + 8, C.A(C.Ok, 0.7f), now, g);
                string line = e.Tool + "  " + (e.Detail.Contains('\\') || e.Detail.Contains('/') ? Fmt.Dir(e.Detail) : e.Detail);
                g.Text(line, g.FMonoXs, e.Done ? C.Faint : C.Dim, cx + 13, yy, cw - 13, 16);
                yy += 16;
            }
    }

    static int _paneView, _tlScroll;     // agent cards' left pane: 0 terminal, 1 timeline; timeline rows scrolled up (PgUp/PgDn)
    static string _confirm = ""; static long _confirmAt;   // a destructive button pressed once, waiting for the second press

    /// The session step by step: your prompts, each tool call with how long it took, the agent's replies.
    /// The same call repeated back to back is folded into one row and flagged: a sign of a loop.
    static void TimelinePane(Gfx g, Usage.Ev[] evs, float x, float y, float w, float h, long now)
    {
        g.FillRect(x, y, w, h + ReplyH + 14, C.Light ? 0xFFF7F7F9 : 0xFF0B0B0D);
        const float RH = 19, PAD = 12;
        var rows = new List<(Usage.Ev E, int N)>();
        foreach (var e in evs)
        {
            if (rows.Count > 0 && e.Kind == 't' && rows[^1].E.Kind == 't' && rows[^1].E.Text == e.Text) { rows[^1] = (e, rows[^1].N + 1); continue; }
            rows.Add((e, 1));
        }
        float top = y + 40;   // below the view switch
        int fit = Math.Max(1, (int)((y + h - 4 - top) / RH));
        _tlScroll = Math.Clamp(_tlScroll, 0, Math.Max(0, rows.Count - fit));
        int end = rows.Count - _tlScroll, start = Math.Max(0, end - fit);
        float yy = y + h - 4 - (end - start) * RH;
        long nowUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int i = start; i < end; i++, yy += RH)
        {
            var (e, n) = rows[i];
            string time = e.T > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(e.T).ToLocalTime().ToString("HH:mm") : "";
            g.Text(time, g.FMonoXs, C.A(C.Faint, 0.8f), x + PAD, yy, 34, RH);
            float tx = x + PAD + 40, tw = w - PAD - 40 - 50;
            uint col; string text = e.Text;
            switch (e.Kind)
            {
                case 'p': col = C.Text; g.Text("›", g.FSemi, C.Accent, tx - 2, yy, 10, RH); text = "You: " + text; break;
                case 'r': col = C.Dim; g.Arc(tx + 2, yy + RH / 2, 3, 0, 360, 1.3f, C.Accent); break;   // a ring: the ⏺ glyph comes out as a colour emoji
                default:
                    bool loop = n >= 3;
                    col = loop ? C.Warn : C.Dim;
                    if (e.Dur < 0) g.Circle(tx + 2, yy + RH / 2, 2.4f, C.Run);
                    else g.Circle(tx + 2, yy + RH / 2, 2, C.A(C.Faint, 0.7f));
                    if (n > 1) text += $"  ×{n}" + (loop ? "  (repeating)" : "");
                    break;
            }
            g.Text(text, e.Kind == 'p' ? g.FSmall : g.FMonoXs, col, tx + 10, yy, tw, RH);
            string right = e.Kind != 't' ? "" : e.Dur < 0 ? (e.T > 0 ? Fmt.Clock(nowUtc - e.T) : "…") : e.Dur >= 1000 ? Fmt.Dur(e.Dur) : "";
            if (right.Length > 0) g.Text(right, g.FMonoXs, e.Dur < 0 ? C.Run : C.Faint, x, yy, w - PAD, RH, 2);
        }
        if (start > 0) g.Text($"↑ {start} earlier  (PgUp)", g.FMonoXs, C.Faint, x, top - 4, w - PAD, 14, 2);
    }

    static void AgentCard(Card c, in Term t, long now)
    {
        var g = G;
        string project = t.Cwd.Length > 0 ? Fmt.Dir(t.Cwd) : AgentTitle(t.Name);
        Face();
        TitleBar(project + " — " + t.Host, false);
        float lw = CW - SideW, top = 31, lh = CH - 31 - 22;

        // left: a summary of the session (what it's on, what it did), or its timeline (V)
        var ut = Usage.Get(t.Transcript);
        bool hasTl = ut != null && ut.Events.Length > 0;
        if (hasTl && _paneView == 1) TimelinePane(g, ut!.Events, 0, top, lw, lh - ReplyH - 14, now);
        else StoryPane(g, t, ut, 0, top, lw, lh - ReplyH - 14, now);

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

        var ch = t.State == "done" ? t.Changes : null;
        bool changed = ch is { Count: > 0 };
        if (changed)
        {   // what it changed this turn
            int add = ch!.Sum(x => x.Add), del = ch!.Sum(x => x.Del);
            g.Text($"Changed {ch.Count} file{(ch.Count == 1 ? "" : "s")}", g.FLabel, C.Dim, sx, sy, sw, 15);
            g.Text($"+{add}  −{del}", g.FMonoXs, C.Faint, sx, sy, sw, 15, 2);
            sy += 18;
            for (int i = 0; i < ch.Count && i < 3; i++)
            {
                var (p, a, d) = ch[i];
                float nw = g.Measure($"+{a} −{d}", g.FMonoXs);
                g.Text(Fmt.Dir(p), g.FMonoXs, C.Faint, sx, sy, sw - nw - 8, 15);
                g.Text($"+{a}", g.FMonoXs, C.A(C.Ok, 0.85f), sx, sy, sw - g.Measure($" −{d}", g.FMonoXs), 15, 2);
                g.Text($" −{d}", g.FMonoXs, C.A(C.Err, 0.85f), sx, sy, sw, 15, 2);
                sy += 15;
            }
            sy += 4;
        }
        if (t.AutoN > 0) { g.Text($"{t.AutoN} approved by your rules", g.FLabel, C.A(C.Ok, 0.8f), sx, sy, sw, 15); sy += 17; }

        // the command box, pinned to the bottom of the pane
        var (_, tn, td) = ToolOf(t.Tool);
        string cmd = tn == "Bash" ? "$ " + td : tn.Length > 0 ? tn + "  " + Fmt.Dir(td) : "";
        bool ask = t.AskPort > 0 && t.State == "wait" && t.Verdict.Length == 0;
        // "Always": Bash commands by their first words, other tools by name, in this repo
        string scope = tn == "Bash" ? Model.RulePrefix(td) : tn;
        bool rules = ask && scope.Length > 0;
        float bh = (cmd.Length > 0 ? 50 : 30) + (ask ? 34 : 0) + (rules ? 30 : 0) + (changed ? 34 : 0), by = CH - 22 - 12 - bh, bx = sx - 2, bw = sw + 4;

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
            float xr = bx + bw - 10, yb = by + bh - 33 - (rules ? 30 : 0);
            xr -= Btn(c, xr, yb, "Allow", true, HK.Allow, t.Key, "A") + 6;
            Btn(c, xr, yb, "Deny", false, HK.Deny, t.Key, "D");
            if (rules)
            {   // remember the answer: forever, or for the next 10 minutes
                xr = bx + bw - 10; yb += 30;
                string lbl = tn == "Bash" ? "Always “" + (scope.Length > 14 ? scope[..13] + "…" : scope) + "”" : "Always " + tn;
                xr -= Btn(c, xr, yb, lbl, false, HK.Always, t.Key, "L") + 6;
                Btn(c, xr, yb, "10 min", false, HK.Timed, t.Key, "T");
            }
        }
        if (changed)
        {
            float xr = bx + bw - 10, yb = by + bh - 33;
            bool sure = _confirm == "undo:" + t.Key && Model.Now - _confirmAt < 4000;
            xr -= Btn(c, xr, yb, sure ? "Stash them?" : "Undo", !sure ? false : true, HK.Undo, t.Key, "U") + 6;
            Btn(c, xr, yb, "Review", false, HK.Review, t.Key, "R");
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
        if (hasTl)
        {   // Terminal / Timeline switch (V)
            float vx = 10, vy = top + 10;
            foreach (var (name, v) in new[] { ("Summary", 0), ("Timeline", 1) })
            {
                float w = g.Measure(name, g.FLabel) + 16;
                bool on = _paneView == v;
                g.FillRR(vx, vy, w, 22, 6, on ? C.A(C.Face, 0.96f) : C.A(C.Face, 0.6f));
                g.StrokeRR(vx + 0.5f, vy + 0.5f, w - 1, 21, 5.5f, on ? C.Track : C.Line, 1);
                g.Text(name, g.FLabel, on ? C.Text : C.Faint, vx, vy, w, 22, 1);
                c.Hits.Add(new Hit { R = new RectF(vx, vy, w, 22), K = HK.View, Arg = v.ToString() });
                vx += w + 4;
            }
        }
        c.Act = hasWin ? HK.Focus : HK.None; c.Arg = t.Key;
        // the chip is a mini status: project, how long it's been at it, how full its context is
        string clock = t.State is "busy" or "wait" ? " · " + Fmt.Clock(now - t.Since) : "";
        string ctx = ut != null && ut.Context > 0 ? $" · {ut.Context * 100 / Math.Max(1, Usage.Window(ut.Model, ut.Context, _cfg.ContextWindow))}%" : "";
        c.Chip = (t.Cwd.Length > 0 ? project : t.Host) + clock + ctx;
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
        string where = (j.Cwd.Length > 0 ? ShortPath(j.Cwd) + " · " : "") + (j.Cmd.Length > 0 && j.Cmd != j.Label ? j.Cmd : "via perch run");
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

        // a failure with its output: the end of what it printed, the first line that looks like the error picked out
        string outp = !running && !ok && !lost ? FailOutput(j) : "";
        if (outp.Length > 0)
        {
            var lines = outp.Split('\n');
            int errAt = Array.FindIndex(lines, l => ErrorLine.IsMatch(l));
            float boxT = y - 6, boxB = CH - 24 - 25 - 10;
            g.FillRR(x - 8, boxT, w + 16, boxB - boxT, 8, C.Light ? 0xFFF4F4F6 : 0xFF0B0B0D);
            int fit = (int)((boxB - boxT - 10) / 14);
            int s0 = Math.Max(0, lines.Length - fit);
            if (errAt >= 0 && errAt < s0) s0 = Math.Max(0, Math.Min(errAt, lines.Length - fit));   // keep the error line in view
            float ly = boxT + 6;
            for (int i = s0; i < lines.Length && i < s0 + fit; i++, ly += 14)
                g.Text(lines[i], g.FMonoXs, i == errAt ? C.Err : C.Dim, x, ly, w, 14);
        }
        // history: one bar per earlier run, height = how long it took, colour = how it ended
        var hist = j.Runs;
        if (hist.Count > 0 && outp.Length == 0)
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
        if (!running && (j.Cmd.Length > 0 || j.Label.Length > 0)) xr -= Btn(c, xr, by, "Run again", outp.Length == 0, HK.Rerun, j.Key, "↵") + 8;
        if (outp.Length > 0) Btn(c, xr, by, Fixable(j) != null ? "Fix with Claude" : "Fix in new Claude", true, HK.Fix, j.Key, "F");
        FaceEdge();
        c.Act = !running ? HK.Rerun : j.Cwd.Length > 0 ? HK.Repo : HK.None;
        c.Arg = c.Act == HK.Rerun ? j.Key : j.Cwd;
        c.Chip = j.Label.Length > 26 ? j.Label[..25] + "…" : j.Label;
        c.ChipCol = running ? C.Run : col;
    }

    static readonly System.Text.RegularExpressions.Regex ErrorLine = new(
        @"\b(error|failed|failure|exception|panic|traceback|cannot|fatal|undefined)\b|✕|✗|×|\bFAIL\b|^E\s",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// The saved output of a job's most recent failure ("" if its last run passed or nothing was captured).
    static string FailOutput(Job j)
    {
        if (j.Live is { Running: false, Output.Length: > 0 } t && t.Code != 0) return t.Output;
        if (j.Runs.Count > 0 && j.Runs[^1].Code == 0) return "";
        return Model.LastOutput.TryGetValue(j.Key, out var o) ? o : "";
    }

    /// A Claude Code session in the same repo that a fix request can be typed into.
    static string? Fixable(Job j)
    {
        string root = Model.FindRoot(j.Cwd) ?? j.Cwd;
        foreach (var t in _terms)
            if (t.Hooked && t.Name.Contains("claude", StringComparison.OrdinalIgnoreCase) && CanReply(t) && t.Cwd.Length > 0
                && string.Equals(Model.FindRoot(t.Cwd) ?? t.Cwd, root, StringComparison.OrdinalIgnoreCase)) return t.Key;
        return null;
    }

    /// "Fix with Claude": write the failure to a file and ask the repo's Claude session to read it and fix the
    /// cause; with no session there, start one (`claude "<prompt>"` in a new terminal tab).
    static void FixWithClaude(string key)
    {
        var j = _jobs.Find(x => x.Key == key);
        if (j == null) return;
        string outp = FailOutput(j);
        string cmd = j.Cmd.Length > 0 ? j.Cmd : j.Label;
        int code = j.Runs.Count > 0 ? j.Runs[^1].Code : 1;
        string dir = Path.Combine(Path.GetTempPath(), "perch");
        string file = Path.Combine(dir, $"failure-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        try { Directory.CreateDirectory(dir); File.WriteAllText(file, $"command: {cmd}\nfolder: {j.Cwd}\nexit code: {code}\n\n--- end of output ---\n{outp}\n"); }
        catch { return; }
        string prompt = $"`{cmd}` failed (exit {code}). The end of its output is in {file} . Read it, find the cause and fix it.";
        if (Fixable(j) is { } agent) { SendTo(agent, prompt); return; }
        Collapse(false);
        string dirq = Directory.Exists(j.Cwd) ? j.Cwd : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string arg = prompt.Replace("\"", "'");
        // through cmd: an npm-installed `claude` is a .cmd script
        if (ShellExecuteW(0, "open", "wt.exe", $"-w 0 nt -d \"{dirq}\" cmd /k claude \"{arg.Replace(";", "\\;")}\"", null, 1) <= 32)
            ShellExecuteW(0, "open", Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", $"/k claude \"{arg}\"", dirq, 1);
    }

    /// Run a job's command again, in a new terminal tab in its folder, through perch so it shows up here.
    static void Rerun(string key)
    {
        var j = _jobs.Find(x => x.Key == key);
        if (j == null) return;
        RunJob(j.Label, j.Cmd, j.Cwd);
    }

    static void RunJob(string label, string jcmd, string cwd)
    {
        var j = new Job { Label = label, Cmd = jcmd, Cwd = cwd };
        string cmd = j.Cmd.Length > 0 ? j.Cmd : j.Label;
        string perch = Path.Combine(AppContext.BaseDirectory, "perch.exe");
        string inner = $"\"{perch}\" run" + (j.Label != cmd ? $" -n \"{j.Label.Replace("\"", "'")}\"" : "") + " -- " + cmd;
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
        var pi = ProcInfo.Get(s.Pid);
        var by = ProcInfo.LaunchedByCached(s.Pid);
        string folder = pi is { Cwd.Length: > 0 } ? ShortPath(pi.Cwd) : "";
        g.Text(folder.Length > 0 ? Fmt.Dir(pi!.Cwd) : s.Proc, g.FUi, C.Dim, x, y, w, 20);
        y += 34;
        var rows = new List<(string, string)> { ("Process", s.Proc + (s.Pid > 4 ? $"  ·  pid {s.Pid}" : "")) };
        if (folder.Length > 0) rows.Add(("Folder", folder));
        if (pi is { StartUtc: > 0 }) rows.Add(("Up", Uptime(pi.StartUtc)));
        if (by != null) rows.Add(("Started by", "perch run " + (by.Cmd.Length > 0 ? by.Cmd : by.Label)));
        rows.Add(("URL", "http://localhost:" + port));
        foreach (var (k, v) in rows)
        {
            g.Text(k, g.FSmall, C.Faint, x, y, 90, 20);
            g.Text(v, k is "Process" or "Up" ? g.FSmall : g.FMonoSm, C.Text, x + 96, y, w - 96, 20);
            y += 24;
        }
        float bx = CW - 26, byy = CH - 24 - 25;
        bx -= Btn(c, bx, byy, "Open ↗", true, HK.Server, port, "↵") + 8;
        if (s.Pid > 4)
        {   // stopping is destructive: the first press arms it, the second (within 4 s) does it
            bool sure = _confirm == "stop:" + port && Model.Now - _confirmAt < 4000;
            bx -= Btn(c, bx, byy, sure ? "Stop it?" : "Stop", sure, HK.Stop, port, "S") + 8;
            if (by != null) Btn(c, bx, byy, "Restart", false, HK.Restart, port, "R");
        }
        FaceEdge();
        c.Act = HK.Server; c.Arg = port;
        c.Chip = ":" + port; c.ChipCol = C.Ok;
    }

    static string Uptime(long startUtc)
    {
        long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startUtc;
        return ms < 60_000 ? "just started" : Fmt.Dur(ms - ms % 60_000);
    }

    /// Stop a dev server (its process and everything it started).
    static void StopServer(string port, bool restart)
    {
        var s = Probe.Servers.Find(x => x.Port.ToString() == port);
        if (s.Pid <= 4) return;
        var by = restart ? ProcInfo.LaunchedByCached(s.Pid) : null;
        if (by != null)
        {   // started with perch run: end that run's whole tree (perch reports it), then run the command again
            ProcInfo.KillTree(by.Pid, keepRoot: true);
            string label = by.Label, cmd = by.Cmd, cwd = by.Cwd;
            Thread.Sleep(700);   // let the port go
            RunJob(label, cmd, cwd);
        }
        else ProcInfo.KillTree(s.Pid);
        _confirm = "";
        Model.Note = (restart ? "Restarting :" : "Stopped :") + port; Model.NoteAt = Model.Now;
        Probe.PollLight(_cfg);
        Invalidate();
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
        var gh = r.Gh;
        bool ghRows = gh != null && (gh.RunId != 0 || gh.Pr != 0);
        if (st != null)
            for (int i = 0; i < st.Files.Count && i < (ghRows ? 2 : 5); i++)
            {
                var (code, path) = st.Files[i];
                uint cc = code switch { 'A' => C.Ok, 'D' => C.Err, 'U' => C.Err, '?' => C.Faint, 'R' => C.Dim, _ => C.Warn };
                g.Text(code == '?' ? "??" : code.ToString(), g.FMonoSm, cc, x, y, 24, 26);
                g.Text(path, g.FMonoSm, C.Dim, x + 28, y, w - 28, 26);
                g.Line(x, y + 26.5f, x + w, y + 26.5f, 1, C.Hair);
                y += 27;
            }
        float bxr = CW - 26, byy = CH - 24 - 25;
        if (ghRows)
        {   // GitHub: the branch's latest workflow run and its pull request
            float gy = byy - 16 - (gh!.RunId != 0 && gh.Pr != 0 ? 52 : 26);
            if (gh.RunId != 0)
            {
                (string word, uint cc) = gh.RunStatus != "completed" ? ("running", C.Run)
                    : gh.RunConclusion switch { "success" => ("passing", C.Ok), "failure" => ("failing", C.Err), "cancelled" => ("cancelled", C.Faint), var o => (o, C.Warn) };
                g.Circle(x + 4, gy + 13, 3.5f, cc);
                g.Text("CI " + word, g.FSemi, cc, x + 16, gy, 120, 26);
                g.Text(gh.RunName, g.FSmall, C.Faint, x + 110, gy, w - 110, 26);
                if (gh.RunUrl.Length > 0) c.Hits.Add(new Hit { R = new RectF(x, gy, w, 26), K = HK.Link, Arg = gh.RunUrl });
                gy += 26;
            }
            if (gh.Pr != 0)
            {
                (string rv, uint rc) = gh.PrReview switch
                {
                    "APPROVED" => ("approved", C.Ok), "CHANGES_REQUESTED" => ("changes requested", C.Warn),
                    "REVIEW_REQUIRED" => ("review required", C.Dim), _ => (gh.PrState.ToLowerInvariant(), C.Dim),
                };
                g.Text($"#{gh.Pr}", g.FMonoSm, C.Dim, x, gy, 50, 26);
                float rw = g.Measure(rv, g.FSmall);
                g.Text(gh.PrTitle, g.FSmall, C.Text, x + 50, gy, w - 60 - rw, 26);
                g.Text(rv, g.FSmall, rc, x, gy, w, 26, 2);
                gy += 26;
            }
            if (gh.PrUrl.Length > 0) bxr -= Btn(c, bxr, byy, "PR ↗", false, HK.Link, gh.PrUrl) + 8;
            else if (gh.RunUrl.Length > 0) bxr -= Btn(c, bxr, byy, "Checks ↗", false, HK.Link, gh.RunUrl) + 8;
        }
        g.Text(ShortPath(r.Root), g.FLabel, C.Faint, x, byy, bxr - x - 100, 25);
        Btn(c, bxr, byy, "Terminal ↗", false, HK.Repo, r.Root);
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
                if (c.Key.StartsWith('a')) ReplyBar(g, x0, y0, c, hr);   // live, over the cached face
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
        if (_tab == 0 && Usage.Totals is { Week: > 0 } tot)
        {   // token use across every Claude Code session: today and the last 7 days
            string s = $"Today {Usage.Tok(tot.Today)}  ·  7 days {Usage.Tok(tot.Week)} tokens";
            float tw = g.Measure(s, g.FLabel) + 22, tx = chx + 10, ty = chy + 3;   // after the chips, on their row
            g.FillRR(tx, ty, tw, 22, 11, C.ChipOff);
            g.Text(s, g.FLabel, C.Dim, tx, ty, tw, 22, 1);
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
        static int Rank(HK k) => k switch
        {
            HK.Allow or HK.Deny or HK.Reply or HK.Always or HK.Timed or HK.Review or HK.Undo or HK.View or HK.Fix or HK.Stop or HK.Restart or HK.Link => 0,
            HK.Card => 3, HK.Chip => 0, _ when k is HK.Focus or HK.Server or HK.Repo or HK.Rerun => 1, _ => 2,
        };
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
            case HK.Reply: StartCompose(arg); break;
            case HK.Always: case HK.Timed: AllowByRule(arg, k == HK.Timed ? 10 : 0); break;
            case HK.Review: ReviewChanges(arg); break;
            case HK.Undo:
                if (Armed("undo:" + arg)) UndoChanges(arg);
                break;
            case HK.View: _paneView = int.Parse(arg); _tlScroll = 0; Invalidate(); break;
            case HK.Fix: FixWithClaude(arg); break;
            case HK.Stop: if (Armed("stop:" + arg)) StopServer(arg, false); break;
            case HK.Restart: StopServer(arg, true); break;
            case HK.Link: Collapse(false); ShellExecuteW(0, "open", arg, null, null, 1); break;
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
        _compose = null; _composeKey = "";
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
            case 0x53:                                                           // S (twice): stop the focused dev server
                if (_tab == 2 && _focus[2] < _cards.Count && _cards[_focus[2]].Hits.Exists(h => h.K == HK.Stop) && Armed("stop:" + _cards[_focus[2]].Arg))
                    StopServer(_cards[_focus[2]].Arg, false);
                break;
            case 0x52:                                                           // R: run the focused build again / review an agent's changes / restart a server
                if (_tab == 2 && _focus[2] < _cards.Count && _cards[_focus[2]].Hits.Exists(h => h.K == HK.Restart)) StopServer(_cards[_focus[2]].Arg, true);
                else if (_tab == 1 && _focus[1] < _cards.Count && _cards[_focus[1]] is { Act: HK.Rerun } rc) Rerun(rc.Arg);
                else if (FocusedTerm() is { State: "done", Changes.Count: > 0 } rt) ReviewChanges(rt.Key);
                break;
            case 0x4C: case 0x54:                                                // L / T: allow always / for 10 minutes
                if (FocusedTerm() is { AskPort: > 0, State: "wait" } lt && lt.Verdict.Length == 0) AllowByRule(lt.Key, vk == 0x54 ? 10 : 0);
                break;
            case 0x55:                                                           // U (twice): stash the agent's changes
                if (FocusedTerm() is { State: "done", Changes.Count: > 0 } ut && Armed("undo:" + ut.Key)) UndoChanges(ut.Key);
                break;
            case 0x56:                                                           // V: terminal / timeline
                if (_tab == 0) { _paneView ^= 1; _tlScroll = 0; Invalidate(); }
                break;
            case 0x21: case 0x22:                                                // PgUp / PgDn: scroll the timeline
                if (_tab == 0 && _paneView == 1) { _tlScroll = Math.Max(0, _tlScroll + (vk == 0x21 ? 6 : -6)); Invalidate(); }
                break;
            case 0x46:                                                           // F: fix a failed build with Claude
                if (_tab == 1 && _focus[1] < _cards.Count && _cards[_focus[1]].Hits.Exists(h => h.K == HK.Fix)) FixWithClaude(_cards[_focus[1]].Arg);
                break;
        }
    }

    static Term? FocusedTerm() => _tab == 0 && _focus[0] < _terms.Count ? _terms[_focus[0]] : null;

    /// Two-press safety for destructive buttons: true on the second press within 4 s.
    static bool Armed(string what)
    {
        long now = Model.Now;
        if (_confirm == what && now - _confirmAt < 4000) { _confirm = ""; return true; }
        _confirm = what; _confirmAt = now;
        Invalidate();
        return false;
    }

    /// Allow the waiting request, and remember it as a rule (Bash: by command prefix; others: by tool), in this repo.
    static void AllowByRule(string key, int minutes)
    {
        var a = Model.Agents.Find(x => x.Key == key);
        if (a == null || a.AskPort <= 0) return;
        var (_, tn, td) = ToolOf(a.Tool);
        string prefix = tn == "Bash" ? Model.RulePrefix(td) : "";
        if (tn.Length == 0 || (tn == "Bash" && prefix.Length == 0)) return;
        Model.AddRule(a.Cwd, tn, prefix, minutes);
        SendVerdict(key, true);
        Model.Note = (tn == "Bash" ? $"Allowing “{prefix}”" : $"Allowing {tn}") + (minutes > 0 ? $" for {minutes} min" : " from now on") + " in " + Fmt.Dir(a.Cwd);
        Model.NoteAt = Model.Now;
        if (_viaHotkey && !Model.Agents.Exists(x => x.AskPort > 0 && x.State == "wait")) Collapse();
    }

    static void ReviewChanges(string key)
    {
        var a = Model.Agents.Find(x => x.Key == key);
        if (a == null || a.Changes is not { Count: > 0 } ch) return;
        string root = Model.FindRoot(a.Cwd) ?? a.Cwd;
        string files = string.Join(' ', ch.Select(x => "\"" + x.Path + "\""));
        Collapse(false);
        // the diff in a new terminal tab (untracked new files are listed by status, since diff doesn't show them)
        string inner = $"git status --short -- {files} && git diff HEAD -- {files}";
        if (ShellExecuteW(0, "open", "wt.exe", $"-w 0 nt -d \"{root}\" cmd /k {inner.Replace(";", "\\;")}", null, 1) <= 32)
            ShellExecuteW(0, "open", Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/k " + inner, root, 1);
    }

    static void UndoChanges(string key)
    {
        var a = Model.Agents.Find(x => x.Key == key);
        if (a == null) return;
        int n = a.Touched.Count;
        bool ok = Git.Stash(a, $"perch: undo {AgentTitle(a.Name)} ({DateTime.Now:HH:mm})");
        Model.Note = ok ? $"Stashed {n} file{(n == 1 ? "" : "s")} · git stash pop brings them back" : "Couldn't stash the changes";
        Model.NoteAt = Model.Now;
        if (ok) { a.Changes = new(); a.Touched.Clear(); }
        Invalidate();
    }

    /// Answer a waiting PermissionRequest hook (perch listens on the port it reported).
    static void SendVerdict(string key, bool allow)
    {
        var a = Model.Agents.Find(x => x.Key == key);
        if (a == null || a.AskPort <= 0 || _sock == 0) return;
        byte* sa = stackalloc byte[16];
        new Span<byte>(sa, 16).Clear();
        sa[0] = 2; sa[2] = (byte)(a.AskPort >> 8); sa[3] = (byte)(a.AskPort & 0xFF); sa[4] = 127; sa[7] = 1;
        var b = Encoding.ASCII.GetBytes((allow ? "allow:" : "deny:") + a.AskToken);
        fixed (byte* p = b) sendto(_sock, p, b.Length, 0, sa, 16);
        a.Verdict = allow ? "allow" : "deny";
        a.AskPort = 0;
        if (allow) { a.State = "busy"; a.Since = Model.Now; }
        Invalidate();
    }
}
