using System.Runtime.InteropServices;
using static Perch.W;

namespace Perch;

/// Details about a dev server's process for the Ports cards: the folder it runs in (its working directory, read
/// from its PEB) and when it started. Read on a worker thread. Also stops process trees.
static unsafe class ProcInfo
{
    public sealed record Info(string Cwd, long StartUtc);

    static volatile Dictionary<uint, Info> _info = new();
    static int _busy;

    [StructLayout(LayoutKind.Sequential)] struct PBI { public nint R1, Peb, R2a, R2b, Pid, R3; }
    [StructLayout(LayoutKind.Sequential)] struct UStr { public ushort Len, Max; public int Pad; public nint Buf; }

    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(nint h, int cls, PBI* pbi, int len, int* ret);
    [DllImport("kernel32.dll")] static extern int ReadProcessMemory(nint h, nint addr, void* buf, nint size, nint* read);
    [DllImport("kernel32.dll")] static extern int IsWow64Process(nint h, int* wow);
    [DllImport("kernel32.dll")] static extern int GetProcessTimes(nint h, long* create, long* exit, long* kern, long* user);
    [DllImport("kernel32.dll")] static extern int TerminateProcess(nint h, uint code);

    public static Info? Get(uint pid) => _info.TryGetValue(pid, out var i) ? i : null;

    public static void Refresh(List<uint> pids, nint hwnd, uint msg)
    {
        if (pids.Count == 0 || Interlocked.Exchange(ref _busy, 1) == 1) return;
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try
            {
                var d = new Dictionary<uint, Info>();
                foreach (var pid in pids.Distinct()) if (Read(pid) is { } i) d[pid] = i;
                bool changed = d.Count != _info.Count || d.Any(kv => !_info.TryGetValue(kv.Key, out var o) || o != kv.Value);
                _info = d;
                if (changed) PostMessageW(hwnd, msg, 0, 0);
            }
            catch { }
            finally { _busy = 0; }
        }, null);
    }

    static Info? Read(uint pid)
    {
        nint h = OpenProcess(0x0400 | 0x0010, 0, pid);   // QUERY_INFORMATION | VM_READ
        if (h == 0)
        {   // at least the start time
            h = OpenProcess(0x1000, 0, pid);
            if (h == 0) return null;
            try { return new Info("", Started(h)); } finally { CloseHandle(h); }
        }
        try
        {
            long start = Started(h);
            int wow = 0;
            if (IsWow64Process(h, &wow) == 0 || wow != 0) return new Info("", start);   // 32-bit process: different layout
            PBI pbi; int ret;
            if (NtQueryInformationProcess(h, 0, &pbi, sizeof(PBI), &ret) != 0 || pbi.Peb == 0) return new Info("", start);
            nint pp; UStr us;
            if (ReadProcessMemory(h, pbi.Peb + 0x20, &pp, 8, null) == 0 || pp == 0) return new Info("", start);   // PEB.ProcessParameters
            if (ReadProcessMemory(h, pp + 0x38, &us, sizeof(UStr), null) == 0 || us.Len == 0 || us.Len > 2048) return new Info("", start);   // CurrentDirectory.DosPath
            var buf = stackalloc char[us.Len / 2];
            if (ReadProcessMemory(h, us.Buf, buf, us.Len, null) == 0) return new Info("", start);
            return new Info(new string(buf, 0, us.Len / 2).TrimEnd('\\'), start);
        }
        finally { CloseHandle(h); }
    }

    static long Started(nint h)
    {
        long c, e, k, u;
        return GetProcessTimes(h, &c, &e, &k, &u) != 0 ? DateTimeOffset.FromFileTime(c).ToUnixTimeMilliseconds() : 0;
    }

    /// Ends `pid` and everything it started (children first). `keepRoot`: spare `pid` itself.
    public static void KillTree(uint pid, bool keepRoot = false)
    {
        var parents = Probe.ParentMap();
        var kids = new List<uint>();
        // a recorded parent id can be stale (that parent exited and its id was reused): only count children
        // that started after the parent did
        long StartOf(uint id) { nint h = OpenProcess(0x1000, 0, id); if (h == 0) return 0; try { return Started(h); } finally { CloseHandle(h); } }
        void Walk(uint p, int depth)
        {
            if (depth > 12) return;
            long ps = StartOf(p);
            foreach (var (c, pp) in parents)
                if (pp == p && c != p && c != 0 && (ps == 0 || StartOf(c) >= ps)) { Walk(c, depth + 1); kids.Add(c); }
        }
        Walk(pid, 0);
        if (!keepRoot) kids.Add(pid);
        foreach (var k in kids)
        {
            nint h = OpenProcess(0x0001, 0, k);   // TERMINATE
            if (h == 0) continue;
            TerminateProcess(h, 1);
            CloseHandle(h);
        }
    }

    static readonly Dictionary<uint, (TaskInfo? T, long At)> _launched = new();

    /// LaunchedBy, remembered for 5 s (it takes a process snapshot).
    public static TaskInfo? LaunchedByCached(uint pid)
    {
        if (_launched.TryGetValue(pid, out var v) && Model.Now - v.At < 5000 && (v.T == null || v.T.Running)) return v.T;
        var t = LaunchedBy(pid);
        if (_launched.Count > 64) _launched.Clear();
        _launched[pid] = (t, Model.Now);
        return t;
    }

    /// The running `perch run` task that started this process (an ancestor), if any.
    public static TaskInfo? LaunchedBy(uint pid)
    {
        var parents = Probe.ParentMap();
        uint p = pid;
        for (int i = 0; i < 12 && p != 0; i++)
        {
            foreach (var t in Model.Tasks) if (t.Running && t.Pid == p) return t;
            if (!parents.TryGetValue(p, out var pp) || pp == p) break;
            p = pp;
        }
        return null;
    }
}
