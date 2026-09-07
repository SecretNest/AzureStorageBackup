using System.Collections.Concurrent;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>An existing single-file data blob: actual storage name + whether it is raw bytes + volume count + each volume's size.</summary>
public sealed record ResolvedBlob(string Ref, bool Raw, int Volumes, IReadOnlyList<long> VolumeSizes);

/// <summary>
/// A piece of content that is "already confirmed to exist in the cloud but not yet in any version index": its content
/// identity plus where it landed.
/// <para>
/// There is exactly one source — a journal left by the previous run (or a few runs back) and adopted by this one.
/// These blocks are in exactly the same situation as blocks in an existing version index (confirmed in the cloud,
/// address already taken); the only difference is that it is a journal recording them rather than an index,
/// so they must be fed into <see cref="LocalDedupResolver.Build"/> as well, so that dedup, collision avoidance and the
/// prescreen can all three see them. The consequence of not seeing them is nowhere near as mild as "upload it again" — see the notes on Build.
/// </para>
/// </summary>
public sealed record ConfirmedBlob(
    string FullHash, long Length, string HeadHash, string TailHash, ResolvedBlob Blob);

/// <summary>
/// One member inside some existing pack. A new entry pointing at it completes dedup — no compression, no upload, no packing.
/// <para>
/// <paramref name="EntryName"/> is the path **as it was first stored** (the member name inside the archive), which may
/// differ from the path referencing it now: same content, different path, which is exactly the case dedup exists for.
/// Restore pulls the member out of the archive by this name and writes it to the index entry's own Path.
/// </para>
/// </summary>
public sealed record PackMemberRef(string PackId, string EntryName, string? TailHash);

/// <summary>
/// Purely local single-file blob dedup / collision resolution (no cloud reads). Every retained version's content
/// identities (fullHash + length + head + tail) and storage info are in the container's <see cref="VersionCatalog"/>,
/// and everything this run has claimed is in its <see cref="RunWorkDb"/>, so dedup, collision avoidance, volume count
/// and raw can all be decided locally.
/// <para>
/// Across versions: ask the catalog, then the run's adopted journal, then the uploads this run has already finished
/// (<see cref="IDedupSource"/> holds that order in one place). Within one backup run: coordinate through the
/// in-flight reservation table (one <see cref="TaskCompletionSource{T}"/> per ref) — a latecomer with the same
/// content waits for the first uploader to finish and gets the same (ref, raw, volume count); different content
/// landing on the same address steps aside to …~N.
/// </para>
/// <para>
/// The table holds <b>only claims that are still uploading</b>. A finished one is written to the work database and
/// leaves the table, so a run that uploads a million blobs holds a million rows in a file rather than a million
/// objects in memory — which is the whole point of the move.
/// </para>
/// Trusting the local catalog = the cloud truth (consistent with the "read the cloud as little as possible" design);
/// external modification of the cloud is for Check to discover.
/// </summary>
public sealed class LocalDedupResolver
{
    private readonly BlobAddressScheme _addressing;
    private readonly IDedupSource _source;
    private readonly ConcurrentDictionary<string, Reservation> _run = new(StringComparer.Ordinal);

    /// <summary>The resolver as the pipeline builds it: the container's catalog for the retained versions, the run's
    /// work database for its journal, its reservations and the heads it has started on.</summary>
    public LocalDedupResolver(BlobAddressScheme addressing, VersionCatalog catalog, RunWorkDb work)
        : this(addressing, new CatalogDedupSource(catalog, work))
    {
    }

    private LocalDedupResolver(BlobAddressScheme addressing, IDedupSource source)
    {
        _addressing = addressing;
        _source = source;
    }

    /// <summary>
    /// The in-memory maps, for the callers not yet rewired to the catalog (the orchestrator, until Task 13). A
    /// resolver built this way answers the synchronous members below and nothing else changes about it; one built
    /// from a catalog refuses them, because those answers cannot be produced without waiting on a database.
    /// </summary>
    /// <param name="confirmed">
    /// The blocks in an adopted journal that are "confirmed in the cloud but not yet in the index" (<see cref="ConfirmedBlob"/>).
    /// <para>
    /// **They must be fed in**, and not merely to avoid one extra upload. Resume accounts by **path**: the previous run
    /// finished uploading A and then suspended, before it reached B, which has the same content as A. This run reuses A
    /// directly without uploading, but B does not recognise that it already exists, so it recompresses, then
    /// ResolveAsync hands it the **same** ref (content addressing: same content, same address), and
    /// <c>UploadStagedBlobAsync</c> writes over A's own volumes. With deterministic output that is mostly waste —
    /// the volumes label-match and skip (see FetchFamilyLabelsAsync) — but an encrypted backup's output never
    /// matches (fresh salt/IV per compression), so every volume of A's family is overwritten, and an interruption
    /// by Stop now or a process crash mid-family leaves the address a splice of two runs' volumes — unopenable,
    /// for an encrypted multi-volume archive — while the next run adopting the journal reuses A as usual and
    /// commits the index as usual, pointing at it. The error only becomes visible at restore or check time.
    /// </para>
    /// <para>
    /// Once they are fed in, B takes the cross-version dedup path: neither recompressed nor re-uploaded, and that set
    /// of volumes never gets the chance to be touched. They also go into the collision table (different content
    /// landing on this address still steps aside to …~N) and into the prescreen set.
    /// </para>
    /// </param>
    public static LocalDedupResolver Build(
        BlobAddressScheme addressing, IEnumerable<VersionIndex> indexes,
        IEnumerable<ConfirmedBlob>? confirmed = null) =>
        new(addressing, LegacyDedupSource.Build(indexes, confirmed));

    /// <summary>
    /// Might there **possibly** be an existing blob with the same content this round? It only looks at length + head
    /// hash, so it can answer without reading the whole file.
    /// <para>
    /// Streaming compression only knows the full-content hash once compression is done, so "compute the full hash
    /// first, then decide on dedup" means reading the file one extra time. This prescreen lets a first backup (not one
    /// candidate anywhere) take the single-read fast path, and only pays for that extra pass when candidates really exist.
    /// Better a false positive than a miss: a false positive only costs one extra read, whereas a miss makes content that could have been skipped entirely get compressed for nothing.
    /// </para>
    /// <para>
    /// The last of its three sources — the heads this run has started on — is a table in the work database, whose
    /// writer batches, so a head noted a moment ago may not be visible yet. That can only produce a miss on content
    /// whose first copy is still in flight, and a miss costs one compression; the address it eventually lands on is
    /// <see cref="ResolveAsync"/>'s business, and that one never guesses.
    /// </para>
    /// </summary>
    public Task<bool> MayDeduplicateAsync(long length, string headHash, CancellationToken ct) =>
        _source.MayDeduplicateAsync(length, headHash, ct);

    /// <summary>Registers content this run has already started on (length + head): on the strength of this, a later
    /// file with the same content takes the prescreen's slow path, looking it up once rather than compressing it for nothing.</summary>
    public ValueTask NoteInFlightAsync(long length, string headHash, CancellationToken ct) =>
        _source.NoteInFlightAsync(length, headHash, ct);

    /// <summary>Whether some retained version marks this blob ref's content unrecoverable — the upload side's cue
    /// to replace rather than trust: an if-missing upload would bounce off the broken family with "already
    /// there", and a label-trusting skip would believe metadata a condemned blob has no right to.</summary>
    public Task<bool> IsDamagedRefAsync(string @ref, CancellationToken ct) => _source.IsDamagedRefAsync(@ref, ct);

    /// <summary>Only consults what is already stored; it does **not** take a ref and creates no reservation. For the
    /// prescreen path of "probe once, and on a hit skip compression entirely"; an actual upload must still go through
    /// <see cref="ResolveAsync"/> to get a reservation.</summary>
    public Task<ResolvedBlob?> TryFindExistingAsync(
        string fullHash, long length, string headHash, string tailHash, CancellationToken ct) =>
        _source.TryFindExistingAsync(fullHash, length, headHash, tailHash, ct);

    /// <summary>
    /// Whether this content is already inside some existing pack. A hit lets a new entry point straight at it — no
    /// compression, no upload, no packing.
    /// <para>
    /// The criterion is **the same** as on the single-file blob path: fullHash + length + head + tail, all four equal.
    /// Two different standards on the two paths would make no sense — both are the judgement "this content already
    /// exists", and getting either wrong points the index at somebody else's content and produces wrong data at restore
    /// time.
    /// </para>
    /// <para>
    /// All four **strictly** equal; missing counts as unequal too. This was once relaxed to "only compare when both
    /// sides have one", so that pack members in old indexes without a tail could take part in dedup too; that
    /// relaxation is gone — the criterion is either all four fields or it is not, and opening a compatibility loophole
    /// leaves a fuzzy semantic in the very place that must least of all be fuzzy ("is this the same content").
    /// Newly written entries all carry a tail; old entries merely do not take part in dedup, and the price is only that their content gets stored one more time.
    /// </para>
    /// </summary>
    public Task<PackMemberRef?> TryFindPackMemberAsync(
        string fullHash, long length, string headHash, string? tailHash, CancellationToken ct) =>
        _source.TryFindPackMemberAsync(fullHash, length, headHash, tailHash, ct);

    /// <summary>Resolves a piece of content: a hit on something existing → dedup; otherwise claim a free ref for the caller to upload, and fill in (raw, volume count) once done.</summary>
    /// <param name="tracker">Optional progress bookkeeping. Within one run, a latecomer with the same content has to
    /// wait for the first uploader to finish the **whole item** — that can be minutes, and the wait falls after
    /// compression and before upload, with neither a stream uploading nor an item compressing on screen.
    /// Without marking it, the UI is simply frozen solid, with no way even to say who is being waited on.</param>
    public async Task<Resolution> ResolveAsync(
        string fullHash, long length, string headHash, string tailHash, CancellationToken ct,
        StageTracker? tracker = null)
    {
        var ck = ContentKey(fullHash, length, headHash, tailHash);
        if (await _source.TryFindExistingAsync(fullHash, length, headHash, tailHash, ct) is { } prior)
            return Resolution.ForExisting(prior, collision: false); // cross-version dedup

        var baseAddr = _addressing.DataAddress(fullHash);
        for (var n = 0; ; n++)
        {
            var refName = n == 0 ? baseAddr : $"{baseAddr}~{n}";
            var collision = n > 0;

            if (await _source.RefOwnerAsync(refName, ct) is { } priorCk)
            {
                if (priorCk != ck)
                    continue;                                     // an older version's different content holds this address → step aside
                if (!await _source.IsDamagedRefAsync(refName, ct))
                    return Resolution.ForExisting(                 // in theory the content lookup already hit; a safe backstop
                        new ResolvedBlob(refName, false, 1, []), collision);
                // The name holds this very content, damaged: fall through and claim it — that upload is the heal,
                // landing at the base address with no collision detour.
            }

            // The same address may need more than one attempt, hence the extra loop here — the reason is in the TryGetValue-misses branch below.
            while (true)
            {
                // A failed claim has to be able to give this ref back (see Reservation.Fail): Task 7's gate retries a
                // failed upload as the same work item, and the retry still carries the same content identity, so it
                // comes back here — if the claim were not withdrawn, the retrier would run into this already-failed
                // claim, match on `held.ContentKey == ck` and wait on a Completion that never succeeds, replaying the
                // same exception forever and never reaching a real second upload attempt.
                Reservation? mine = null;
                mine = new Reservation(ck, () =>
                    ((ICollection<KeyValuePair<string, Reservation>>)_run)
                        .Remove(new KeyValuePair<string, Reservation>(refName, mine!)));
                if (_run.TryAdd(refName, mine))
                    return Resolution.ForClaim(this, refName, collision, mine); // I will do the upload

                // The indexer `_run[refName]` **must not** be used here. It used to be total: once a reservation landed
                // in the table it was never removed. But "give the ref back on upload failure" is exactly what this
                // feature just added (see above), so between the failed TryAdd above and this line the holder may well
                // have failed on **another thread** and pulled that record — at which point the indexer throws
                // KeyNotFoundException. It is not among TransientErrors' transient criteria, the gate cannot catch it,
                // and the whole backup run is declared dead; and it shows up only in a failure storm (several workers,
                // the same content, failures and table lookups crowded together), which is exactly the moment the gate
                // is most needed.
                if (!_run.TryGetValue(refName, out var held))
                    continue;   // the holder just withdrew its claim → re-contend for this same address **in place**

                // On a miss it must never continue to the outer loop (moving to the next candidate address …~N): that
                // is not a collision, yet it would report a "Hash collision avoided" as if it were, and burn a
                // step-aside address for nothing.
                if (held.ContentKey == ck)
                {
                    // Same content in the same run → wait for the first uploader. Wait for its **whole item** to finish, not one volume.
                    tracker?.BeginWait(UploadWait.Peer);
                    try
                    {
                        return Resolution.ForExisting(await held.Completion, collision);
                    }
                    finally
                    {
                        tracker?.EndWait(UploadWait.Peer);
                    }
                }
                break; // different content in the same run holds this address → step aside to the next one
            }
        }
    }

    /// <summary>How a content identity is composed: fullHash + length + head + tail, all four.
    /// It is public so that in-run pack member dedup (<see cref="PackAliasTable"/>) uses the **same** composition —
    /// let the two paths each compose their own and sooner or later some change makes them quietly diverge, and diverging means the index points at somebody else's content.</summary>
    public static string ContentKey(string fullHash, long length, string? head, string? tail) =>
        $"{fullHash}\n{length}\n{head}\n{tail}";

    /// <summary>The prescreen's key: length + head hash. Shared with the sources, which store it as a row of its own
    /// (<c>reserved_heads</c>) — one composition, so the two can never disagree about what "this head" means.</summary>
    internal static string HeadKey(long length, string headHash) => $"{length}\n{headHash}";

    // ---- the synchronous surface, for callers not yet rewired (deleted with Build in Task 13) ----------------

    /// <inheritdoc cref="MayDeduplicateAsync"/>
    public bool MayDeduplicate(long length, string headHash) => Legacy.MayDeduplicate(length, headHash);

    /// <inheritdoc cref="NoteInFlightAsync"/>
    public void NoteInFlight(long length, string headHash) => Legacy.NoteInFlight(length, headHash);

    /// <inheritdoc cref="IsDamagedRefAsync"/>
    public bool IsDamagedRef(string @ref) => Legacy.IsDamagedRef(@ref);

    /// <inheritdoc cref="TryFindExistingAsync"/>
    public ResolvedBlob? TryFindExisting(string fullHash, long length, string headHash, string tailHash) =>
        Legacy.TryFindExisting(fullHash, length, headHash, tailHash);

    /// <inheritdoc cref="TryFindPackMemberAsync"/>
    public PackMemberRef? TryFindPackMember(string fullHash, long length, string headHash, string? tailHash) =>
        Legacy.TryFindPackMember(fullHash, length, headHash, tailHash);

    /// <summary>The token-less overload the not-yet-rewired callers use. It is the same call on either kind of
    /// resolver — resolving is asynchronous in both worlds — so unlike the members above it does not refuse.</summary>
    public Task<Resolution> ResolveAsync(
        string fullHash, long length, string headHash, string tailHash, StageTracker? tracker = null) =>
        ResolveAsync(fullHash, length, headHash, tailHash, CancellationToken.None, tracker);

    /// <summary>The maps, or a refusal. A catalog-backed resolver cannot answer synchronously and must not pretend
    /// to: blocking on the database here would be a deadlock waiting to happen, and a plausible-looking wrong answer
    /// would be worse than either.</summary>
    private LegacyDedupSource Legacy => _source as LegacyDedupSource ?? throw new InvalidOperationException(
        "This resolver answers from the catalog and the run's work database; use the asynchronous members.");

    /// <summary>
    /// Records a finished upload and retires the claim: the reservation row goes in first, so that from the instant
    /// the in-flight entry disappears there is already something for the next file with this content to find.
    /// </summary>
    private async Task CompleteAsync(
        Reservation reservation, string refName, bool raw, int volumes, IReadOnlyList<long> volumeSizes,
        CancellationToken ct)
    {
        var blob = new ResolvedBlob(refName, raw, volumes, volumeSizes);
        var recorded = await _source.RecordUploadAsync(reservation.ContentKey, blob, ct);
        reservation.Complete(blob);
        if (recorded)
            reservation.Release();
    }

    /// <summary>An in-run reservation for a ref: content identity + upload-completion signal.</summary>
    internal sealed class Reservation(string contentKey, Action release)
    {
        private readonly TaskCompletionSource<ResolvedBlob> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ContentKey => contentKey;
        public Task<ResolvedBlob> Completion => _tcs.Task;

        /// <summary>Hands the latecomers already waiting on this content their answer.</summary>
        public void Complete(ResolvedBlob blob) => _tcs.TrySetResult(blob);

        /// <summary>Withdraws the claim from the reservation table. Called once the upload's outcome is somewhere
        /// else — a reservation row (success) or nowhere at all (failure).</summary>
        public void Release() => release();

        /// <summary>Upload failed: first wake the latecomers already waiting on the same content in this run (they must
        /// never dedup onto a blob that was not uploaded successfully — that half of the behaviour is unchanged), then
        /// withdraw this ref's claim from the reservation table, so that the next round of ResolveAsync for the same
        /// content identity (whether a whole-item retry driven by Task 7's gate, or just the next same-content file by
        /// coincidence) can claim it afresh and really upload a second time, instead of running into an already-dead
        /// claim and replaying this very same exception.</summary>
        public void Fail(Exception ex)
        {
            _tcs.TrySetException(ex);
            release();
        }
    }

    /// <summary>The resolution result: a dedup hit (Exists) or a claim that needs uploading (Claim).</summary>
    public sealed class Resolution
    {
        private readonly LocalDedupResolver? _owner;
        private readonly Reservation? _reservation;

        private Resolution(
            LocalDedupResolver? owner, string @ref, bool collision, bool exists, ResolvedBlob? existing,
            Reservation? reservation)
        {
            _owner = owner;
            Ref = @ref;
            Collision = collision;
            Exists = exists;
            Existing = existing;
            _reservation = reservation;
        }

        public string Ref { get; }
        public bool Collision { get; }
        public bool Exists { get; }
        public ResolvedBlob? Existing { get; }

        internal static Resolution ForExisting(ResolvedBlob blob, bool collision) =>
            new(null, blob.Ref, collision, exists: true, blob, null);

        internal static Resolution ForClaim(
            LocalDedupResolver owner, string @ref, bool collision, Reservation reservation) =>
            new(owner, @ref, collision, exists: false, null, reservation);

        /// <summary>Called after a successful upload, so latecomers with the same content in this run get the same
        /// storage info, and so the next file with this content finds it without claiming the address again.</summary>
        public Task CompleteAsync(bool raw, int volumes, IReadOnlyList<long> volumeSizes, CancellationToken ct) =>
            _reservation is null
                ? Task.CompletedTask
                : _owner!.CompleteAsync(_reservation, Ref, raw, volumes, volumeSizes, ct);

        /// <summary>The token-less form, for the callers not yet rewired. It cannot write the reservation row, so it
        /// is only offered on a resolver built from the in-memory maps, where the claim itself is the record.</summary>
        public void Complete(bool raw, int volumes, IReadOnlyList<long> volumeSizes)
        {
            if (_reservation is null)
                return;

            _ = _owner!.Legacy;   // refuses a catalog-backed resolver rather than leaving a claim nothing can retire
            _reservation.Complete(new ResolvedBlob(Ref, raw, volumes, volumeSizes));
        }

        /// <summary>Called when the upload fails, making the waiting latecomers fail with it (so they never wrongly dedup onto a blob that does not exist).</summary>
        public void Fail(Exception ex) => _reservation?.Fail(ex);
    }
}
