using static Perch.W;

namespace Perch;

/// Quick reply: a message box on the focused agent card. `/` or a click starts typing, Enter sends, Esc cancels.
/// Sending brings the agent's window to the front and types the message into it, but only when the window's
/// title shows Claude Code is running in it (its activity glyph: ✳ idle, ◐◑◒◓ / braille / ✢✶✻✽ busy). Anything
/// else gets the text on the clipboard instead, so a message is never typed into a plain shell as a command.
static unsafe partial class App
{
    static string? _compose;          // non-null while typing
    static string _composeKey = "";   // the agent it's for
    static long _caretAt;

    static bool Composing => _compose != null;

    /// Whether a quick reply can go to this agent now (not while a permission prompt is up: keys would answer it).
    static bool CanReply(in Term t) => t.Pid != 0 && !(t.State == "wait" && t.Msg.StartsWith("Claude needs your permission", StringComparison.Ordinal));

    static void StartCompose(string key)
    {
        var t = _terms.Find(x => x.Key == key);
        if (t.Key == null || !CanReply(t)) return;
        _compose = ""; _composeKey = key; _caretAt = Hr;
        Invalidate();
    }

    static void EndCompose() { if (_compose == null) return; _compose = null; _composeKey = ""; Invalidate(); }

    /// WM_CHAR while the cards are open. Returns true when it was used.
    static bool OnChar(char ch)
    {
        if (!Expanded) return false;
        if (_compose == null)
        {   // "/" on an agent card starts a message
            if (ch == '/' && _tab == 0 && _focus[0] < _terms.Count) { StartCompose(_terms[_focus[0]].Key); return true; }
            return false;
        }
        switch (ch)
        {
            case '\r': SendReply(); break;
            case '\x1b': EndCompose(); break;
            case '\b': if (_compose.Length > 0) _compose = _compose[..^(char.IsLowSurrogate(_compose[^1]) && _compose.Length > 1 ? 2 : 1)]; break;
            case '\x7f':   // Ctrl+Backspace: the last word
                _compose = _compose.TrimEnd();
                int sp = _compose.LastIndexOf(' ');
                _compose = sp < 0 ? "" : _compose[..(sp + 1)];
                break;
            case '\x16': _compose += (ReadClipboard() ?? "").ReplaceLineEndings(" "); break;   // Ctrl+V
            case '\x15': _compose = ""; break;                                                // Ctrl+U: clear
            default: if (ch >= ' ') _compose += ch; break;
        }
        if (_compose is { Length: > 4000 }) _compose = _compose[..4000];
        _caretAt = Hr;
        Invalidate();
        return true;
    }

    // ---- the reply bar, drawn live over the focused agent card (the card face itself is a cached image)

    const float ReplyH = 30;

    static RectF ReplyRect(float x0, float y0) => new(x0 + 10, y0 + CH - 22 - 10 - ReplyH, CW - SideW - 20, ReplyH);

    static void ReplyBar(Gfx g, float x0, float y0, Card c, long hr)
    {
        var t = _terms.Find(x => x.Key == c.Arg);
        if (t.Key == null) return;
        var r = ReplyRect(x0, y0);
        bool can = CanReply(t), on = _compose != null && _composeKey == t.Key, hot = _dhk == HK.Reply;
        g.FillRR(r.X, r.Y, r.W, r.H, 8, C.A(C.Face, 0.94f));
        g.StrokeRR(r.X + 0.5f, r.Y + 0.5f, r.W - 1, r.H - 1, 7.5f, on ? C.A(C.Accent, 0.8f) : hot ? C.Track : C.Line, on ? 1.4f : 1);
        float tx = r.X + 12, tw = r.W - 24;
        if (!can)
        {
            g.Text(t.Pid == 0 ? "No window to send to" : "Answer the permission request first", g.FSmall, C.Faint, tx, r.Y, tw, r.H);
            return;
        }
        if (on)
        {
            string s = _compose!;
            float w = g.Measure(s, g.FUi), avail = tw - 60;
            // keep the end of a long message (where the caret is) in view
            string shown = s;
            if (w > avail)
            {
                int cut = 0;
                while (cut < s.Length - 1 && g.Measure(s[cut..], g.FUi) > avail - 12) cut += Math.Max(1, (s.Length - cut) / 12);
                shown = "…" + s[cut..];
                w = g.Measure(shown, g.FUi);
            }
            if (s.Length == 0) g.Text("Message " + AgentTitle(t.Name) + "…", g.FUi, C.Faint, tx, r.Y, tw, r.H);
            else g.Text(shown, g.FUi, C.Text, tx, r.Y, avail + 4, r.H);
            bool blink = _cfg.LowMotion || (hr - _caretAt) % 1060 < 600;
            float cx = tx + (s.Length == 0 ? 0 : w) + 1;
            if (blink) g.Line(cx, r.Y + 8, cx, r.Y + r.H - 8, 1.4f, C.Accent);
            g.Text("↵ send", g.FMonoXs, C.Faint, r.X, r.Y, r.W - 10, r.H, 2);
        }
        else
        {
            g.Text("Message " + AgentTitle(t.Name) + "…", g.FUi, hot ? C.Dim : C.Faint, tx, r.Y, tw, r.H);
            float kx = r.X + r.W - 26;
            g.StrokeRR(kx + 0.5f, r.Y + 7.5f, 15, 15, 3.5f, C.A(C.Faint, 0.6f), 1);
            g.Text("/", g.FMonoXs, C.Faint, kx, r.Y + 7, 16, 16, 1);
            _dhits.Add(new Hit { R = r, K = HK.Reply, Arg = t.Key });
        }
    }

    // ---- sending

    static void SendReply()
    {
        string text = (_compose ?? "").Trim();
        string key = _composeKey;
        EndCompose();
        if (text.Length == 0) return;
        SendTo(key, text);
    }

    /// Type `text` into an agent's window (with the Claude Code check), or put it on the clipboard.
    static void SendTo(string key, string text)
    {
        var t = _terms.Find(x => x.Key == key);
        nint h = _prev.TryGetValue(key, out var pv) ? pv.Hwnd : 0;
        string who = t.Key != null ? AgentTitle(t.Name) : "the agent";
        Collapse(false);
        if (h == 0 || IsWindow(h) == 0) { Copied(text, who); return; }
        if (IsIconic(h) != 0) ShowWindow(h, 9);
        SetForegroundWindow(h);
        for (int i = 0; i < 30 && GetForegroundWindow() != h; i++) Thread.Sleep(10);
        if (GetForegroundWindow() != h || !LooksLikeClaude(h)) { Copied(text, who); return; }
        TypeText(text);
        Model.Note = "Sent to " + who; Model.NoteAt = Model.Now;
        Invalidate();
    }

    static void Copied(string text, string who)
    {
        WriteClipboard(text);
        Model.Note = $"Copied: paste it into {who} with Ctrl+V"; Model.NoteAt = Model.Now;
        Invalidate();
    }

    /// Claude Code puts its activity glyph at the start of the terminal title.
    static bool LooksLikeClaude(nint h)
    {
        var buf = stackalloc char[8];
        int n = GetWindowTextW(h, buf, 8);
        if (n == 0) return false;
        char c = buf[0];
        return c is '✳' or '✢' or '✶' or '✻' or '✽' || c is >= '◐' and <= '◓' || c is >= '⠀' and <= '⣿';
    }

    /// Type text as Unicode key events, then Enter.
    static void TypeText(string s)
    {
        // let go of any modifier still held from the keystroke that sent it
        var inputs = new List<INPUT>();
        foreach (int vk in new[] { 0x10, 0x11, 0x12, 0x5B })
            if (GetKeyState(vk) < 0) inputs.Add(new INPUT { Type = 1, Vk = (ushort)vk, Flags = 2 });
        foreach (char ch in s)
        {
            inputs.Add(new INPUT { Type = 1, Scan = ch, Flags = 4 });       // KEYEVENTF_UNICODE
            inputs.Add(new INPUT { Type = 1, Scan = ch, Flags = 4 | 2 });   // ...KEYUP
        }
        inputs.Add(new INPUT { Type = 1, Vk = 0x0D });
        inputs.Add(new INPUT { Type = 1, Vk = 0x0D, Flags = 2 });
        var arr = inputs.ToArray();
        fixed (INPUT* p = arr) SendInput((uint)arr.Length, p, sizeof(INPUT));
    }

    // ---- clipboard (CF_UNICODETEXT)

    static string? ReadClipboard()
    {
        if (OpenClipboard(_hwnd) == 0) return null;
        try
        {
            nint h = GetClipboardData(13);
            if (h == 0) return null;
            nint p = GlobalLock(h);
            if (p == 0) return null;
            try { return new string((char*)p); } finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    static void WriteClipboard(string s)
    {
        if (OpenClipboard(_hwnd) == 0) return;
        try
        {
            EmptyClipboard();
            nint m = GlobalAlloc(0x0002, (nuint)((s.Length + 1) * 2));   // GMEM_MOVEABLE
            if (m == 0) return;
            char* p = (char*)GlobalLock(m);
            s.AsSpan().CopyTo(new Span<char>(p, s.Length)); p[s.Length] = '\0';
            GlobalUnlock(m);
            SetClipboardData(13, m);
        }
        finally { CloseClipboard(); }
    }
}
