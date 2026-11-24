using Perch;
using Xunit;

public class RulesTests
{
    const string Repo = @"C:\code\my-app";
    const long Now = 1_800_000_000_000;

    static string[] One(string tool, string prefix, long exp = 0, string root = Repo) => [$"{exp}\t{root}\t{tool}\t{prefix}"];

    [Theory]
    [InlineData("npm test", true)]
    [InlineData("npm test -- --watch=false", true)]
    [InlineData("npm testing", false)]            // whole words only
    [InlineData("npm", false)]
    [InlineData("npm test && rm -rf /", false)]    // chained
    [InlineData("npm test; rm -rf /", false)]
    [InlineData("npm test | sh", false)]
    [InlineData("npm test > out.txt", false)]      // redirected
    [InlineData("npm test `whoami`", false)]
    [InlineData("npm test $(whoami)", false)]
    [InlineData("npm test\nrm -rf /", false)]
    public void Bash_prefix_rules_match_whole_commands_only(string cmd, bool allowed) =>
        Assert.Equal(allowed, Rules.Matches(One("Bash", "npm test"), Repo, "Bash", cmd, Now));

    [Fact]
    public void Rules_are_per_repository_and_tool()
    {
        Assert.False(Rules.Matches(One("Bash", "npm test"), @"C:\code\other", "Bash", "npm test", Now));
        Assert.False(Rules.Matches(One("Bash", "npm test"), Repo, "Edit", "npm test", Now));
        Assert.True(Rules.Matches(One("Bash", "npm test", root: Repo + "\\"), Repo, "Bash", "npm test", Now));   // trailing slash
        Assert.True(Rules.Matches(One("Bash", "npm test"), Repo.ToUpperInvariant(), "Bash", "npm test", Now));   // case
    }

    [Fact]
    public void Tool_rules_without_a_prefix_allow_any_input()
    {
        Assert.True(Rules.Matches(One("Edit", ""), Repo, "Edit", @"C:\code\my-app\src\a.ts", Now));
        Assert.False(Rules.Matches(One("Edit", ""), Repo, "", "x", Now));   // no tool: never
    }

    [Fact]
    public void Timed_rules_expire()
    {
        Assert.True(Rules.Matches(One("Read", "", exp: Now + 1000), Repo, "Read", "a.txt", Now));
        Assert.False(Rules.Matches(One("Read", "", exp: Now - 1), Repo, "Read", "a.txt", Now));
    }

    [Fact]
    public void Malformed_lines_are_skipped()
    {
        string[] lines = ["", "garbage", "x\ty\tz\tw", $"0\t{Repo}\tBash"];
        Assert.False(Rules.Matches(lines, Repo, "Bash", "anything", Now));
    }

    [Theory]
    [InlineData("npm test", "npm test")]
    [InlineData("npm run build --prod", "npm run")]
    [InlineData("git status", "git status")]
    [InlineData("cargo test --workspace", "cargo test")]
    [InlineData("ls -la", "ls")]
    [InlineData("make && make install", "make")]
    [InlineData("echo $(whoami)", "echo")]
    [InlineData("   ", "")]
    public void Prefix_takes_the_first_two_words(string cmd, string prefix) => Assert.Equal(prefix, Rules.Prefix(cmd));

    [Fact]
    public void Line_round_trips_through_matching()
    {
        string line = Rules.Line(Repo, "Bash", "npm test", 10, Now);
        Assert.True(Rules.Matches([line], Repo, "Bash", "npm test", Now + 9 * 60_000));
        Assert.False(Rules.Matches([line], Repo, "Bash", "npm test", Now + 11 * 60_000));
        Assert.True(Rules.Matches([Rules.Line(Repo, "Bash", "npm test", 0, Now)], Repo, "Bash", "npm test", long.MaxValue));
    }

    [Fact]
    public void Root_finds_the_repository()
    {
        string dir = Path.Combine(Path.GetTempPath(), "perch-test-" + Guid.NewGuid().ToString("N"));
        string sub = Path.Combine(dir, "src", "deep");
        Directory.CreateDirectory(sub);
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        try
        {
            Assert.Equal(dir, Rules.Root(sub));
            Assert.Equal(dir, Rules.Root(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}
