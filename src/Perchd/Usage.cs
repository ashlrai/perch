using System.Text.Json;

namespace Perch;

/// Token use of a Claude Code session, read from its transcript (.jsonl) incrementally on a worker thread:
/// only the bytes appended since the last read are parsed. Context = the last main-thread request's
/// input (incl. cache reads/writes); Total = every unique API message's input + output across the session.
static class Usage
{
    public sealed record Snap(long Context, long Total, string Model, Ev[] Events);

    /// One step of a session, for the timeline: a prompt (p), a tool call (t) or a reply (r).
    public sealed class Ev
    {
        public long T, Dur = -1, Ctx;   // unix ms; tool duration (-1 = still running); context after it
        public char Kind;
        public string Text = "", Id = "";
    }

    sealed class Track
    {
        public string Path = "";
        public long Offset, Total, Context, LastRead;
        public string Model = "";
        public readonly HashSet<string> Seen = new();
        public readonly List<Ev> Events = new();
        public int Busy, EvVersion, SnapVersion;
        public volatile Snap? Snap;
    }

    static readonly Dictionary<string, Track> _t = new(StringComparer.OrdinalIgnoreCase);

    public static Snap? Get(string transcript) =>
        transcript.Length > 0 && _t.TryGetValue(transcript, out var t) ? t.Snap : null;

    /// Re-read a transcript if it may have grown (at most every 2 s). Posts `msg` to `hwnd` when the numbers change.
    public static void Refresh(string transcript, nint hwnd, uint msg)
    {
        if (transcript.Length == 0) return;
        if (!_t.TryGetValue(transcript, out var t))
        {
            if (_t.Count > 24) _t.Clear();
            _t[transcript] = t = new Track { Path = transcript };
        }
        long now = Model.Now;
        if (now - t.LastRead < 2000 || Interlocked.CompareExchange(ref t.Busy, 1, 0) != 0) return;
        t.LastRead = now;
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try { if (Read(t)) W.PostMessageW(hwnd, msg, 0, 0); }
            catch { }
            finally { t.Busy = 0; }
        }, null);
    }

    static bool Read(Track t)
    {
        using var fs = new FileStream(t.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < t.Offset) { t.Offset = 0; t.Total = 0; t.Context = 0; t.Seen.Clear(); t.Events.Clear(); }   // rewritten
        if (fs.Length == t.Offset) return false;
        fs.Position = t.Offset;
        var buf = new byte[fs.Length - t.Offset];
        int n = 0;
        while (n < buf.Length) { int r = fs.Read(buf, n, buf.Length - n); if (r <= 0) break; n += r; }
        int start = 0;
        var span = buf.AsSpan(0, n);
        while (true)
        {
            int nl = span[start..].IndexOf((byte)'\n');
            if (nl < 0) break;   // a partial last line: read it next time
            var line = span.Slice(start, nl);
            start += nl + 1;
            if (line.IndexOf("\"type\":\"user\""u8) >= 0 || line.IndexOf("\"type\":\"assistant\""u8) >= 0) Line(t, line);
        }
        t.Offset += start;
        if (t.Events.Count > 300) t.Events.RemoveRange(0, t.Events.Count - 300);
        var old = t.Snap;
        if (old != null && old.Context == t.Context && old.Total == t.Total && old.Model == t.Model && t.SnapVersion == t.EvVersion) return false;
        t.SnapVersion = t.EvVersion;
        t.Snap = new Snap(t.Context, t.Total, t.Model, t.Events.Select(e => new Ev { T = e.T, Dur = e.Dur, Ctx = e.Ctx, Kind = e.Kind, Text = e.Text, Id = e.Id }).ToArray());
        return true;
    }

    static void Line(Track t, ReadOnlySpan<byte> line)
    {
        try
        {
            using var d = JsonDocument.Parse(line.ToArray());
            var r = d.RootElement;
            if (!r.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object) return;
            bool side = r.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True;
            bool meta = r.TryGetProperty("isMeta", out var mt) && mt.ValueKind == JsonValueKind.True;
            long ts = r.TryGetProperty("timestamp", out var tv) && tv.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(tv.GetString(), out var dto) ? dto.ToUnixTimeMilliseconds() : 0;
            string type = r.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String ? ty.GetString() ?? "" : "";
            m.TryGetProperty("content", out var content);

            if (m.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                long N(string k) => u.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
                long input = N("input_tokens") + N("cache_creation_input_tokens") + N("cache_read_input_tokens"), output = N("output_tokens");
                // one API response is written as several lines (one per content block) with the same id and usage
                string id = m.TryGetProperty("id", out var idv) && idv.ValueKind == JsonValueKind.String ? idv.GetString() ?? "" : "";
                if (id.Length == 0 || t.Seen.Add(id)) t.Total += input + output;
                if (!side && input > 0)
                {
                    t.Context = input;
                    if (m.TryGetProperty("model", out var mv) && mv.ValueKind == JsonValueKind.String) t.Model = mv.GetString() ?? "";
                }
            }
            if (side || meta) return;   // the timeline follows the main conversation

            if (type == "user")
            {
                if (content.ValueKind == JsonValueKind.String) Prompt(t, content.GetString() ?? "", ts);
                else if (content.ValueKind == JsonValueKind.Array)
                    foreach (var c in content.EnumerateArray())
                    {
                        string ct = c.TryGetProperty("type", out var x) ? x.GetString() ?? "" : "";
                        if (ct == "tool_result" && c.TryGetProperty("tool_use_id", out var tid))
                        {   // a tool call finished: how long it took
                            string id = tid.GetString() ?? "";
                            for (int i = t.Events.Count - 1; i >= 0 && i >= t.Events.Count - 60; i--)
                                if (t.Events[i].Id == id) { t.Events[i].Dur = ts > 0 ? Math.Max(0, ts - t.Events[i].T) : 0; t.EvVersion++; break; }
                        }
                        else if (ct == "text" && c.TryGetProperty("text", out var tx)) Prompt(t, tx.GetString() ?? "", ts);
                    }
            }
            else if (type == "assistant" && content.ValueKind == JsonValueKind.Array)
                foreach (var c in content.EnumerateArray())
                {
                    string ct = c.TryGetProperty("type", out var x) ? x.GetString() ?? "" : "";
                    if (ct == "tool_use")
                    {
                        string name = c.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                        string what = "";
                        if (c.TryGetProperty("input", out var inp) && inp.ValueKind == JsonValueKind.Object)
                            foreach (var k in new[] { "command", "file_path", "notebook_path", "pattern", "url", "query", "description", "path" })
                                if (inp.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                                {
                                    what = v.GetString() ?? "";
                                    if (k is "file_path" or "notebook_path" or "path") what = System.IO.Path.GetFileName(what.TrimEnd('\\', '/'));
                                    break;
                                }
                        Add(t, new Ev { T = ts, Kind = 't', Text = (name + "  " + what.ReplaceLineEndings(" ")).Trim(), Id = c.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "", Ctx = t.Context });
                    }
                    else if (ct == "text" && c.TryGetProperty("text", out var tx) && (tx.GetString() ?? "").Trim() is { Length: > 0 } s)
                        Add(t, new Ev { T = ts, Kind = 'r', Text = s.ReplaceLineEndings(" "), Ctx = t.Context, Dur = 0 });
                }
        }
        catch { }
    }

    static void Prompt(Track t, string s, long ts)
    {
        s = s.Trim();
        if (s.Length == 0 || s.StartsWith('<')) return;   // command wrappers, interruptions, hook output
        Add(t, new Ev { T = ts, Kind = 'p', Text = s.ReplaceLineEndings(" "), Ctx = t.Context, Dur = 0 });
    }

    static void Add(Track t, Ev e)
    {
        if (e.Text.Length > 240) e.Text = e.Text[..239] + "…";
        t.Events.Add(e); t.EvVersion++;
    }

    // ---------------------------------------------------------------- today / this week, across every session

    public sealed record Days(long Today, long Week);
    static volatile Days? _days;
    static int _daysBusy; static long _daysAt;
    static readonly Dictionary<string, long> _dayOffsets = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<DateOnly, long> _perDay = new();
    static readonly HashSet<string> _dayIds = new();

    public static Days? Totals => _days;

    /// Sum the tokens of every Claude Code transcript touched in the last 7 days (incrementally; at most once a minute).
    public static void RefreshTotals(nint hwnd, uint msg)
    {
        long now = Model.Now;
        if (_daysAt != 0 && now - _daysAt < 60_000 || Interlocked.CompareExchange(ref _daysBusy, 1, 0) != 0) return;
        _daysAt = now;
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try
            {
                string dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } cd ? System.IO.Path.Combine(cd, "projects")
                    : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
                if (!Directory.Exists(dir)) return;
                var since = DateTime.UtcNow.AddDays(-7);
                foreach (var f in Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories))
                {
                    var fi = new FileInfo(f);
                    if (fi.LastWriteTimeUtc < since || fi.Length > 400_000_000) continue;
                    _dayOffsets.TryGetValue(f, out long off);
                    if (fi.Length < off) off = 0;
                    if (fi.Length == off) continue;
                    _dayOffsets[f] = off + DayRead(f, off);
                }
                var today = DateOnly.FromDateTime(DateTime.Now);
                long t = 0, w = 0;
                foreach (var (day, n) in _perDay)
                {
                    if (day == today) t += n;
                    if (day > today.AddDays(-7)) w += n;
                }
                var old = _days;
                _days = new Days(t, w);
                if (old != _days) W.PostMessageW(hwnd, msg, 0, 0);
            }
            catch { }
            finally { _daysBusy = 0; }
        }, null);
    }

    /// Reads whole lines from `off`; returns how many bytes it consumed.
    static long DayRead(string path, long off)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.Position = off;
        var buf = new byte[fs.Length - off];
        int n = 0;
        while (n < buf.Length) { int r = fs.Read(buf, n, buf.Length - n); if (r <= 0) break; n += r; }
        var span = buf.AsSpan(0, n);
        int start = 0;
        while (true)
        {
            int nl = span[start..].IndexOf((byte)'\n');
            if (nl < 0) break;
            var line = span.Slice(start, nl);
            start += nl + 1;
            if (line.IndexOf("\"usage\""u8) < 0) continue;
            try
            {
                using var d = JsonDocument.Parse(line.ToArray());
                var r = d.RootElement;
                if (!r.TryGetProperty("message", out var m) || !m.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) continue;
                string id = m.TryGetProperty("id", out var iv) && iv.ValueKind == JsonValueKind.String ? iv.GetString() ?? "" : "";
                if (id.Length > 0 && !_dayIds.Add(id)) continue;
                if (!(r.TryGetProperty("timestamp", out var tv) && DateTimeOffset.TryParse(tv.GetString(), out var ts))) continue;
                long N(string k) => u.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
                var day = DateOnly.FromDateTime(ts.LocalDateTime);
                _perDay[day] = _perDay.GetValueOrDefault(day) + N("input_tokens") + N("cache_creation_input_tokens") + N("cache_read_input_tokens") + N("output_tokens");
            }
            catch { }
        }
        return start;
    }

    /// Context window for a model id: 1M for current models; 200K for Haiku 4.5 and Claude 4.5-and-older models.
    public static long Window(string model, long used, long configured)
    {
        if (configured > 0) return configured;
        string m = model.ToLowerInvariant();
        bool small = m.Contains("haiku-4") || m.Contains("-4-5") || m.Contains("-4-1") || m.Contains("-4-0")
                     || m.Contains("-3-") || m.Contains("claude-3") || m.Contains("opus-4-2") || m.Contains("sonnet-4-2");
        if (m.Contains("[1m]")) small = false;
        long w = small ? 200_000 : 1_000_000;
        return used > w ? 1_000_000 : w;
    }

    public static string Tok(long n) => n >= 1_000_000 ? $"{n / 1e6:0.0#}M" : n >= 1000 ? $"{n / 1000}k" : n.ToString();
}
