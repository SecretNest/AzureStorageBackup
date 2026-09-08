using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The trigger half of "mark it and leave it to the next backup version": which marked paths a finished backup
/// hands to the deferred repair. The filter is a stat, not a hash — its job is convergence, not verification
/// (the repair's own hash gate verifies): a path whose local length matches its recorded length can heal, so it
/// goes; one whose length differs cannot, so it is skipped silently — otherwise every nightly backup would run a
/// repair that re-marks the same unhealable file and pushes the same notification, forever.
/// </summary>
public sealed class DeferredRepairsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "asb-defer-" + Guid.NewGuid().ToString("N"));

    public DeferredRepairsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private IndexEntry Entry(string path, long length) => new()
    {
        Path = path, Kind = "file", Permissions = "0644", Length = length, FullHash = "h",
        Storage = new StorageRef { Kind = "blob", Ref = "data/" + path },
    };

    [Fact]
    public void Only_Marked_Paths_Whose_Local_Length_Still_Matches_Are_Candidates()
    {
        File.WriteAllText(Path.Combine(_root, "healable.bin"), "0123456789");        // 10 = recorded
        File.WriteAllText(Path.Combine(_root, "appended.bin"), "0123456789ABCDEF"); // grew past 10
        // "gone.bin" does not exist locally at all.

        // What VersionCatalog.EntriesAtAsync(latest.Version, marked, ct) would hand back: only the latest
        // version's entries at the marked paths. "unmarked.bin" was never marked, so the catalog query would
        // never have returned it — it does not appear here either. A path marked but with no entry in the
        // latest version (renamed away, say) likewise never comes back from that query, so there is nothing to
        // represent for it here.
        var markedEntries = new List<IndexEntry> { Entry("healable.bin", 10), Entry("appended.bin", 10), Entry("gone.bin", 10) };

        var candidates = DeferredRepairs.HealCandidates(markedEntries, _root);

        Assert.Equal(["healable.bin"], candidates);
    }

    /// <summary>An entry that escapes the local root is skipped — the same import-oracle reasoning as every other
    /// place a cloud-index path is combined with the local root.</summary>
    [Fact]
    public void An_Escaping_Marked_Path_Is_Never_Statted()
    {
        var markedEntries = new List<IndexEntry> { Entry("../outside.bin", 10) };

        Assert.Empty(DeferredRepairs.HealCandidates(markedEntries, _root));
    }
}
