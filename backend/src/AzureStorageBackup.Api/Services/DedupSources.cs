using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// What holds a blob address: the content identity occupying it, and — when the holder is an upload <em>this run</em>
/// has already finished — the blob itself.
/// <para>
/// <paramref name="Blob"/> is null for an address held by a retained version or by the adopted journal, because
/// those two answer the address question without carrying the storage details, and a claim that matches one of them
/// has to go through the damage check before it can be treated as a dedup hit. A non-null one is the opposite case
/// and needs no such check: this run put those exact bytes at that address a moment ago (a heal included), so a
/// later file with the same content deduplicates onto it, which is precisely what the in-flight table used to do
/// with its completed reservations.
/// </para>
/// </summary>
internal sealed record DedupRefOwner(string ContentKey, ResolvedBlob? Blob);

/// <summary>
/// Where <see cref="LocalDedupResolver"/>'s answers about content that already exists come from:
/// <see cref="CatalogDedupSource"/> asks the container's catalog and the run's work database, which is where those
/// facts live. It is an interface rather than a class so a test can hold one resolution still between two of its
/// lookups and see what a peer sees in that gap — which is not something a database can be asked to do.
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

    /// <summary>The content occupying a blob ref, or null if the address is free — collision avoidance's one
    /// question. Content identities are compared as <see cref="LocalDedupResolver.ContentKey"/> strings, so both
    /// implementations answer in the same currency.</summary>
    Task<DedupRefOwner?> RefOwnerAsync(string @ref, CancellationToken ct);

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

    public async Task<DedupRefOwner?> RefOwnerAsync(string @ref, CancellationToken ct)
    {
        // This run's own finished uploads come first, and they are the reason this lookup consults three places
        // rather than two. A completed claim used to stay in the in-flight table for the life of the run, so an
        // address this run had already written was never free again; the claim now leaves the table as soon as its
        // row is committed, and without this row the next file with that content — arriving in the instant between
        // the content probe and this one — would find the address free and upload straight over those volumes.
        // First, too, and not merely present: an address the catalog declares damaged has already been healed by
        // this very upload, so the peer must deduplicate onto it rather than heal it a second time.
        if (await work.ReservationByRefAsync(@ref, ct) is { } reserved)
        {
            var (contentKey, row) = reserved;
            return new DedupRefOwner(contentKey, new ResolvedBlob(row.Ref, row.Raw, row.Volumes, row.VolumeSizes));
        }

        if (await CatalogAsync(() => catalog.FindRefOwnerAsync(@ref, ct), ct) is { } owner)
            return new DedupRefOwner(
                LocalDedupResolver.ContentKey(owner.FullHash, owner.Length, owner.HeadHash, owner.TailHash), null);

        // Then the journal's record of the address. Head and tail are required for the same reason
        // JournalResume.ConfirmedBlobs requires them: without all four fields there is no content identity to
        // compare a claim against.
        return await work.ResumeBlobByRefAsync(@ref, ct) is { FullHash: { } full, HeadHash: { } head, TailHash: { } tail } record
            ? new DedupRefOwner(LocalDedupResolver.ContentKey(full, record.Length, head, tail), null)
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
