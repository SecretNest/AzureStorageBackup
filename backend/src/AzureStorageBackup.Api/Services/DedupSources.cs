using System.Collections.Concurrent;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Where <see cref="LocalDedupResolver"/>'s answers about content that already exists come from. Two
/// implementations, for exactly as long as the migration lasts: <see cref="CatalogDedupSource"/> asks the container's
/// catalog and the run's work database, which is where those facts live now, and <see cref="LegacyDedupSource"/>
/// holds the dictionaries <c>LocalDedupResolver.Build</c> used to build, keeping the callers that have not been
/// rewired yet (the orchestrator, until Task 13) on exactly their old behaviour.
/// <para>
/// The in-flight reservation table is deliberately <em>not</em> behind this interface. It is the one piece of state
/// that is the same on both paths — a claim that has not finished uploading exists only in this process — so the
/// resolver owns it, and the source is only asked about what is already stored.
/// </para>
/// </summary>
internal interface IDedupSource
{
    /// <summary>The prescreen: might content with this length and head hash already exist? See
    /// <see cref="LocalDedupResolver.MayDeduplicateAsync"/> for why a false positive is the cheap error.</summary>
    Task<bool> MayDeduplicateAsync(long length, string headHash, CancellationToken ct);

    /// <summary>Records content this run has started on, so a later file with the same head takes the slow path.</summary>
    ValueTask NoteInFlightAsync(long length, string headHash, CancellationToken ct);

    /// <summary>Whether some retained version declares this blob ref's content unrecoverable.</summary>
    Task<bool> IsDamagedRefAsync(string @ref, CancellationToken ct);

    /// <summary>The existing single-file blob for this content identity, if there is one.</summary>
    Task<ResolvedBlob?> TryFindExistingAsync(
        string fullHash, long length, string headHash, string tailHash, CancellationToken ct);

    /// <summary>The existing pack member for this content identity, if there is one.</summary>
    Task<PackMemberRef?> TryFindPackMemberAsync(
        string fullHash, long length, string headHash, string? tailHash, CancellationToken ct);

    /// <summary>The content identity occupying a blob ref, or null if the address is free — collision avoidance's
    /// one question. Content identities are compared as <see cref="LocalDedupResolver.ContentKey"/> strings, so both
    /// implementations answer in the same currency.</summary>
    Task<string?> RefOwnerAsync(string @ref, CancellationToken ct);

    /// <summary>
    /// Records an upload this run has just finished. Returns whether it is now somewhere a later arrival with the
    /// same content will find it — which is the resolver's cue to drop the in-flight claim. The legacy source has
    /// nowhere to put it and says no, so its claims stay in the table and keep answering off their completion, the
    /// way they always did.
    /// </summary>
    Task<bool> RecordUploadAsync(string contentKey, ResolvedBlob blob, CancellationToken ct);
}

/// <summary>
/// The stored facts as they really live: the container's <see cref="VersionCatalog"/> for the retained versions, and
/// the run's <see cref="RunWorkDb"/> for the adopted journal's confirmed blocks, the heads this run has started on
/// and the uploads it has finished.
/// <para>
/// Every lookup that has two sources asks the catalog first. That is the <c>TryAdd</c> precedence the in-memory
/// build had: a block already committed to a version index has the last word over the journal's record of it.
/// </para>
/// </summary>
internal sealed class CatalogDedupSource(VersionCatalog catalog, RunWorkDb work) : IDedupSource
{
    /// <summary>
    /// <see cref="VersionCatalog"/> wraps a single connection and every reader streams off it, so two concurrent
    /// callers would interleave on one handle — and a resolver is asked by every upload worker at once. The work
    /// database needs no such gate (it opens a connection per read), so only the catalog's calls take it, and each
    /// one holds it for a single indexed lookup.
    /// </summary>
    private readonly SemaphoreSlim _catalogGate = new(1, 1);

    public async Task<bool> MayDeduplicateAsync(long length, string headHash, CancellationToken ct) =>
        await CatalogAsync(() => catalog.HeadSeenAsync(length, headHash, ct), ct)
        || await work.ResumeHeadSeenAsync(length, headHash, ct)
        || await work.ReservedHeadAsync(LocalDedupResolver.HeadKey(length, headHash), ct);

    public ValueTask NoteInFlightAsync(long length, string headHash, CancellationToken ct) =>
        work.InsertReservedHeadAsync(LocalDedupResolver.HeadKey(length, headHash), ct);

    public Task<bool> IsDamagedRefAsync(string @ref, CancellationToken ct) =>
        CatalogAsync(() => catalog.IsDamagedRefAsync(@ref, ct), ct);

    public async Task<ResolvedBlob?> TryFindExistingAsync(
        string fullHash, long length, string headHash, string tailHash, CancellationToken ct)
    {
        if (await CatalogAsync(() => catalog.FindBlobByContentAsync(fullHash, length, headHash, tailHash, ct), ct) is { } hit)
            return new ResolvedBlob(hit.Ref, hit.Raw, Math.Max(1, hit.Volumes), hit.VolumeSizes);

        // The journal's confirmed blocks, which are in the same situation as an indexed blob (in the cloud, address
        // taken) and only differ in that it is a journal recording them. Normalised exactly as
        // JournalResume.ConfirmedBlobs normalised them, volume count included.
        if (await work.ResumeBlobByContentAsync(fullHash, length, headHash, tailHash, ct) is { } resumed)
            return new ResolvedBlob(resumed.Ref, resumed.Raw, Math.Max(1, resumed.Volumes), resumed.VolumeSizes);

        // This run's own finished uploads. They used to be found through the in-flight table's completed
        // reservations; they are rows now, so the table can hold nothing but claims that are still going.
        return await work.ReservationAsync(
                LocalDedupResolver.ContentKey(fullHash, length, headHash, tailHash), ct) is { } reserved
            ? new ResolvedBlob(reserved.Ref, reserved.Raw, Math.Max(1, reserved.Volumes), reserved.VolumeSizes)
            : null;
    }

    public async Task<PackMemberRef?> TryFindPackMemberAsync(
        string fullHash, long length, string headHash, string? tailHash, CancellationToken ct) =>
        await CatalogAsync(() => catalog.FindPackMemberAsync(fullHash, length, headHash, ct), ct) is { } member
        && member.TailHash == tailHash
            ? new PackMemberRef(member.PackId, member.EntryName, member.TailHash)
            : null;

    public async Task<string?> RefOwnerAsync(string @ref, CancellationToken ct)
    {
        // An entry with no full hash at all never took part in the in-memory build (it was skipped before its
        // storage was even looked at), so an address only such an entry holds counts as free here too — otherwise
        // brand new content would step aside from an address nothing can ever claim. The catalog reports a missing
        // full hash as an empty string, which no real hash can be.
        if (await CatalogAsync(() => catalog.FindRefOwnerAsync(@ref, ct), ct) is { FullHash.Length: > 0 } owner)
            return LocalDedupResolver.ContentKey(owner.FullHash, owner.Length, owner.HeadHash, owner.TailHash);

        // Then the journal's record of the address. Head and tail are required for the same reason
        // JournalResume.ConfirmedBlobs requires them: without all four fields there is no content identity to
        // compare a claim against.
        return await work.ResumeBlobByRefAsync(@ref, ct) is { FullHash: { } full, HeadHash: { } head, TailHash: { } tail } record
            ? LocalDedupResolver.ContentKey(full, record.Length, head, tail)
            : null;
    }

    public async Task<bool> RecordUploadAsync(string contentKey, ResolvedBlob blob, CancellationToken ct)
    {
        await work.InsertReservationAsync(
            contentKey, new ReservationRow(blob.Ref, blob.Raw, blob.Volumes, blob.VolumeSizes), ct);
        // Flushed, not merely enqueued. The caller drops the in-flight claim as soon as this returns, and the work
        // database's writer batches for up to a fifth of a second — long enough for the next file with this content
        // to find neither the claim nor the row, claim the same address a second time and upload over the volumes
        // that were just written. One commit per finished upload is nothing next to the upload it follows.
        await work.FlushAsync(ct);
        return true;
    }

    private async Task<T> CatalogAsync<T>(Func<Task<T>> query, CancellationToken ct)
    {
        await _catalogGate.WaitAsync(ct);
        try
        {
            return await query();
        }
        finally
        {
            _catalogGate.Release();
        }
    }
}

/// <summary>
/// The dictionaries <c>LocalDedupResolver.Build</c> used to build from the retained versions' indexes, kept as a
/// source of their own so that callers still on the synchronous surface answer out of exactly the same maps, with
/// exactly the same precedence, as before the catalog existed. It dies with <c>Build</c> in Task 13.
/// </summary>
internal sealed class LegacyDedupSource : IDedupSource
{
    private readonly IReadOnlyDictionary<string, ResolvedBlob> _priorByContent; // content identity → existing blob (across versions)
    private readonly IReadOnlyDictionary<string, string> _priorRefs;            // ref already taken → its content identity (collision avoidance)
    private readonly IReadOnlySet<string> _priorHeads;                          // prescreen: existing content's "length\nhead"
    private readonly ConcurrentDictionary<string, byte> _runHeads =
        new(StringComparer.Ordinal);                                            // prescreen: what this run has started on
    // A pack member's content identity (three fields) → which member of which pack it sits on.
    private readonly IReadOnlyDictionary<string, PackMemberRef> _packMembers;
    // Blob refs some retained version marks unrecoverable: occupied names holding broken bytes. They stay in
    // _priorRefs (the name really is taken, by this very content), but a same-content claim on such a name is the
    // healing upload, not a reuse.
    private readonly IReadOnlySet<string> _damagedRefs;

    private LegacyDedupSource(
        IReadOnlyDictionary<string, ResolvedBlob> priorByContent,
        IReadOnlyDictionary<string, string> priorRefs,
        IReadOnlySet<string> priorHeads,
        IReadOnlyDictionary<string, PackMemberRef> packMembers,
        IReadOnlySet<string> damagedRefs)
    {
        _priorByContent = priorByContent;
        _priorRefs = priorRefs;
        _priorHeads = priorHeads;
        _packMembers = packMembers;
        _damagedRefs = damagedRefs;
    }

    /// <summary>Builds the maps from the retained versions' second-level indexes (single-file blobs use content
    /// addressing; pack members get a separate table). The body is <c>LocalDedupResolver.Build</c>'s, unchanged —
    /// see <see cref="LocalDedupResolver.Build"/> for what <paramref name="confirmed"/> is and why it must be fed in.</summary>
    public static LegacyDedupSource Build(IEnumerable<VersionIndex> indexes, IEnumerable<ConfirmedBlob>? confirmed)
    {
        var byContent = new Dictionary<string, ResolvedBlob>(StringComparer.Ordinal);
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        var heads = new HashSet<string>(StringComparer.Ordinal);
        var packMembers = new Dictionary<string, PackMemberRef>(StringComparer.Ordinal);
        var damagedRefs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var index in indexes)
        {
            // Damage is a first-class fact dedup must respect (volume-identity.md): an entry whose path this
            // version marks unrecoverable references a broken blob, and offering it as a dedup target would hand
            // a brand new file — its bytes sitting right there on disk — a reference to garbage, born dead.
            // Excluded, the new file compresses and uploads through the replacement primitive to the same content
            // address, and thereby heals the family in passing. The ref stays in the collision table below: the
            // name really is occupied, by this very content, so a healing upload lands at the base address with
            // no collision detour.
            var marked = index.UnrecoverablePaths.Count == 0
                ? null
                : new HashSet<string>(index.UnrecoverablePaths, StringComparer.Ordinal);
            foreach (var e in index.Entries)
            {
                if (e.FullHash is null)
                    continue;
                if (marked?.Contains(e.Path) == true)
                {
                    // Blob refs still occupy their name for collision avoidance; everything else is withheld.
                    if (e.Storage is { Kind: "blob" } damaged)
                    {
                        refs.TryAdd(damaged.Ref, LocalDedupResolver.ContentKey(e.FullHash, e.Length, e.HeadHash, e.TailHash));
                        damagedRefs.Add(damaged.Ref);
                    }
                    continue;
                }

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
                        // onto the old pack, where dead-weight compaction is less likely to rewrite it. Newer-version
                        // entries with the same content point at the same content anyway, so any of them will do.
                        packMembers.TryAdd(
                            PackMemberKey(e.FullHash, e.Length, e.HeadHash),
                            new PackMemberRef(p.Ref, p.EntryName ?? e.Path, e.TailHash));
                    }
                    continue;
                }

                if (e.Storage is not { Kind: "blob" } s)
                    continue;
                var ck = LocalDedupResolver.ContentKey(e.FullHash, e.Length, e.HeadHash, e.TailHash);
                byContent[ck] = new ResolvedBlob(s.Ref, s.Raw, Math.Max(1, s.Volumes), s.VolumeSizes);
                refs[s.Ref] = ck;
                // Old entries whose HeadHash is null do not join the prescreen set: head is null in their content
                // identity too, so they match no new file that can compute a head, and could never have hit dedup anyway.
                if (e.HeadHash is not null)
                    heads.Add(LocalDedupResolver.HeadKey(e.Length, e.HeadHash));
            }
        }

        foreach (var c in confirmed ?? [])
        {
            // TryAdd rather than overwrite: the copy already committed to a version index has the last word. When the
            // two really do collide (same content identity) they record the same ref anyway, so it makes no difference
            // who wins; the only way they differ is the case where the index is the more authoritative one.
            var ck = LocalDedupResolver.ContentKey(c.FullHash, c.Length, c.HeadHash, c.TailHash);
            byContent.TryAdd(ck, c.Blob);
            refs.TryAdd(c.Blob.Ref, ck);
            heads.Add(LocalDedupResolver.HeadKey(c.Length, c.HeadHash));
        }

        return new LegacyDedupSource(byContent, refs, heads, packMembers, damagedRefs);
    }

    public bool MayDeduplicate(long length, string headHash)
    {
        var key = LocalDedupResolver.HeadKey(length, headHash);
        return _priorHeads.Contains(key) || _runHeads.ContainsKey(key);
    }

    public void NoteInFlight(long length, string headHash) =>
        _runHeads.TryAdd(LocalDedupResolver.HeadKey(length, headHash), 0);

    public bool IsDamagedRef(string @ref) => _damagedRefs.Contains(@ref);

    public ResolvedBlob? TryFindExisting(string fullHash, long length, string headHash, string tailHash) =>
        _priorByContent.GetValueOrDefault(LocalDedupResolver.ContentKey(fullHash, length, headHash, tailHash));

    public PackMemberRef? TryFindPackMember(string fullHash, long length, string headHash, string? tailHash) =>
        _packMembers.GetValueOrDefault(PackMemberKey(fullHash, length, headHash)) is { } member
        && member.TailHash == tailHash
            ? member
            : null;

    public string? RefOwner(string @ref) => _priorRefs.GetValueOrDefault(@ref);

    // The asynchronous surface is the synchronous one wrapped: the maps are in memory, so there is nothing here to
    // await, and answering both ways off one body is what keeps the two paths from drifting.
    Task<bool> IDedupSource.MayDeduplicateAsync(long length, string headHash, CancellationToken ct) =>
        Task.FromResult(MayDeduplicate(length, headHash));

    ValueTask IDedupSource.NoteInFlightAsync(long length, string headHash, CancellationToken ct)
    {
        NoteInFlight(length, headHash);
        return ValueTask.CompletedTask;
    }

    Task<bool> IDedupSource.IsDamagedRefAsync(string @ref, CancellationToken ct) => Task.FromResult(IsDamagedRef(@ref));

    Task<ResolvedBlob?> IDedupSource.TryFindExistingAsync(
        string fullHash, long length, string headHash, string tailHash, CancellationToken ct) =>
        Task.FromResult(TryFindExisting(fullHash, length, headHash, tailHash));

    Task<PackMemberRef?> IDedupSource.TryFindPackMemberAsync(
        string fullHash, long length, string headHash, string? tailHash, CancellationToken ct) =>
        Task.FromResult(TryFindPackMember(fullHash, length, headHash, tailHash));

    Task<string?> IDedupSource.RefOwnerAsync(string @ref, CancellationToken ct) => Task.FromResult(RefOwner(@ref));

    /// <summary>Nowhere to record it: a completed reservation stays in the resolver's in-flight table and keeps
    /// answering later arrivals off its completion, which is what this path has always done.</summary>
    Task<bool> IDedupSource.RecordUploadAsync(string contentKey, ResolvedBlob blob, CancellationToken ct) =>
        Task.FromResult(false);

    private static string PackMemberKey(string fullHash, long length, string head) =>
        $"{fullHash}\n{length}\n{head}";
}
