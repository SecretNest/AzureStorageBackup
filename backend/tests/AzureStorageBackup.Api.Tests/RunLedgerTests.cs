using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using static AzureStorageBackup.Api.Tests.IndexAssert;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The run ledger against the code it replaces. The orchestrator used to build the new version's entries out of the
/// diff plus four path-keyed dictionaries (<c>storageByPath</c>, <c>tailByPath</c>, <c>overrides</c>,
/// <c>postDiffUnreadable</c>), all of which grew with the file count; the ledger keeps the same facts in the run's
/// scratch database and streams the entries back out. The one thing that must not change is the entries themselves,
/// so <see cref="LegacyBuildEntries"/> is the old <c>BackupOrchestrator.BuildEntries</c> copied verbatim, fed exactly
/// the same data, and every row is compared field by field.
/// </summary>
public sealed class RunLedgerTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "asb-runledger-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => CancellationToken.None;

    public RunLedgerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leaked handle must not fail a test that already passed */ }
    }

    private Task<RunWorkDb> OpenAsync(string runId = "run") => new RunWorkDbFactory(_dir).CreateAsync(runId, Ct);

    private static readonly DateTimeOffset Mtime = new(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(8));

    private static ScannedEntry Current(
        string path, long length = 10, EntryKind kind = EntryKind.File, string? target = null) =>
        new(path, kind, length, Mtime, "0644", target);

    private static IndexEntry Previous(
        string path, long length = 10, string? tail = "prev-tail", StorageRef? storage = null,
        DateTimeOffset? unreadableAt = null) =>
        new()
        {
            Path = path, Kind = "file", Length = length, Mtime = Mtime.AddDays(-1), Permissions = "0600",
            HeadHash = "prev-head", TailHash = tail, FullHash = "prev-full", UnreadableAt = unreadableAt,
            Storage = storage ?? new StorageRef { Kind = "blob", Ref = "data/prev", Volumes = 1 },
        };

    private static StorageRef Blob(string reference) =>
        new() { Kind = "blob", Ref = reference, Volumes = 2, Raw = true, VolumeSizes = [7, 3] };

    /// <summary>One seeded change plus whatever the run went on to record about it — the four dictionaries' worth of
    /// facts for one path, so the ledger and the legacy dictionaries are fed from a single source and cannot drift
    /// apart inside the test itself.</summary>
    private sealed record Case(
        FileChange Change,
        StorageRef? Storage = null,
        string? Tail = null,
        EntryOverride? Override = null,
        string? PostDiffUnreadable = null);

    // ---- the cases: one per branch of BuildEntries -------------------------------------------------------------

    private static List<Case> Cases() =>
    [
        // Added, uploaded as a single-file blob this run: storage and the tail hash from the compression pass.
        new(new FileChange("a/added", ChangeKind.Added, Current("a/added"), null, "head-a", "full-a", null),
            Storage: Blob("data/a"), Tail: "tail-a"),

        // Modified, and the content settled to something else while it was being processed: the override wins over
        // every field the diff had recorded.
        new(new FileChange("a/modified", ChangeKind.Modified, Current("a/modified"), Previous("a/modified", 5),
                "head-b", "full-b", null, null, "diff-tail-b"),
            Storage: new StorageRef { Kind = "pack", Ref = "packs/1", EntryName = "a/modified" },
            Override: new EntryOverride("override-full", "override-head", 20, Mtime.AddDays(1))),

        // MetadataOnly: nothing was uploaded, so the previous version's storage is carried and the tail hash falls
        // through the diff (which has none here) to the previous entry's.
        new(new FileChange("a/meta", ChangeKind.MetadataOnly, Current("a/meta", 7),
            Previous("a/meta", 7, tail: "prev-tail-meta"), "head-c", "full-c", Blob("data/carried"))),

        // Unreadable with no Current at all — the whole subtree under an unreadable directory looks like this, and
        // the previous entry has to be carried forward or the files vanish from the new index.
        new(new FileChange("a/unreadable-prev", ChangeKind.Unreadable, null,
            Previous("a/unreadable-prev", 11), null, null, null, "permission denied")),

        // Unreadable again, but the previous entry already carries an UnreadableAt: that answers "since when", and
        // refreshing it every run would erase the answer.
        new(new FileChange("a/unreadable-stale", ChangeKind.Unreadable, Current("a/unreadable-stale"),
            Previous("a/unreadable-stale", unreadableAt: Mtime.AddYears(-1)), null, null, null, "still locked")),

        // Unreadable with nothing to carry: there is no content to point at, so no entry at all.
        new(new FileChange("a/unreadable-new", ChangeKind.Unreadable, Current("a/unreadable-new"), null,
            null, null, null, "vanished")),

        // Deleted: out of the new index, but its previous length is what the run reports as deleted bytes.
        new(new FileChange("a/deleted", ChangeKind.Deleted, null, Previous("a/deleted", 100), null, null, null)),

        // Readable at diff time, unreadable when the upload stage reopened it — after the storage had already been
        // recorded. Treated exactly like a diff-time unreadable: the previous entry, stamped.
        new(new FileChange("a/postdiff", ChangeKind.Modified, Current("a/postdiff"), Previous("a/postdiff", 3),
                "head-d", "full-d", null),
            Storage: Blob("data/d"), Tail: "tail-d", PostDiffUnreadable: "gone mid-run"),

        // …and the same thing with nothing to carry forward.
        new(new FileChange("a/postdiff-new", ChangeKind.Added, Current("a/postdiff-new"), null, "head-e", "full-e",
                null),
            PostDiffUnreadable: "gone mid-run"),

        // A zero-length file never carries a storage reference, even when one was recorded for it.
        new(new FileChange("a/empty", ChangeKind.Added, Current("a/empty", 0), null, "head-f", "full-f", null),
            Storage: Blob("data/f")),

        // Shrunk to empty *during* processing: the judgement is made on the length that finally goes into the
        // index, which is the override's.
        new(new FileChange("a/shrunk", ChangeKind.Modified, Current("a/shrunk", 50), Previous("a/shrunk", 50),
                "head-g", "full-g", null),
            Storage: Blob("data/g"),
            Override: new EntryOverride("full-g2", null, 0, Mtime.AddHours(1))),

        // A symlink is zero-length too, and the empty-file rule must not strip its storage.
        new(new FileChange("a/link", ChangeKind.Added, Current("a/link", 0, EntryKind.Symlink, "../target"), null,
                null, null, null),
            Storage: Blob("data/link")),

        // Unchanged: never read this run, so it keeps the previous version's tail hash and storage.
        new(new FileChange("a/unchanged", ChangeKind.Unchanged, Current("a/unchanged", 12),
            Previous("a/unchanged", 12, tail: "prev-tail-unchanged"), null, null, Blob("data/unchanged"))),

        // Under a different directory, for the per-directory unreadable count.
        new(new FileChange("b/deep/unreadable", ChangeKind.Unreadable, Current("b/deep/unreadable"),
            Previous("b/deep/unreadable", 9), null, null, null, "denied")),
    ];

    /// <summary>Feeds one set of cases to the ledger and to the four dictionaries at the same time, so both sides
    /// see exactly the same run.</summary>
    private static async Task<Legacy> SeedAsync(RunLedger ledger, IReadOnlyList<Case> cases)
    {
        var legacy = new Legacy();
        for (var seq = 0; seq < cases.Count; seq++)
        {
            await ledger.SeedAsync(seq, cases[seq].Change, Ct);
            legacy.Changes.Add(cases[seq].Change);
        }

        foreach (var c in cases)
        {
            var path = c.Change.Path;
            if (c.Storage is not null)
            {
                await ledger.SetStorageAsync(path, c.Storage, Ct);
                legacy.StorageByPath[path] = c.Storage;
            }

            if (c.Tail is not null)
            {
                await ledger.SetTailAsync(path, c.Tail, Ct);
                legacy.TailByPath[path] = c.Tail;
            }

            if (c.Override is not null)
            {
                await ledger.SetOverrideAsync(path, c.Override, Ct);
                legacy.Overrides[path] = c.Override;
            }

            if (c.PostDiffUnreadable is not null)
            {
                await ledger.MarkPostDiffUnreadableAsync(path, c.PostDiffUnreadable, Ct);
                legacy.PostDiffUnreadable[path] = c.PostDiffUnreadable;
            }
        }

        await ledger.FlushAsync(Ct);
        return legacy;
    }

    /// <summary>The run as the orchestrator used to hold it: the diff's changes plus the four dictionaries.</summary>
    private sealed class Legacy
    {
        public List<FileChange> Changes { get; } = [];
        public Dictionary<string, StorageRef> StorageByPath { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> TailByPath { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, EntryOverride> Overrides { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> PostDiffUnreadable { get; } = new(StringComparer.Ordinal);

        public List<IndexEntry> Entries() => LegacyBuildEntries(
            new DiffResult(Changes, 0, 0), StorageByPath, TailByPath, Overrides, PostDiffUnreadable);
    }

    // ---- Test 1: the entries are the ones BuildEntries built ---------------------------------------------------

    [Fact]
    public async Task Final_entries_match_the_dictionaries_row_for_row()
    {
        await using var db = await OpenAsync();
        var ledger = new RunLedger(db);
        var legacy = await SeedAsync(ledger, Cases());

        var expected = legacy.Entries();
        var actual = new List<IndexEntry>();
        await foreach (var entry in ledger.FinalEntriesAsync(Ct))
            actual.Add(entry);

        // Spelled out as well as compared: a branch that silently stopped emitting would otherwise show up only as
        // an off-by-one in a list nobody reads, and both sides could drop the same row together.
        Assert.Equal(
            [
                "a/added", "a/modified", "a/meta", "a/unreadable-prev", "a/unreadable-stale", "a/postdiff",
                "a/empty", "a/shrunk", "a/link", "a/unchanged", "b/deep/unreadable",
            ],
            expected.Select(e => e.Path));
        Assert.Equal(expected.Select(e => e.Path), actual.Select(e => e.Path));

        for (var i = 0; i < expected.Count; i++)
            AssertSame(expected[i], actual[i]);
    }

    // ---- Test 2: the numbers the run reports come off the same rows --------------------------------------------

    [Fact]
    public async Task Counts_and_stats_agree_with_the_entries_and_the_changes()
    {
        await using var db = await OpenAsync();
        var ledger = new RunLedger(db);
        var legacy = await SeedAsync(ledger, Cases());
        var expected = legacy.Entries();

        Assert.Equal(expected.Count, await ledger.FinalEntryCountAsync(Ct));
        Assert.Equal(((long)expected.Count, expected.Sum(e => e.Length)), await ledger.FinalStatsAsync(Ct));

        // The same single pass the orchestrator's summary loop made over diff.Changes.
        var changes = legacy.Changes;
        Assert.Equal(
            (changes.Count(c => c.Kind == ChangeKind.Added),
             changes.Count(c => c.Kind == ChangeKind.Modified),
             changes.Count(c => c.Kind == ChangeKind.Deleted),
             changes.Where(c => c.Kind == ChangeKind.Deleted).Sum(c => c.Previous?.Length ?? 0)),
            await ledger.ChangeCountsAsync(Ct));

        // Unreadable is the diff's verdict; post-diff unreadable is counted on its own, exactly as the run result
        // adds the two together rather than folding one into the other.
        var unreadable = new List<string>();
        await foreach (var path in ledger.UnreadablePathsAsync(Ct))
            unreadable.Add(path);
        Assert.Equal([.. changes.Where(c => c.Kind == ChangeKind.Unreadable).Select(c => c.Path)], unreadable);
        Assert.Equal(2, await ledger.PostDiffUnreadableCountAsync(Ct));

        // What the directory warning reports: strictly under the directory, the same as PathUnder.IsUnder.
        Assert.Equal(1, await ledger.UnreadableUnderAsync("b", Ct));
        Assert.Equal(1, await ledger.UnreadableUnderAsync("b/deep", Ct));
        Assert.Equal(0, await ledger.UnreadableUnderAsync("b/deep/unreadable", Ct));
        Assert.Equal(0, await ledger.UnreadableUnderAsync("c", Ct));
        Assert.Equal(unreadable.Count, await ledger.UnreadableUnderAsync("", Ct));
    }

    // ---- Test 3: the per-path questions the pipeline asks mid-run ----------------------------------------------

    /// <summary>
    /// The alias backfill asks these three about a leader before handing its address to everything that deduped
    /// against it. <c>StorageAsync</c> in particular must answer only for storage <em>this run</em> recorded: the
    /// value carried over from the previous version sits in the same columns, and answering with it would tell the
    /// backfill a leader was uploaded when nothing of the sort happened.
    /// </summary>
    [Fact]
    public async Task Per_path_lookups_see_only_what_the_run_recorded()
    {
        await using var db = await OpenAsync();
        var ledger = new RunLedger(db);
        await SeedAsync(ledger, Cases());

        var storage = await ledger.StorageAsync("a/added", Ct);
        Assert.NotNull(storage);
        Assert.Equal("data/a", storage.Ref);
        Assert.Equal([7, 3], storage.VolumeSizes);
        Assert.Null(await ledger.StorageAsync("a/meta", Ct));        // carried over, not recorded by this run
        Assert.Null(await ledger.StorageAsync("nosuch", Ct));

        Assert.True(await ledger.HasOverrideAsync("a/modified", Ct));
        Assert.True(await ledger.HasOverrideAsync("a/shrunk", Ct));  // an override that only shrinks the file
        Assert.False(await ledger.HasOverrideAsync("a/added", Ct));
        Assert.False(await ledger.HasOverrideAsync("nosuch", Ct));

        Assert.True(await ledger.IsPostDiffUnreadableAsync("a/postdiff", Ct));
        Assert.False(await ledger.IsPostDiffUnreadableAsync("a/added", Ct));
        Assert.False(await ledger.IsPostDiffUnreadableAsync("nosuch", Ct));
    }

    // ---- comparison --------------------------------------------------------------------------------------------

    /// <summary>
    /// Every field exactly, except <see cref="IndexEntry.UnreadableAt"/> on an entry that is being stamped for the
    /// first time: both sides fill it with their own <c>DateTimeOffset.UtcNow</c> (the old code once per entry, the
    /// ledger once per pass), and the index does not depend on the sub-second difference. A minute is far wider than
    /// that gap and far narrower than the year-old stamp the "keep the original" case carries, so a regression there
    /// still fails.
    /// </summary>
    private static void AssertSame(IndexEntry expected, IndexEntry actual)
    {
        if (expected.UnreadableAt is { } want && actual.UnreadableAt is { } got
            && (want - got).Duration() < TimeSpan.FromMinutes(1))
            actual = actual with { UnreadableAt = want };

        AssertSameEntry(expected, actual);
    }

    // ---- the code under replacement ----------------------------------------------------------------------------

    /// <summary>
    /// <c>BackupOrchestrator.BuildEntries</c> as it stood before the ledger, copied verbatim (only its explanatory
    /// comments were left behind, and they are quoted where <see cref="RunLedger"/> now implements them). It is the
    /// oracle for <see cref="RunLedger.FinalEntriesAsync"/>: the ledger changes where the run's facts are kept, and
    /// it may not change what the index says.
    /// </summary>
    private static List<IndexEntry> LegacyBuildEntries(
        DiffResult diff, IReadOnlyDictionary<string, StorageRef> storageByPath,
        IReadOnlyDictionary<string, string> tailByPath,
        IReadOnlyDictionary<string, EntryOverride> overrides,
        IReadOnlyDictionary<string, string> postDiffUnreadable)
    {
        var entries = new List<IndexEntry>();
        foreach (var c in diff.Changes)
        {
            if (c.Kind == ChangeKind.Unreadable || postDiffUnreadable.ContainsKey(c.Path))
            {
                if (c.Previous is not null)
                    entries.Add(c.Previous with { UnreadableAt = c.Previous.UnreadableAt ?? DateTimeOffset.UtcNow });
                continue;
            }

            if (c.Kind == ChangeKind.Deleted || c.Current is null)
                continue;

            var ov = overrides.GetValueOrDefault(c.Path);
            var kind = c.Current.Kind == EntryKind.File ? "file" : "symlink";
            var length = ov?.Length ?? c.Current.Length;
            entries.Add(new IndexEntry
            {
                Path = c.Path,
                Kind = kind,
                Length = length,
                Mtime = ov?.Mtime ?? c.Current.ModifiedAt,
                Permissions = c.Current.Permissions,
                HeadHash = ov?.HeadHash ?? c.HeadHash,
                TailHash = tailByPath.GetValueOrDefault(c.Path) ?? c.TailHash ?? c.Previous?.TailHash,
                FullHash = ov?.FullHash ?? c.FullHash,
                Target = c.Current.Target,
                Storage = kind == "file" && length == 0
                    ? null
                    : storageByPath.GetValueOrDefault(c.Path) ?? c.CarriedStorage,
            });
        }

        return entries;
    }
}
