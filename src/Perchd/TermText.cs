using System.Runtime.InteropServices;
using static Perch.W;

namespace Perch;

/// The visible text of a terminal window (Windows Terminal, or a classic console), read through UI Automation's
/// TextPattern on a worker thread. The agent cards render it as crisp, live text instead of a shrunken picture.
/// Only the viewport is read (GetVisibleRanges), never the scrollback, so a read is small. Raw COM vtable calls.
static unsafe class TermText
{
    public sealed record Snap(string[] Lines);

    static volatile Dictionary<nint, Snap> _snaps = new();
    static int _busy;
    static nint _uia, _cond;

    [DllImport("ole32.dll")] static extern int CoInitializeEx(nint reserved, uint coinit);
    [DllImport("ole32.dll")] static extern int CoCreateInstance(Guid* clsid, nint outer, uint ctx, Guid* iid, nint* ppv);
    [DllImport("oleaut32.dll")] static extern void SysFreeString(nint s);

    static nint* VT(nint o) => *(nint**)o;
    static void Release(nint o) { if (o != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)VT(o)[2])(o); }

    public static Snap? Get(nint hwnd) => hwnd != 0 && _snaps.TryGetValue(hwnd, out var s) ? s : null;

    /// Re-read these windows in the background; posts `msg` to `notify` when any text changed.
    public static void Refresh(List<nint> hwnds, nint notify, uint msg)
    {
        if (hwnds.Count == 0 || Interlocked.Exchange(ref _busy, 1) == 1) return;
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            bool changed = false;
            try
            {
                var d = new Dictionary<nint, Snap>(_snaps);
                foreach (var h in hwnds.Distinct())
                {
                    string[]? lines = null;
                    try { lines = Read(h); } catch { }
                    if (lines == null) { changed |= d.Remove(h); continue; }
                    if (!d.TryGetValue(h, out var old) || !old.Lines.AsSpan().SequenceEqual(lines)) { d[h] = new Snap(lines); changed = true; }
                }
                foreach (var k in d.Keys.ToList()) if (IsWindow(k) == 0) d.Remove(k);
                _snaps = d;
            }
            catch { }
            finally { _busy = 0; if (changed) PostMessageW(notify, msg, 0, 0); }
        }, null);
    }

    public static void Forget() => _snaps = new();

    static string[]? Read(nint hwnd)
    {
        var cls = stackalloc char[64];
        var name = new ReadOnlySpan<char>(cls, GetClassNameW(hwnd, cls, 64));
        bool wt = name is "CASCADIA_HOSTING_WINDOW_CLASS", con = name is "ConsoleWindowClass";
        if (!wt && !con) return null;
        CoInitializeEx(0, 0);   // MTA
        if (_uia == 0)
        {
            Guid c = new("ff48dba4-60ef-4201-aa87-54103eef594e"), i = new("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");
            nint p, cond;
            if (CoCreateInstance(&c, 0, 1, &i, &p) != 0) return null;
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)VT(p)[21])(p, &cond) != 0) { Release(p); return null; }   // CreateTrueCondition
            _uia = p; _cond = cond;
        }
        nint root = 0, arr = 0;
        try
        {
            if (((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)VT(_uia)[6])(_uia, hwnd, &root) != 0 || root == 0) return null;   // ElementFromHandle
            if (con) return Text(root);
            // Windows Terminal: the active tab's text lives in a "TermControl" element (one per visible pane)
            if (((delegate* unmanaged[Stdcall]<nint, int, nint, nint*, int>)VT(root)[6])(root, 4, _cond, &arr) != 0 || arr == 0) return null;   // FindAll(Descendants)
            int n = 0;
            ((delegate* unmanaged[Stdcall]<nint, int*, int>)VT(arr)[3])(arr, &n);
            string[]? best = null;
            for (int k = 0; k < n; k++)
            {
                nint e = 0;
                if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)VT(arr)[4])(arr, k, &e) != 0 || e == 0) continue;
                try
                {
                    if (Bstr(e, 30) != "TermControl") continue;   // CurrentClassName
                    var t = Text(e);
                    // split panes: prefer the one with keyboard focus, else the one with the most text
                    int focus = 0;
                    ((delegate* unmanaged[Stdcall]<nint, int*, int>)VT(e)[26])(e, &focus);   // CurrentHasKeyboardFocus
                    if (t != null && (best == null || focus != 0 || t.Length > best.Length)) { best = t; if (focus != 0) break; }
                }
                finally { Release(e); }
            }
            return best;
        }
        finally { Release(arr); Release(root); }
    }

    /// The visible lines of an element with a TextPattern, trailing blanks trimmed.
    static string[]? Text(nint el)
    {
        Guid iid = new("32eba289-3583-42c9-9c59-3b6d9a1e9b6a");   // IUIAutomationTextPattern
        nint tp = 0, ranges = 0;
        if (((delegate* unmanaged[Stdcall]<nint, int, Guid*, nint*, int>)VT(el)[14])(el, 10014, &iid, &tp) != 0 || tp == 0) return null;   // GetCurrentPatternAs(Text)
        try
        {
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)VT(tp)[6])(tp, &ranges) != 0 || ranges == 0) return null;   // GetVisibleRanges
            int n = 0;
            ((delegate* unmanaged[Stdcall]<nint, int*, int>)VT(ranges)[3])(ranges, &n);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < n && i < 400; i++)
            {
                nint r = 0, b = 0;
                if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)VT(ranges)[4])(ranges, i, &r) != 0 || r == 0) continue;
                if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)VT(r)[12])(r, 20000, &b) == 0 && b != 0)   // GetText
                {
                    var s = Marshal.PtrToStringBSTR(b);
                    SysFreeString(b);
                    sb.Append(s);
                    if (s.Length > 0 && s[^1] != '\n') sb.Append('\n');
                }
                Release(r);
            }
            var lines = sb.ToString().Replace("\r", "").Split('\n');
            int end = lines.Length;
            while (end > 0 && string.IsNullOrWhiteSpace(lines[end - 1])) end--;
            var o = new string[end];
            for (int i = 0; i < end; i++) o[i] = lines[i].TrimEnd();
            return o;
        }
        finally { Release(ranges); Release(tp); }
    }

    static string Bstr(nint e, int slot)
    {
        nint b = 0;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)VT(e)[slot])(e, &b) != 0 || b == 0) return "";
        string s = Marshal.PtrToStringBSTR(b);
        SysFreeString(b);
        return s;
    }
}
