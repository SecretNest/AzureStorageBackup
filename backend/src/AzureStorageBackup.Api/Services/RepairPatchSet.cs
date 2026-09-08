using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// The repair's pending verdicts, held as <see cref="CatalogPatch"/>es instead of as edits to whole
/// <c>VersionIndex</c> objects in memory.
/// <para>
/// It exists because of the order the repair has to persist in: a rewritten index goes to the cloud FIRST and is
/// only recorded in the catalog once that upload succeeded (see <c>BackupRepairer.PersistChangedAsync</c>). Between
/// the moment a verdict is reached and the moment it is recorded, it lives here — which is also what lets a version
/// be serialized with its not-yet-stored changes applied on the way out.
/// </para>
/// <para>
/// Several operations can land on the same (version, path): a path pre-marked at start of run is cleared again when
/// its object is repaired, and a repaired entry gets both new volume sizes and a clear. They are merged into ONE
/// patch per path — last <c>Unrecoverable</c> wins, the storage rewrite is carried alongside it — because
/// <see cref="VersionCatalog.SerializeVersionAsync"/> indexes the patches by path and a second patch for the same
/// path would simply be dropped (or, worse, win over the first depending on which way the dictionary was built).
/// </para>
/// <para>
/// Patches keep their first-touch order within a version. The version's unrecoverable list is written out in list
/// order and that order is part of the index's bytes, so "the order the marks happened in" has to survive the trip
/// through this class.
/// </para>
/// <para><b>Not thread-safe</b>: the repair reaches its verdicts on one thread, between the parallel stretches.</para>
/// </summary>
public sealed class RepairPatchSet
{
    private readonly Dictionary<int, VersionPatches> _versions = [];

    /// <summary>The versions holding at least one patch — exactly the versions whose index has to be rewritten.</summary>
    public IReadOnlyCollection<int> ChangedVersions => _versions.Keys;

    /// <summary>Rules the path unrecoverable in that version.</summary>
    public void Mark(int version, string path) =>
        Merge(version, path, static p => p with { Unrecoverable = true });

    /// <summary>
    /// Lifts the verdict: the damage this path was marked for turned out to be repaired (by this run or by
    /// something else that healed the object in passing), and a verdict overturned must come off the record.
    /// <para>
    /// A mark this run raised and has not persisted yet is <b>withdrawn</b> rather than turned into a clear: the
    /// catalog never heard of it, so there is nothing there to clear, and a patch saying "clear a mark nobody
    /// holds" would rewrite that version's whole index for a change that cancels itself out. The two cases cannot
    /// be confused, because a pending <c>true</c> only ever exists where the catalog said no — the repairer raises
    /// a mark only for a path that is not already marked.
    /// </para>
    /// </summary>
    public void Clear(int version, string path) =>
        Merge(version, path, static p => p with { Unrecoverable = p.Unrecoverable == true ? null : false });

    /// <summary>Rewrites the entry's storage — a repaired object keeps its content address but is republished with a
    /// new volume count and new volume sizes.</summary>
    public void SetStorage(int version, string path, StorageRef storage) =>
        Merge(version, path, p => p with { Storage = storage });

    /// <summary>The version's patches, in the order they were first raised.</summary>
    public IReadOnlyList<CatalogPatch> PatchesFor(int version)
    {
        if (!_versions.TryGetValue(version, out var patches))
            return [];

        var ordered = new List<CatalogPatch>(patches.ByPath.Count);
        foreach (var path in patches.Order)
            if (patches.ByPath.TryGetValue(path, out var patch))
                ordered.Add(patch);   // a withdrawn path keeps its slot in Order and is skipped here
        return ordered;
    }

    /// <summary>
    /// What this set has already decided about (version, path): <c>true</c> marked, <c>false</c> cleared,
    /// <c>null</c> "no opinion — ask the catalog".
    /// <para>
    /// The repair used to read its own marks straight off the index it was mutating, so "is this path already
    /// marked" answered from one place. Now the truth is split in two — what the catalog holds, plus what this set
    /// has decided since — and a caller that consults only the catalog would re-mark a path this run has already
    /// marked (adding a version to <see cref="ChangedVersions"/> that has nothing new to say), or fail to clear a
    /// mark it raised itself moments earlier.
    /// </para>
    /// </summary>
    public bool? IsMarkedPending(int version, string path) =>
        _versions.TryGetValue(version, out var patches) && patches.ByPath.TryGetValue(path, out var patch)
            ? patch.Unrecoverable
            : null;

    /// <summary>Drops a version's patches once they are in the cloud AND in the catalog. From that moment the
    /// catalog answers for them, and keeping them here would rewrite the same version again at the next persist.</summary>
    public void Forget(int version) => _versions.Remove(version);

    private void Merge(int version, string path, Func<CatalogPatch, CatalogPatch> change)
    {
        if (!_versions.TryGetValue(version, out var patches))
            _versions[version] = patches = new VersionPatches();

        var merged = change(patches.ByPath.TryGetValue(path, out var existing)
            ? existing
            : new CatalogPatch(version, path, UnreadableAt: null, Unrecoverable: null, Storage: null));

        if (merged is { UnreadableAt: null, Unrecoverable: null, Storage: null })
        {
            // Nothing left to say about this path. Withdrawn rather than kept as an empty patch — and with it the
            // version, once it is the last one, so a version whose every change cancelled out is not rewritten.
            patches.ByPath.Remove(path);
            if (patches.ByPath.Count == 0)
                _versions.Remove(version);
            return;
        }

        if (patches.Seen.Add(path))
            patches.Order.Add(path);
        patches.ByPath[path] = merged;
    }

    /// <summary>One version's patches: the list carries the order, the dictionary makes "have I already got one for
    /// this path" a lookup rather than a scan over a repair that can touch millions of paths. <c>Seen</c> keeps the
    /// order list free of duplicates when a withdrawn path is patched again — it keeps its original position.</summary>
    private sealed class VersionPatches
    {
        internal List<string> Order { get; } = [];

        internal HashSet<string> Seen { get; } = new(StringComparer.Ordinal);

        internal Dictionary<string, CatalogPatch> ByPath { get; } = new(StringComparer.Ordinal);
    }
}
