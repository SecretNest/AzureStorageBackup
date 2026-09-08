using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Books <see cref="IVersionCatalogs.EnsureVersionsAsync"/>'s events into the "Loading versions" stage as source-side
/// workload counted in <b>entries</b>, so that its completion and remaining time extrapolate from rows rather than
/// from versions. Versions differ in size by orders of magnitude (one container's history on the NAS runs from a
/// 395 KB index to a 274 MB one), so "3 of 13 versions in 20 minutes" says nothing about the next ten; the row
/// count does, because an import's cost is per row and, with the content-keyed indexes down, near-constant per row.
/// <para>
/// Each version's share is its info-file file count, declared up front; a hit books its whole share at once, an
/// import books rows as they land and the remainder at its end. A share is never overbooked — a version's entries
/// can outnumber its files (directories, links) — so the sum stays exactly the declared total and the percentage
/// reaches 100 on the last version rather than before it. A history whose info file carries no counts declares
/// nothing and leaves the tracker to its item-count fallback.
/// </para>
/// </summary>
internal sealed class VersionLoadAccounting : IProgress<VersionLoadProgress>
{
    private readonly StageTracker _tracker;
    private readonly Dictionary<int, long> _share;
    private readonly Dictionary<int, long> _booked = new();
    private int? _current;

    public VersionLoadAccounting(StageTracker tracker, IReadOnlyList<BackupVersion> versions)
    {
        _tracker = tracker;
        _share = new Dictionary<int, long>(versions.Count);
        foreach (var version in versions)
        {
            var files = Math.Max(0, version.Stats.Files);
            _share[version.Version] = files;
            tracker.DeclareWork(files);
        }
    }

    public void Report(VersionLoadProgress value)
    {
        var share = _share.GetValueOrDefault(value.Version);
        var booked = _booked.GetValueOrDefault(value.Version);
        switch (value.Event)
        {
            case VersionLoadEvent.Importing:
                // Rows first, label second: each call publishes, and the tracker lets one publish through per
                // 200 ms, so the one that carries the new count has to be the first. The label is set once per
                // version; the publishes that follow carry it.
                var landed = Math.Min(value.Entries, share);
                if (landed > booked)
                {
                    _tracker.AdvanceWork(landed - booked);
                    _booked[value.Version] = landed;
                }

                if (_current != value.Version)
                {
                    _current = value.Version;
                    _tracker.Touch($"version {value.Version}");
                }

                break;
            case VersionLoadEvent.Present:
            case VersionLoadEvent.Imported:
                _tracker.Advance(0, work: Math.Max(0, share - booked));
                _booked[value.Version] = share;
                break;
        }
    }
}
