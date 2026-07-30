using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 打包成员去重映射的建法。多个保留版本各有一条同内容成员时，两件事要分开定：
/// **指向**取最旧那条（引用聚到老包上，不易被死重压实重写），**尾部 hash** 取能拿到的最强值
/// （老索引一项都没有，备份跑一轮就补进新版本的索引——只认最旧那条的话，补上的值要等老版本
/// 退役才生效，判定白白多按三项走好几轮）。
/// </summary>
public class LocalDedupResolverPackTests
{
    private static IndexEntry Member(string path, string packId, string? tail) => new()
    {
        Path = path, Kind = "file", Permissions = "0644", Length = 100,
        FullHash = "full-x", HeadHash = "head-x", TailHash = tail,
        Storage = new StorageRef { Kind = "pack", Ref = packId, EntryName = path },
    };

    private static VersionIndex Index(int version, params IndexEntry[] entries) =>
        new() { Version = version, Entries = [.. entries] };

    private static LocalDedupResolver Build(params VersionIndex[] indexes) =>
        LocalDedupResolver.Build(new BlobAddressScheme(null, null), indexes);

    /// <summary>指向老包——那是它不易被压实重写的地方。</summary>
    [Fact]
    public void The_Reference_Points_At_The_Oldest_Version()
    {
        var resolver = Build(
            Index(1, Member("a.txt", "pOLD", null)),
            Index(2, Member("b.txt", "pNEW", null)));

        var hit = resolver.TryFindPackMember("full-x", 100, "head-x", null);
        Assert.Equal("pOLD", hit!.PackId);
        Assert.Equal("a.txt", hit.EntryName);
    }

    /// <summary>尾部对不上就不命中——四项是**严格**相等。</summary>
    [Fact]
    public void A_Differing_Tail_Misses()
    {
        var resolver = Build(Index(1, Member("a.txt", "p1", "tail-x")));

        Assert.NotNull(resolver.TryFindPackMember("full-x", 100, "head-x", "tail-x"));
        Assert.Null(resolver.TryFindPackMember("full-x", 100, "head-x", "tail-DIFFERENT"));
    }

    /// <summary>
    /// **缺失也算不等**。老索引里的打包成员没有尾部，它们就不参与去重——代价只是那份内容
    /// 被再存一次。曾经放宽成"两边都有才比"，撤掉了：判据要么是四项要么不是，
    /// 为兼容开个口子等于在"这份内容是不是同一份"这个问题上留一档说不清的语义。
    /// </summary>
    [Fact]
    public void A_Missing_Tail_On_Either_Side_Also_Misses()
    {
        var oldIndex = Build(Index(1, Member("a.txt", "p1", null)));
        Assert.Null(oldIndex.TryFindPackMember("full-x", 100, "head-x", "tail-x"));   // 老条目缺
        Assert.NotNull(oldIndex.TryFindPackMember("full-x", 100, "head-x", null));    // 两边都缺才算等

        var newIndex = Build(Index(1, Member("a.txt", "p1", "tail-x")));
        Assert.Null(newIndex.TryFindPackMember("full-x", 100, "head-x", null));       // 来问的缺
    }

    /// <summary>三项里任何一项不同都不该命中。</summary>
    [Fact]
    public void Any_Differing_Part_Misses()
    {
        var resolver = Build(Index(1, Member("a.txt", "p1", "tail-x")));

        Assert.Null(resolver.TryFindPackMember("full-OTHER", 100, "head-x", "tail-x"));
        Assert.Null(resolver.TryFindPackMember("full-x", 999, "head-x", "tail-x"));
        Assert.Null(resolver.TryFindPackMember("full-x", 100, "head-OTHER", "tail-x"));
    }
}
