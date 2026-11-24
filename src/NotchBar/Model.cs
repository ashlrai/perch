using System.Diagnostics;

namespace NotchBar;

sealed class TaskInfo
{
    public string Key = "", Label = "", Cwd = "", Cmd = "";
    public uint Pid;
    public long Start, End;
    public int Code;
    public bool Running => End == 0;
    public const int Lost = int.MinValue;
}

sealed class AgentInfo
{
    public string Key = "", Name = "", Cwd = "", State = "", Msg = "";
    public string Prompt = "", Tool = "", Host = "";   // last prompt; "event\ttool\tdetail"; editor/terminal
    public long PromptAt;
    public uint Pid;                                      // the agent process (its window is found from it)
    public int AskPort;                                   // > 0: a PermissionRequest hook is waiting for Allow/Deny here
    public string Verdict = "";                           // "allow" / "deny" when answered from the notch
    public readonly List<LogEntry> Log = new();          // tool calls since the last prompt
    public long Since, Updated;
    public string Transcript = "";                        // Claude Code's session transcript (.jsonl), for the context meter
}

/// One finished `nb run`, kept across restarts for the Builds page (wall-clock times).
sealed class Run
{
    public string Label = "", Cmd = "", Cwd = "";
    public long EndUtc, Ms; public int Code;
    public string Job => Cwd.ToLowerInvariant() + "\n" + (Cmd.Length > 0 ? Cmd : Label);
}

/// Something worth a chime and a flash on the notch.
readonly record struct Alert(string Kind, uint Pid, string Key);   // Kind: done, wait, ok, fail

sealed class LogEntry
{
    public long T; public string Tool = "", Detail = ""; public bool Done;
}

sealed record GitStatus(string Branch, int Changed, int Untracked, int Ahead, int Behind, List<(char Code, string Path)> Files);

sealed class Repo
{
    public string Root = "", Name = "";
    public long Used;
    public volatile GitStatus? St;
    public int Busy;
    public long Last;
}

/// All state lives on the UI thread, except Repo.St which a worker swaps atomically.
static class Model
{
    public static readonly List<TaskInfo> Tasks = new();
    public static readonly List<AgentInfo> Agents = new();
    public static readonly List<Repo> Repos = new();
    public static string Note = "";
    public static long NoteAt = -100_000;
    public static readonly List<Alert> Alerts = new();

    static readonly Dictionary<string, string?> _roots = new(StringComparer.OrdinalIgnoreCase);

    public static long Now => Environment.TickCount64;

    public static void Apply(string msg)
    {
        var f = msg.Split(Proto.Sep);
        if (f.Length < 8 || f[0] != "nb1") return;
        long now = Now;
        switch (f[1])
        {
            case "T": OnTask(f, now); break;
            case "A": OnAgent(f, now); break;
            case "N": Note = f[4]; NoteAt = now; break;
        }
    }

    static void OnTask(string[] f, long now)
    {
        var t = Tasks.Find(x => x.Key == f[2]);
        if (f[3] == "start")
        {
            if (t == null) { t = new TaskInfo { Key = f[2] }; Tasks.Add(t); }
            t.Label = f[4]; t.Cwd = f[5]; uint.TryParse(f[6], out t.Pid);
            t.Cmd = f.Length > 8 ? f[8] : "";
            t.Start = now; t.End = 0;
            Touch(f[5], now);
            while (Tasks.Count > 8)
            {
                TaskInfo? old = null;
                foreach (var x in Tasks) if (!x.Running && (old == null || x.End < old.End)) old = x;
                Tasks.Remove(old ?? Tasks[0]);
            }
        }
        else if (t != null && t.Running)
        {
            t.End = Math.Max(now, t.Start + 1);
            int.TryParse(f[7], out t.Code);
            if (t.Code == 0) LearnEta(t.Label, t.End - t.Start);
            AddRun(new Run { Label = t.Label, Cmd = t.Cmd, Cwd = t.Cwd, EndUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Ms = t.End - t.Start, Code = t.Code });
            if (t.End - t.Start > 15_000 || t.Code != 0) Alerts.Add(new Alert(t.Code == 0 ? "ok" : "fail", t.Pid, t.Key));
            var r = RepoFor(t.Cwd);
            if (r != null) r.Last = 0; // force git refresh after a task finishes
        }
    }

    static void OnAgent(string[] f, long now)
    {
        string key = f[2], st = f[3];
        int i = Agents.FindIndex(a => a.Key == key);
        if (st == "off") { if (i >= 0) Agents.RemoveAt(i); return; }
        var a = i >= 0 ? Agents[i] : null;
        if (a == null) { a = new AgentInfo { Key = key }; Agents.Add(a); }
        // Claude's 60s idle nag after a finished turn shouldn't flip "done" back to amber.
        if (st == "wait" && a.State == "done" && f[7].Contains("waiting for your input", StringComparison.OrdinalIgnoreCase))
        { a.Updated = now; return; }
        if (a.State != st)
        {
            a.Since = now;
            if (st is "done" or "wait") Alerts.Add(new Alert(st, f.Length > 11 && uint.TryParse(f[11], out var ap) ? ap : a.Pid, key));
        }
        a.State = st;
        if (f.Length > 13 && f[13].Length > 0) a.Transcript = f[13]; a.Name = f[4].Length > 0 ? f[4] : "Agent"; a.Cwd = f[5]; a.Msg = f[7]; a.Updated = now;
        if (f.Length > 8 && f[8].Length > 0) { a.Prompt = f[8]; a.Tool = ""; a.PromptAt = now; a.Log.Clear(); a.Verdict = ""; }   // new prompt: start a fresh log
        if (f.Length > 9 && f[9].Length > 0) { a.Tool = f[9]; AddLog(a, f[9], now); }
        if (st is "done" or "off") foreach (var e in a.Log) e.Done = true;
        if (f.Length > 10 && f[10].Length > 0) a.Host = f[10];
        if (f.Length > 11 && uint.TryParse(f[11], out var pid) && pid > 0) a.Pid = pid;
        // a waiting PermissionRequest hook keeps its Allow/Deny through Claude's own "needs permission"
        // notification; any other event (or nb giving up, port -1) ends the question
        int port = f.Length > 12 && int.TryParse(f[12], out var pp) ? pp : 0;
        if (port != 0 || st != "wait") a.AskPort = Math.Max(0, port);
        Touch(f[5], now);
        while (Agents.Count > 6)
        {
            AgentInfo old = Agents[0];
            foreach (var x in Agents) if (x.Updated < old.Updated) old = x;
            Agents.Remove(old);
        }
    }

    /// PreToolUse opens a log line, the matching PostToolUse ticks it off.
    static void AddLog(AgentInfo a, string tool, long now)
    {
        var p = tool.Split('\t');
        if (p.Length < 2) return;
        string ev = p[0], name = p[1], detail = p.Length > 2 ? p[2] : "";
        if (a.PromptAt == 0) a.PromptAt = now;
        if (ev == "PostToolUse")
            for (int i = a.Log.Count - 1; i >= 0; i--)
                if (!a.Log[i].Done && a.Log[i].Tool == name) { a.Log[i].Done = true; return; }
        a.Log.Add(new LogEntry { T = now, Tool = name, Detail = detail, Done = ev != "PreToolUse" });
        if (a.Log.Count > 12) a.Log.RemoveAt(0);
    }

    /// Drop stale entries; mark tasks whose nb.exe vanished (killed terminal) as lost.
    public static void Expire(long now)
    {
        foreach (var t in Tasks)
            if (t.Running && now - t.Start > 3000 && !Probe.IsAlive(t.Pid)) { t.End = now; t.Code = TaskInfo.Lost; }
        Agents.RemoveAll(a => a.State == "done" ? now - a.Updated > 30 * 60_000 : now - a.Updated > 3 * 3600_000);
    }

    // ---------------------------------------------------------------- ETA
    // Remembers how long each command took last time (smoothed), persisted across restarts.

    static Dictionary<string, long>? _eta;
    static string EtaPath => Path.Combine(Config.Dir, "eta.tsv");

    public static long EtaFor(string label)
    {
        _eta ??= LoadEta();
        return _eta.TryGetValue(label, out var v) ? v : 0;
    }

    static void LearnEta(string label, long ms)
    {
        _eta ??= LoadEta();
        _eta[label] = _eta.TryGetValue(label, out var old) ? (old + ms) / 2 : ms;
        try
        {
            Directory.CreateDirectory(Config.Dir);
            var sb = new System.Text.StringBuilder();
            foreach (var (k, v) in _eta.Take(300)) sb.Append(v).Append('\t').Append(k.Replace('\n', ' ')).Append('\n');
            File.WriteAllText(EtaPath, sb.ToString());
        }
        catch { }
    }

    static Dictionary<string, long> LoadEta()
    {
        var d = new Dictionary<string, long>();
        try
        {
            if (File.Exists(EtaPath))
                foreach (var line in File.ReadAllLines(EtaPath))
                {
                    int tab = line.IndexOf('\t');
                    if (tab > 0 && long.TryParse(line.AsSpan(0, tab), out var ms)) d[line[(tab + 1)..]] = ms;
                }
        }
        catch { }
        return d;
    }

    // ---------------------------------------------------------------- run history
    // The last 60 finished `nb run`s, persisted, newest last. Drives the Builds page and "Run again".

    static List<Run>? _runs;
    static string RunsPath => Path.Combine(Config.Dir, "history.tsv");

    public static List<Run> Runs => _runs ??= LoadRuns();

    static void AddRun(Run r)
    {
        Runs.Add(r);
        if (_runs!.Count > 60) _runs.RemoveRange(0, _runs.Count - 60);
        try
        {
            Directory.CreateDirectory(Config.Dir);
            var sb = new System.Text.StringBuilder();
            static string F(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
            foreach (var x in _runs) sb.Append(x.EndUtc).Append('\t').Append(x.Ms).Append('\t').Append(x.Code).Append('\t')
                .Append(F(x.Cwd)).Append('\t').Append(F(x.Label)).Append('\t').Append(F(x.Cmd)).Append('\n');
            File.WriteAllText(RunsPath, sb.ToString());
        }
        catch { }
    }

    static List<Run> LoadRuns()
    {
        var l = new List<Run>();
        try
        {
            if (File.Exists(RunsPath))
                foreach (var line in File.ReadAllLines(RunsPath))
                {
                    var p = line.Split('\t');
                    if (p.Length >= 6 && long.TryParse(p[0], out var end) && long.TryParse(p[1], out var ms) && int.TryParse(p[2], out var code))
                        l.Add(new Run { EndUtc = end, Ms = ms, Code = code, Cwd = p[3], Label = p[4], Cmd = p[5] });
                }
        }
        catch { }
        return l;
    }

    // ---------------------------------------------------------------- repos

    public static void Pin(IEnumerable<string> roots)
    {
        foreach (var p in roots)
        {
            var root = FindRoot(p);
            if (root != null && RepoFor(root) == null) Repos.Add(new Repo { Root = root, Name = Path.GetFileName(root), Used = 1 });
        }
    }

    static void Touch(string cwd, long now)
    {
        var root = FindRoot(cwd);
        if (root == null) return;
        var r = Repos.Find(x => string.Equals(x.Root, root, StringComparison.OrdinalIgnoreCase));
        if (r == null) { r = new Repo { Root = root, Name = Path.GetFileName(root) }; Repos.Add(r); }
        r.Used = now;
        while (Repos.Count > 8)
        {
            Repo old = Repos[0];
            foreach (var x in Repos) if (x.Used < old.Used) old = x;
            Repos.Remove(old);
        }
    }

    static Repo? RepoFor(string cwd)
    {
        var root = FindRoot(cwd);
        return root == null ? null : Repos.Find(x => string.Equals(x.Root, root, StringComparison.OrdinalIgnoreCase));
    }

    static string? FindRoot(string cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return null;
        if (_roots.TryGetValue(cwd, out var r)) return r;
        try
        {
            for (string? d = cwd; d != null; d = Path.GetDirectoryName(d))
                if (Path.Exists(Path.Combine(d, ".git"))) { r = d; break; }
        }
        catch { }
        _roots[cwd] = r;
        return r;
    }
}

static class Git
{
    /// Runs `git status` off the UI thread; result is swapped in and the window is poked.
    public static void Refresh(Repo r, nint hwnd, uint msg)
    {
        if (Interlocked.CompareExchange(ref r.Busy, 1, 0) != 0) return;
        r.Last = Model.Now;
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try { r.St = Status(r.Root) ?? r.St; }
            catch { }
            finally { r.Busy = 0; W.PostMessageW(hwnd, msg, 0, 0); }
        }, null);
    }

    static GitStatus? Status(string root)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "-C", root, "status", "--porcelain=v2", "--branch" }) psi.ArgumentList.Add(a);
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0"; // never fight the user's git for index.lock
        using var p = Process.Start(psi);
        if (p == null) return null;
        string outp = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(4000) || p.ExitCode != 0) return null;

        string branch = "?"; int changed = 0, untracked = 0, ahead = 0, behind = 0;
        var files = new List<(char, string)>();
        foreach (var line in outp.Split('\n'))
        {
            if (line.StartsWith("# branch.head ")) branch = line[14..].Trim();
            else if (line.StartsWith("# branch.oid ") && branch == "(detached)") branch = line[13..Math.Min(line.Length, 20)];
            else if (line.StartsWith("# branch.ab "))
            {
                var ab = line[12..].Trim().Split(' ');
                if (ab.Length == 2) { int.TryParse(ab[0].TrimStart('+'), out ahead); int.TryParse(ab[1].TrimStart('-'), out behind); }
            }
            else if (line.StartsWith("1 ") || line.StartsWith("2 ") || line.StartsWith("u "))
            {
                changed++;
                // "1 XY sub mH mI mW hH hI path": X = staged, Y = worktree; renames ("2 ...") end "path	orig"
                var parts = line.TrimEnd('\r').Split(' ', line[0] == '2' ? 10 : line[0] == 'u' ? 11 : 9);
                if (files.Count < 6 && parts.Length >= 9)
                {
                    string xy = parts[1], path = parts[^1].Split('\t')[0];
                    char code = line[0] == 'u' ? 'U' : xy[0] == 'A' ? 'A' : xy[0] == 'D' || xy[1] == 'D' ? 'D' : line[0] == '2' ? 'R' : 'M';
                    files.Add((code, path));
                }
            }
            else if (line.StartsWith("? ")) { untracked++; if (files.Count < 6) files.Add(('?', line[2..].TrimEnd('\r'))); }
        }
        return new GitStatus(branch, changed, untracked, ahead, behind, files);
    }
}

sealed class Config
{
    public readonly List<string> Repos = new();
    public readonly HashSet<int> Ports = new();
    public bool HideFullscreen = true, LowMotion, Terrain, Glass, ClearTaskbar = true, Sound = true, FollowMonitor = true, TuckMaximized = true;
    public string Hotkey = "win+alt+n", Theme = "auto", Accent = "auto";
    public long ContextWindow;   // 0: from the model

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "notchbar");
    public static string FilePath => Path.Combine(Dir, "config.ini");

    public static Config Load()
    {
        var c = new Config();
        try
        {
            if (!File.Exists(FilePath)) return c;
            foreach (var raw in File.ReadAllLines(FilePath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line[..eq].Trim().ToLowerInvariant(), v = line[(eq + 1)..].Trim();
                switch (k)
                {
                    case "repo": c.Repos.Add(Environment.ExpandEnvironmentVariables(v)); break;
                    case "port": if (int.TryParse(v, out var p)) c.Ports.Add(p); break;
                    case "hide_fullscreen": c.HideFullscreen = v is "1" or "true" or "yes" or "on"; break;
                    case "motion": c.LowMotion = v == "low"; break;
                    case "taskbar": c.ClearTaskbar = v != "system"; break;
                    case "backdrop": c.Terrain = v == "terrain"; c.Glass = v is "glass" or "blur"; break;
                    case "sound": c.Sound = v is "1" or "true" or "yes" or "on"; break;
                    case "hotkey": c.Hotkey = v.ToLowerInvariant(); break;
                    case "theme": c.Theme = v.ToLowerInvariant(); break;
                    case "accent": c.Accent = v.ToLowerInvariant(); break;
                    case "monitor": c.FollowMonitor = v != "primary"; break;
                    case "maximized": c.TuckMaximized = v != "stay"; break;
                    case "context_window": long.TryParse(v.Replace("_", "").Replace(",", ""), out c.ContextWindow); break;
                }
            }
        }
        catch { }
        return c;
    }

    public static void EnsureFile()
    {
        if (File.Exists(FilePath)) return;
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, """
            # notchbar config - right-click the notch > "Reload config" after editing.

            # Repos to always list under GIT (one per line). Repos you `nb run` in appear automatically.
            # repo = C:\code\my-app

            # Extra ports to treat as dev servers
            # port = 4040

            # Sink out of sight while a fullscreen app, video, game or presentation is in front
            hide_fullscreen = true

            # maximized = tuck | stay   (tuck: with a maximized window in front, the notch sinks into the taskbar,
            #   leaving a slim status line; hover it to bring the notch back)
            maximized = tuck

            # motion = full | low    (low: static icons, 1 fps refresh while busy)
            motion = full

            # taskbar = clear | system
            #   clear: hide the Windows taskbar's own background so notchbar's glass boxes show through
            #   (restored when notchbar quits; needs a restart of notchbar to change)
            taskbar = clear

            # backdrop = plain | glass
            #   plain: the desktop is just dimmed behind the cards    glass: dimmed and blurred
            backdrop = plain

            # Keyboard shortcut that opens the cards (and jumps to an agent waiting for approval)
            # e.g. win+alt+n, ctrl+alt+space, ctrl+shift+f9, or "off"
            hotkey = win+alt+n

            # Chime when an agent finishes or needs you, or a long `nb run` ends, while you're in another window
            sound = on

            # theme = auto | dark | light      (auto follows Windows' taskbar theme)
            theme = auto
            # accent = auto | none | #RRGGBB  (colour for progress, spinners and the selected tab)
            accent = auto

            # monitor = follow | primary   (follow: the notch moves to the taskbar of the screen you're working on)
            monitor = follow

            # Context window used by the agent cards' context meter (default: from the model, 1M or 200K)
            # context_window = 200000

            """);
    }
}
