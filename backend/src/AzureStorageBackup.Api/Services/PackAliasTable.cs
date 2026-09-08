namespace AzureStorageBackup.Api.Services;

/// <summary>
/// A latecomer that does not get packed: its content equals some leader's, and its index entry will point
/// straight at the leader's member inside the pack.
/// <para>
/// It carries the full four-part content identity so that, if the leader goes astray, it can be rerun as an
/// ordinary pending file (see the orphan rerun at the end of the orchestrator) — that needs its own length and hashes.
/// </para>
/// </summary>
public sealed record PlannedAlias(
    string Path, long Length, string FullHash, string HeadHash, string TailHash);

/// <summary>
/// Cross-pack dedup of packed members within a single backup run.
/// <para>
/// Packed small files used to have only two dedup paths: within one pack via 7z's solid archive (dictionary
/// matching across members), and across versions via <see cref="LocalDedupResolver.TryFindPackMember"/>. What
/// was missing is the **within-run, cross-pack** stretch — different packs share no compression dictionary, so
/// the same content really does get stored twice, and <c>_packMembers</c> is built from historical version
/// indexes only, so packs sealed in this run never make it into that table.
/// </para>
/// <para>
/// This table only records "who came first", **not** "where it finally ended up" — that is unknown until every
/// consumer has finished (the leader may be rewritten inside the compression window, may be unreadable, may grow
/// past the threshold into a single-file blob). Backfill therefore happens once at the end, judging only the final
/// state. Aliases wait a little longer; in exchange this class needs no concurrency primitive at all.
/// </para>
/// <para>
/// Exclusively owned by the single-threaded diff, so no locking — the same constraint as
/// <c>dirPending</c>/<c>crossPending</c> in the orchestrator, and the same constraint
/// <see cref="PackLeaderStore"/>'s single connection is under.
/// </para>
/// </summary>
public sealed class PackAliasTable(PackLeaderStore leaders)
{
    // Leader path → the aliases hanging off it. **Only leaders that actually have aliases get a list**:
    // a first backup has hundreds of thousands of leaders, and an empty List for each wastes tens of MB.
    //
    // This is the half of the table that is genuinely bounded by duplicates, and the only half still in memory.
    // The other half — content key → the path that saw it first — used to be a Dictionary here, one entry for
    // **every** packed member rather than only for duplicates. Task 23's benchmark caught it: ~56 MB of live
    // managed heap per 100 000 files, surviving a forced collection. It now lives in PackLeaderStore's per-run
    // SQLite file.
    private readonly Dictionary<string, List<PlannedAlias>> _aliasesByLeader = new(StringComparer.Ordinal);

    /// <summary>Contains only leaders that actually have aliases. The end-of-run backfill walks this.</summary>
    public IReadOnlyDictionary<string, List<PlannedAlias>> AliasesByLeader => _aliasesByLeader;

    /// <summary>
    /// Whether this content already has a leader in this run.
    /// <para>
    /// Returns <c>true</c>: it does, <paramref name="path"/> has been registered as an alias of that leader,
    /// and the caller must **not** pack it. Returns <c>false</c>: this is the first copy (or the four parts are
    /// incomplete, so it takes no part in dedup) and it gets packed as usual.
    /// </para>
    /// <para>
    /// All four parts must match **exactly**, and a missing one counts as unequal — the same criteria as
    /// <see cref="LocalDedupResolver.TryFindPackMember"/>. Both are answering "does this content already exist",
    /// and two different standards for the same question makes no sense: get it wrong and the index points at
    /// someone else's content, and restore hands back wrong data.
    /// </para>
    /// <para>
    /// The four parts and <paramref name="path"/> are assembled into a <see cref="PlannedAlias"/> here, inside,
    /// rather than letting each caller build one and pass it in — the values used to decide "is this the same
    /// content" and the values finally recorded in <see cref="PlannedAlias"/> can then only ever be the same set,
    /// leaving no opening for "two paths each build their own and drift apart sooner or later" (same lesson as in
    /// the comment written when <see cref="LocalDedupResolver.ContentKey"/> was promoted to public).
    /// </para>
    /// </summary>
    public async ValueTask<bool> TryClaimAsync(
        string? fullHash, long length, string? headHash, string? tailHash, string path, CancellationToken ct)
    {
        // On the production path the caller has already done the three non-null checks with pattern matching
        // (file.FullHash is { } / c.HeadHash is { } / c.TailHash is { }), so what arrives here is always
        // non-null — today this branch is exercised only by unit tests (see
        // PackAliasTableTests.A_Missing_Component_Never_Participates). It stays not to guard the production
        // caller, but to guard against someone later calling this public method from elsewhere without that check.
        if (fullHash is null || headHash is null || tailHash is null)
            return false;

        var key = LocalDedupResolver.ContentKey(fullHash, length, headHash, tailHash);
        if (await leaders.ClaimAsync(key, path, ct).ConfigureAwait(false) is not { } leader)
            return false;   // this path is the leader now; the caller packs it as usual

        if (!_aliasesByLeader.TryGetValue(leader, out var list))
            _aliasesByLeader[leader] = list = [];
        list.Add(new PlannedAlias(path, length, fullHash, headHash, tailHash));
        return true;
    }
}
