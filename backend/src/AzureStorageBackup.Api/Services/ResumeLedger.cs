namespace AzureStorageBackup.Api.Services;

/// <summary>
/// The answer to "was this content already uploaded by the run we are resuming", read out of the run's work database
/// rather than out of dictionaries.
/// <para>
/// The questions and their answers are unchanged from the in-memory table this replaced; what changed is where the
/// records live. A first run over millions of files used to mean millions of <see cref="JournalRecord"/>s resident for
/// the whole run, held for the sake of lookups that mostly miss. They now sit in <c>resume_blobs</c> /
/// <c>resume_packs</c> in the per-run scratch file, indexed by exactly the keys these three lookups use, and the run's
/// memory no longer grows with the size of the journal it adopted.
/// </para>
/// <para>
/// The test is still **path plus evidence that the file is still the one that was uploaded**. Path alone will not do —
/// the file may well have been modified after the interruption; content hash alone will not do either — the journal
/// records by path, and identical content at a different path is a separate entry in the index.
/// </para>
/// <para>
/// The evidence comes at two prices. <see cref="FindBlobAsync"/> takes a full content identity, which costs a read of
/// the whole file; <see cref="FindUntouchedBlobAsync"/> takes length + mtime, which costs a <c>stat</c> — the same
/// judgement the diff makes to call a file unchanged. The caller asks the cheap one first and falls back to the
/// expensive one on any miss; see <see cref="FindUntouchedBlobAsync"/> for why the cheap one is not a weakening.
/// </para>
/// <para>
/// "The newer volume wins a path recorded twice" is not decided here: the rows go in newest volume first and the
/// insert is <c>INSERT OR IGNORE</c> keyed by path, so the first record for a path is the one that stays — the same
/// rule, moved from a <c>TryAdd</c> into the schema. See <see cref="BackupRunControl.OpenJournalAsync"/> for the
/// ordering and why it must not be left to chance.
/// </para>
/// <para>
/// Purely local, no cloud reads. A record only enters the journal once "the upload has been confirmed returned", so
/// there is no need (and no business) checking against the cloud again here.
/// </para>
/// <para>
/// A run that adopted no journal has **no ledger at all** (<c>BackupRunControl.Resume</c> is null) rather than an
/// empty one. An empty sentinel would have every caller pay a database round trip to be told there is nothing to
/// resume, which is every ordinary backup.
/// </para>
/// </summary>
public sealed class ResumeLedger(RunWorkDb work)
{
    /// <summary>-1 = not counted yet. See <see cref="RecordCountAsync"/> for why one count is enough.</summary>
    private int _count = -1;

    /// <summary>
    /// How many records this resume can answer from: distinct blob paths plus distinct pack member sets, which is
    /// what the in-memory table counted.
    /// <para>
    /// Counted once and remembered. The resume tables are filled in <see cref="BackupRunControl.OpenJournalAsync"/>
    /// and flushed **before** this object exists, and nothing appends to them afterwards — so the number cannot
    /// change under us, and re-asking would be a full table scan. Which matters: <see cref="IsEmptyAsync"/> is asked
    /// once per file by the orchestrator, in front of the <c>stat</c>, and a scan per file on a million-file run
    /// would cost far more than the dictionaries this class was written to get rid of. Two callers racing the first
    /// count merely both compute the same answer.
    /// </para>
    /// </summary>
    public async Task<int> RecordCountAsync(CancellationToken ct)
    {
        var cached = Volatile.Read(ref _count);
        if (cached >= 0)
            return cached;
        var counted = await work.ResumeRecordCountAsync(ct);
        Volatile.Write(ref _count, counted);
        return counted;
    }

    /// <summary>Nothing to resume. Only true for a journal that held no usable record at all — an adopted volume
    /// whose every line was a half-written stub, say.</summary>
    public async Task<bool> IsEmptyAsync(CancellationToken ct) => await RecordCountAsync(ct) == 0;

    /// <summary>Exact match for one single-file blob. Only accepted when all four content tests match.</summary>
    public async Task<JournalRecord?> FindBlobAsync(
        string path, string fullHash, long length, string headHash, string tailHash, CancellationToken ct)
        => await work.ResumeBlobByPathAsync(path, ct) is { } r
            && string.Equals(r.FullHash, fullHash, StringComparison.Ordinal)
            && r.Length == length
            && string.Equals(r.HeadHash, headHash, StringComparison.Ordinal)
            && string.Equals(r.TailHash, tailHash, StringComparison.Ordinal)
            ? r
            : null;

    /// <summary>
    /// The previous run uploaded this exact path and the file has not been touched since.
    /// <para>
    /// Returns null when the record predates <see cref="JournalRecord.MtimeUtcTicks"/>, when the path is absent, or
    /// when either metadata test fails — in every one of those the caller must fall back to the content test, which
    /// is the route that ran before this method existed and is unchanged by it. A null mtime is the case to be most
    /// careful about: a record that **cannot** answer the cheap question must not be allowed to answer it by
    /// default, or every journal written by an older build would start matching on path alone.
    /// </para>
    /// <para>
    /// This is weaker than <see cref="FindBlobAsync"/> on purpose, and exactly as weak as the diff. Path alone will
    /// not do, as the class remarks say — but path **and metadata** is a different proposition, and it is the one the
    /// diff already makes a thousand times a second: a file whose length and mtime match its previous version is
    /// called Unchanged in <c>BackupDiffer</c> without a byte being read. So a file that slips past this check would
    /// also have slipped past the diff and never entered the pipeline at all — the resume accepts nothing the run as
    /// a whole had not already accepted.
    /// </para>
    /// <para>
    /// Permissions, which the diff also compares, are deliberately left out: they do not affect the **content**,
    /// which is all a journal record claims, and the index entry's metadata is rewritten from the current scan
    /// regardless.
    /// </para>
    /// <para>
    /// What the strictness of <see cref="FindBlobAsync"/> bought — catching a file rewritten between the interruption
    /// and the resume — it still buys, on the fallback path every non-match takes. A rewrite that preserves both
    /// length and mtime defeats this check, and it defeats the diff identically; that is a pre-existing boundary of
    /// the whole design, not a hole opened here.
    /// </para>
    /// <para>
    /// The reason for having it at all is what the content test costs: <see cref="FindBlobAsync"/> needs a full
    /// content identity, and that means reading the whole file. Measured on a real resume, 194.1 GB of source was
    /// read to establish that 704.4 MB needed sending — better than 99% of it spent proving that nothing had to be
    /// done.
    /// </para>
    /// </summary>
    /// <param name="mtimeUtc">The source file's current last-write time, from the <c>stat</c> the caller has done anyway.</param>
    public async Task<JournalRecord?> FindUntouchedBlobAsync(
        string path, DateTimeOffset mtimeUtc, long length, CancellationToken ct)
        => await work.ResumeBlobByPathAsync(path, ct) is { } r
            // Spelled out rather than left to `r.MtimeUtcTicks == mtimeUtc.UtcTicks`, which would also be correct (a
            // null long? equals no long) but reads as an ordinary comparison — and "the record has an answer" is the
            // single condition this whole method's safety rests on. It deserves to be visible.
            && r.MtimeUtcTicks is { } ticks && ticks == mtimeUtc.UtcTicks
            && r.Length == length
            ? r
            : null;

    /// <summary>
    /// Exact match for one pack. The member sets must be identical item for item; no leniency.
    /// <para>
    /// The reason is not about names (a member's name in the archive is just the member's own path, see
    /// <see cref="RunWorkDb.MemberKey"/>) but that **the accounting and the archive must line up exactly**: on a hit,
    /// <c>RecordPackAsync</c> takes **this run's** member list to write <c>PackInfo.Members</c> /
    /// <c>OriginalBytes</c>, and writes an index entry pointing at this pack for every member. Allowing a partial
    /// match (this run's group being a superset of last run's pack) amounts to claiming the archive holds members it
    /// simply does not: restore cannot extract the file, check reports it missing, and the index insists it is there.
    /// A subset is no good either — <c>OriginalBytes</c> comes out short, and dead-weight compaction uses it to judge
    /// how much live flesh is left in this pack.
    /// </para>
    /// <para>
    /// Grouping itself is deterministic (same baseline, same source, same bounds), so strict equality hits often
    /// enough in practice; and when it does not match we recompress — a pack is all small files, so recompressing is
    /// cheap.
    /// </para>
    /// </summary>
    public async Task<JournalRecord?> FindPackAsync(IReadOnlyList<JournalMember> members, CancellationToken ct)
        => members.Count > 0 ? await work.ResumePackAsync(RunWorkDb.MemberKey(members), ct) : null;
}
