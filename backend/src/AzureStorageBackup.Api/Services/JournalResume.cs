namespace AzureStorageBackup.Api.Services;

/// <summary>
/// A lookup table built from however many journal volumes still count, answering "was this content already uploaded last run".
/// <para>
/// The test is always **path plus content, both matching**. Path alone will not do — the file may well have been modified after
/// the interruption; content hash alone will not do either — the journal records by path, and identical content at a different path is a separate entry in the index.
/// </para>
/// <para>
/// When the same path shows up in several volumes the newer one wins; for the rule see <see cref="FromVolumes"/>.
/// </para>
/// <para>
/// Pure memory, purely local, no cloud reads. A record only enters the journal once "the upload has been confirmed returned", so
/// there is no need (and no business) checking against the cloud again here — that would violate the "zero cloud reads during a backup" bottom line.
/// </para>
/// </summary>
public sealed class JournalResume(IReadOnlyList<JournalRecord> records)
{
    public static readonly JournalResume Empty = new([]);

    /// <summary>
    /// Build the table from however many journal volumes. Records are chained **newest to oldest by start time**, so that the "first hit wins" below lands as "the newer one wins".
    /// <para>
    /// Without that ordering the order comes from <see cref="BackupJournalStore.ListAsync"/>, which is ordinal by file name, and
    /// the file name is the runId — a freshly generated GUID prefix each run. So when "the same path is recorded with different
    /// content in two volumes" (the file was modified between two suspends), who wins is a dice roll. Losing the roll loses no
    /// upload: the shadowed record is out of reach from both <see cref="FindBlob"/> and
    /// <see cref="ConfirmedBlobs"/>, and if the four content tests do not match we treat it as absent and upload it anyway;
    /// while over in the cleaner <c>LoadActiveRefsAsync</c> walks every record one by one, so the shadowed block stays protected.
    /// The cost is only "re-uploading the very version last run already uploaded", and it comes and goes from run to run — that
    /// kind of nondeterminism has no business on the resume path.
    /// </para>
    /// </summary>
    public static JournalResume FromVolumes(IReadOnlyList<JournalContent> volumes)
        => volumes.Count == 0
            ? Empty
            : new JournalResume([..
                volumes.OrderByDescending(v => v.Header.StartedAt).SelectMany(v => v.Records)]);

    /// <summary>Single-file blob records indexed by path. On a duplicate path the first hit wins; the caller
    /// (<see cref="FromVolumes"/>) has already sorted the volumes newest to oldest by start time, so the winner is whatever the newest volume recorded.</summary>
    private readonly Dictionary<string, JournalRecord> _blobs = BuildBlobs(records);

    /// <summary>Pack records indexed by the canonical key of their member set.</summary>
    private readonly Dictionary<string, JournalRecord> _packs = BuildPacks(records);

    public bool IsEmpty => _blobs.Count == 0 && _packs.Count == 0;

    public int RecordCount => _blobs.Count + _packs.Count;

    private static Dictionary<string, JournalRecord> BuildBlobs(IReadOnlyList<JournalRecord> records)
    {
        var map = new Dictionary<string, JournalRecord>(StringComparer.Ordinal);
        foreach (var r in records)
            if (r.Kind == "blob" && r.Path is { } p && r.FullHash is not null)
                map.TryAdd(p, r);
        return map;
    }

    private static Dictionary<string, JournalRecord> BuildPacks(IReadOnlyList<JournalRecord> records)
    {
        var map = new Dictionary<string, JournalRecord>(StringComparer.Ordinal);
        foreach (var r in records)
            if (r.Kind == "pack" && r.Members.Count > 0)
                map.TryAdd(MemberKey(r.Members), r);
        return map;
    }

    /// <summary>
    /// Canonical key of a member set: path + full hash + length, joined in order.
    /// <para>
    /// **Deliberately excludes <see cref="JournalMember.EntryName"/>**. In this repo it is always identical to the member's own
    /// path (<c>new PackEntry(f.Path, f.Path, …)</c>, see <c>BackupOrchestrator.ProcessPackAsync</c>; the passage in
    /// <c>RestoreOrchestrator</c> about "fetching by EntryName rather than Path" is about **cross-version dedup**, where a new
    /// path points at an old member name in an old pack — it is not that packing invents a separate numbering scheme), so
    /// folding it into the key has no discriminating power at all beyond counting the same content's path a second time.
    /// </para>
    /// <para>
    /// Order counts too: <c>PackInfo.Members</c> is a run of member hashes in order, and a key blind to order would let two
    /// orderings of the same member set hit each other, leaving the order recorded in the info file at odds with the archive's actual contents.
    /// </para>
    /// </summary>
    private static string MemberKey(IReadOnlyList<JournalMember> members)
        => string.Join('\n', members.Select(m => $"{m.Path}\0{m.FullHash}\0{m.Length}"));

    /// <summary>Exact match for one single-file blob. Only accepted when all four content tests match.</summary>
    public JournalRecord? FindBlob(string path, string fullHash, long length, string headHash, string tailHash)
        => _blobs.TryGetValue(path, out var r)
            && string.Equals(r.FullHash, fullHash, StringComparison.Ordinal)
            && r.Length == length
            && string.Equals(r.HeadHash, headHash, StringComparison.Ordinal)
            && string.Equals(r.TailHash, tailHash, StringComparison.Ordinal)
            ? r
            : null;

    /// <summary>
    /// Hand these single-file records over keyed by **content identity**, to feed <see cref="LocalDedupResolver.Build"/>.
    /// <para>
    /// Resume itself accounts by path (see the class remarks), but in the cloud these blocks are in exactly the same position as
    /// blocks in the index: uploaded, address taken. Without telling the dedup table, a file with **the same content at a
    /// different path** would not be recognised as it; after recompressing, ResolveAsync hands back the same address, and the
    /// stale-volume cleanup just before upload deletes last run's work and uploads it all over again.
    /// See the notes on the <c>confirmed</c> parameter of <c>LocalDedupResolver.Build</c>.
    /// </para>
    /// <para>All four content tests are required; records missing any are skipped — an incomplete identity has no business taking part in dedup.</para>
    /// </summary>
    public IReadOnlyList<ConfirmedBlob> ConfirmedBlobs()
    {
        var list = new List<ConfirmedBlob>(_blobs.Count);
        foreach (var r in _blobs.Values)
            if (r is { FullHash: { } full, HeadHash: { } head, TailHash: { } tail })
                list.Add(new ConfirmedBlob(
                    full, r.Length, head, tail,
                    new ResolvedBlob(r.Ref, r.Raw, Math.Max(1, r.Volumes), r.VolumeSizes)));
        return list;
    }

    /// <summary>
    /// Exact match for one pack. The member sets must be identical item for item; no leniency.
    /// <para>
    /// The reason is not about names (a member's name in the archive is just the member's own path, see <see cref="MemberKey"/>)
    /// but that **the accounting and the archive must line up exactly**: on a hit, <c>RecordPackAsync</c> takes **this run's**
    /// member list to write <c>PackInfo.Members</c> / <c>OriginalBytes</c>, and writes an index entry pointing at
    /// this pack for every member. Allowing a partial match (this run's group being a superset of last run's pack) amounts to
    /// claiming the archive holds members it simply does not: restore cannot extract the file, check reports it missing, and the index insists it is there.
    /// A subset is no good either — <c>OriginalBytes</c> comes out short, and dead-weight compaction uses it to judge how much live flesh is left in this pack.
    /// </para>
    /// <para>
    /// Grouping itself is deterministic (same baseline, same source, same bounds), so strict equality hits often enough in practice;
    /// and when it does not match we recompress — a pack is all small files, so recompressing is cheap.
    /// </para>
    /// </summary>
    public JournalRecord? FindPack(IReadOnlyList<JournalMember> members)
        => members.Count > 0 && _packs.TryGetValue(MemberKey(members), out var r) ? r : null;
}
