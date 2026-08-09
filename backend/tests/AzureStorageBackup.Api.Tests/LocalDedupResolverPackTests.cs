using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// How the pack-member dedup map is built. When several retained versions each hold a member with the same
/// content, two things get decided separately: the **reference** takes the oldest one (references pile onto the
/// old pack, where dead-weight compaction is less likely to rewrite it); the **tail hash** takes the strongest
/// value obtainable (old indexes have none at all, and one backup run fills it into the newer version's index —
/// honour only the oldest entry and the filled-in value stays inert until the old version retires, making the
/// match run on three fields for several more rounds for nothing).
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

    /// <summary>Point at the old pack — that is where compaction is least likely to rewrite it.</summary>
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

    /// <summary>A mismatched tail is a miss — the four fields are **strictly** equal.</summary>
    [Fact]
    public void A_Differing_Tail_Misses()
    {
        var resolver = Build(Index(1, Member("a.txt", "p1", "tail-x")));

        Assert.NotNull(resolver.TryFindPackMember("full-x", 100, "head-x", "tail-x"));
        Assert.Null(resolver.TryFindPackMember("full-x", 100, "head-x", "tail-DIFFERENT"));
    }

    /// <summary>
    /// **Missing counts as unequal too.** Pack members in old indexes have no tail, so they simply do not take
    /// part in dedup — the price is only that their content gets stored one more time. This was once relaxed to
    /// "only compare when both sides have one"; that is gone: the criterion is either all four fields or it is not,
    /// and opening a compatibility loophole leaves a fuzzy semantic on the question "is this the same content".
    /// </summary>
    [Fact]
    public void A_Missing_Tail_On_Either_Side_Also_Misses()
    {
        var oldIndex = Build(Index(1, Member("a.txt", "p1", null)));
        Assert.Null(oldIndex.TryFindPackMember("full-x", 100, "head-x", "tail-x"));   // the old entry has none
        Assert.NotNull(oldIndex.TryFindPackMember("full-x", 100, "head-x", null));    // equal only when both sides have none

        var newIndex = Build(Index(1, Member("a.txt", "p1", "tail-x")));
        Assert.Null(newIndex.TryFindPackMember("full-x", 100, "head-x", null));       // the querying side has none
    }

    /// <summary>Any one of the three parts differing must not match.</summary>
    [Fact]
    public void Any_Differing_Part_Misses()
    {
        var resolver = Build(Index(1, Member("a.txt", "p1", "tail-x")));

        Assert.Null(resolver.TryFindPackMember("full-OTHER", 100, "head-x", "tail-x"));
        Assert.Null(resolver.TryFindPackMember("full-x", 999, "head-x", "tail-x"));
        Assert.Null(resolver.TryFindPackMember("full-x", 100, "head-OTHER", "tail-x"));
    }
}
