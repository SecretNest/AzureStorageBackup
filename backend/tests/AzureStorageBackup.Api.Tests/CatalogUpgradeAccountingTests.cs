using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>The "Upgrading catalog" line counts versions across the whole conversion, not the readings of one run:
/// a conversion resumed after a Suspend iterates only the versions left, and both of the counts line's numbers come
/// off the catalog with the first reading.</summary>
public sealed class CatalogUpgradeAccountingTests
{
    /// <summary>The tracker throttles publishes to one per 200 ms, so each report here is a second apart on a
    /// virtual clock. One reading can move the tracker several times (rows, then the item line, then the version
    /// count) and only the first of those gets through the throttle, so <c>Last</c> carries what the reading
    /// <b>before</b> it left; the stage's own end (<see cref="StageTracker.Complete"/>) publishes unconditionally,
    /// which is where the final line is read.</summary>
    private sealed class Harness
    {
        private long _now;
        private StageProgress? _last;
        public StageTracker Tracker { get; }
        public Harness(int total) => Tracker = new StageTracker("UpgradingCatalog", total, p => _last = p) { Clock = () => _now };
        public StageProgress Last => _last ?? throw new InvalidOperationException("nothing published yet");
        public void Report(CatalogUpgradeAccounting accounting, CatalogUpgradeProgress value)
        {
            _now += 1000;
            accounting.Report(value);
        }
        public StageProgress Final() { _now += 1000; Tracker.Complete(); return Last; }
    }

    [Fact]
    public void A_resumed_conversion_counts_the_versions_an_earlier_attempt_put_in()
    {
        var h = new Harness(10);   // the info file's version count, the denominator until the first reading lands
        var accounting = new CatalogUpgradeAccounting(h.Tracker);

        // Versions 1 and 2 went in before the Suspend; this run starts on 3 with their rows already landed.
        h.Report(accounting, new CatalogUpgradeProgress(3, 200, 500, VersionDone: false, VersionsDone: 2, VersionsTotal: 4));
        Assert.Equal(4, h.Last.Total);      // the catalog's count, not the info file's
        Assert.Equal(500, h.Last.WorkTotal);

        h.Report(accounting, new CatalogUpgradeProgress(3, 350, 500, VersionDone: true, VersionsDone: 3, VersionsTotal: 4));
        Assert.Equal(2, h.Last.Processed);  // the two an earlier attempt converted, booked on the first reading
        Assert.Equal(350, h.Last.WorkDone); // this reading's rows: they are the call that got through the throttle
        Assert.Equal("version 3", h.Last.CurrentItem);

        h.Report(accounting, new CatalogUpgradeProgress(4, 500, 500, VersionDone: true, VersionsDone: 4, VersionsTotal: 4));
        Assert.Equal(3, h.Last.Processed);

        var final = h.Final();
        Assert.Equal(4, final.Processed);   // "4 of 4 versions"…
        Assert.Equal(4, final.Total);
        Assert.Equal(500, final.WorkDone);  // …beside "500 / 500 entries (100%)", which is the whole point
        Assert.Equal(100, final.WorkPercent);
    }

    [Fact]
    public void A_fresh_conversion_books_nothing_before_its_first_version()
    {
        var h = new Harness(0);
        var accounting = new CatalogUpgradeAccounting(h.Tracker);

        h.Report(accounting, new CatalogUpgradeProgress(1, 0, 300, VersionDone: false, VersionsDone: 0, VersionsTotal: 2));
        Assert.Equal(2, h.Last.Total);
        Assert.Equal(0, h.Last.Processed);
        Assert.Equal(0, h.Last.WorkDone);

        // Rows land inside the version, and a repeated reading cannot book them twice.
        h.Report(accounting, new CatalogUpgradeProgress(1, 120, 300, VersionDone: false, VersionsDone: 0, VersionsTotal: 2));
        h.Report(accounting, new CatalogUpgradeProgress(1, 120, 300, VersionDone: false, VersionsDone: 0, VersionsTotal: 2));
        Assert.Equal(120, h.Last.WorkDone);
        Assert.Equal(0, h.Last.Processed);
        Assert.Equal("version 1", h.Last.CurrentItem);

        h.Report(accounting, new CatalogUpgradeProgress(1, 150, 300, VersionDone: true, VersionsDone: 1, VersionsTotal: 2));
        var final = h.Final();
        Assert.Equal(1, final.Processed);
        Assert.Equal(150, final.WorkDone);
    }
}
