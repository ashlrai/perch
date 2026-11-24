using Perch;
using Xunit;

public class HotkeyTests
{
    [Theory]
    [InlineData("win+alt+n", 8u | 1u, (uint)'N')]
    [InlineData("ctrl+shift+f9", 2u | 4u, 0x78u)]
    [InlineData("ctrl+alt+space", 2u | 1u, 0x20u)]
    [InlineData(" Win + Alt + 7 ", 8u | 1u, (uint)'7')]
    public void Parses(string s, uint mods, uint vk)
    {
        Assert.True(App.ParseHotkey(s.ToLowerInvariant(), out var m, out var v));
        Assert.Equal(mods, m);
        Assert.Equal(vk, v);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("")]
    [InlineData("n")]              // no modifier
    [InlineData("win+alt")]        // no key
    [InlineData("win+alt+banana")]
    [InlineData("ctrl+f25")]
    public void Rejects(string s) => Assert.False(App.ParseHotkey(s, out _, out _));
}

public class FormatTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(65_000, "1:05")]
    [InlineData(3_725_000, "1:02:05")]
    [InlineData(-5, "0:00")]
    public void Clock(long ms, string s) => Assert.Equal(s, Fmt.Clock(ms));

    [Theory]
    [InlineData(2_500, "2.5s")]
    [InlineData(42_000, "42s")]
    [InlineData(125_000, "2m 5s")]
    [InlineData(7_500_000, "2h 5m")]
    public void Durations(long ms, string s) => Assert.Equal(s, Fmt.Dur(ms));

    [Fact]
    public void Jobs_are_one_command_in_one_folder()
    {
        var a = new Run { Cwd = @"C:\Code\App", Cmd = "npm test", Label = "tests" };
        var b = new Run { Cwd = @"c:\code\app", Cmd = "npm test", Label = "other label" };
        var c = new Run { Cwd = @"C:\Code\App", Cmd = "", Label = "npm test" };   // older perch: no command, the label is it
        Assert.Equal(a.Job, b.Job);
        Assert.Equal(a.Job, c.Job);
        Assert.NotEqual(a.Job, new Run { Cwd = @"C:\Code\Other", Cmd = "npm test" }.Job);
    }
}
