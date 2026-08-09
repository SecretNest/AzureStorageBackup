using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class IgnoreRuleSetTests
{
    private static IgnoreRuleSet Rules(params string[] patterns) => new(patterns);

    [Fact]
    public void Simple_Extension_Glob_Ignores_Match()
    {
        var r = Rules("*.log");
        Assert.True(r.IsIgnored("a.log"));
        Assert.False(r.IsIgnored("a.txt"));
    }

    [Fact]
    public void Glob_Without_Slash_Matches_At_Any_Depth()
    {
        var r = Rules("*.log");
        Assert.True(r.IsIgnored("sub/dir/a.log"));
    }

    [Fact]
    public void Negation_ReIncludes()
    {
        var r = Rules("*.log", "!keep.log");
        Assert.True(r.IsIgnored("other.log"));
        Assert.False(r.IsIgnored("keep.log"));
    }

    [Fact]
    public void Last_Match_Wins()
    {
        var r = Rules("a.txt", "!a.txt");
        Assert.False(r.IsIgnored("a.txt"));
    }

    [Fact]
    public void Directory_Only_Rule_Matches_Directory()
    {
        var r = Rules("build/");
        Assert.True(r.IsIgnored("build", isDirectory: true));
    }

    [Fact]
    public void Directory_Only_Rule_Does_Not_Match_File()
    {
        var r = Rules("build/");
        Assert.False(r.IsIgnored("build", isDirectory: false));
    }

    [Fact]
    public void Anchored_Leading_Slash_Matches_Only_At_Root()
    {
        var r = Rules("/root.txt");
        Assert.True(r.IsIgnored("root.txt"));
        Assert.False(r.IsIgnored("sub/root.txt"));
    }

    [Fact]
    public void DoubleStar_Matches_Across_Directories()
    {
        var r = Rules("**/temp");
        Assert.True(r.IsIgnored("a/b/temp"));
        Assert.True(r.IsIgnored("temp"));
    }

    [Fact]
    public void Comments_And_Blank_Lines_Ignored()
    {
        var r = Rules("# comment", "", "*.log");
        Assert.True(r.IsIgnored("a.log"));
        Assert.False(r.IsIgnored("comment"));
    }

    [Fact]
    public void Unmatched_Not_Ignored()
    {
        var r = Rules("*.log");
        Assert.False(r.IsIgnored("readme.md"));
    }

    [Fact]
    public void Question_Mark_Matches_Single_Char()
    {
        var r = Rules("file?.txt");
        Assert.True(r.IsIgnored("file1.txt"));
        Assert.False(r.IsIgnored("file10.txt"));
    }

    [Fact]
    public void Path_With_Internal_Slash_Is_Anchored()
    {
        var r = Rules("sub/a.txt");
        Assert.True(r.IsIgnored("sub/a.txt"));
        Assert.False(r.IsIgnored("other/sub/a.txt"));
    }

    [Fact]
    public void Directory_Rule_Matches_Files_Beneath_It()
    {
        var rules = Rules("logs/", "*.iso"); // a directory rule plus a file rule
        Assert.True(rules.MatchesFileOrAncestorDir("logs/app.log"));   // matched via the ancestor directory logs/
        Assert.True(rules.MatchesFileOrAncestorDir("a/logs/b/c.bin")); // matched via a deeper ancestor
        Assert.True(rules.MatchesFileOrAncestorDir("disk.iso"));       // matched directly by the file rule
        Assert.False(rules.MatchesFileOrAncestorDir("src/main.cs"));   // no match
    }
}
