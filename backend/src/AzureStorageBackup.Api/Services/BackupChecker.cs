using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Backup integrity check (M5, PRD 2.3), tiered and two-axis:
/// the cloud axis <see cref="CloudCheckLevel"/> (skip / metadata comparison / existence+size / download and rehash);
/// the local axis <see cref="LocalCheckLevel"/> (skip / existence+size+permissions / content hash).
/// "Local content matches" (repairable) is the criterion for repair; the result gives a cloud/local state per file,
/// which feeds repair and restore-time substitution.
/// </summary>
public sealed class BackupChecker(
    IBlobClientFactory factory,
    IBackupInfoStore store,
    IFileCompressor? compressor = null,
    IFileHasher? hasher = null,
    string? tempRoot = null,
    INotifier? notifier = null,
    IOperationLog? opLog = null,
    TrackedInfoStore? trackedInfo = null,
    BackupJournalStore? journals = null)
{
    /// <summary>Test-injected millisecond time source, handed through verbatim to every
    /// <see cref="StageTracker"/> built internally via <see cref="Track"/> (see the comment on the field of the same
    /// name there; it is a mirror of <see cref="RestoreOrchestrator.Clock"/>). Null in production, which uses the
    /// real wall clock. It exists so timing assertions like "the in-flight marker comes off the moment the download
    /// ends, and extraction / hashing no longer counts as in flight" can escape the 200ms throttle window — once
    /// injected, every time query is guaranteed to move forward, so throttling never takes effect and every state
    /// change gets published; the assertion no longer has to gamble on whether the real clock happened to cross a
    /// throttle window.</summary>
    internal Func<long>? Clock { get; init; }

    /// <param name="onProgress">
    /// Stage progress callback (nullable). A check used to have no progress at all: the content level downloads the
    /// whole backup and rehashes it, running for hours is normal, and yet the UI showed nothing but a spinner — you
    /// could not tell "still checking" from "wedged".
    /// </param>
    /// <param name="sentinelPath">
    /// This backup's sentinel (<see cref="SentinelGate"/>), or null when it has none — in which case
    /// <paramref name="localRoot"/> stands in for one. When the probed path is absent, the **local axis only**
    /// is demoted to <see cref="LocalCheckLevel.None"/>: the source is not there to
    /// compare against, so every entry would come back <see cref="LocalState.Missing"/> — the same false alarm the
    /// backup gate exists to prevent, rendered as a failed check instead of a version with everything deleted. The
    /// cloud axis is untouched, because the cloud copy is still there and still worth verifying.
    /// <para>
    /// It sits next to <paramref name="localRoot"/> rather than at the end of the parameter list on purpose: the
    /// two describe one thing (the source, and whether to believe in it), and putting it here makes every existing
    /// call site — which passed <c>localRoot, ct</c> positionally — a compile error that has to be answered
    /// deliberately, instead of a silent default that a caller can forget.
    /// </para>
    /// </param>
    public async Task<CheckReport> CheckAsync(
        Account account, string container, string? password, int? version, CheckOptions options, string? localRoot = null,
        string? sentinelPath = null,
        CancellationToken ct = default, int downloadConcurrency = 5, Action<StageProgress>? onProgress = null,
        int? headConcurrency = null)
    {
        var source = $"check:{account.Id}/{container}";
        // Demoted before anything is recorded, so the "what was this asked to do" line below states what the run
        // will actually do rather than what was requested. A log that says `local Content` for a run whose local
        // axis never executed is worse than no log: it is the exact question this line exists to answer.
        var localSkipped = SentinelGate.Missing(sentinelPath, localRoot);
        if (localSkipped is not null)
            options = options with { Local = LocalCheckLevel.None };
        // What this run was actually asked to do. The two axes and the orphan scan are each independently
        // switchable, and every one of them changes what a later "passed" is worth — a pass at Cloud=None/Local=None
        // establishes almost nothing. Without them recorded, the log cannot answer "what did that check cover?",
        // which is the first question anyone asks of it.
        await Record(
            NotificationEvents.CheckStart, source, $"Check started: {container}",
            DescribeLevels(options)
                + (options.ListOrphans ? "; scanning for unreferenced blobs" : "")
                + SkipNote(localSkipped), ct);
        try
        {
            var report = await CheckCoreAsync(
                account, container, password, version, options, localRoot, downloadConcurrency,
                headConcurrency ?? downloadConcurrency, onProgress, ct);
            if (localSkipped is not null)
                report = report with { LocalSkippedSentinel = localSkipped };
            // The orphan scan is a separate axis from Ok (orphans are not corruption, so they never fail a check),
            // and it therefore has to be stated separately — otherwise a run that turned up tens of thousands of
            // reclaimable blobs is logged as a bare "passed" and the finding is lost.
            var orphanNote = report.OrphanScanIssue is { } issue
                ? $"; unreferenced-blob scan abandoned: {issue}"
                : report.OrphansChecked ? $"; {report.OrphanBlobs.Count} unreferenced blob(s)" : "";
            await Record(
                report.Ok ? NotificationEvents.CheckSuccess : NotificationEvents.CheckFailure, source,
                $"Check {(report.Ok ? "passed" : "failed")}: {container}",
                (report.Ok
                    ? $"{report.Findings.Count} file(s) OK"
                    : ProblemsSummary(report))
                + orphanNote
                // Repeated on the closing line and not only on the opening one, because these two lines are read
                // in completely different circumstances: the opening one scrolls away, and the closing one is what
                // a notification carries and what anyone auditing "did this backup verify?" months later reads.
                // Without it, a cloud-only pass is written down as an unqualified "Check passed".
                + SkipNote(localSkipped), ct);
            return report;
        }
        catch (Exception ex)
        {
            await Record(NotificationEvents.CheckFailure, source, $"Check failed: {container}", ex.Message, ct);
            throw;
        }
    }

    /// <summary>The one wording for "the local axis was demoted", so the opening and closing lines cannot describe
    /// the same event differently. Empty when nothing was demoted.</summary>
    private static string SkipNote(string? localSkipped) =>
        localSkipped is null ? "" : $"; local check skipped: sentinel '{localSkipped}' does not exist";

    /// <summary>The failing check's closing line. Repairability is only a verdict where the local content was
    /// actually hashed; a problem whose local side was never checked must read as "not assessed", never as "not
    /// repairable" — the latter sends the user away from the repair that would have hashed exactly the affected
    /// files and fixed the recoverable ones.</summary>
    internal static string ProblemsSummary(CheckReport report)
    {
        var problems = report.Findings.Where(f => f.Cloud == CloudState.MissingOrBad).ToList();
        var unassessed = problems.Count(f => f.Local == LocalState.NotChecked);
        var repairability = unassessed == problems.Count && problems.Count > 0
            ? "local repairability not assessed — run repair to hash just the affected files"
            : $"{report.RepairablePaths.Count} repairable from local"
              + (unassessed > 0 ? $", {unassessed} not assessed" : "");
        return $"{problems.Count} problem(s), {repairability}";
    }

    /// <summary>The two check levels in plain words for the start notification: this line lands in a push message,
    /// and enum identifiers such as "ExistenceSize" are code, not prose.</summary>
    internal static string DescribeLevels(CheckOptions options)
    {
        var cloud = options.Cloud switch
        {
            CloudCheckLevel.None => "cloud skipped",
            CloudCheckLevel.Metadata => "cloud metadata only",
            CloudCheckLevel.ExistenceSize => "cloud existence and size",
            CloudCheckLevel.Content => "cloud content (download and rehash)",
            _ => $"cloud {options.Cloud}",
        };
        var local = options.Local switch
        {
            LocalCheckLevel.None => "local skipped",
            LocalCheckLevel.Attributes => "local existence, size and permissions",
            LocalCheckLevel.Content => "local content hash",
            _ => $"local {options.Local}",
        };
        return cloud + "; " + local;
    }

    private async Task Record(NotificationEvents evt, string source, string title, string body, CancellationToken ct)
    {
        if (opLog is not null)
            // The separator joins two things; with only one of them present it is punctuation hanging off the end of
            // the line, and a reader who sees "Check started: public — " goes looking for the part that got cut off.
            await opLog.AppendAsync(
                EventLog.LevelOf(evt), source,
                string.IsNullOrWhiteSpace(body) ? title : $"{title} — {body}", ct, durable: true);
        if (notifier is not null)
            await notifier.NotifyAsync(evt, title, body, ct);
    }

    /// <summary>Shortcut for building a stage tracker: when nobody asked for progress, null flows all the way through and costs nothing.</summary>
    /// <param name="inFlight">Whether this stage registers in-flight items. Only the ones that do (Verifying) let the
    /// speed clock start and stop with the stream; the ones that do not (local / listing / metadata) must use the wall
    /// clock, otherwise the virtual clock never advances and the speed stays at 0 forever.</param>
    private StageTracker? Track(
        Action<StageProgress>? onProgress, string stage, int total, bool inFlight = false) =>
        onProgress is null ? null : new StageTracker(stage, total, onProgress, inFlight) { Clock = Clock };

    private async Task<CheckReport> CheckCoreAsync(
        Account account, string container, string? password, int? version, CheckOptions options, string? localRoot,
        int downloadConcurrency, int headConcurrency, Action<StageProgress>? onProgress, CancellationToken ct)
    {
        // How many entries the index holds is only known once it has been read through → report a total of 0, so
        // the UI shows "… so far" instead of a made-up percentage.
        var loading = Track(onProgress, "LoadingIndex", 0);
        loading?.Touch(container);

        var info = await store.ReadInfoAsync(account, container, password, ct)
            ?? throw new InvalidOperationException("No backup found in container.");
        if (info.Versions.Count == 0)
            throw new InvalidOperationException("Backup has no versions.");

        var ver = version is { } v
            ? info.Versions.FirstOrDefault(x => x.Version == v)
              ?? throw new InvalidOperationException($"Version {v} not found.")
            : info.Versions[^1];

        var index = await store.ReadIndexAsync(account, container, ver.IndexBlob, password, ver.IndexVolumes, ct);
        loading?.Advance(0);
        loading?.Complete();

        string? metaIssue = null;
        if (options.Cloud == CloudCheckLevel.Metadata)
        {
            var meta = Track(onProgress, "Metadata", 1);
            metaIssue = await CheckMetadataDriftAsync(account, container, password, info, ct);
            meta?.Advance(0);
            meta?.Complete();
        }

        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(container);

        // Cloud state (per file): data blobs are only actually queried at the ExistenceSize/Content levels.
        var cloudBad = new HashSet<string>(StringComparer.Ordinal);
        if (options.Cloud >= CloudCheckLevel.ExistenceSize)
            cloudBad = await CloudCheckAsync(cc, info, index, options, password, downloadConcurrency, headConcurrency, onProgress, ct);

        // Local axis: compare each entry against its source file. The Content level reads every file end to end to
        // hash it — as slow as the backup's Diffing stage, so it likewise has to report progress entry by entry.
        var localTracker = Track(onProgress, "Local", index.Entries.Count);
        var findings = new List<FileFinding>(index.Entries.Count);
        foreach (var e in index.Entries)
        {
            localTracker?.Touch(e.Path);
            var refName = e.Storage is { } s ? BlobNameOf(s) : null;
            // A zero-length regular file **is not supposed to have** a cloud object at all (the backup side never
            // produces a storage ref for it, see BackupOrchestrator.IsEmptyFile). Reporting NotChecked would make a
            // whole column of empty files look like the check skipped them; their cloud state is settled — it is fine.
            var cloud = e.Storage is null && e.Kind == "file" && e.Length == 0
                ? CloudState.Ok
                : options.Cloud < CloudCheckLevel.ExistenceSize || e.Storage is null
                    ? CloudState.NotChecked
                    : cloudBad.Contains(e.Path) ? CloudState.MissingOrBad : CloudState.Ok;
            var local = await LocalCheckAsync(e, localRoot, options.Local, ct);
            findings.Add(new FileFinding(e.Path, refName, cloud, local) { UnreadableAt = e.UnreadableAt });
            // Only count bytes when the file was really read, or the Attributes/None levels report an astronomical "speed".
            localTracker?.Advance(options.Local == LocalCheckLevel.Content ? e.Length : 0);
        }
        localTracker?.Complete();

        var (orphans, orphanIssue) = options.ListOrphans
            ? await ListOrphansAsync(cc, account, container, password, info, onProgress, ct)
            : ([], null);

        return new CheckReport(ver.Version, findings, metaIssue)
        {
            OrphanBlobs = orphans,
            // Whether it ran, carried on the report rather than left for the caller to infer from an empty list —
            // see CheckReport.OrphansChecked. Abandoned counts as not run, and the reason travels with it.
            OrphansChecked = options.ListOrphans && orphanIssue is null,
            OrphanScanIssue = orphanIssue,
        };
    }

    /// <summary>
    /// Cloud listing check (§4.8): every blob in the container minus the reference set = the orphans. If the
    /// **complete** reference set cannot be built (a version index is missing and the cloud read fails) → give up on
    /// listing, log a Warning, return empty (never call a referenced blob an orphan on incomplete information).
    /// </summary>
    /// <returns>The orphan names, and — when the scan was abandoned — why. The two never both carry content.</returns>
    private async Task<(IReadOnlyList<string> Orphans, string? Issue)> ListOrphansAsync(
        BlobContainerClient cc, Account account, string container, string? password, BackupInfoFile info,
        Action<StageProgress>? onProgress, CancellationToken ct)
    {
        HashSet<string> referenced;
        // Active journals are the other "don't call me an orphan" list, exactly as in the retention sweep: a
        // suspended run's uploads are in the cloud but in no version index, and only the journal records that
        // they exist. Reporting them reclaimable — and repair then deleting them — makes the eventual resume
        // re-upload everything it had already sent. Empty when no journal store is wired (older call sites).
        var active = journals is null
            ? ActiveJournalRefs.Empty
            : await journals.LoadActiveRefsAsync(account.Id, container, ct);
        try
        {
            referenced = await BuildReferencedSetAsync(account, container, password, info, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var issue = $"could not build the full reference set ({ex.Message})";
            if (opLog is not null)
                await opLog.AppendAsync(OperationLogLevel.Warning, $"check:{account.Id}/{container}",
                    $"Orphan detection skipped: {issue}.", ct, durable: true);
            // Returned as well as logged: an operator who ticked the box is owed an answer on the screen they
            // ticked it on, and "no orphans" is not that answer — it is a different claim entirely.
            return ([], issue);
        }

        // Named for what it does, not for what it is looking for. The counter below ticks once per blob **listed**,
        // orphan or not, so a stage called Orphans put a five-figure container size on screen under a heading that
        // reads as a five-figure orphan count. The number is right and its unit is right; only the name lied.
        //
        // How many blobs the container holds is only learned while listing → total 0, report "how many listed so far".
        var listing = Track(onProgress, "Listing", 0);
        var orphans = new List<string>();
        await foreach (var b in cc.GetBlobsAsync(cancellationToken: ct))
        {
            listing?.Touch(b.Name);
            if (!referenced.Contains(b.Name) && !JournalProtected(b.Name, active))
                orphans.Add(b.Name);
            listing?.Advance(0);
        }
        listing?.Complete();
        return (orphans, null);
    }

    /// <summary>Whether this blob name is held by an active journal. The journal records base refs (a data blob's
    /// base name, a pack's id) without volume counts, so the listed name is normalized back to its base the same
    /// way the retention sweep normalizes: a pack name is cut at ".7z" whatever the suffix width, and a data
    /// volume sheds its all-digit suffix (three digits is the uploader's padding, not its width).</summary>
    internal static bool JournalProtected(string name, ActiveJournalRefs active)
    {
        if (name.StartsWith("packs/", StringComparison.Ordinal))
        {
            var rest = name["packs/".Length..];
            var cut = rest.IndexOf(".7z", StringComparison.Ordinal);
            return active.Packs.Contains(cut >= 0 ? rest[..cut] : rest);
        }
        return active.Blobs.Contains(RetentionCleaner.BaseRef(name));
    }

    /// <summary>
    /// Build the set of blob names referenced by every retained version: read the second-level index of every version
    /// (through the local-authoritative store), then call the pure function <see cref="ReferencedBlobNames"/>. If any
    /// version index cannot be read (missing locally and the cloud read fails) this throws — which is the caller's cue
    /// to give up on deleting.
    /// </summary>
    public async Task<HashSet<string>> BuildReferencedSetAsync(
        Account account, string container, string? password, BackupInfoFile info, CancellationToken ct = default)
    {
        var indexes = new Dictionary<int, VersionIndex>();
        foreach (var ver in info.Versions)
            indexes[ver.Version] = await store.ReadIndexAsync(account, container, ver.IndexBlob, password, ver.IndexVolumes, ct);
        return ReferencedBlobNames(info, indexes);
    }

    /// <summary>
    /// **Pure function**: given the info file + every retained version index, return every referenced blob name (the
    /// load-bearing safety basis for deleting orphans). Covered: the info file (both the plaintext and the encrypted
    /// name are protected); each version's <c>IndexBlob</c>; **every volume** of each <see cref="StorageRef"/>
    /// (single-file blobs via <see cref="StorageRef.Volumes"/>, packs via <see cref="PackInfo.Volumes"/>) — across all
    /// versions, including the ones only an older version references. A pack that is referenced but has no metadata in
    /// <c>info.Packs</c> → its volume count cannot be determined → throw (forcing the caller to give up on deleting).
    /// </summary>
    public static HashSet<string> ReferencedBlobNames(BackupInfoFile info, IReadOnlyDictionary<int, VersionIndex> indexes)
    {
        var refs = new HashSet<string>(StringComparer.Ordinal)
        {
            // The info file: both names go into the reference set, so under no circumstances is it deleted as an orphan.
            BackupDiscovery.IndexBlobName,
            BackupDiscovery.EncryptedIndexBlobName,
        };

        // The second-level index blob of every version (its name must be protected even when that version's index was not supplied in indexes).
        // Every volume of it, not just the base name: a split index whose .002 onwards were left out of this set
        // would have them swept as orphans, and the version would stop being readable at all.
        foreach (var v in info.Versions)
            foreach (var n in VolumeBlobIO.VolumeNames(v.IndexBlob, v.IndexVolumes))
                refs.Add(n);

        // Every volume of every storage ref of every version index.
        foreach (var idx in indexes.Values)
            foreach (var e in idx.Entries)
            {
                if (e.Storage is not { } s)
                    continue;
                var baseName = BlobNameOf(s);
                var volumes = s.Kind == "pack"
                    ? info.Packs.TryGetValue(s.Ref, out var pi)
                        ? pi.Volumes
                        : throw new InvalidOperationException(
                            $"Pack '{s.Ref}' is referenced but missing from info.Packs; cannot determine its volumes.")
                    : s.Volumes;
                foreach (var name in VolumeBlobIO.VolumeNames(baseName, volumes))
                    refs.Add(name);
            }

        return refs;
    }

    /// <summary>
    /// Cloud data check; returns the **set of file paths that are bad in the cloud**. ExistenceSize: HEAD every
    /// blob/volume to verify existence + size. Content: on top of that, download every readable blob and recompute its
    /// hash (a blob still in Archive without rehydration is skipped, not mistaken for corruption).
    /// </summary>
    private async Task<HashSet<string>> CloudCheckAsync(
        BlobContainerClient cc, BackupInfoFile info, VersionIndex index, CheckOptions options, string? password,
        int downloadConcurrency, int headConcurrency, Action<StageProgress>? onProgress, CancellationToken ct)
    {
        var bad = new HashSet<string>(StringComparer.Ordinal);

        // Group by blob (blobName → the entries in that blob + the expected volume count/sizes).
        var groups = index.Entries
            .Where(e => e.Storage is not null)
            .GroupBy(e => BlobNameOf(e.Storage!))
            .ToList();

        // What gets counted are **volumes** (probes), not objects and not files: a probe is the stage's unit of
        // real work, and a thousand-volume object counted as one tick freezes the bar for minutes while a run of
        // single-volume packs then races it forward. The UI unit is labelled volumes to match.
        //
        // All objects go to the prober as one worklist: the head budget spans the whole stage, so a container of
        // thousands of single-volume packs advances at budget×(1/RTT) objects a second, not one per round-trip.
        var presentGroups = new List<IGrouping<string, IndexEntry>>();
        var families = groups.Select(g =>
        {
            var (vols, sizes) = ExpectedVolumes(info, g.First().Storage!);
            return (g.Key, vols, sizes);
        }).ToList();
        var tracker = Track(onProgress, "Cloud", families.Sum(f => Math.Max(1, f.vols)));
        var verdicts = await VolumeBlobIO.VerifyFamiliesAsync(
            cc, families, headConcurrency, ct,
            // HEAD downloads no content: count 0 bytes, or the reported "speed" has nothing to do with actual traffic.
            onProbe: i => { tracker?.Touch(groups[i].Key); tracker?.Advance(0); });
        tracker?.Complete();
        for (var i = 0; i < groups.Count; i++)
        {
            if (verdicts[i] is { Present: true, SizeOk: true })
            {
                presentGroups.Add(groups[i]);
            }
            else
            {
                foreach (var e in groups[i])
                    bad.Add(e.Path);
            }
        }

        if (options.Cloud >= CloudCheckLevel.Content)
        {
            var corrupted = await DeepVerifyAsync(cc, info, presentGroups, options, password, downloadConcurrency, onProgress, ct);
            foreach (var p in corrupted)
                bad.Add(p);
        }

        return bad;
    }

    private static (int Volumes, IReadOnlyList<long> Sizes) ExpectedVolumes(BackupInfoFile info, StorageRef s) =>
        s.Kind == "pack"
            ? info.Packs.TryGetValue(s.Ref, out var pi) ? (pi.Volumes, pi.VolumeSizes) : (1, [])
            : (s.Volumes, s.VolumeSizes);

    /// <summary>Deep verification: download and extract concurrently, recompute fullHash and compare it with the
    /// index. Only a content mismatch counts as corruption; a blob still in Archive (the download reports archived)
    /// does not — it cannot be verified, so it is skipped.</summary>
    /// <param name="info">Used only to work out how many bytes each object will pull (a pack's volume sizes live in
    /// the info file, not on the entry — compaction rewrites them). The UI uses this to show "how much transferred /
    /// how much in total".</param>
    private async Task<IReadOnlyList<string>> DeepVerifyAsync(
        BlobContainerClient cc, BackupInfoFile info, List<IGrouping<string, IndexEntry>> presentGroups,
        CheckOptions options, string? password, int downloadConcurrency, Action<StageProgress>? onProgress, CancellationToken ct)
    {
        if (compressor is null || hasher is null || string.IsNullOrEmpty(tempRoot))
            throw new InvalidOperationException("Content check requires compressor/hasher/tempRoot.");

        var work = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        using var gate = new SemaphoreSlim(Math.Max(1, downloadConcurrency));
        // This is the only stage in the whole check that actually downloads data, and the only one that can run for hours.
        var tracker = Track(onProgress, "Verifying", presentGroups.Count, inFlight: true);
        // Declare the workload in bytes, the same two units the restore side reports: what will be re-hashed
        // (source bytes) and what comes over the wire (compressed volumes). A group count alone distorts the
        // progress — one group can be a single 100 GB file or a box of several hundred small ones. The download
        // total is only reported when every group can answer it (an old index without volume sizes must not hand
        // out an undersized denominator that pins the percentage at 100% early).
        var groupWork = presentGroups.ToDictionary(g => g.Key, g => g.Sum(e => e.Length), StringComparer.Ordinal);
        var downloadSizes = presentGroups.ToDictionary(
            g => g.Key, g => TransferLabel.DownloadBytesOf(g.First().Storage!, info), StringComparer.Ordinal);
        var downloadTotalKnown = downloadSizes.Values.All(b => b > 0);
        foreach (var g in presentGroups)
            tracker?.Enqueue(groupWork[g.Key], downloadTotalKnown ? downloadSizes[g.Key] : 0);
        try
        {
            var perGroup = await Task.WhenAll(presentGroups.Select(async g =>
            {
                try { return await VerifyGroupAsync(cc, info, work, g.Key, g.ToList(), options, password, gate, tracker, ct); }
                finally
                {
                    // Counting and in-flight are separate concerns: one group takes exactly one slot. Work is
                    // retired in one go — failed groups too, or the remainder never reaches zero and the ETA hangs.
                    tracker?.Advance(0, groupWork[g.Key]);
                }
            }));
            return perGroup.SelectMany(x => x).ToList();
        }
        finally
        {
            tracker?.Complete(); // without forcing a final publish, the last group's bytes stay pinned by the throttle and never come out
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    private async Task<IReadOnlyList<string>> VerifyGroupAsync(
        BlobContainerClient cc, BackupInfoFile info, string work, string blobName, List<IndexEntry> members,
        CheckOptions options, string? password, SemaphoreSlim gate, StageTracker? tracker, CancellationToken ct)
    {
        var corrupted = new List<string>();
        var groupDir = Path.Combine(work, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(groupDir);
        await gate.WaitAsync(ct);
        // The in-flight marker goes on **only after the gate is taken**: every group's delegate is enumerated up to
        // its first real await right at the start, so marking before that would count thousands of packs as
        // "verifying" all at once (see the same comment in RestoreOrchestrator).
        // The name uses the **source file path** (packs use the pack id + member count), not the content-addressed
        // blob name — the same shape as the upload/restore sides.
        tracker?.BeginItem(
            blobName,
            TransferLabel.For(members[0].Storage!, members),
            TransferLabel.DownloadBytesOf(members[0].Storage!, info));
        try
        {
            // A factory rather than one IProgress<long>: see the comment on VolumeBlobIO.DownloadAsync — sharing a
            // single instance across volumes miscomputes the baseline of a large volume's first report in the
            // "small volume followed by a large one" case, leaving a stretch of that volume uncounted (bounded by the
            // previous volume's size); it undercounts, it does not inflate.
            Func<IProgress<long>>? itemProgress = tracker is null ? null : () => tracker.ItemProgress(blobName);

            string firstVolume;
            try
            {
                firstVolume = await VolumeBlobIO.DownloadAsync(cc, blobName, groupDir, ct, itemProgress);
            }
            finally
            {
                // The moment the download ends (success or throw), take the in-flight marker off: the bytes were
                // counted as they streamed, and the speed window must not keep stretching over the extraction and
                // rehashing that follow, which use no network at all. This finally wraps the download only — it
                // swallows nothing, a failed download still falls through to the two catches below, and the set of
                // exceptions they see is exactly what it was before this change.
                tracker?.EndItem(blobName, 0);
            }

            // The download has left the in-flight window, but the local CPU work of extracting and rehashing must
            // not vanish from the UI along with it — that is the slowest step of a content-level check, and without
            // this pair the UI freezes on the snapshot taken the instant the download ended, which looks exactly like
            // a hang (same comment in RestoreOrchestrator.RestoreGroupAsync).
            try
            {
                // BeginPacking moved inside the try: it now calls publish(...) under _gate, and on the non-heartbeat
                // path an exception from publish is deliberately allowed to propagate (see the notes on BeginPacking
                // in StageProgress.cs). Left outside the try, a throw here would increment _inPacking with no matching
                // EndPacking, and preparing would sit at an inflated number for the rest of the run; moved inside, the
                // finally below backstops it.
                tracker?.BeginPacking(TransferLabel.Folders(members.Select(m => m.Path)));
                // The shared contract of this stretch is "extraction/hashing happens after this item has already
                // left ActiveItems", the same shape as in RestoreOrchestrator; on this side it is pinned by the
                // identically named Extraction_Starts_After_Item_Is_Removed_From_ActiveItems in BackupCheckerTests
                // (a mirror of the restore-side test, but the two are independent and neither substitutes for the
                // other), so the restore-side test is no longer borrowed as a backstop.
                corrupted.AddRange(members[0].Storage!.Kind == "blob"
                    ? await VerifyBlobAsync(firstVolume, members, password, ct)
                    : await VerifyPackAsync(firstVolume, groupDir, members, password, ct));
            }
            finally
            {
                tracker?.EndPacking();
            }
        }
        catch (RequestFailedException ex) when (IsArchived(ex))
        {
            // Still in Archive without rehydration → cannot be downloaded to verify; kick off rehydration if a tier
            // was given. Not counted as corruption.
            if (options.RehydrateTier is { } tier)
                await RehydrateAsync(cc, blobName, tier, ct);
        }
        catch
        {
            corrupted.AddRange(members.Select(m => m.Path)); // any other download/extract failure → the whole group is corrupt
        }
        finally
        {
            // Clear in-flight first, then release the gate: the other way round, the next group has already started
            // verifying while the UI still shows the previous one. EndItem(blobName, 0) is a backstop, not the normal
            // path: normally the marker was already taken off in the finally above when the download completed, and
            // the bytes were fully counted as they streamed — this only guards the edge case of a throw after
            // BeginItem but before entering the download try. EndItem is not idempotent (_bytes += bytes and
            // PublishIfDue both run unconditionally, outside TryRemove); the reason this call does not top the speed
            // window up a second time with the "extract + hash" bytes on the normal path is purely that the byte count
            // it passes is 0, not that EndItem is safe to re-enter.
            //
            // Releasing the gate and deleting the temp directory each have to hide in a finally behind EndItem:
            // EndItem calls into the publish the caller supplied (external code that writes to the database, pushes
            // SSE and the like), it can throw, and an exception on this path is **deliberately** propagated. Written
            // as three statements in a row, a throw from the first skips the other two entirely — the permit is gone
            // for good, the next group waits at the gate forever, the whole check never comes back, and the UI shows a
            // spinner that never stops. The same shape appears in VolumeUploadScope.RunAsync and
            // RestoreOrchestrator.RestoreGroupAsync.
            try
            {
                tracker?.EndItem(blobName, 0);
            }
            finally
            {
                gate.Release();
                try { Directory.Delete(groupDir, recursive: true); } catch { /* best effort */ }
            }
        }
        return corrupted;
    }

    /// <summary>
    /// Content verification of a single-file blob, **without touching disk**: a raw-uploaded blob is the file
    /// itself; otherwise the archive holds exactly one member and the output of `x -so` with no member name is
    /// precisely its content — so the entry name need not be known in advance. That matters: after dedup, the entry
    /// name inside the archive comes from whichever path **uploaded this content first**, which need not equal the
    /// current index entry's Path.
    /// <para>
    /// Both the length and the hash must be checked. When `x -so` cannot get the content it produces empty output yet
    /// exits 0, so going by "nothing was thrown" alone would pass an empty archive — exactly the pitfall this project
    /// has already fallen into once (7z exited 1 when it dropped a member, and it still passed silently).
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<string>> VerifyBlobAsync(
        string firstVolume, List<IndexEntry> members, string? password, CancellationToken ct)
    {
        string actualHash;
        long actualLength;
        if (members[0].Storage!.Raw)
        {
            actualHash = await hasher!.FullHashAsync(firstVolume, ct);
            actualLength = new FileInfo(firstVolume).Length;
        }
        else
        {
            var streamHasher = new StreamingHasher(0, 0);
            await using var sink = new HashingStream(streamHasher);
            await compressor!.ExtractToStreamAsync(firstVolume, entryName: null, password, sink, ct);
            actualHash = streamHasher.FullHash;
            actualLength = streamHasher.Length;
        }

        return [.. members
            .Where(e => actualLength != e.Length || (e.FullHash is not null && actualHash != e.FullHash))
            .Select(e => e.Path)];
    }

    /// <summary>
    /// Content verification of a pack, **without touching disk**: one `x -so` (with no member name) streams the
    /// whole pack out, which is then cut into segments following the member order and sizes reported by `l -slt`,
    /// hashing each segment. Invoking 7z once per member is not an option — the archive is solid, so pulling the k-th
    /// member re-extracts the preceding k-1 as well, and a pack with thousands of members degenerates into O(N²).
    /// <para>
    /// The segmentation relies on the 7z behaviour "output order = listing order". That holds (a test pins it), but
    /// the day some version breaks it, the consequence is reporting a good pack as bad — and the repair flow would
    /// re-upload it on that basis. So the moment a single segment fails to line up, fall back to extracting the whole
    /// pack to disk and re-checking member by member, and let **that** deliver the verdict: the fast path only saves
    /// work in the normal case, it never raises a false alarm.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<string>> VerifyPackAsync(
        string firstVolume, string groupDir, List<IndexEntry> members, string? password, CancellationToken ct)
    {
        var listing = await compressor!.ListEntriesAsync(firstVolume, password, ct);
        var files = listing.Where(e => !e.IsDirectory).Select(e => (e.Name, e.Size)).ToList();

        var actual = new Dictionary<string, (long Length, string Hash)>(StringComparer.Ordinal);
        var splitter = new SegmentHashingStream(files, (name, len, hash) => actual.TryAdd(name, (len, hash)));
        await using (splitter)
        {
            await compressor.ExtractToStreamAsync(firstVolume, entryName: null, password, splitter, ct);
            splitter.Finish();
        }

        // The archive spat out more bytes than the listing accounts for, or some member was never filled → the
        // premise of the segmentation does not hold, so do not trust this round's results.
        var splitTrustworthy = splitter.ExtraBytes == 0 && splitter.CompletedSegments == files.Count;

        var suspect = new List<IndexEntry>();
        var corrupted = new List<string>();
        foreach (var e in members)
        {
            var entryName = SevenZipCli.NormalizeEntryName(e.Storage!.EntryName ?? e.Path);
            if (!actual.TryGetValue(entryName, out var got))
            {
                // The index says this member is in the pack and the pack simply does not have it — definite
                // corruption, no need to verify the content.
                // (Absent from the listing ≠ the fast path is untrustworthy: this is a problem with the content
                // itself, and extracting to disk would show the same.)
                if (splitTrustworthy && !listing.Any(l => l.Name == entryName))
                    corrupted.Add(e.Path);
                else
                    suspect.Add(e);
                continue;
            }
            if (got.Length != e.Length || (e.FullHash is not null && got.Hash != e.FullHash))
                suspect.Add(e);
        }

        if (suspect.Count > 0)
            corrupted.AddRange(await VerifyPackOnDiskAsync(firstVolume, groupDir, suspect, password, ct));
        return corrupted;
    }

    /// <summary>Slow path: extract the whole pack to disk and re-check member by member. Only taken when the streaming segmentation reports a problem, and it delivers the final verdict.</summary>
    private async Task<IReadOnlyList<string>> VerifyPackOnDiskAsync(
        string firstVolume, string groupDir, IReadOnlyList<IndexEntry> members, string? password, CancellationToken ct)
    {
        var extractDir = Path.Combine(groupDir, "x");
        await compressor!.ExtractAsync(firstVolume, extractDir, password, ct);

        var corrupted = new List<string>();
        foreach (var e in members)
        {
            var entryName = e.Storage!.EntryName ?? e.Path;
            var path = Path.Combine(extractDir, entryName.Replace('/', Path.DirectorySeparatorChar));
            // The entry name comes from the cloud index, which after /import is attacker-controlled (design §5):
            // `..` can fling the probe point outside the extraction directory, turning this into a confirmation
            // oracle for "is the content of some file equal to some hash". Out of bounds is always ruled corruption.
            if (!PathBoundary.IsWithin(extractDir, path)
                || !File.Exists(path)
                || new FileInfo(path).Length != e.Length
                || (e.FullHash is not null && await hasher!.FullHashAsync(path, ct) != e.FullHash))
                corrupted.Add(e.Path);
        }
        return corrupted;
    }

    private static bool IsArchived(RequestFailedException ex) =>
        ex.ErrorCode == "BlobArchived" || ex.Status == 409;

    private static Task RehydrateAsync(BlobContainerClient cc, string baseRef, AccessTier tier, CancellationToken ct) =>
        // Start rehydration on every volume of the archived object (asynchronous; hours later the user has to re-run
        // the check); failures are ignored (best effort).
        BlobRehydration.BeginAsync(cc, baseRef, tier, ct);

    /// <summary>State of the local source file. A missing localRoot or a disabled local axis → NotChecked.</summary>
    private async Task<LocalState> LocalCheckAsync(IndexEntry e, string? localRoot, LocalCheckLevel level, CancellationToken ct)
    {
        if (level == LocalCheckLevel.None || string.IsNullOrEmpty(localRoot))
            return LocalState.NotChecked;

        var local = Path.Combine(localRoot, e.Path.Replace('/', Path.DirectorySeparatorChar));

        // e.Path comes from the cloud index, which after /import is attacker-controlled (design §5): `..` or an
        // absolute path can make Path.Combine fling the probe point outside localRoot, turning this into a
        // confirmation oracle for "does this file exist / is its content equal to some hash". Out of bounds is always
        // treated as Missing — local cannot produce a usable copy, so it is neither read nor allowed to become a
        // repair source, the same handling as "the local file is not there".
        if (!PathBoundary.IsWithin(localRoot, local))
            return LocalState.Missing;

        if (e.Kind == "symlink")
        {
            var target = TryLinkTarget(local);
            if (target is null)
                return LocalState.Missing;
            return target == e.Target ? LocalState.Ok : LocalState.Changed;
        }

        if (!File.Exists(local))
            return LocalState.Missing;

        // The local file exists but cannot be read (locked / permissions revoked / media read error): always treated
        // as Missing — local cannot produce a usable copy, so it is neither read nor allowed to become a repair
        // source, the same handling as "out of bounds" and "not there" above.
        // Without this guard one unreadable file takes down the **entire check run**, and "some file cannot be read"
        // is precisely when the check is needed most: the backup just skipped it, and the operator wants to know
        // whether the cloud copy is still there.
        try
        {
            if (level == LocalCheckLevel.Attributes)
            {
                var permOk = ReadPermissions(local) == e.Permissions;
                return new FileInfo(local).Length == e.Length && permOk ? LocalState.Ok : LocalState.Changed;
            }

            // Content: a matching hash = repairable from local.
            if (hasher is null)
                return LocalState.NotChecked;
            var full = await hasher.FullHashAsync(local, ct);
            return full == e.FullHash ? LocalState.Ok : LocalState.Changed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LocalState.Missing;
        }
    }

    private static string? TryLinkTarget(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.LinkTarget;
        }
        catch { return null; }
    }

    private static string ReadPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
            return "0000";
        var mode = (int)File.GetUnixFileMode(path);
        return Convert.ToString(mode, 8).PadLeft(4, '0');
    }

    /// <summary>Metadata drift check: compare the cloud info file against the local-authoritative cache (version count / each version's IndexBlob / CreatedAt).</summary>
    private async Task<string?> CheckMetadataDriftAsync(
        Account account, string container, string? password, BackupInfoFile cloud, CancellationToken ct)
    {
        if (trackedInfo is null)
            return null; // no local cache to compare against
        if (!await trackedInfo.HasLocalAsync(account, container, ct))
            return "No local cache to compare against (backup not synced on this device).";

        var local = await trackedInfo.LoadAsync(account, container, password, ct);
        if (local is null)
            return "Local cache missing while cloud has a backup.";
        if (local.Versions.Count != cloud.Versions.Count)
            return $"Version count differs: local {local.Versions.Count} vs cloud {cloud.Versions.Count}.";
        for (var i = 0; i < cloud.Versions.Count; i++)
        {
            if (local.Versions[i].IndexBlob != cloud.Versions[i].IndexBlob
                || local.Versions[i].CreatedAt != cloud.Versions[i].CreatedAt)
                return $"Version {cloud.Versions[i].Version} metadata differs between local cache and cloud.";
        }
        return null;
    }

    private static string BlobNameOf(StorageRef s) => s.Kind == "pack" ? $"packs/{s.Ref}.7z" : s.Ref;
}
