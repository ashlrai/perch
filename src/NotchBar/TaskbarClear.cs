using System.Runtime.InteropServices;
using static NotchBar.W;

namespace NotchBar;

/// Makes the Windows 11 taskbar's own background invisible, so the glass boxes drawn behind it show.
///
/// The taskbar is XAML inside explorer.exe; its background is two rectangles. notchbar-tap.dll (next to
/// this exe) is loaded into Explorer through InitializeXamlDiagnosticsEx, the XAML diagnostics API, and
/// sets just those two to invisible; it puts them back when notchbar exits. The loading runs in a
/// short-lived second notchbar process (`notchbar --clear-taskbar`) so the main one never maps the
/// XAML framework.
static unsafe class TaskbarClear
{
    static readonly Guid TapClsid = new("6F1A3B8E-2C4D-4E5F-9A7B-1C2D3E4F5A6B");
    public static string DllPath => Path.Combine(AppContext.BaseDirectory, "notchbar-tap.dll");

    /// Start the helper process (returns at once).
    public static void Launch()
    {
        if (!File.Exists(DllPath) || Environment.ProcessPath is not { } exe) return;
        ShellExecuteW(0, null, exe, "--clear-taskbar", null, 0);
    }

    /// The helper process's job: load the DLL into the Explorer that owns the taskbar. Retries while
    /// a freshly started Explorer is still building its taskbar.
    public static int Inject()
    {
        if (!File.Exists(DllPath)) return 2;
        nint lib = LoadLibraryW("Windows.UI.Xaml.dll");
        nint fn = lib == 0 ? 0 : GetProcAddressA(lib, "InitializeXamlDiagnosticsEx");
        if (fn == 0) return 3;
        var init = (delegate* unmanaged<char*, uint, char*, char*, Guid, char*, int>)fn;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            nint tray = FindWindowW("Shell_TrayWnd", null);
            uint pid = 0;
            if (tray != 0) GetWindowThreadProcessId(tray, &pid);
            if (pid != 0)
            {
                // the endpoint must be this exact name: XAML only answers to its own diagnostics connection
                int hr;
                fixed (char* e = "VisualDiagConnection1", d = DllPath) hr = init(e, pid, null, d, TapClsid, null);
                if (hr >= 0) return 0;
            }
            Thread.Sleep(1000);
        }
        return 1;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint LoadLibraryW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, EntryPoint = "GetProcAddress")] static extern nint GetProcAddressA(nint mod, string name);
}
