using System.Text.Json.Nodes;

namespace AzureStorageBackup.Api.Models;

// Data models for the info record file (the authoritative metadata blob, PRD 1.5) and the second-level version index (M4 design §3).

/// <summary>Info record file (§3.1): config + version list + pack metadata. The single source of truth for cross-device recovery.</summary>
public sealed record BackupInfoFile
{
    public int SchemaVersion { get; init; } = 1;
    public required BackupMeta Backup { get; init; }
    public List<BackupVersion> Versions { get; init; } = [];
    public Dictionary<string, PackInfo> Packs { get; init; } = [];
}

/// <summary>Snapshot of the backup config (immutable after creation, apart from name/description).</summary>
public sealed record BackupMeta
{
    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>Source root path hint, for reference only; the user re-specifies it at recovery time (§3.1).</summary>
    public string? SourceRootHint { get; init; }

    public bool Encrypted { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The settings in effect for this backup (a snapshot of the resolved defaults). The schema waits on the M4 settings page being finalized, so an open object is used for now.</summary>
    public JsonObject? Settings { get; init; }

    /// <summary>
    /// Key derivation salt for encrypted backups (generated randomly at first creation). Used for keyed addressing of data blobs (defeats fingerprinting):
    /// key = HKDF(password, KdfSalt), blob name = data/{HMAC(key, fullHash)}. null for unencrypted backups.
    /// </summary>
    public byte[]? KdfSalt { get; init; }
}

/// <summary>One immutable version (§3.1 versions[]), referencing its second-level index.</summary>
public sealed record BackupVersion
{
    public int Version { get; init; }

    /// <summary>The moment the version was committed (end of the backup). The trailing cleanup runs for a while after this and is not counted.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The moment this backup started running. Versions written before info format 3 don't carry it → null,
    /// and it cannot be backfilled (a guessed number is worse than an empty one).</summary>
    public DateTimeOffset? StartedAt { get; init; }

    public required string IndexBlob { get; init; }

    /// <summary>
    /// How many volumes the second-level index was split into (1 = a single blob under <see cref="IndexBlob"/>).
    /// <para>
    /// The index is the one blob in a backup whose size nobody can bound in advance: it grows with the file count,
    /// measured here at 161 bytes per entry, and the file count is not knowable before the scan. A million files
    /// puts it past the SDK's single-shot threshold and well past what one request can carry up a home uplink.
    /// Volumes are named exactly like a data blob's (<c>name.001</c>…), so <see cref="Services.VolumeBlobIO.VolumeNames"/>
    /// and <see cref="Services.VolumeBlobIO.IsVolumeOf"/> apply unchanged.
    /// </para>
    /// <para>
    /// Recorded here rather than probed for: an info file written before this field reads back 1 and keeps taking
    /// the single-blob path, so every existing backup stays readable without a single extra request. Probing would
    /// have meant a 404 round trip on every index read, and — worse — would make a genuinely missing index
    /// indistinguishable from an old single-blob one.
    /// </para>
    /// </summary>
    public int IndexVolumes { get; init; } = 1;

    public required VersionStats Stats { get; init; }
}

/// <summary>Version stats (for progress/display; deletions do not count toward changed).</summary>
public sealed record VersionStats(long Files, long Bytes, long ChangedFiles, long ChangedBytes);

/// <summary>Pack metadata (§6 dead-weight compaction tracking).</summary>
public sealed record PackInfo
{
    public required string Blob { get; init; }
    public List<string> Members { get; init; } = [];
    public long OriginalBytes { get; init; }
    public long DeadBytes { get; init; }

    /// <summary>Number of volumes in the pack archive (1 = not split). Compaction changes it, so it is updated along with PackInfo, letting a check verify that every volume exists (§7).</summary>
    public int Volumes { get; init; } = 1;

    /// <summary>Byte size of each volume (in .001..N order). Lets the "exists + size" level of check spot truncation/wrong packs without downloading. May be empty in older info files (→ existence check only).</summary>
    public List<long> VolumeSizes { get; init; } = [];

    /// <summary>This box is store-only (<c>-mx0</c>): every one of its members hit the configured don't-compress rules.
    /// <para>
    /// It has to be recorded on the pack, because two paths **rewrite the archive of an existing packId** — dead-weight
    /// compaction and repair repacking. All they hold is the surviving members and a packId, not the rules from back
    /// then; without this, a store-only pack that survives one version retirement gets repacked with the default compression, and nothing about it shows.
    /// </para>
    /// <para>
    /// Nor do we switch to "re-derive from the rules at rewrite time": then a rule change would silently change the
    /// compression of old packs at the next compaction, whereas this value recorded on the pack is stable. Older info files read back <c>false</c>, which is exactly the historical behavior (always compress).
    /// </para></summary>
    public bool StoreOnly { get; init; }
}

/// <summary>Second-level version index (§3.2): the full file manifest of that version + empty directories.</summary>
public sealed record VersionIndex
{
    public int Version { get; init; }
    public List<IndexEntry> Entries { get; init; } = [];

    /// <summary>Empty directories (backup must include them, restore must create them).</summary>
    public List<string> EmptyDirs { get; init; } = [];

    /// <summary>File paths in this version that can no longer be recovered (damaged in the cloud and unrepairable from local
    /// as well). Written by the repair flow; restore uses it to let the user substitute each one from another version.</summary>
    public List<string> UnrecoverablePaths { get; init; } = [];
}

/// <summary>Index entry: one file/symlink and where it is stored.</summary>
public sealed record IndexEntry
{
    public required string Path { get; init; }

    /// <summary>"file" | "symlink".</summary>
    public required string Kind { get; init; }

    public long Length { get; init; }
    public DateTimeOffset Mtime { get; init; }
    public required string Permissions { get; init; }

    public string? HeadHash { get; init; }

    /// <summary>Hash of the file's tail segment (§ dedup collision hardening). Together with HeadHash/Length/FullHash it forms
    /// the content identity, so a self-hosted backup can decide dedup/collision purely locally (without reading the cloud). May be null in older indexes.</summary>
    public string? TailHash { get; init; }

    public string? FullHash { get; init; }

    /// <summary>symlink target (only when kind=symlink).</summary>
    public string? Target { get; init; }

    /// <summary>This round failed to re-read the file (locked/no permission/read error), so the entry's content is carried over
    /// from the previous version. null = read normally in this version. The value is when it happened, so the operator can tell how old this stale content is.</summary>
    public DateTimeOffset? UnreadableAt { get; init; }

    public StorageRef? Storage { get; init; }
}

/// <summary>Where an entry is stored: a single-file blob, or a member inside a grouped pack.</summary>
public sealed record StorageRef
{
    /// <summary>"blob" | "pack".</summary>
    public required string Kind { get; init; }

    /// <summary>blob: data/{fullHash}; pack: packId.</summary>
    public required string Ref { get; init; }

    /// <summary>Entry name inside the pack (only when kind=pack).</summary>
    public string? EntryName { get; init; }

    /// <summary>
    /// Number of volumes for a single-file blob (1 = not split, §7). Content addressing is immutable, so the count is stable, letting a check verify every volume exists.
    /// Meaningless for pack members (a pack's volume count lives in <see cref="PackInfo.Volumes"/>, because compaction changes it).
    /// </summary>
    public int Volumes { get; init; } = 1;

    /// <summary>
    /// This blob holds the **raw file bytes** rather than a 7z archive (PRD 3.3.2: uncompressed + unencrypted + no splitting needed means the original file is uploaded directly, saving one wrapping step).
    /// Single-file blobs only; restore/check copy/hash it directly instead of extracting.
    /// </summary>
    public bool Raw { get; init; }

    /// <summary>Byte size of each volume (in .001..N order). Lets the "exists + size" level of check spot truncation/wrong packs without downloading. May be empty in older indexes (→ existence check only).</summary>
    public List<long> VolumeSizes { get; init; } = [];
}
