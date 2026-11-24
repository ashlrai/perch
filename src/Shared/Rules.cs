namespace Perch;

/// Approval rules made from the notch ("Always" / "10 min"), stored in %APPDATA%\perch\rules.tsv, one per line:
///   expiresUtcMs (0 = never) \t repository folder \t tool \t command prefix (Bash; empty = any input)
/// `perch agent` checks them on every PermissionRequest; the notch writes them. Shared by both.
public static class Rules
{
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "perch", "rules.tsv");

    /// Whether a rule in the rules file allows `tool` acting on `detail` (a Bash command, a file...) in `cwd`.
    public static bool Allows(string cwd, string tool, string detail)
    {
        try { return File.Exists(FilePath) && Matches(File.ReadAllLines(FilePath), Root(cwd), tool, detail, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); }
        catch { return false; }
    }

    /// The matching itself, on the file's lines (separate for tests). `root` is the repository of the request.
    public static bool Matches(IEnumerable<string> lines, string root, string tool, string detail, long nowUtcMs)
    {
        if (tool.Length == 0) return false;
        string cmd = detail.Trim();
        foreach (var line in lines)
        {
            var p = line.Split('\t');
            if (p.Length < 4 || !long.TryParse(p[0], out var exp) || (exp != 0 && exp < nowUtcMs)) continue;
            if (!string.Equals(p[1].TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) || p[2] != tool) continue;
            if (p[3].Length == 0) return true;
            if (Chained(cmd)) continue;   // a prefix rule never covers a chained or redirected command
            if (cmd == p[3] || cmd.StartsWith(p[3] + " ", StringComparison.Ordinal)) return true;   // whole words only
        }
        return false;
    }

    /// Commands that do more than one thing (or write somewhere) can hide anything after an allowed prefix.
    public static bool Chained(string cmd) =>
        cmd.IndexOfAny(['&', '|', ';', '>', '<', '`', '\n', '\r']) >= 0 || cmd.Contains("$(");

    /// The command prefix an "Always" rule uses: the first two words, stopping at options and operators.
    public static string Prefix(string command)
    {
        var take = new List<string>();
        foreach (var w in command.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (take.Count == 2 || w.IndexOfAny(['&', '|', ';', '>', '<', '`']) >= 0 || w.StartsWith('-') || w.Contains("$(")) break;
            take.Add(w);
        }
        return string.Join(' ', take);
    }

    /// One rule's line. `minutes` = 0: no expiry.
    public static string Line(string root, string tool, string prefix, int minutes, long nowUtcMs) =>
        $"{(minutes > 0 ? nowUtcMs + minutes * 60_000L : 0)}\t{root}\t{tool}\t{prefix.Replace('\t', ' ').Replace('\n', ' ')}";

    /// The repository a folder is in (the nearest parent with a .git), or the folder itself.
    public static string Root(string cwd)
    {
        try { for (string? d = cwd; d != null; d = Path.GetDirectoryName(d)) if (Path.Exists(Path.Combine(d, ".git"))) return d; } catch { }
        return cwd;
    }
}
