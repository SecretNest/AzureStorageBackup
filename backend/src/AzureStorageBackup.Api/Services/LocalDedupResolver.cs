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
/// Purely local single-file blob dedup / collision resolution (no cloud reads). The self-hosted backup's local cache
/// index already holds every blob's content identity (fullHash + length + head + tail) and its storage info, so dedup,
/// collision avoidance, volume count and raw can all be decided locally.
/// <para>
/// Across versions: look up the "content identity → existing blob" map built from the retained version indexes.
/// Within one backup run: coordinate through the in-run reservation table (one
/// <see cref="TaskCompletionSource{T}"/> per ref) — a latecomer with the same content waits for the first uploader to
/// finish and gets the same (ref, raw, volume count); different content landing on the same address steps aside to …~N.
/// </para>
/// Trusting the local index = the cloud truth (consistent with the "read the cloud as little as possible" design);
/// external modification of the cloud is for Check to discover.
/// </summary>
public sealed class LocalDedupResolver
{
    private readonly BlobAddressScheme _addressing;
    private readonly IReadOnlyDictionary<string, ResolvedBlob> _priorByContent; // content identity → existing blob (across versions)
    private readonly IReadOnlyDictionary<string, string> _priorRefs;            // ref already taken → its content identity (collision avoidance)
    private readonly ConcurrentDictionary<string, Reservation> _run = new(StringComparer.Ordinal);
    private readonly IReadOnlySet<string> _priorHeads;                                  // prescreen: existing content's "length\nhead"
    private readonly ConcurrentDictionary<string, byte> _runHeads = new(StringComparer.Ordinal); // prescreen: what this run has started on
    // A pack member's content identity (three fields) → which member of which pack it sits on. See the notes on TryFindPackMember.
    private readonly IReadOnlyDictionary<string, PackMemberRef> _packMembers;

    private LocalDedupResolver(
        BlobAddressScheme addressing,
        IReadOnlyDictionary<string, ResolvedBlob> priorByContent,
        IReadOnlyDictionary<string, string> priorRefs,
        IReadOnlySet<string> priorHeads,
        IReadOnlyDictionary<string, PackMemberRef> packMembers)
    {
        _addressing = addressing;
        _priorByContent = priorByContent;
        _priorRefs = priorRefs;
        _priorHeads = priorHeads;
        _packMembers = packMembers;
    }

    /// <summary>
    /// Might there **possibly** be an existing blob with the same content this round? It only looks at length + head
    /// hash, so it can answer without reading the whole file.
    /// <para>
    /// Streaming compression only knows the full-content hash once compression is done, so "compute the full hash
    /// first, then decide on dedup" means reading the file one extra time. This prescreen lets a first backup (not one
    /// candidate anywhere) take the single-read fast path, and only pays for that extra pass when candidates really exist.
    /// Better a false positive than a miss: a false positive only costs one extra read, whereas a miss makes content that could have been skipped entirely get compressed for nothing.
    /// </para>
    /// </summary>
    public bool MayDeduplicate(long length, string headHash)
    {
        var key = HeadKey(length, headHash);
        return _priorHeads.Contains(key) || _runHeads.ContainsKey(key);
    }

    /// <summary>Registers content this run has already started on (length + head): on the strength of this, a later
    /// file with the same content takes the prescreen's slow path, looking it up once rather than compressing it for nothing.</summary>
    public void NoteInFlight(long length, string headHash) => _runHeads.TryAdd(HeadKey(length, headHash), 0);

    /// <summary>Only consults the cross-version map; it does **not** take a ref and creates no reservation. For the
    /// prescreen path of "probe once, and on a hit skip compression entirely"; an actual upload must still go through <see cref="ResolveAsync"/> to get a reservation.</summary>
    public ResolvedBlob? TryFindExisting(string fullHash, long length, string headHash, string tailHash) =>
        _priorByContent.GetValueOrDefault(ContentKey(fullHash, length, headHash, tailHash));

    private static string HeadKey(long length, string headHash) => $"{length}\n{headHash}";

    /// <summary>
    /// Builds the maps from the retained versions' second-level indexes (single-file blobs use content addressing;
    /// pack members get a separate table, see below).
    /// </summary>
    /// <param name="confirmed">
    /// The blocks in an adopted journal that are "confirmed in the cloud but not yet in the index" (<see cref="ConfirmedBlob"/>).
    /// <para>
    /// **They must be fed in**, and not merely to avoid one extra upload. Resume accounts by **path**: the previous run
    /// finished uploading A and then suspended, before it reached B, which has the same content as A. This run reuses A
    /// directly without uploading, but B does not recognise that it already exists, so it recompresses, then
    /// ResolveAsync hands it the **same** ref (content addressing: same content, same address), and then
    /// <c>UploadStagedBlobAsync</c> first calls <c>ClearLeftoverVolumesAsync</c> to delete every volume under that ref
    /// before re-uploading — and A's index entry is pointing at exactly those. If that delete-then-upload window is
    /// interrupted by Stop now or by a process crash, only half a set of volumes is left in the cloud, and the next run
    /// adopting the journal reuses A as usual and commits the index as usual,
    /// pointing at content that is missing volumes. The error only becomes visible at restore or check time.
    /// </para>
    /// <para>
    /// Once they are fed in, B takes the cross-version dedup path: neither recompressed nor re-uploaded, and that set
    /// of volumes never gets the chance to be touched. They also go into <c>refs</c> (different content landing on this
    /// address still steps aside to …~N) and into the prescreen set.
    /// </para>
    /// </param>
    public static LocalDedupResolver Build(
        BlobAddressScheme addressing, IEnumerable<VersionIndex> indexes,
        IEnumerable<ConfirmedBlob>? confirmed = null)
    {
        var byContent = new Dictionary<string, ResolvedBlob>(StringComparer.Ordinal);
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        var heads = new HashSet<string>(StringComparer.Ordinal);
        var packMembers = new Dictionary<string, PackMemberRef>(StringComparer.Ordinal);
        foreach (var index in indexes)
        {
            foreach (var e in index.Entries)
            {
                if (e.FullHash is null)
                    continue;

                // Pack member: the content already sits inside some existing pack, so a new file with the same content
                // points straight at it instead of packing another box. Duplicates within one pack are already
                // eliminated by 7z's solid archive (the dictionary matches across members); what this really saves is
                // the **cross-pack, cross-version** part — separate packs do not share a compression dictionary, so the
                // same content really would be stored twice.
                if (e.Storage is { Kind: "pack" } p)
                {
                    if (e.HeadHash is not null)
                    {
                        // Several retained versions may each hold a member with the same content. The **reference**
                        // takes the first one encountered (versions are passed in oldest to newest): references pile
                        // onto the old pack, where dead-weight compaction is less likely to rewrite it.
                        // Take the first one encountered (versions passed in oldest to newest): references pile onto the
                        // old pack, where dead-weight compaction is less likely to rewrite it. Newer-version entries
                        // with the same content point at the same content anyway, so any of them will do.
                        packMembers.TryAdd(
                            PackMemberKey(e.FullHash, e.Length, e.HeadHash),
                            new PackMemberRef(p.Ref, p.EntryName ?? e.Path, e.TailHash));
                    }
                    continue;
                }

                if (e.Storage is not { Kind: "blob" } s)
                    continue;
                var ck = ContentKey(e.FullHash, e.Length, e.HeadHash, e.TailHash);
                byContent[ck] = new ResolvedBlob(s.Ref, s.Raw, Math.Max(1, s.Volumes), s.VolumeSizes);
                refs[s.Ref] = ck;
                // Old entries whose HeadHash is null do not join the prescreen set: head is null in their content
                // identity too, so they match no new file that can compute a head, and could never have hit dedup anyway.
                if (e.HeadHash is not null)
                    heads.Add(HeadKey(e.Length, e.HeadHash));
            }
        }
        foreach (var c in confirmed ?? [])
        {
            // TryAdd rather than overwrite: the copy already committed to a version index has the last word. When the
            // two really do collide (same content identity) they record the same ref anyway, so it makes no difference
            // who wins; the only way they differ is the case where the index is the more authoritative one.
            var ck = ContentKey(c.FullHash, c.Length, c.HeadHash, c.TailHash);
            byContent.TryAdd(ck, c.Blob);
            refs.TryAdd(c.Blob.Ref, ck);
            heads.Add(HeadKey(c.Length, c.HeadHash));
        }
        return new LocalDedupResolver(addressing, byContent, refs, heads, packMembers);
    }

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
    public PackMemberRef? TryFindPackMember(string fullHash, long length, string headHash, string? tailHash) =>
        _packMembers.GetValueOrDefault(PackMemberKey(fullHash, length, headHash)) is { } member
        && member.TailHash == tailHash
            ? member
            : null;

    private static string PackMemberKey(string fullHash, long length, string head) =>
        $"{fullHash}\n{length}\n{head}";

    /// <summary>Resolves a piece of content: a hit on something existing → dedup; otherwise claim a free ref for the caller to upload, and fill in (raw, volume count) once done.</summary>
    /// <param name="tracker">Optional progress bookkeeping. Within one run, a latecomer with the same content has to
    /// wait for the first uploader to finish the **whole item** — that can be minutes, and the wait falls after
    /// compression and before upload, with neither a stream uploading nor an item compressing on screen.
    /// Without marking it, the UI is simply frozen solid, with no way even to say who is being waited on.</param>
    public async Task<Resolution> ResolveAsync(
        string fullHash, long length, string headHash, string tailHash, StageTracker? tracker = null)
    {
        var ck = ContentKey(fullHash, length, headHash, tailHash);
        if (_priorByContent.TryGetValue(ck, out var prior))
            return Resolution.ForExisting(prior, collision: false); // cross-version dedup

        var baseAddr = _addressing.DataAddress(fullHash);
        for (var n = 0; ; n++)
        {
            var refName = n == 0 ? baseAddr : $"{baseAddr}~{n}";
            var collision = n > 0;

            if (_priorRefs.TryGetValue(refName, out var priorCk))
            {
                if (priorCk != ck)
                    continue;                                     // an older version's different content holds this address → step aside
                return Resolution.ForExisting(                     // in theory _priorByContent already hit; a safe backstop
                    new ResolvedBlob(refName, false, 1, []), collision);
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
                    return Resolution.ForClaim(refName, collision, mine); // I will do the upload

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

    /// <summary>An in-run reservation for a ref: content identity + upload-completion signal.</summary>
    internal sealed class Reservation(string contentKey, Action release)
    {
        private readonly TaskCompletionSource<ResolvedBlob> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ContentKey => contentKey;
        public Task<ResolvedBlob> Completion => _tcs.Task;
        public void Complete(string refName, bool raw, int volumes, IReadOnlyList<long> volumeSizes) =>
            _tcs.TrySetResult(new ResolvedBlob(refName, raw, volumes, volumeSizes));

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
        private readonly Reservation? _reservation;

        private Resolution(string @ref, bool collision, bool exists, ResolvedBlob? existing, Reservation? reservation)
        {
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
            new(blob.Ref, collision, exists: true, blob, null);

        internal static Resolution ForClaim(string @ref, bool collision, Reservation reservation) =>
            new(@ref, collision, exists: false, null, reservation);

        /// <summary>Called after a successful upload, so latecomers with the same content in this run get the same storage info.</summary>
        public void Complete(bool raw, int volumes, IReadOnlyList<long> volumeSizes) =>
            _reservation?.Complete(Ref, raw, volumes, volumeSizes);

        /// <summary>Called when the upload fails, making the waiting latecomers fail with it (so they never wrongly dedup onto a blob that does not exist).</summary>
        public void Fail(Exception ex) => _reservation?.Fail(ex);
    }
}
