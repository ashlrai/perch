using System.Text.Json;

namespace NotchBar;

/// Token use of a Claude Code session, read from its transcript (.jsonl) incrementally on a worker thread:
/// only the bytes appended since the last read are parsed. Context = the last main-thread request's
/// input (incl. cache reads/writes); Total = every unique API message's input + output across the session.
static class Usage
{
    public sealed record Snap(long Context, long Total, string Model);

    sealed class Track
    {
        public string Path = "";
        public long Offset, Total, Context, LastRead;
        public string Model = "";
        public readonly HashSet<string> Seen = new();
        public int Busy;
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
        if (fs.Length < t.Offset) { t.Offset = 0; t.Total = 0; t.Context = 0; t.Seen.Clear(); }   // rewritten
        if (fs.Length == t.Offset) return false;
        fs.Position = t.Offset;
        var buf = new byte[fs.Length - t.Offset];
        int n = 0;
        while (n < buf.Length) { int r = fs.Read(buf, n, buf.Length - n); if (r <= 0) break; n += r; }
        int start = 0;
        var span = buf.AsSpan(0, n);
        ReadOnlySpan<byte> usageKey = "\"usage\""u8;
        while (true)
        {
            int nl = span[start..].IndexOf((byte)'\n');
            if (nl < 0) break;   // a partial last line: read it next time
            var line = span.Slice(start, nl);
            start += nl + 1;
            if (line.IndexOf(usageKey) >= 0) Line(t, line);
        }
        t.Offset += start;
        var s = new Snap(t.Context, t.Total, t.Model);
        if (t.Snap == s) return false;
        t.Snap = s;
        return true;
    }

    static void Line(Track t, ReadOnlySpan<byte> line)
    {
        try
        {
            using var d = JsonDocument.Parse(line.ToArray());
            var r = d.RootElement;
            if (!r.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object) return;
            if (!m.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return;
            long N(string k) => u.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
            long input = N("input_tokens") + N("cache_creation_input_tokens") + N("cache_read_input_tokens"), output = N("output_tokens");
            // one API response is written as several lines (one per content block) with the same id and usage
            string id = m.TryGetProperty("id", out var idv) && idv.ValueKind == JsonValueKind.String ? idv.GetString() ?? "" : "";
            if (id.Length == 0 || t.Seen.Add(id)) t.Total += input + output;
            bool side = r.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True;
            if (!side && input > 0)
            {
                t.Context = input;
                if (m.TryGetProperty("model", out var mv) && mv.ValueKind == JsonValueKind.String) t.Model = mv.GetString() ?? "";
            }
        }
        catch { }
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
