using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NotchBar;

// nb — tiny CLI that reports to the notchbar daemon over a localhost UDP datagram.

static class Nb
{
    const string Help = """
        nb — report builds & agents to notchbar

          nb run [-n label] <command...>   run a command, show it live in the notch
          nb agent [busy|wait|done|off] [name]
                                           report agent state (reads Claude Code hook JSON from stdin)
          nb note <text>                   flash a message in the notch
          nb hooks                         print Claude Code hooks config

        examples:
          nb run npm run build
          nb run -n tests cargo test --workspace
        """;

    const string Hooks = """
        Merge this into ~/.claude/settings.json (or a project's .claude/settings.json):

        {
          "hooks": {
            "UserPromptSubmit": [{ "hooks": [{ "type": "command", "command": "nb agent" }] }],
            "PreToolUse":       [{ "matcher": "*", "hooks": [{ "type": "command", "command": "nb agent" }] }],
            "PostToolUse":      [{ "matcher": "*", "hooks": [{ "type": "command", "command": "nb agent" }] }],
            "PermissionRequest":[{ "matcher": "*", "hooks": [{ "type": "command", "command": "nb agent", "timeout": 60 }] }],
            "Notification":     [{ "hooks": [{ "type": "command", "command": "nb agent" }] }],
            "Stop":             [{ "hooks": [{ "type": "command", "command": "nb agent" }] }],
            "SessionEnd":       [{ "hooks": [{ "type": "command", "command": "nb agent" }] }]
          }
        }

        `nb agent` maps the hook event automatically: prompt/tool -> working,
        Notification -> needs you, Stop -> done, SessionEnd -> removed.
        PermissionRequest puts Allow / Deny on the notch; if you don't answer there
        within 45 s, Claude's own prompt takes over.
        """;

    const int AskSeconds = 45;

    static int Main(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine(Help); return 0; }
        switch (args[0])
        {
            case "run": return Run(args);
            case "agent": Agent(args); return 0; // never fail a hook
            case "note": Send("N", "", "info", string.Join(' ', args, 1, args.Length - 1), Environment.CurrentDirectory, "", ""); return 0;
            case "hooks": Console.WriteLine(Hooks); return 0;
            default: Console.WriteLine(Help); return args[0] is "-h" or "--help" or "help" ? 0 : 2;
        }
    }

    static int Run(string[] a)
    {
        int i = 1; string? label = null;
        while (i < a.Length)
        {
            if ((a[i] == "-n" || a[i] == "--name") && i + 1 < a.Length) { label = a[i + 1]; i += 2; }
            else if (a[i] == "--") { i++; break; }
            else break;
        }
        if (i >= a.Length) { Console.Error.WriteLine("nb run: missing command"); return 2; }

        string cmd = a.Length - i == 1 ? a[i] : Join(a, i);
        label ??= cmd;
        string pid = Environment.ProcessId.ToString();
        string id = pid + "." + Environment.TickCount64;
        string cwd = Environment.CurrentDirectory;

        Send("T", id, "start", label, cwd, pid, "", Clip(cmd, 600));   // the command, so the notch can run it again
        Console.CancelKeyPress += (_, e) => e.Cancel = true; // Ctrl+C goes to the child; we stay to report it
        int code;
        try
        {
            var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
            {
                UseShellExecute = false,
                Arguments = "/d /s /c \"" + cmd + "\"",
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            code = p.ExitCode;
        }
        catch (Exception ex) { Console.Error.WriteLine("nb: " + ex.Message); code = 127; }
        Send("T", id, "end", label, cwd, pid, code.ToString());
        return code;
    }

    static string Join(string[] a, int from)
    {
        var sb = new StringBuilder();
        for (int i = from; i < a.Length; i++)
        {
            if (sb.Length > 0) sb.Append(' ');
            var s = a[i];
            if (s.Length == 0 || s.IndexOfAny([' ', '\t', '"', '&', '|', '<', '>', '^']) >= 0) sb.Append('"').Append(s.Replace("\"", "\\\"")).Append('"');
            else sb.Append(s);
        }
        return sb.ToString();
    }

    static void Agent(string[] a)
    {
        string? state = a.Length > 1 ? a[1] : null;
        string name = a.Length > 2 ? a[2] : "Claude";
        string cwd = Environment.CurrentDirectory, key = "", msg = "", prompt = "", tool = "", ev = "", tn = "", transcript = "";
        try
        {
            if (Console.IsInputRedirected)
            {
                var read = Task.Run(() => Console.In.ReadToEnd());
                string json = (read.Wait(500) ? read.Result : "").Trim().TrimStart('﻿');
                if (json.Length > 0)
                {
                    using var d = JsonDocument.Parse(json);
                    var r = d.RootElement;
                    string S(string p) => r.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    if (S("cwd") is { Length: > 0 } c) cwd = c;
                    key = S("session_id");
                    transcript = S("transcript_path");   // for the context meter on the agent's card
                    msg = S("message");
                    prompt = S("prompt");
                    tn = S("tool_name");
                    if (tn.Length > 0)
                    {   // "event<TAB>tool<TAB>detail": the command, file or pattern the tool is acting on
                        string detail = "";
                        if (r.TryGetProperty("tool_input", out var ti) && ti.ValueKind == JsonValueKind.Object)
                            foreach (var k in new[] { "command", "file_path", "notebook_path", "path", "pattern", "url", "query", "description" })
                                if (ti.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String) { detail = v.GetString() ?? ""; break; }
                        tool = S("hook_event_name") + "\t" + tn + "\t" + Clip(detail.ReplaceLineEndings(" "), 200);
                    }
                    ev = S("hook_event_name");
                    if (ev == "PermissionRequest") { msg = "Claude needs your permission to use " + tn; tool = ""; }
                    state ??= ev switch
                    {
                        "Notification" or "PermissionRequest" => "wait",
                        "Stop" => "done",
                        "SessionEnd" => "off",
                        _ => "busy",
                    };
                }
            }
        }
        catch { }
        state ??= "busy";
        if (key.Length == 0) key = name + "@" + cwd;
        string host = Host(), pid = AgentPid().ToString();
        msg = Clip(msg, 300); prompt = Clip(prompt.ReplaceLineEndings(" "), 300);
        if (ev != "PermissionRequest") { Send("A", key, state, name, cwd, "", msg, prompt, tool, host, pid, "", transcript); return; }

        // Ask on the notch: report a reply port, wait for "allow"/"deny", answer the hook with it.
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)s.LocalEndPoint!).Port;
        Send("A", key, "wait", name, cwd, "", msg, "", "", host, pid, port.ToString(), transcript);
        string answer = "";
        try
        {
            var buf = new byte[64];
            s.ReceiveTimeout = AskSeconds * 1000;
            int n = s.Receive(buf);
            answer = Encoding.ASCII.GetString(buf, 0, n);
        }
        catch { }   // timed out: leave it to Claude's own prompt
        if (answer is "allow" or "deny")
        {
            Console.Out.Write(answer == "allow"
                ? """{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}"""
                : """{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"deny","message":"Denied from the notch"}}}""");
            Console.Out.Flush();
        }
        else Send("A", key, "wait", name, cwd, "", msg, "", "", host, pid, "-1");
    }

    /// The agent process this hook runs under: the first ancestor that isn't a shell (hooks run via one).
    static unsafe uint AgentPid()
    {
        try
        {
            var parent = new Dictionary<uint, uint>(); var exe = new Dictionary<uint, string>();
            nint snap = CreateToolhelp32Snapshot(2, 0);
            if (snap == -1) return 0;
            PE e; e.dwSize = (uint)sizeof(PE);
            if (Process32FirstW(snap, &e) != 0)
                do { parent[e.pid] = e.ppid; exe[e.pid] = new string(e.exe).ToLowerInvariant(); } while (Process32NextW(snap, &e) != 0);
            CloseHandle(snap);
            uint p = (uint)Environment.ProcessId;
            for (int i = 0; i < 8 && parent.TryGetValue(p, out var pp) && pp != 0 && pp != p; i++)
            {
                p = pp;
                if (!exe.TryGetValue(p, out var n)) break;
                if (n is not ("cmd.exe" or "bash.exe" or "sh.exe" or "pwsh.exe" or "powershell.exe" or "conhost.exe" or "nb.exe" or "env.exe" or "usr/bin/bash")) return p;
            }
        }
        catch { }
        return 0;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    unsafe struct PE
    {
        public uint dwSize, cntUsage, pid; public nuint heap; public uint module, threads, ppid; public int pri; public uint flags;
        public fixed char exe[260];
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern unsafe int Process32FirstW(nint snap, PE* e);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern unsafe int Process32NextW(nint snap, PE* e);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern int CloseHandle(nint h);

    static string Clip(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";   // keep the datagram small

    /// The editor or terminal this agent runs in, from the environment its hooks inherit.
    static string Host()
    {
        static string E(string n) => Environment.GetEnvironmentVariable(n) ?? "";
        const StringComparison I = StringComparison.OrdinalIgnoreCase;
        string tp = E("TERM_PROGRAM"), vs = E("VSCODE_GIT_ASKPASS_NODE") + E("VSCODE_IPC_HOOK_CLI");
        if (E("CURSOR_TRACE_ID").Length > 0 || vs.Contains("cursor", I)) return "Cursor";
        if (vs.Contains("windsurf", I)) return "Windsurf";
        if (tp == "vscode") return vs.Contains("insiders", I) ? "VS Code Insiders" : "VS Code";
        if (E("TERMINAL_EMULATOR").Contains("JetBrains", I)) return "JetBrains";
        if (E("WT_SESSION").Length > 0) return "Windows Terminal";
        return tp.Length > 0 ? tp : "";
    }

    static void Send(params string[] f)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            var bytes = Encoding.UTF8.GetBytes("nb1" + Proto.Sep + string.Join(Proto.Sep, f));
            s.SendTo(bytes, new IPEndPoint(IPAddress.Loopback, Proto.Port));
        }
        catch { }
    }
}
