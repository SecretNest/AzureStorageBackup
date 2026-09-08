using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// How the pack-member lookup picks its answer. When several retained versions each hold a member with the same
/// content, the **reference** takes the oldest one (references pile onto the old pack, where dead-weight compaction
/// is less likely to rewrite it), and the tail must match exactly — all four fields or nothing.
/// </summary>
public class LocalDedupResolverPackTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private static IndexEntry Member(string path, string packId, string? tail) => new()
    {
        Path = path, Kind = "file", Permissions = "0644", Length = 100,
        FullHash = "full-x", HeadHash = "head-x", TailHash = tail,
        Storage = new StorageRef { Kind = "pack", Ref = packId, EntryName = path },
    };

    private static VersionIndex Index(int version, params IndexEntry[] entries) =>
        new() { Version = version, Entries = [.. entries] };

    private static Task<(LocalDedupResolver Resolver, IAsyncDisposable Cleanup)> Build(params VersionIndex[] indexes) =>
        TestResolver.From(new BlobAddressScheme(null, null), indexes, ct: Ct);

    /// <summary>Point at the old pack — that is where compaction is least likely to rewrite it.</summary>
    [Fact]
    public async Task The_Reference_Points_At_The_Oldest_Version()
    {
        var (resolver, cleanup) = await Build(
            Index(1, Member("a.txt", "pOLD", null)),
            Index(2, Member("b.txt", "pNEW", null)));
        await using var _ = cleanup;

        var hit = await resolver.TryFindPackMemberAsync("full-x", 100, "head-x", null, Ct);
        Assert.Equal("pOLD", hit!.PackId);
        Assert.Equal("a.txt", hit.EntryName);
    }

    /// <summary>A mismatched tail is a miss — the four fields are **strictly** equal.</summary>
    [Fact]
    public async Task A_Differing_Tail_Misses()
    {
        var (resolver, cleanup) = await Build(Index(1, Member("a.txt", "p1", "tail-x")));
        await using var _ = cleanup;

        Assert.NotNull(await resolver.TryFindPackMemberAsync("full-x", 100, "head-x", "tail-x", Ct));
        Assert.Null(await resolver.TryFindPackMemberAsync("full-x", 100, "head-x", "tail-DIFFERENT", Ct));
    }

    /// <summary>
    /// **Missing counts as unequal too.** Pack members in old indexes have no tail, so they simply do not take
    /// part in dedup — the price is only that their content gets stored one more time. This was once relaxed to
    /// "only compare when both sides have one"; that is gone: the criterion is either all four fields or it is not,
    /// and opening a compatibility loophole leaves a fuzzy semantic on the question "is this the same content".
    /// </summary>
    [Fact]
    public async Task A_Missing_Tail_On_Either_Side_Also_Misses()
    {
        var (oldIndex, oldCleanup) = await Build(Index(1, Member("a.txt", "p1", null)));
        await using var _ = oldCleanup;
        Assert.Null(await oldIndex.TryFindPackMemberAsync("full-x", 100, "head-x", "tail-x", Ct));   // the old entry has none
        Assert.NotNull(await oldIndex.TryFindPackMemberAsync("full-x", 100, "head-x", null, Ct));    // equal only when both sides have none

        var (newIndex, newCleanup) = await Build(Index(1, Member("a.txt", "p1", "tail-x")));
        await using var __ = newCleanup;
        Assert.Null(await newIndex.TryFindPackMemberAsync("full-x", 100, "head-x", null, Ct));       // the querying side has none
    }

    /// <summary>Any one of the three parts differing must not match.</summary>
    [Fact]
    public async Task Any_Differing_Part_Misses()
    {
        var (resolver, cleanup) = await Build(Index(1, Member("a.txt", "p1", "tail-x")));
        await using var _ = cleanup;

        Assert.Null(await resolver.TryFindPackMemberAsync("full-OTHER", 100, "head-x", "tail-x", Ct));
        Assert.Null(await resolver.TryFindPackMemberAsync("full-x", 999, "head-x", "tail-x", Ct));
        Assert.Null(await resolver.TryFindPackMemberAsync("full-x", 100, "head-OTHER", "tail-x", Ct));
    }
}
