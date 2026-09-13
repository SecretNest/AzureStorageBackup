namespace AzureStorageBackup.Api.Services;

/// <summary>Books a catalog conversion into the "Upgrading catalog" stage: the counts line is versions, the done line
/// is entries (the whole history's declared rows, landed as they go), the item line names the version under
/// conversion. Synchronous, like <see cref="VersionLoadAccounting"/>: a Progress&lt;T&gt; would post to the pool and a
/// conversion that finishes in milliseconds would report after the stage closed.
/// <para>
/// Both of the counts line's numbers come from the first reading rather than from the caller or from the readings
/// counted: a conversion resumed after a Suspend converts only the versions left, so a stage told "10 versions" by
/// the info file and left to count <c>VersionDone</c> readings would finish at "6 of 10" beside "entries (100%)".
/// The rows behave by themselves — the store's first reading already carries the resumed history's landed rows.
/// </para>
/// </summary>
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
            tracker.SetTotal(value.VersionsTotal);
            // The versions an earlier attempt converted, booked one by one because Advance is the only way to move
            // the count — at most one call per version in the history, once per run.
            for (var i = 0; i < value.VersionsDone; i++)
                tracker.Advance(0, work: 0);
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
