using System.Runtime.InteropServices;
using static Perch.W;

namespace Perch;

/// Where the real taskbar's three groups are on screen (widgets, Start + apps, tray), read through
/// UI Automation so perch can draw its glass boxes exactly behind them. Raw COM vtable calls keep
/// this AOT-friendly; it runs on a thread-pool (MTA) thread and posts a message when the layout changes.
static unsafe class TaskbarProbe
{
    public sealed class Layout
    {
        public RECT Bar, Left, Center, Right;
        public bool HasLeft, HasCenter, HasRight;
    }

    public static volatile Layout? Current;
    static int _busy;
    static nint _uia, _cond;

    // CLSID_CUIAutomation, IID_IUIAutomation
    static readonly Guid Clsid = new("ff48dba4-60ef-4201-aa87-54103eef594e");
    static readonly Guid Iid = new("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");

    [DllImport("ole32.dll")] static extern int CoInitializeEx(nint reserved, uint coinit);
    [DllImport("ole32.dll")] static extern int CoCreateInstance(Guid* clsid, nint outer, uint ctx, Guid* iid, nint* ppv);
    [DllImport("oleaut32.dll")] static extern void SysFreeString(nint s);

    static nint* VT(nint o) => *(nint**)o;
    static void Release(nint o) { if (o != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)VT(o)[2])(o); }

    public static void Refresh(nint notify, uint msg, nint tray)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try
            {
                var l = Read(tray);
                if (!Same(l, Current)) { Current = l; PostMessageW(notify, msg, 0, 0); }
            }
            catch { }
            finally { _busy = 0; }
        }, null);
    }

    static Layout? Read(nint tray)
    {
        if (tray == 0) tray = FindWindowW("Shell_TrayWnd", null);
        if (tray == 0) return null;
        RECT bar; GetWindowRect(tray, &bar);
        CoInitializeEx(0, 0); // MTA
        if (_uia == 0)
        {
            Guid c = Clsid, i = Iid; nint p;
            if (CoCreateInstance(&c, 0, 1, &i, &p) != 0) return null;
            nint cond;
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)VT(p)[21])(p, &cond) != 0) { Release(p); return null; } // CreateTrueCondition
            _uia = p; _cond = cond;
        }
        nint el = 0, arr = 0;
        var l = new Layout { Bar = bar };
        try
        {
            if (((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)VT(_uia)[6])(_uia, tray, &el) != 0 || el == 0) return null; // ElementFromHandle
            if (((delegate* unmanaged[Stdcall]<nint, int, nint, nint*, int>)VT(el)[6])(el, 4, _cond, &arr) != 0 || arr == 0) return null; // FindAll(Descendants)
            int n = 0;
            ((delegate* unmanaged[Stdcall]<nint, int*, int>)VT(arr)[3])(arr, &n);
            for (int k = 0; k < n; k++)
            {
                nint e = 0;
                if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)VT(arr)[4])(arr, k, &e) != 0 || e == 0) continue;
                string id = Bstr(e, 29), cls = Bstr(e, 30); // CurrentAutomationId, CurrentClassName
                RECT r;
                bool ok = ((delegate* unmanaged[Stdcall]<nint, RECT*, int>)VT(e)[43])(e, &r) == 0 && r.R > r.L; // CurrentBoundingRectangle
                Release(e);
                if (!ok) continue;
                if (id == "WidgetsButton") Grow(ref l.Left, ref l.HasLeft, r);
                else if (id is "StartButton" or "SearchButton" or "TaskViewButton" || cls == "Taskbar.TaskListButtonAutomationPeer")
                    Grow(ref l.Center, ref l.HasCenter, r);
                else if (cls.StartsWith("SystemTray.", StringComparison.Ordinal) && cls != "SystemTray.ShowDesktopButton")
                    Grow(ref l.Right, ref l.HasRight, r);
            }
        }
        finally { Release(arr); Release(el); }
        return l;
    }

    static string Bstr(nint e, int slot)
    {
        nint b = 0;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)VT(e)[slot])(e, &b) != 0 || b == 0) return "";
        string s = Marshal.PtrToStringBSTR(b);
        SysFreeString(b);
        return s;
    }

    static void Grow(ref RECT a, ref bool has, RECT r)
    {
        if (!has) { a = r; has = true; return; }
        a.L = Math.Min(a.L, r.L); a.T = Math.Min(a.T, r.T); a.R = Math.Max(a.R, r.R); a.B = Math.Max(a.B, r.B);
    }

    static bool Eq(RECT a, RECT b) => a.L == b.L && a.T == b.T && a.R == b.R && a.B == b.B;

    static bool Same(Layout? a, Layout? b) =>
        a == null ? b == null : b != null && Eq(a.Bar, b.Bar) && a.HasLeft == b.HasLeft && a.HasCenter == b.HasCenter && a.HasRight == b.HasRight
            && Eq(a.Left, b.Left) && Eq(a.Center, b.Center) && Eq(a.Right, b.Right);
}
