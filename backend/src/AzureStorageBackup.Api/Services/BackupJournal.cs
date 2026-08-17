using System.Text;
using System.Text.Json;

namespace AzureStorageBackup.Api.Services;

/// <summary>Serialization settings shared by journal reads and writes. The read side is not just <see cref="BackupJournal"/> (there is also the directory overview),
/// and both sides must use one and the same set, or the same line of bytes decodes to different results in the two places.</summary>
internal static class JournalJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
}

/// <summary>The journal's first line: everything the resume preflight checks need is in here.</summary>
public sealed record JournalHeader
{
    public required string RunId { get; init; }
    public required int ConfigId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>The baseline version this run diffs against. If the baseline changed (somebody else completed a run), this journal volume is void.</summary>
    public required int BaselineVersion { get; init; }

    /// <summary>The local source root. Change the root directory and the meaning of the paths changes, so it is void.</summary>
    public required string LocalRoot { get; init; }

    /// <summary>Encryption identity fingerprint (<see cref="BlobAddressScheme.Identity"/>). Change the password and the address space changes, so it is void.</summary>
    public required string EncryptionIdentity { get; init; }
}

/// <summary>One member of a pack. Resume relies on it to rebuild <c>PackInfo</c> and each member's StorageRef.</summary>
public sealed record JournalMember(string Path, string EntryName, string FullHash, long Length);

/// <summary>One "this block of content has been confirmed in the cloud".</summary>
public sealed record JournalRecord
{
    /// <summary>"blob" or "pack".</summary>
    public required string Kind { get; init; }

    /// <summary>blob: the data blob's base name (e.g. <c>data/abc</c>); pack: the packId.</summary>
    public required string Ref { get; init; }

    // blob only, below
    public string? Path { get; init; }
    public string? FullHash { get; init; }
    public string? HeadHash { get; init; }
    public string? TailHash { get; init; }
    public long Length { get; init; }
    public bool Raw { get; init; }

    /// <summary>
    /// The source file's last-write time when this blob was uploaded, as UTC ticks.
    /// <para>
    /// Nullable because journals written before this field existed must keep working: null means "this record
    /// cannot answer the cheap question", and the resume falls back to reading the file exactly as it did
    /// before. No format version and no migration — an absent field deserialises to null.
    /// </para>
    /// <para>
    /// Ticks rather than a formatted timestamp so the comparison is exact. A round trip through a rendered
    /// time is where "equal" quietly stops meaning equal.
    /// </para>
    /// </summary>
    public long? MtimeUtcTicks { get; init; }

    // pack only, below
    public bool StoreOnly { get; init; }
    public IReadOnlyList<JournalMember> Members { get; init; } = [];

    public int Volumes { get; init; } = 1;
    public IReadOnlyList<long> VolumeSizes { get; init; } = [];
}

/// <summary>A whole journal volume as read back.</summary>
public sealed record JournalContent(JournalHeader Header, IReadOnlyList<JournalRecord> Records);

/// <summary>
/// The resume log of one backup run: append-only JSONL whose first line is a <see cref="JournalHeader"/>,
/// with one <see cref="JournalRecord"/> per line after it.
/// <para>
/// **The ordering is this file's entire reason for existing**: compress → upload → upload confirmed returned → only then append a line.
/// Get the order backwards and we record a block that is not actually in the cloud, and the next resume skips straight over it — data loss.
/// </para>
/// <para>
/// **No fsync per record**: the costs are asymmetric. One record missing = one extra file uploaded next time; fsync on every
/// record = one extra disk sync per file. So after a crash the last line may be a half-written stub, and <see cref="ReadAsync"/> skips lines it cannot parse.
/// Only the deliberate-suspend tail really fsyncs (that is the moment we promise "flushed to disk before returning").
/// </para>
/// </summary>
public sealed class BackupJournal : IAsyncDisposable
{
    private readonly FileStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private BackupJournal(FileStream stream) => _stream = stream;

    /// <summary>Create a new journal volume and write its first line. The parent directory is created if it does not exist.</summary>
    public static async Task<BackupJournal> CreateAsync(string path, JournalHeader header, CancellationToken ct)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var journal = new BackupJournal(stream);
        await journal.WriteLineAsync(JsonSerializer.Serialize(header, JournalJson.Options), ct);
        await journal.FlushAsync(fsync: true, ct);   // if the header does not land, everything after it is pointless; this one sync is worth it
        return journal;
    }

    /// <summary>
    /// Append to a journal volume that **already exists**.
    /// <para>
    /// The header line is not rewritten: the caller has already checked term by term that it still counts (see
    /// <see cref="BackupRunControl.OpenJournalAsync"/>), and rewriting it would turn append-only into rewritable. The only
    /// occasion for using this instead of <see cref="CreateAsync"/> is this run's runId colliding with the volume on disk — there we must append; see the notes at the call site.
    /// </para>
    /// </summary>
    public static async Task<BackupJournal> OpenForAppendAsync(string path, CancellationToken ct)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var journal = new BackupJournal(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read));
        // Emit a newline first. This file is not fsynced per record, so after a crash the last line may be a half-written stub
        // (see the class remarks); appending straight on would glue that stub and the newly recorded line into one, so **the new
        // one** fails to parse as well — and it records content this run just confirmed uploaded, so losing it means uploading
        // that block for nothing next run. ReadAsync always skips blank lines, so this one extra byte is harmless.
        await journal.WriteLineAsync("", ct);
        return journal;
    }

    /// <summary>Append one record. The call site must be **after the upload has been confirmed returned**.</summary>
    public async Task AppendAsync(JournalRecord record, CancellationToken ct)
        => await WriteLineAsync(JsonSerializer.Serialize(record, JournalJson.Options), ct);

    /// <param name="fsync">When true, flush the operating system buffers to disk as well (used by the deliberate-suspend tail).</param>
    public async Task FlushAsync(bool fsync, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await _stream.FlushAsync(ct);
            if (fsync)
                _stream.Flush(flushToDisk: true);
        }
        finally { _writeLock.Release(); }
    }

    private async Task WriteLineAsync(string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await _writeLock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(bytes, ct);
            await _stream.FlushAsync(ct);   // only flush to the OS, not to disk; see the class remarks
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>Read the whole volume. Missing file, empty file, or a broken header all return null (= this volume is void, treat it as no resume point).</summary>
    public static async Task<JournalContent?> ReadAsync(string path, CancellationToken ct)
    {
        JournalHeader? header = null;
        var records = new List<JournalRecord>();

        // No File.Exists before opening — there is a real gap between those two steps: while the cleaner scans one container's
        // active journals, another backup run may finish and delete its own volume. A delete inside that gap throws
        // FileNotFoundException and takes the whole cleanup run down. Just open, catch "it is gone", and fold it into the same answer as "it was never there": this volume is void.
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
                continue;
            if (header is null)
            {
                try { header = JsonSerializer.Deserialize<JournalHeader>(line, JournalJson.Options); }
                catch (JsonException) { return null; }   // broken header, the whole volume is void
                if (header is null)
                    return null;
                continue;
            }
            try
            {
                if (JsonSerializer.Deserialize<JournalRecord>(line, JournalJson.Options) is { } record)
                    records.Add(record);
            }
            catch (JsonException)
            {
                // A half-written line left by a crash. Normally it can only be the last line; if one really does show up in the
                // middle we merely recognise a few records fewer, costing a few extra file uploads, not data loss. Keep reading to the end.
            }
        }

        return header is null ? null : new JournalContent(header, records);
    }

    public async ValueTask DisposeAsync()
    {
        try { await _stream.FlushAsync(); } catch { /* can't flush on close; never mind */ }
        await _stream.DisposeAsync();
        _writeLock.Dispose();
    }
}
