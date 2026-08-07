using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 本轮内跨箱打包成员去重的表结构。判据与 <see cref="LocalDedupResolver.TryFindPackMember"/>
/// 一致：fullHash + 长度 + head + tail 四项严格相等，缺失也算不等。
/// </summary>
public sealed class PackAliasTableTests
{
    private static PlannedAlias Alias(string path) => new(path, 100, "xxh128:aa", "xxh128:hh", "xxh128:tt");

    [Fact]
    public void First_Occurrence_Becomes_Leader_And_Is_Not_An_Alias()
    {
        var table = new PackAliasTable();

        // 第一份内容：调用方照旧入箱。
        Assert.False(table.TryClaim("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", Alias("a/x.txt")));
        Assert.Empty(table.AliasesByLeader);
    }

    [Fact]
    public void Second_Occurrence_Of_Same_Content_Becomes_An_Alias_Of_The_First()
    {
        var table = new PackAliasTable();
        table.TryClaim("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", Alias("a/x.txt"));

        Assert.True(table.TryClaim("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", Alias("c/z.txt")));

        var (leader, aliases) = Assert.Single(table.AliasesByLeader);
        Assert.Equal("a/x.txt", leader);
        Assert.Equal(["c/z.txt"], aliases.Select(a => a.Path));
    }

    [Fact]
    public void Many_Aliases_All_Hang_On_The_Same_Leader()
    {
        var table = new PackAliasTable();
        table.TryClaim("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", Alias("a/x.txt"));
        table.TryClaim("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", Alias("b/y.txt"));
        table.TryClaim("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", Alias("c/z.txt"));

        var (leader, aliases) = Assert.Single(table.AliasesByLeader);
        Assert.Equal("a/x.txt", leader);
        Assert.Equal(["b/y.txt", "c/z.txt"], aliases.Select(a => a.Path));
    }

    // 四项各差一项：都不该合并。判错的后果是索引指向别人的内容、还原出错数据。
    [Theory]
    [InlineData("xxh128:bb", 100L, "xxh128:hh", "xxh128:tt")]  // fullHash 不同
    [InlineData("xxh128:aa", 101L, "xxh128:hh", "xxh128:tt")]  // 长度不同
    [InlineData("xxh128:aa", 100L, "xxh128:zz", "xxh128:tt")]  // head 不同
    [InlineData("xxh128:aa", 100L, "xxh128:hh", "xxh128:zz")]  // tail 不同
    public void Any_Differing_Component_Prevents_Aliasing(
        string full, long length, string head, string tail)
    {
        var table = new PackAliasTable();
        table.TryClaim("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", Alias("a/x.txt"));

        Assert.False(table.TryClaim(full, length, head, tail, Alias("c/z.txt")));
        Assert.Empty(table.AliasesByLeader);
    }

    // 缺项即不参与——老索引里那些没有尾部的成员就是这么被挡在外面的，
    // 代价只是那份内容会被再存一次，而这正是我们要的方向。
    [Theory]
    [InlineData(null, "xxh128:hh", "xxh128:tt")]
    [InlineData("xxh128:aa", null, "xxh128:tt")]
    [InlineData("xxh128:aa", "xxh128:hh", null)]
    public void A_Missing_Component_Never_Participates(string? full, string? head, string? tail)
    {
        var table = new PackAliasTable();

        // 既不登记为 leader……
        Assert.False(table.TryClaim(full, 100, head, tail, Alias("a/x.txt")));
        // ……第二次同样缺项的也不会认出它来。
        Assert.False(table.TryClaim(full, 100, head, tail, Alias("c/z.txt")));
        Assert.Empty(table.AliasesByLeader);
    }

    [Fact]
    public void A_Leader_Without_Aliases_Does_Not_Occupy_A_List()
    {
        var table = new PackAliasTable();
        table.TryClaim("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", Alias("a/x.txt"));
        table.TryClaim("xxh128:bb", 100, "xxh128:hh", "xxh128:tt", Alias("b/y.txt"));
        table.TryClaim("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", Alias("c/z.txt"));

        // 只有真有别名的 leader 才进这张表：一次首备有几十万个 leader，
        // 给每个都建一个空 List 是白占几十 MB。
        Assert.Equal(["a/x.txt"], table.AliasesByLeader.Keys);
    }
}
