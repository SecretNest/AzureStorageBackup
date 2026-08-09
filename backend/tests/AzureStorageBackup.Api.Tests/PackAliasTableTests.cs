using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The table behind within-run, cross-pack dedup of packed members. The criteria match
/// <see cref="LocalDedupResolver.TryFindPackMember"/>: fullHash + length + head + tail, all four strictly
/// equal, and a missing component counts as unequal.
/// </summary>
public sealed class PackAliasTableTests
{
    [Fact]
    public void First_Occurrence_Becomes_Leader_And_Is_Not_An_Alias()
    {
        var table = new PackAliasTable();

        // First occurrence of this content: the caller packs it as usual.
        Assert.False(table.TryClaim("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", "a/x.txt"));
        Assert.Empty(table.AliasesByLeader);
    }

    [Fact]
    public void Second_Occurrence_Of_Same_Content_Becomes_An_Alias_Of_The_First()
    {
        var table = new PackAliasTable();
        table.TryClaim("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", "a/x.txt");

        Assert.True(table.TryClaim("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", "c/z.txt"));

        var (leader, aliases) = Assert.Single(table.AliasesByLeader);
        Assert.Equal("a/x.txt", leader);
        Assert.Equal(["c/z.txt"], aliases.Select(a => a.Path));
    }

    [Fact]
    public void Many_Aliases_All_Hang_On_The_Same_Leader()
    {
        var table = new PackAliasTable();
        table.TryClaim("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", "a/x.txt");
        table.TryClaim("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", "b/y.txt");
        table.TryClaim("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", "c/z.txt");

        var (leader, aliases) = Assert.Single(table.AliasesByLeader);
        Assert.Equal("a/x.txt", leader);
        Assert.Equal(["b/y.txt", "c/z.txt"], aliases.Select(a => a.Path));
    }

    // Vary one of the four components at a time: none of these may be merged. Getting it wrong means
    // the index points at someone else's content and restore hands back wrong data.
    [Theory]
    [InlineData("xxh128:bb", 100L, "xxh128:hh", "xxh128:tt")]  // different fullHash
    [InlineData("xxh128:aa", 101L, "xxh128:hh", "xxh128:tt")]  // different length
    [InlineData("xxh128:aa", 100L, "xxh128:zz", "xxh128:tt")]  // different head
    [InlineData("xxh128:aa", 100L, "xxh128:hh", "xxh128:zz")]  // different tail
    public void Any_Differing_Component_Prevents_Aliasing(
        string full, long length, string head, string tail)
    {
        var table = new PackAliasTable();
        table.TryClaim("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", "a/x.txt");

        Assert.False(table.TryClaim(full, length, head, tail, "c/z.txt"));
        Assert.Empty(table.AliasesByLeader);
    }

    // A missing component means no participation — that is exactly how members without a tail in old
    // indexes are kept out. The cost is only that the content gets stored one more time, and that is
    // the direction we want.
    [Theory]
    [InlineData(null, "xxh128:hh", "xxh128:tt")]
    [InlineData("xxh128:aa", null, "xxh128:tt")]
    [InlineData("xxh128:aa", "xxh128:hh", null)]
    public void A_Missing_Component_Never_Participates(string? full, string? head, string? tail)
    {
        var table = new PackAliasTable();

        // Neither registered as a leader...
        Assert.False(table.TryClaim(full, 100, head, tail, "a/x.txt"));
        // ...nor recognized by a second call that is missing the same component.
        Assert.False(table.TryClaim(full, 100, head, tail, "c/z.txt"));
        Assert.Empty(table.AliasesByLeader);
    }

    [Fact]
    public void A_Leader_Without_Aliases_Does_Not_Occupy_A_List()
    {
        var table = new PackAliasTable();
        table.TryClaim("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", "a/x.txt");
        table.TryClaim("xxh128:bb", 100, "xxh128:hh", "xxh128:tt", "b/y.txt");
        table.TryClaim("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", "c/z.txt");

        // Only leaders that actually have aliases go into this table: a first backup has hundreds of
        // thousands of leaders, and giving each one an empty List wastes tens of MB for nothing.
        Assert.Equal(["a/x.txt"], table.AliasesByLeader.Keys);
    }
}
