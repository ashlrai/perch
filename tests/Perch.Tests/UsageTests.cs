using Perch;
using Xunit;

/// The transcript reader behind the context meter, token totals, summary and timeline.
public class UsageTests
{
    static string Transcript(params string[] lines)
    {
        string p = Path.Combine(Path.GetTempPath(), "perch-test-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(p, string.Join("\n", lines) + "\n");
        return p;
    }

    static string User(string text, string ts) =>
        "{\"type\":\"user\",\"timestamp\":\"" + ts + "\",\"message\":{\"role\":\"user\",\"content\":\"" + text + "\"}}";
    static string ToolUse(string id, string name, string input, string msgId, int input_tokens, int cacheRead, int output, string ts, bool side = false) =>
        "{\"type\":\"assistant\",\"isSidechain\":" + (side ? "true" : "false") + ",\"timestamp\":\"" + ts + "\",\"message\":{\"id\":\"" + msgId
        + "\",\"model\":\"claude-opus-5-5\",\"content\":[{\"type\":\"tool_use\",\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"input\":" + input
        + "}],\"usage\":{\"input_tokens\":" + input_tokens + ",\"cache_read_input_tokens\":" + cacheRead + ",\"cache_creation_input_tokens\":0,\"output_tokens\":" + output + "}}}";
    static string Result(string id, string ts) =>
        "{\"type\":\"user\",\"timestamp\":\"" + ts + "\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"" + id + "\",\"content\":\"ok\"}]}}";
    static string Reply(string text, string msgId, string ts) =>
        "{\"type\":\"assistant\",\"timestamp\":\"" + ts + "\",\"message\":{\"id\":\"" + msgId + "\",\"model\":\"claude-opus-5-5\",\"content\":[{\"type\":\"text\",\"text\":\"" + text
        + "\"}],\"usage\":{\"input_tokens\":10,\"cache_read_input_tokens\":5000,\"cache_creation_input_tokens\":0,\"output_tokens\":40}}}";

    [Fact]
    public void Context_totals_and_timeline()
    {
        string p = Transcript(
            User("fix the login bug", "2026-11-24T10:00:00.000Z"),
            ToolUse("t1", "Read", """{"file_path":"C:\\code\\app\\src\\login.ts"}""", "m1", 100, 2000, 50, "2026-11-24T10:00:01.000Z"),
            // the same API message written twice (one line per content block): counted once
            ToolUse("t1", "Read", """{"file_path":"C:\\code\\app\\src\\login.ts"}""", "m1", 100, 2000, 50, "2026-11-24T10:00:01.000Z"),
            Result("t1", "2026-11-24T10:00:03.500Z"),
            ToolUse("t2", "Bash", """{"command":"npm test"}""", "m2", 200, 3000, 60, "2026-11-24T10:00:04.000Z"),
            Reply("Fixed the null check in login.ts.", "m3", "2026-11-24T10:00:09.000Z"),
            """{"type":"assistant","timestamp":"2026-11-24T10:00:10.000Z","message":{"id":"partial""");   // a half-written last line
        try
        {
            var s = Usage.ReadOnce(p);
            Assert.NotNull(s);
            Assert.Equal(5010, s!.Context);                                    // the last main-thread request's input
            Assert.Equal((100 + 2000 + 50) + (200 + 3000 + 60) + (10 + 5000 + 40), s.Total);
            Assert.Equal("claude-opus-5-5", s.Model);
            Assert.Collection(s.Events,
                e => { Assert.Equal('p', e.Kind); Assert.Equal("fix the login bug", e.Text); },
                e => { Assert.Equal('t', e.Kind); Assert.Equal("Read  login.ts", e.Text); Assert.Equal(2500, e.Dur); },   // once, with its duration
                e => { Assert.Equal('t', e.Kind); Assert.Equal("Bash  npm test", e.Text); Assert.Equal(-1, e.Dur); },   // still running
                e => { Assert.Equal('r', e.Kind); Assert.StartsWith("Fixed the null check", e.Text); });
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void Subagents_count_towards_tokens_but_not_context_or_timeline()
    {
        string p = Transcript(
            ToolUse("t1", "Read", """{"file_path":"a.txt"}""", "m1", 100, 0, 10, "2026-11-24T10:00:01.000Z"),
            ToolUse("t9", "Grep", """{"pattern":"x"}""", "s1", 900, 0, 90, "2026-11-24T10:00:02.000Z", side: true));
        try
        {
            var s = Usage.ReadOnce(p)!;
            Assert.Equal(100, s.Context);
            Assert.Equal(110 + 990, s.Total);
            Assert.Single(s.Events);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void Command_wrappers_are_not_prompts()
    {
        string p = Transcript(User("<command-name>/clear</command-name>", "2026-11-24T10:00:00.000Z"), User("hello", "2026-11-24T10:00:01.000Z"));
        try { Assert.Equal("hello", Assert.Single(Usage.ReadOnce(p)!.Events).Text); }
        finally { File.Delete(p); }
    }

    [Theory]
    [InlineData("claude-opus-5-5", 0, 1_000_000)]
    [InlineData("claude-sonnet-4-5-20250929", 0, 200_000)]
    [InlineData("claude-haiku-4-5", 0, 200_000)]
    [InlineData("claude-sonnet-4-5", 300_000, 1_000_000)]   // more than 200K used: it must be the 1M window
    [InlineData("claude-fable-5-2", 0, 1_000_000)]
    public void Context_window_by_model(string model, long used, long window) => Assert.Equal(window, Usage.Window(model, used, 0));

    [Fact]
    public void Configured_context_window_wins() => Assert.Equal(123_000, Usage.Window("claude-opus-5-5", 0, 123_000));

    [Theory]
    [InlineData(999, "999")]
    [InlineData(45_300, "45k")]
    [InlineData(1_250_000, "1.25M")]
    [InlineData(23_460_000, "23.5M")]
    [InlineData(230_690_000, "231M")]
    public void Token_counts_are_short(long n, string s) => Assert.Equal(s, Usage.Tok(n));

    [Fact]
    public void A_byte_order_mark_does_not_cost_the_first_line()
    {
        string p = Path.Combine(Path.GetTempPath(), "perch-test-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(p, User("hello", "2026-11-24T10:00:00.000Z") + "\n", new System.Text.UTF8Encoding(true));
        try { Assert.Equal("hello", Assert.Single(Usage.ReadOnce(p)!.Events).Text); }
        finally { File.Delete(p); }
    }
}
