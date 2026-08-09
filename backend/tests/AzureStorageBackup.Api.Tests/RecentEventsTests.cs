using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Restore's per-file messages used to go into a **single-valued** field where each new one overwrote the last — skip or fail dozens of
/// files and the run ends with only the final message, the rest showing up merely as the FailedFiles number, when "which ones, and why" is what you need to see.
/// </summary>
public sealed class RecentEventsTests
{
    [Fact]
    public void Keeps_Every_Message_Instead_Of_Only_The_Last()
    {
        var events = new RecentEvents();
        events.Add("Failed to restore 'a.txt': permission denied");
        events.Add("Skipped unsafe directory entry: ../evil");
        events.Add("Failed to restore 'b.txt': disk full");

        Assert.Equal(3, events.Snapshot().Count);
        Assert.Contains("a.txt", events.Snapshot()[0]);
        Assert.Contains("b.txt", events.Snapshot()[2]);
    }

    /// <summary>Bounded: messages like these can be of the same order as the file count, and keeping them unbounded trades memory for a log nobody can read to the end.
    /// When it is full the oldest goes — what happened most recently is more likely to bear on the problem at hand.</summary>
    [Fact]
    public void Drops_The_Oldest_Once_Full()
    {
        var events = new RecentEvents(capacity: 3);
        foreach (var i in Enumerable.Range(1, 5))
            events.Add($"event {i}");

        Assert.Equal(["event 3", "event 4", "event 5"], events.Snapshot());
    }

    /// <summary>The snapshot must be a copy: the writer sits on the restore thread, the reader on the HTTP serialization thread.</summary>
    [Fact]
    public void Snapshot_Is_Isolated_From_Later_Writes()
    {
        var events = new RecentEvents();
        events.Add("first");
        var snapshot = events.Snapshot();
        events.Add("second");

        Assert.Single(snapshot);
    }
}
