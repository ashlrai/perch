using static NotchBar.W;

namespace NotchBar;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--clear-taskbar") return TaskbarClear.Inject();
        CreateMutexW(0, 1, @"Local\notchbar.singleton");
        if (System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == 183) return 0; // already running
        SetProcessDpiAwarenessContext(-4); // per-monitor v2
        App.Run();
        return 0;
    }
}
