using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>The "Loading versions" line counts entries, not versions: its workload is the sum of the versions'
/// file counts, a hit books its share whole, an import books rows as they land and never past its share.</summary>
public sealed class VersionLoadAccountingTests
{
    private static BackupVersion Version(int version, long files) => new()
    {
        Version = version,
        IndexBlob = $"idx/{version}",
        IndexVolumes = 1,
        CreatedAt = DateTimeOffset.UtcNow,
        Stats = new VersionStats(files, 0, 0, 0),
    };

    /// <summary>The tracker throttles publishes to one per 200 ms, so each report here is a second apart on a
    /// virtual clock and <c>Last</c> always reflects the report just made.</summary>
    private sealed class Harness
    {
        private long _now;
        private StageProgress? _last;
        public StageTracker Tracker { get; }
        public Harness(int total) => Tracker = new StageTracker("LoadingVersions", total, p => _last = p) { Clock = () => _now };
        public StageProgress Last => _last ?? throw new InvalidOperationException("nothing published yet");
        public void Report(VersionLoadAccounting accounting, VersionLoadProgress value)
        {
            _now += 1000;
            accounting.Report(value);
        }
    }

    [Fact]
    public void Declares_the_file_counts_as_the_workload_and_books_a_hit_whole()
    {
        var h = new Harness(2);
        var accounting = new VersionLoadAccounting(h.Tracker, [Version(1, 100), Version(2, 900)]);

        h.Report(accounting, new VersionLoadProgress(2, VersionLoadEvent.Present, 0));

        Assert.Equal(1000, h.Last.WorkTotal);
        Assert.Equal(900, h.Last.WorkDone);
        Assert.Equal(1, h.Last.Processed);
        Assert.Equal(0, h.Last.Queued);   // declared, not queued: nothing waits in this stage
    }

    [Fact]
    public void An_import_books_rows_as_they_land_and_its_end_books_the_remainder()
    {
        var h = new Harness(1);
        var accounting = new VersionLoadAccounting(h.Tracker, [Version(7, 50_000)]);

        h.Report(accounting, new VersionLoadProgress(7, VersionLoadEvent.Importing, 10_000));
        Assert.Equal(10_000, h.Last.WorkDone);
        Assert.Equal(0, h.Last.Processed);

        h.Report(accounting, new VersionLoadProgress(7, VersionLoadEvent.Importing, 30_000));
        Assert.Equal(30_000, h.Last.WorkDone);
        Assert.Equal("version 7", h.Last.CurrentItem);   // the label set on the first heartbeat rides every publish after it

        h.Report(accounting, new VersionLoadProgress(7, VersionLoadEvent.Imported, 50_000));
        Assert.Equal(50_000, h.Last.WorkDone);
        Assert.Equal(1, h.Last.Processed);
        Assert.Equal(100, h.Last.WorkPercent);
    }

    [Fact]
    public void A_version_with_more_entries_than_files_never_overbooks_its_share()
    {
        var h = new Harness(2);
        var accounting = new VersionLoadAccounting(h.Tracker, [Version(1, 100), Version(2, 100)]);

        h.Report(accounting, new VersionLoadProgress(1, VersionLoadEvent.Importing, 150));   // directories and links on top of the files
        Assert.Equal(100, h.Last.WorkDone);
        h.Report(accounting, new VersionLoadProgress(1, VersionLoadEvent.Imported, 160));
        Assert.Equal(100, h.Last.WorkDone);
        Assert.Equal(50, h.Last.WorkPercent);   // the second version's share is still ahead
    }

    [Fact]
    public void A_history_without_counts_declares_nothing_and_still_counts_versions()
    {
        var h = new Harness(2);
        var accounting = new VersionLoadAccounting(h.Tracker, [Version(1, 0), Version(2, 0)]);

        h.Report(accounting, new VersionLoadProgress(1, VersionLoadEvent.Present, 0));

        Assert.Equal(0, h.Last.WorkTotal);
        Assert.Null(h.Last.WorkPercent);
        Assert.Equal(50, h.Last.Percent);
    }
}
