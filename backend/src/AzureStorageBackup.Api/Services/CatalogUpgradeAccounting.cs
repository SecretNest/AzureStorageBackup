namespace AzureStorageBackup.Api.Services;

/// <summary>Books a catalog conversion into the "Upgrading catalog" stage: the counts line is versions, the done line
/// is entries (the whole history's declared rows, landed as they go), the item line names the version under
/// conversion. Synchronous, like <see cref="VersionLoadAccounting"/>: a Progress&lt;T&gt; would post to the pool and a
/// conversion that finishes in milliseconds would report after the stage closed.</summary>
internal sealed class CatalogUpgradeAccounting(StageTracker tracker) : IProgress<CatalogUpgradeProgress>
{
    private bool _declared;
    private long _booked;
    private int? _current;

    public void Report(CatalogUpgradeProgress value)
    {
        if (!_declared)
        {
            tracker.DeclareWork(value.RowsTotal);
            _declared = true;
        }
        if (value.RowsDone > _booked)
        {
            tracker.AdvanceWork(value.RowsDone - _booked);
            _booked = value.RowsDone;
        }
        if (_current != value.Version)
        {
            _current = value.Version;
            tracker.Touch($"version {value.Version}");
        }
        if (value.VersionDone)
            tracker.Advance(0, work: 0);
    }
}
