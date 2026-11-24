using static Perch.W;

namespace Perch;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--clear-taskbar") return TaskbarClear.Inject();
        if (args.Length > 1 && args[0] == "--after" && int.TryParse(args[1], out int prev))
        {   // restarting: wait for the previous instance to finish exiting (it holds the single-instance mutex)
            try { using var p = System.Diagnostics.Process.GetProcessById(prev); p.WaitForExit(8000); } catch { }
            Thread.Sleep(300);
        }
        CreateMutexW(0, 1, @"Local\perch.singleton");
        if (System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == 183) return 0; // already running
        SetProcessDpiAwarenessContext(-4); // per-monitor v2
        MigrateSettings();
        App.Run();
        return 0;
    }

    /// perch used to be called notchbar: carry its settings, run history and approval rules over, once.
    static void MigrateSettings()
    {
        try
        {
            string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "notchbar");
            if (Directory.Exists(old) && !Directory.Exists(Config.Dir)) Directory.Move(old, Config.Dir);
        }
        catch { }
    }
}
