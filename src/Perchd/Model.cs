using System.Diagnostics;

namespace Perch;

sealed class TaskInfo
{
    public string Key = "", Label = "", Cwd = "", Cmd = "";
    public uint Pid;
    public long Start, End;
    public int Code;
    public bool Watch;          // started by `perch watch`
    public string Output = "";  // the end of its output, when it failed
    public string Job => Cwd.ToLowerInvariant() + "\n" + (Cmd.Length > 0 ? Cmd : Label);
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
    public string AskToken = "";                          // ...and only accepts an answer carrying this one-time token
    public string Verdict = "";                           // "allow" / "deny" when answered from the notch
    public readonly List<LogEntry> Log = new();          // tool calls since the last prompt
    public long Since, Updated;
    public string Transcript = "";                        // Claude Code's session transcript (.jsonl), for the context meter
    public int AutoN;                                     // permission requests answered by a rule this turn
    public readonly HashSet<string> Touched = new(StringComparer.OrdinalIgnoreCase);   // files it edited this turn
    public volatile List<(string Path, int Add, int Del)>? Changes;                    // their diff stats, once done
    public long ChangesFor;                               // the Since of the turn Changes was read for
}

/// One finished `perch run`, kept across restarts for the Builds page (wall-clock times).
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
    public long T; public string Tool = "", Detail = ""; public bool Done, Auto;
}

sealed record GitStatus(string Branch, int Changed, int Untracked, int Ahead, int Behind, List<(char Code, string Path)> Files);

/// GitHub state of a repo's current branch, from `gh`: the latest workflow run and the open pull request.
sealed record GhStatus(string RunName, string RunStatus, string RunConclusion, string RunUrl, long RunId,
    int Pr, string PrTitle, string PrUrl, string PrReview, string PrState);

sealed class Repo
{
    public string Root = "", Name = "";
    public long Used;
    public volatile GitStatus? St;
    public volatile GhStatus? Gh;
    public int Busy, GhBusy;
    public long Last, GhLast;
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
            case "O":   // the end of a failed command's output
                if (f.Length > 9)
                {
                    var t = Tasks.Find(x => x.Key == f[2]);
                    if (t != null) t.Output = f[9];
                    LastOutput[f[5].ToLowerInvariant() + "\n" + (f[8].Length > 0 ? f[8] : f[4])] = f[9];
                }
                break;
        }
    }

    /// The output of each command's most recent failure (by job), for the Builds cards.
    public static readonly Dictionary<string, string> LastOutput = new();

    static void OnTask(string[] f, long now)
    {
        var t = Tasks.Find(x => x.Key == f[2]);
        if (f[3] == "start")
        {
            if (t == null) { t = new TaskInfo { Key = f[2] }; Tasks.Add(t); }
            t.Label = f[4]; t.Cwd = f[5]; uint.TryParse(f[6], out t.Pid);
            t.Cmd = f.Length > 8 ? f[8] : "";
            t.Watch = f[7] == "watch"; t.Output = "";
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
            if (t.Code == 0) LastOutput.Remove(t.Job);
            // chime for a long run, or a failure, but only when the result changed (a watch failing on every save stays quiet)
            Run? prev = null;
            for (int i = Runs.Count - 1; i >= 0 && prev == null; i--) if (Runs[i].Job == t.Job) prev = Runs[i];
            bool flipped = prev == null ? t.Code != 0 : (prev.Code == 0) != (t.Code == 0);
            AddRun(new Run { Label = t.Label, Cmd = t.Cmd, Cwd = t.Cwd, EndUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Ms = t.End - t.Start, Code = t.Code });
            if (flipped || (!t.Watch && t.End - t.Start > 15_000)) Alerts.Add(new Alert(t.Code == 0 ? "ok" : "fail", t.Pid, t.Key));
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
        if (f.Length > 8 && f[8].Length > 0)
        {   // new prompt: start a fresh log
            a.Prompt = f[8]; a.Tool = ""; a.PromptAt = now; a.Log.Clear(); a.Verdict = "";
            a.AutoN = 0; a.Touched.Clear(); a.Changes = null;
        }
        if (f.Length > 9 && f[9].StartsWith("AutoAllow\t", StringComparison.Ordinal))
        {   // a rule answered a permission request: log it, and don't treat it as the agent's current tool
            var p = f[9].Split('\t');
            a.AutoN++;
            a.Log.Add(new LogEntry { T = now, Tool = p.Length > 1 ? p[1] : "", Detail = p.Length > 2 ? p[2] : "", Done = true, Auto = true });
            if (a.Log.Count > 12) a.Log.RemoveAt(0);
            f[9] = "";
        }
        if (f.Length > 9 && f[9].Length > 0) { a.Tool = f[9]; AddLog(a, f[9], now); }
        if (st is "done" or "off") foreach (var e in a.Log) e.Done = true;
        if (f.Length > 10 && f[10].Length > 0) a.Host = f[10];
        if (f.Length > 11 && uint.TryParse(f[11], out var pid) && pid > 0) a.Pid = pid;
        // a waiting PermissionRequest hook keeps its Allow/Deny through Claude's own "needs permission"
        // notification; any other event (or perch giving up, port -1) ends the question
        int port = f.Length > 12 && int.TryParse(f[12], out var pp) ? pp : 0;
        if (port != 0 || st != "wait") { a.AskPort = Math.Max(0, port); a.AskToken = port > 0 && f.Length > 14 ? f[14] : ""; }
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
        if (name is "Edit" or "Write" or "MultiEdit" or "NotebookEdit" && detail.Length > 0 && a.Touched.Count < 200) a.Touched.Add(detail);
        if (ev == "PostToolUse")
            for (int i = a.Log.Count - 1; i >= 0; i--)
                if (!a.Log[i].Done && a.Log[i].Tool == name) { a.Log[i].Done = true; return; }
        a.Log.Add(new LogEntry { T = now, Tool = name, Detail = detail, Done = ev != "PreToolUse" });
        if (a.Log.Count > 12) a.Log.RemoveAt(0);
    }

    /// Drop stale entries; mark tasks whose perch.exe vanished (killed terminal) as lost.
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
    // The last 60 finished `perch run`s, persisted, newest last. Drives the Builds page and "Run again".

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

    // ---------------------------------------------------------------- approval rules
    // Read by `perch agent` on every PermissionRequest; format and matching in Shared/Rules.cs.

    static string RulesPath => Rules.FilePath;

    /// Allow `tool` in the repository of `cwd` (Bash: commands starting with `prefix`), forever or for `minutes`.
    public static void AddRule(string cwd, string tool, string prefix, int minutes)
    {
        try
        {
            Directory.CreateDirectory(Config.Dir);
            File.AppendAllText(RulesPath, Rules.Line(Rules.Root(cwd), tool, prefix, minutes, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) + "\n");
        }
        catch { }
    }

    public static int RuleCount()
    {
        try
        {
            if (!File.Exists(RulesPath)) return 0;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return File.ReadAllLines(RulesPath).Count(l => l.Split('\t') is { Length: >= 4 } p && long.TryParse(p[0], out var e) && (e == 0 || e > now));
        }
        catch { return 0; }
    }

    public static void ClearRules() { try { File.Delete(RulesPath); } catch { } }

    public static string RulePrefix(string command) => Rules.Prefix(command);

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

    public static string? FindRoot(string cwd)
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

    static (int Code, string Out) Exec(string exe, string cwd, int timeoutMs, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = cwd,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["GH_PROMPT_DISABLED"] = "1";
        using var p = Process.Start(psi);
        if (p == null) return (-1, "");
        var o = p.StandardOutput.ReadToEndAsync();
        p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, ""); }
        return (p.ExitCode, o.Result);
    }

    // ---- what an agent changed

    /// +/- line counts for the files an agent edited (new files count every line as added). Worker thread.
    public static void Changes(AgentInfo a, nint hwnd, uint msg)
    {
        var files = a.Touched.ToList();
        string cwd = a.Cwd;
        long turn = a.Since;
        if (files.Count == 0 || cwd.Length == 0) return;
        a.ChangesFor = turn;
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try
            {
                string root = Model.FindRoot(cwd) ?? cwd;
                var rel = files.Select(f => Path.IsPathRooted(f) ? Path.GetRelativePath(root, f) : f).Where(f => !f.StartsWith("..")).Distinct().ToList();
                var res = new List<(string, int, int)>();
                if (rel.Count > 0)
                {
                    var args = new List<string> { "-C", root, "diff", "--numstat", "HEAD", "--" };
                    args.AddRange(rel);
                    var (code, outp) = Exec("git", root, 4000, args.ToArray());
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (code == 0)
                        foreach (var line in outp.Split('\n'))
                        {
                            var p = line.TrimEnd('\r').Split('\t');
                            if (p.Length < 3) continue;
                            int.TryParse(p[0], out int add); int.TryParse(p[1], out int del);
                            string path = p[2].Replace('/', Path.DirectorySeparatorChar);
                            res.Add((path, add, del)); seen.Add(path);
                        }
                    foreach (var f in rel)
                    {   // untracked (new) files aren't in the diff
                        if (seen.Contains(f)) continue;
                        string full = Path.Combine(root, f);
                        if (!File.Exists(full)) continue;
                        int n = 0;
                        try { foreach (var line2 in File.ReadLines(full)) if (++n > 100_000) break; } catch { }
                        if (Exec("git", root, 3000, "-C", root, "ls-files", "--error-unmatch", "--", f).Code != 0) res.Add((f, n, 0));
                    }
                }
                res.Sort((x, y) => (y.Item2 + y.Item3).CompareTo(x.Item2 + x.Item3));
                a.Changes = res;
            }
            catch { }
            finally { W.PostMessageW(hwnd, msg, 0, 0); }
        }, null);
    }

    /// Put an agent's changes aside: `git stash push -u` of just the files it edited (recoverable with `git stash pop`).
    public static bool Stash(AgentInfo a, string label)
    {
        try
        {
            string root = Model.FindRoot(a.Cwd) ?? a.Cwd;
            var rel = a.Touched.Select(f => Path.IsPathRooted(f) ? Path.GetRelativePath(root, f) : f).Where(f => !f.StartsWith("..")).Distinct().ToList();
            if (rel.Count == 0) return false;
            var args = new List<string> { "-C", root, "stash", "push", "-u", "-m", label, "--" };
            args.AddRange(rel);
            return Exec("git", root, 8000, args.ToArray()).Code == 0;
        }
        catch { return false; }
    }

    // ---- GitHub: CI and pull requests, through the `gh` CLI when it's installed and signed in

    static int _ghState;   // 0 unknown, 1 available, 2 not available

    public static bool GhAvailable
    {
        get
        {
            if (_ghState == 0)
                try { _ghState = Exec("gh", Environment.CurrentDirectory, 5000, "auth", "status").Code == 0 ? 1 : 2; } catch { _ghState = 2; }
            return _ghState == 1;
        }
    }

    /// Refresh a repo's CI run and pull request (worker thread); posts `msg` when done.
    public static void RefreshGh(Repo r, nint hwnd, uint msg)
    {
        if (Interlocked.CompareExchange(ref r.GhBusy, 1, 0) != 0) return;
        r.GhLast = Model.Now;
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try
            {
                if (!GhAvailable) return;
                string branch = r.St?.Branch ?? "";
                if (branch is "" or "?" or "(detached)") branch = Exec("git", r.Root, 3000, "-C", r.Root, "branch", "--show-current").Out.Trim();
                if (branch.Length == 0) return;
                string runName = "", runStatus = "", runConc = "", runUrl = ""; long runId = 0;
                var (rc, ro) = Exec("gh", r.Root, 15000, "run", "list", "--branch", branch, "--limit", "1", "--json", "databaseId,status,conclusion,workflowName,url");
                if (rc == 0 && ro.Trim().StartsWith('['))
                {
                    using var d = System.Text.Json.JsonDocument.Parse(ro);
                    if (d.RootElement.GetArrayLength() > 0)
                    {
                        var e = d.RootElement[0];
                        runName = S(e, "workflowName"); runStatus = S(e, "status"); runConc = S(e, "conclusion"); runUrl = S(e, "url");
                        if (e.TryGetProperty("databaseId", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.Number) runId = id.GetInt64();
                    }
                }
                int pr = 0; string prTitle = "", prUrl = "", prReview = "", prState = "";
                var (pc, po) = Exec("gh", r.Root, 15000, "pr", "view", branch, "--json", "number,title,url,reviewDecision,state");
                if (pc == 0 && po.Trim().StartsWith('{'))
                {
                    using var d = System.Text.Json.JsonDocument.Parse(po);
                    var e = d.RootElement;
                    if (e.TryGetProperty("number", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.Number) pr = n.GetInt32();
                    prTitle = S(e, "title"); prUrl = S(e, "url"); prReview = S(e, "reviewDecision"); prState = S(e, "state");
                }
                var old = r.Gh;
                r.Gh = new GhStatus(runName, runStatus, runConc, runUrl, runId, pr, prTitle, prUrl, prReview, prState);
                // CI just failed on this branch: the UI thread turns this into a chime and a red ripple
                if (runConc == "failure" && (old == null || old.RunId != runId || old.RunConclusion != "failure") && old != null)
                    Interlocked.Exchange(ref _ciFailed, r);
            }
            catch { }
            finally { r.GhBusy = 0; W.PostMessageW(hwnd, msg, 0, 0); }
        }, null);
        static string S(System.Text.Json.JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    static Repo? _ciFailed;
    public static Repo? TakeCiFailure() => Interlocked.Exchange(ref _ciFailed, null);

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
    public bool HideFullscreen = true, LowMotion, Terrain, Glass, ClearTaskbar, Sound = true, FollowMonitor = true, TuckMaximized = true;
    public string Hotkey = "win+alt+n", Theme = "auto", Accent = "auto";
    public long ContextWindow;   // 0: from the model

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "perch");
    public static string FilePath => Path.Combine(Dir, "config.ini");

    /// Set one key in config.ini: replace its line, or add it (creating the file from the template).
    public static void Set(string key, string value)
    {
        try
        {
            EnsureFile();
            var lines = File.ReadAllLines(FilePath).ToList();
            int i = lines.FindIndex(l => l.TrimStart().StartsWith(key, StringComparison.OrdinalIgnoreCase) && l.Contains('=')
                                         && l[..l.IndexOf('=')].Trim().Equals(key, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) lines[i] = key + " = " + value; else lines.Add(key + " = " + value);
            File.WriteAllLines(FilePath, lines);
        }
        catch { }
    }

    /// Windows 11's taskbar (build 22000+): the only one the taskbar probe and the Explorer helper understand.
    public static bool Win11 => Environment.OSVersion.Version.Build >= 22000;

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
                    case "taskbar": c.ClearTaskbar = v == "clear"; break;
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
            # perch config - right-click the notch > "Reload config" after editing.

            # Repos to always list under GIT (one per line). Repos you `perch run` in appear automatically.
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

            # taskbar = system | clear     (also: right-click the notch > Clear the taskbar background)
            #   clear: hide the Windows 11 taskbar's own background so perch's glass boxes show through.
            #   This loads a small helper into Explorer (like TranslucentTB does); it puts the background
            #   back when perch quits. Changing it restarts perch.
            taskbar = system

            # backdrop = plain | glass
            #   plain: the desktop is just dimmed behind the cards    glass: dimmed and blurred
            backdrop = plain

            # Keyboard shortcut that opens the cards (and jumps to an agent waiting for approval)
            # e.g. win+alt+n, ctrl+alt+space, ctrl+shift+f9, or "off"
            hotkey = win+alt+n

            # Chime when an agent finishes or needs you, or a long `perch run` ends, while you're in another window
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
