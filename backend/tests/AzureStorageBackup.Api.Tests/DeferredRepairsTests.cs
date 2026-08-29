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

        var latest = new VersionIndex
        {
            Version = 10,
            Entries = [Entry("healable.bin", 10), Entry("appended.bin", 10), Entry("gone.bin", 10), Entry("unmarked.bin", 10)],
        };
        var marked = new HashSet<string>(StringComparer.Ordinal) { "healable.bin", "appended.bin", "gone.bin", "elsewhere.bin" };

        var candidates = DeferredRepairs.HealCandidates(latest, marked, _root);

        Assert.Equal(["healable.bin"], candidates);
    }

    /// <summary>An entry that escapes the local root is skipped — the same import-oracle reasoning as every other
    /// place a cloud-index path is combined with the local root.</summary>
    [Fact]
    public void An_Escaping_Marked_Path_Is_Never_Statted()
    {
        var latest = new VersionIndex { Version = 10, Entries = [Entry("../outside.bin", 10)] };
        var marked = new HashSet<string>(StringComparer.Ordinal) { "../outside.bin" };

        Assert.Empty(DeferredRepairs.HealCandidates(latest, marked, _root));
    }
}
