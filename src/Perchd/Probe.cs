using System.Runtime.InteropServices;
using static Perch.W;

namespace Perch;

/// One cheap sweep: TCP listener table + Toolhelp process snapshot + CPU/RAM counters.
/// No process spawns, no WMI, near-zero steady-state allocation.
static unsafe class Probe
{
    public readonly record struct Server(int Port, string Proc, uint Pid);

    public static readonly List<Server> Servers = new();
    public static int Shells, AgentProcs;
    /// Distinct agent CLIs running at the top level (e.g. "claude", "codex"), for glyphs.
    public static readonly List<string> AgentNames = new();
    static readonly Dictionary<uint, string> _agentCanon = new(), _hostCanon = new();
    /// Every top-level agent CLI process with the editor/terminal it runs in.
    public static readonly List<(string Name, string Host, uint Pid)> Agents = new();
    public static string Active = "";
    public static float Cpu, Mem;

    enum Cat : byte { Other, Shell, Host, Agent, Build, Runtime, System, Nb }

    static readonly Dictionary<string, Cat> Cats = Build();
    static readonly Dictionary<string, Cat>.AlternateLookup<ReadOnlySpan<char>> Lookup = Cats.GetAlternateLookup<ReadOnlySpan<char>>();

    static readonly HashSet<int> DevPorts = new()
    {
        1420, 3000, 3001, 3002, 3003, 3004, 3005, 3030, 3333, 4000, 4173, 4200, 4321, 4322, 5000, 5001, 5050,
        5173, 5174, 5175, 5176, 5177, 5178, 5500, 6006, 7000, 7860, 8000, 8001, 8008, 8080, 8081, 8082, 8088,
        8188, 8501, 8787, 8888, 9000, 11434, 19006,
    };
    static readonly HashSet<int> NoisePorts = new() { 9229, 24678 }; // node inspector, vite HMR

    static readonly Dictionary<int, uint> _listen = new();
    static readonly HashSet<uint> _listenPids = new();
    static readonly Dictionary<uint, (Cat Cat, uint Parent)> _procs = new(512);
    static readonly Dictionary<uint, string> _names = new();
    static nint _buf; static int _bufLen;
    static long _pIdle, _pKern, _pUser;

    static Dictionary<string, Cat> Build()
    {
        var d = new Dictionary<string, Cat>(StringComparer.OrdinalIgnoreCase);
        void Add(Cat c, params string[] names) { foreach (var n in names) d[n] = c; }
        Add(Cat.Shell, "cmd", "powershell", "pwsh", "bash", "zsh", "fish", "nu", "wsl", "sh", "elvish", "xonsh");
        Add(Cat.Host, "WindowsTerminal", "OpenConsole", "explorer", "Code", "Code - Insiders", "Cursor", "Windsurf", "Zed",
            "devenv", "idea64", "pycharm64", "webstorm64", "rider64", "clion64", "goland64", "rustrover64", "phpstorm64",
            "mintty", "alacritty", "wezterm-gui", "Hyper", "Tabby", "Warp", "ghostty", "Fluent Terminal", "kitty");
        Add(Cat.Agent, "claude", "codex", "aider", "opencode", "gemini", "amp", "cursor-agent", "goose", "crush", "qwen", "droid", "kiro-cli");
        Add(Cat.Build, "cargo", "rustc", "MSBuild", "cl", "link", "ninja", "make", "mingw32-make", "cmake", "zig",
            "gcc", "g++", "clang", "clang++", "javac", "go", "swiftc", "nvcc");
        Add(Cat.Runtime, "node", "bun", "deno", "python", "pythonw", "python3", "py", "dotnet", "java", "javaw", "php",
            "php-cgi", "ruby", "uvicorn", "hypercorn", "caddy", "nginx", "httpd", "hugo", "postgres", "mysqld", "mariadbd",
            "redis-server", "memurai", "mongod", "wslrelay", "com.docker.backend", "ollama", "workerd", "mailpit", "minio",
            "surreal", "air", "temporal", "lms");
        Add(Cat.System, "System", "Idle", "svchost", "lsass", "services", "wininit", "spoolsv", "MsMpEng", "Spotify",
            "steam", "Discord", "nvcontainer", "NVIDIA Web Helper", "OneDrive", "PhoneExperienceHost", "msedgewebview2");
        Add(Cat.Nb, "perch");
        return d;
    }

    /// Collapsed-state poll (~0.3 ms): TCP listener table only. Names come from a per-PID
    /// cache filled via OpenProcess, so no system-wide snapshot is taken.
    public static void PollLight(Config cfg)
    {
        _listen.Clear(); _listenPids.Clear();
        ReadTcp(2, cfg); ReadTcp(23, cfg);
        foreach (var pid in _listen.Values)
        {
            _listenPids.Add(pid);
            if (!_names.ContainsKey(pid)) _names[pid] = NameOf(pid);
        }
        foreach (var pid in _names.Keys) if (!_listenPids.Contains(pid)) _names.Remove(pid);
        BuildServers(cfg, null);
        Sample();
    }

    static string NameOf(uint pid)
    {
        if (pid == 4) return "System";
        nint h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, 0, pid);
        if (h == 0) return "?";
        char* buf = stackalloc char[520];
        int len = 520;
        string name = "?";
        if (QueryFullProcessImageNameW(h, 0, buf, &len) != 0)
        {
            var s = new ReadOnlySpan<char>(buf, len);
            s = s[(s.LastIndexOf('\\') + 1)..];
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
            name = s.ToString();
        }
        CloseHandle(h);
        return name;
    }

    public static bool IsAlive(uint pid)
    {
        nint h = OpenProcess(0x100000 /* SYNCHRONIZE */, 0, pid);
        if (h == 0) return false;
        bool alive = WaitForSingleObject(h, 0) == 0x102; // WAIT_TIMEOUT
        CloseHandle(h);
        return alive;
    }

    /// Expanded-state poll: adds a full process snapshot for shell/agent/build counts.
    public static void Poll(Config cfg)
    {
        // ---- 1. listening TCP sockets (v4 + v6)
        _listen.Clear(); _listenPids.Clear();
        ReadTcp(2, cfg); ReadTcp(23, cfg);
        foreach (var pid in _listen.Values) _listenPids.Add(pid);

        // ---- 2. process snapshot
        _procs.Clear(); _names.Clear(); _agentCanon.Clear(); _hostCanon.Clear();
        string active = "";
        nint snap = CreateToolhelp32Snapshot(2, 0);
        if (snap != -1)
        {
            PROCESSENTRY32W e;
            e.dwSize = (uint)sizeof(PROCESSENTRY32W);
            if (Process32FirstW(snap, &e) != 0)
            {
                do
                {
                    var name = new ReadOnlySpan<char>(e.szExeFile, 260);
                    int z = name.IndexOf('\0');
                    if (z >= 0) name = name[..z];
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                    uint pid = e.th32ProcessID;
                    Cat cat = Cat.Other;
                    if (Lookup.TryGetValue(name, out string? canon, out var c))
                    {
                        cat = c;
                        if (c == Cat.Build && active.Length == 0) active = canon;
                        if (c == Cat.Agent) _agentCanon[pid] = canon;
                        else if (c == Cat.Host) _hostCanon[pid] = canon;
                    }
                    _procs[pid] = (cat, e.th32ParentProcessID);
                    if (_listenPids.Contains(pid)) _names[pid] = name.ToString();
                } while (Process32NextW(snap, &e) != 0);
            }
            CloseHandle(snap);
        }
        Active = active;

        int shells = 0, agents = 0;
        foreach (var (_, p) in _procs)
        {
            if (p.Cat == Cat.Shell && _procs.TryGetValue(p.Parent, out var par) && par.Cat == Cat.Host) shells++;
            else if (p.Cat == Cat.Agent && !(_procs.TryGetValue(p.Parent, out var ap) && ap.Cat == Cat.Agent)) agents++;
        }
        AgentNames.Clear();
        foreach (var (pid, canon) in _agentCanon)
            if (!(_procs.TryGetValue(_procs[pid].Parent, out var pp) && pp.Cat == Cat.Agent) && !AgentNames.Contains(canon)) AgentNames.Add(canon);
        AgentNames.Sort(StringComparer.Ordinal);
        Agents.Clear();
        foreach (var (pid, canon) in _agentCanon)
            if (!(_procs.TryGetValue(_procs[pid].Parent, out var pp) && pp.Cat == Cat.Agent)) Agents.Add((canon, HostOf(pid), pid));
        Agents.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        Shells = shells; AgentProcs = agents;

        BuildServers(cfg, _procs);
        Sample();
    }

    /// pid -> parent pid for every process, from a fresh snapshot (touches no shared state: worker-safe).
    public static Dictionary<uint, uint> ParentMap()
    {
        var d = new Dictionary<uint, uint>(512);
        nint snap = CreateToolhelp32Snapshot(2, 0);
        if (snap == -1) return d;
        PROCESSENTRY32W e;
        e.dwSize = (uint)sizeof(PROCESSENTRY32W);
        if (Process32FirstW(snap, &e) != 0)
            do d[e.th32ProcessID] = e.th32ParentProcessID; while (Process32NextW(snap, &e) != 0);
        CloseHandle(snap);
        return d;
    }

    /// Walk up the parent chain to the first editor/terminal process.
    static string HostOf(uint pid)
    {
        for (int i = 0; i < 16 && _procs.TryGetValue(pid, out var p); i++)
        {
            if (p.Cat == Cat.Host && _hostCanon.TryGetValue(pid, out var h) && h is not ("OpenConsole" or "explorer")) return HostName(h);
            if (p.Parent == pid || p.Parent == 0) break;
            pid = p.Parent;
        }
        return "Terminal";
    }

    static string HostName(string exe) => exe switch
    {
        "WindowsTerminal" => "Windows Terminal", "Code" => "VS Code", "Code - Insiders" => "VS Code Insiders",
        "devenv" => "Visual Studio", "idea64" => "IntelliJ", "pycharm64" => "PyCharm", "webstorm64" => "WebStorm",
        "rider64" => "Rider", "clion64" => "CLion", "goland64" => "GoLand", "rustrover64" => "RustRover",
        "phpstorm64" => "PhpStorm", "mintty" => "Git Bash", "wezterm-gui" => "WezTerm", "alacritty" => "Alacritty",
        "ghostty" => "Ghostty", "kitty" => "kitty",
        _ => exe,
    };

    /// CPU history for the telemetry chart (one sample per poll).
    public static readonly float[] Hist = new float[40];
    public static int HistN;

    static void Sample()
    {
        long idle, kern, user;
        if (GetSystemTimes(&idle, &kern, &user) != 0)
        {
            long di = idle - _pIdle, dt = kern - _pKern + user - _pUser;
            if (_pKern != 0 && dt > 0)
            {
                Cpu = Math.Clamp(100f * (dt - di) / dt, 0, 100);
                Array.Copy(Hist, 1, Hist, 0, Hist.Length - 1);
                Hist[^1] = Cpu;
                HistN = Math.Min(HistN + 1, Hist.Length);
            }
            _pIdle = idle; _pKern = kern; _pUser = user;
        }
        MEMORYSTATUSEX m = default; m.dwLength = (uint)sizeof(MEMORYSTATUSEX);
        if (GlobalMemoryStatusEx(&m) != 0) Mem = m.dwMemoryLoad;
    }

    static void BuildServers(Config cfg, Dictionary<uint, (Cat Cat, uint Parent)>? procs)
    {
        Servers.Clear();
        foreach (var (port, pid) in _listen)
        {
            var nm0 = _names.TryGetValue(pid, out var n0) ? n0 : "?";
            Cat cat = procs != null && procs.TryGetValue(pid, out var p) ? p.Cat
                    : Lookup.TryGetValue(nm0, out var c0) ? c0 : Cat.Other;
            bool dev = cat == Cat.Runtime || cfg.Ports.Contains(port) || (DevPorts.Contains(port) && cat != Cat.System);
            if (!dev || (NoisePorts.Contains(port) && !cfg.Ports.Contains(port))) continue;
            Servers.Add(new Server(port, nm0 == "wslrelay" ? "wsl" : nm0 == "com.docker.backend" ? "docker" : nm0, pid));
        }
        Servers.Sort((a, b) => a.Port.CompareTo(b.Port));
    }

    static void ReadTcp(int af, Config cfg)
    {
        if (_buf == 0) { _bufLen = 32 * 1024; _buf = Marshal.AllocHGlobal(_bufLen); }
        int size = _bufLen;
        uint r = GetExtendedTcpTable(_buf, &size, 0, af, 3 /* OWNER_PID_LISTENER */, 0);
        if (r == 122)
        {
            Marshal.FreeHGlobal(_buf);
            _bufLen = size + 8192; _buf = Marshal.AllocHGlobal(_bufLen); size = _bufLen;
            r = GetExtendedTcpTable(_buf, &size, 0, af, 3, 0);
        }
        if (r != 0) return;
        int n = *(int*)_buf, rowSize = af == 2 ? 24 : 56, portOff = af == 2 ? 8 : 20, pidOff = af == 2 ? 20 : 52;
        byte* rows = (byte*)_buf + 4;
        for (int i = 0; i < n; i++)
        {
            byte* row = rows + i * rowSize;
            uint raw = *(uint*)(row + portOff);
            int port = (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
            if ((port < 1024 || port >= 49152) && !cfg.Ports.Contains(port)) continue;
            _listen.TryAdd(port, *(uint*)(row + pidOff));
        }
    }
}
