using System.Text;
using System.Text.Json;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Overview of one journal volume. <b>Does not parse record bodies</b>: only the first line is deserialized, the rest are merely counted.
/// The UI's "which runs stopped partway" list calls this over and over, and one journal can hold hundreds of thousands of lines —
/// deserializing every line as JSON just to show a single number is not worth the cost.
/// </summary>
public sealed record JournalSummary(string RunId, JournalHeader Header, int Records, long SizeBytes);

/// <summary>
/// The blob base names and packIds referenced by every **active** journal on a container.
/// The cleaner uses it as the "don't delete me" list: this content exists in the cloud but not yet in the index,
/// and only the journal records that it exists — deleting it makes the resume run for nothing.
/// </summary>
public sealed record ActiveJournalRefs(IReadOnlySet<string> Blobs, IReadOnlySet<string> Packs)
{
    public static readonly ActiveJournalRefs Empty =
        new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
}

/// <summary>
/// Journal layout on disk: <c>{root}/{accountId}/{container}/{runId}.jsonl</c>.
/// <para>
/// Directories keyed by (accountId, container) rather than by configId — the cleaner locates a container by exactly those two
/// and does not have a configId at all. The configId is recorded in the journal header and read from there when needed.
/// </para>
/// </summary>
public sealed class BackupJournalStore(string rootDir)
{
    /// <summary>
    /// Container names are not supposed to contain slashes, but do not treat that as a guarantee: always flatten before joining a path.
    /// <para>
    /// All-dot names (<c>.</c>, <c>..</c>) get replaced too. They contain no invalid character at all, yet they are path segments
    /// rather than names, and joining them walks up a level — while <see cref="DeleteAll"/> is <c>Directory.Delete(recursive: true)</c>,
    /// so walking up one level too far deletes another container's restore points. Nothing can reach this today (Azure container
    /// names may not contain dots at all, and runIds we generate ourselves); this line exists purely because we do not count on upstream behaving forever.
    /// </para>
    /// </summary>
    private static string Safe(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), chars[i]) >= 0 || chars[i] is '/' or '\\')
                chars[i] = '_';
        var flat = new string(chars);
        return flat.Length > 0 && flat.All(c => c == '.') ? new string('_', flat.Length) : flat;
    }

    private string DirFor(int accountId, string container)
        => Path.Combine(rootDir, accountId.ToString(), Safe(container));

    public string PathFor(int accountId, string container, string runId)
        => Path.Combine(DirFor(accountId, container), Safe(runId) + ".jsonl");

    /// <summary>Suspend marker: same name and same directory as the journal, just one extra suffix. <c>ListAsync</c>/<c>PeekAsync</c>
    /// only enumerate <c>*.jsonl</c>, so by construction it can never be mistaken for a journal volume and parsed.</summary>
    private string MarkPathFor(int accountId, string container, string runId)
        => PathFor(accountId, container, runId) + ".suspend";

    /// <summary>
    /// Record **why** this volume stopped. Automatic resume honours exactly one of them: only on reading
    /// <see cref="SuspendReason.ShuttingDown"/> may we pick the run back up unasked (that was a planned restart or upgrade);
    /// everything else waits for an operator to press Resume.
    /// <para>
    /// Be precise about what an **absent** marker means — it does not equal "killed": it could be a kill, a crashed process, a
    /// shutdown whose flush-to-disk timed out leaving this volume stranded, an operator pressing Cancel themselves (the cancel
    /// path still flushes, but writes no marker), or simply this file write itself failing. None of those should be restarted
    /// automatically — hence the test is "act only when we read ShuttingDown", not "no marker means it crashed".
    /// </para>
    /// <para>
    /// There is one more asymmetry in durability; do not lean on it. On the <c>SettleStopAsync</c> path the marker is written
    /// **after** the journal fsync, so the marker can never be "newer" than the records it describes; but the gate downgrade
    /// (<see cref="SuspendReason.AutoSuspended"/>) path is thrown straight up from deep in the pipeline, so the marker lands
    /// first and the journal is only closed when control is released — and this uses <c>File.WriteAllText</c>, with no fsync.
    /// Crash-safe, not power-cut-safe: worst case the marker survives and the records it describes do not, costing one extra file upload next run.
    /// </para>
    /// <para>
    /// A separate file rather than one more record in the journal: the journal is an append-only record stream, and
    /// <see cref="LoadActiveRefsAsync"/> is the binary split <c>r.Kind == "pack" ? packs : blobs</c> —
    /// a third Kind would be silently dumped into the blobs bucket, polluting the cleaner's "don't delete me" list.
    /// </para>
    /// </summary>
    public void MarkSuspended(int accountId, string container, string runId, SuspendReason reason)
    {
        try
        {
            var path = MarkPathFor(accountId, container, runId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, reason.ToString());
        }
        catch { /* can't write it, so treat it as unmarked: the cost is one extra run, not lost data */ }
    }

    /// <summary>
    /// Wipe this volume's suspend marker. For the "this volume changed hands" case: the marker describes why **one particular
    /// run** stopped, and once a journal volume is adopted by a new run, the run that wrote that reason has been superseded and its reason is void with it.
    /// <para>
    /// The difference from <see cref="Delete"/> is that the journal itself stays: adoption is read-only, and the old volume is
    /// not deleted until the new run successfully commits its index (<see cref="BackupRunControl.CompleteAsync"/>). In the
    /// meantime its marker should be rewritten by **the run that took it over** (see <see cref="BackupRunControl.MarkSuspended"/>),
    /// rather than left carrying the previous run's stale value.
    /// </para>
    /// </summary>
    public void ClearSuspendMark(int accountId, string container, string runId)
    {
        try { File.Delete(MarkPathFor(accountId, container, runId)); } catch { /* can't delete it; try again next time */ }
    }

    /// <summary>Read the suspend reason. Missing file, unreadable file, or contents we do not recognise all return null (= treat as unmarked).</summary>
    public SuspendReason? ReadSuspendMark(int accountId, string container, string runId)
    {
        try
        {
            var text = File.ReadAllText(MarkPathFor(accountId, container, runId)).Trim();
            return Enum.TryParse<SuspendReason>(text, ignoreCase: false, out var reason) ? reason : null;
        }
        catch { return null; }
    }

    public Task<BackupJournal> CreateAsync(
        int accountId, string container, string runId, JournalHeader header, CancellationToken ct)
        => BackupJournal.CreateAsync(PathFor(accountId, container, runId), header, ct);

    /// <summary>Append to a journal volume that already exists. Used only when "this run's runId collides with the volume on disk";
    /// for the reasoning see <see cref="BackupRunControl.OpenJournalAsync"/>.</summary>
    public Task<BackupJournal> AppendAsync(int accountId, string container, string runId, CancellationToken ct)
        => BackupJournal.OpenForAppendAsync(PathFor(accountId, container, runId), ct);

    /// <summary>List every journal on this container that reads cleanly. The ones that do not (broken header) are simply treated as absent.</summary>
    public async Task<IReadOnlyList<(string RunId, JournalContent Content)>> ListAsync(
        int accountId, string container, CancellationToken ct)
    {
        var dir = DirFor(accountId, container);
        if (!Directory.Exists(dir))
            return [];

        var result = new List<(string, JournalContent)>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal))
        {
            var content = await BackupJournal.ReadAsync(file, ct);
            if (content is not null)
                result.Add((Path.GetFileNameWithoutExtension(file), content));
        }
        return result;
    }

    /// <summary>
    /// The line count already tallied for one journal volume. <see cref="PeekAsync"/> relies on it to avoid rewalking the whole volume on every poll.
    /// </summary>
    /// <param name="StartedAt">The start time written in that volume's header when this tally was taken. See the invalidation rules in <see cref="PeekAsync"/>.</param>
    /// <param name="Length">The file length when the tally finished.</param>
    /// <param name="SafeOffset">The offset just past the last newline. Only lines before it count as fully tallied.</param>
    /// <param name="CompleteLines">Non-empty lines before SafeOffset (including the header line).</param>
    /// <param name="TotalLines">Non-empty lines counting the trailing unterminated partial line as well (including the header line).</param>
    private sealed record LineMemo(
        DateTimeOffset StartedAt, long Length, long SafeOffset, int CompleteLines, int TotalLines);

    private readonly Dictionary<string, LineMemo> _lineMemos = new(StringComparer.Ordinal);
    private readonly Lock _memoLock = new();

    /// <summary>For tests: how many bytes this instance has actually read so far in order to count lines. Whether the memo works can only be pinned down by measuring.
    /// It hangs off the instance rather than a static field: test classes run in parallel, and a static counter would be muddied by another class's <see cref="PeekAsync"/>.</summary>
    internal long BytesScanned;

    /// <summary>
    /// List an overview of every journal volume on this container. Volumes whose header does not read are skipped (= that volume is void).
    /// <para>
    /// Line counts are tallied **incrementally**, not rewalked every time. With the UI open this endpoint is called once every 5
    /// seconds for every config, and one journal can grow to hundreds of MB over a run of two hundred thousand files — rewalking
    /// means hundreds of MB of reads per minute, contending for the very disk the backup is reading; and a suspended volume just sits there on disk, so that cost would never stop.
    /// </para>
    /// <para>
    /// Memo invalidation rules (the journal is append-only, so these three together are enough):
    /// <list type="bullet">
    /// <item>The header's <see cref="JournalHeader.StartedAt"/> changed → this volume was <b>rewritten by another run</b>
    /// (<see cref="BackupJournal.CreateAsync"/> uses <c>FileMode.Create</c>, which truncates); the old count is worthless, recount from scratch.
    /// Length alone does not catch this one: the rewritten file can easily be the same length as the old one, or longer.</item>
    /// <item>The file is shorter than recorded → likewise replaced, recount from scratch.</item>
    /// <item>Length unchanged and StartedAt unchanged → for an append-only file, unchanged length means unchanged content, so hand back last time's number.</item>
    /// </list>
    /// If it grew, count only the new stretch, and resume from **the last newline we counted to** and no further: this file is
    /// not fsynced per record, the snapshot may land mid-line, and resuming from the end of the file would count the second half of that partial line as a line all over again.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<JournalSummary>> PeekAsync(int accountId, string container, CancellationToken ct)
    {
        var dir = DirFor(accountId, container);
        if (!Directory.Exists(dir))
            return [];

        var result = new List<JournalSummary>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal))
        {
            JournalHeader? header;
            long length;
            int lines;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                string? first;
                using (var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true))
                    first = await reader.ReadLineAsync(ct);
                if (first is null)
                    continue;
                try { header = JsonSerializer.Deserialize<JournalHeader>(first, JournalJson.Options); }
                catch (JsonException) { continue; }
                if (header is null)
                    continue;

                // Take the length from the handle we already have open rather than a separate FileInfo: counting lines and recording
                // the length must be the **same** snapshot — between two samples the volume may have grown, and the memo we store would then claim "at this length I counted this many lines".
                length = stream.Length;
                lines = await CountLinesAsync(file, stream, header, length, ct);
            }
            catch (IOException)
            {
                continue;   // the volume being written is occasionally not openable; leave it to the next poll
            }
            // The header line is not a record, so subtract it from the total.
            result.Add(new JournalSummary(
                Path.GetFileNameWithoutExtension(file), header, Math.Max(0, lines - 1), length));
        }
        return result;
    }

    /// <summary>Count the non-empty lines of this volume up to <paramref name="length"/> (including the header line), resuming from last time's tally wherever possible.</summary>
    private async Task<int> CountLinesAsync(
        string file, FileStream stream, JournalHeader header, long length, CancellationToken ct)
    {
        LineMemo? memo;
        lock (_memoLock)
            memo = _lineMemos.GetValueOrDefault(file);

        var reusable = memo is not null && memo.StartedAt == header.StartedAt && memo.Length <= length;
        if (reusable && memo!.Length == length)
            return memo.TotalLines;

        var from = reusable ? memo!.SafeOffset : 0;
        var completeBefore = reusable ? memo!.CompleteLines : 0;

        stream.Seek(from, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        var complete = 0;
        var scanned = 0L;
        var lastNewlineEnd = from;
        var segmentHasContent = false;
        while (scanned < length - from)
        {
            var want = (int)Math.Min(buffer.Length, length - from - scanned);
            var n = await stream.ReadAsync(buffer.AsMemory(0, want), ct);
            if (n <= 0)
                break;
            // Find line ends by byte. In UTF-8 0x0A can never appear as a continuation byte of a multi-byte character, so scanning
            // by byte gives the same result as scanning by character, without decoding hundreds of MB into strings just to produce
            // one number. IndexOf takes the vectorized path; comparing byte by byte ourselves is several times slower — and hundreds of MB is exactly what this code has to chew through.
            var rest = buffer.AsSpan(0, n);
            var consumed = 0;
            while (true)
            {
                var nl = rest.IndexOf((byte)'\n');
                if (nl < 0)
                {
                    segmentHasContent |= HasContent(rest);
                    break;
                }
                if (segmentHasContent || HasContent(rest[..nl]))
                    complete++;
                segmentHasContent = false;
                consumed += nl + 1;
                lastNewlineEnd = from + scanned + consumed;
                rest = rest[(nl + 1)..];
            }
            scanned += n;
        }
        Interlocked.Add(ref BytesScanned, scanned);

        // The trailing unterminated partial line counts as a line, per StreamReader.ReadLine's old rule, but it does **not** go into the memo:
        // later bytes may complete it into a whole line at any moment, and having recorded it we would then count it again along with the new line.
        var total = completeBefore + complete + (segmentHasContent ? 1 : 0);
        lock (_memoLock)
        {
            if (_lineMemos.Count > 512 && !_lineMemos.ContainsKey(file))
                foreach (var stale in _lineMemos.Keys.Where(k => !File.Exists(k)).ToList())
                    _lineMemos.Remove(stale);
            _lineMemos[file] = new LineMemo(
                header.StartedAt, length, lastNewlineEnd, completeBefore + complete, total);
        }
        return total;
    }

    /// <summary>Whether this segment has any body. An empty line is not a line, and neither is one holding only <c>\r</c> (<c>ReadLine</c> treats CRLF as a single line ending).</summary>
    private static bool HasContent(ReadOnlySpan<byte> segment) => segment.IndexOfAnyExcept((byte)'\r') >= 0;

    /// <summary>Gather everything referenced by every active journal on this container. Half of the cleanup test.</summary>
    public async Task<ActiveJournalRefs> LoadActiveRefsAsync(int accountId, string container, CancellationToken ct)
    {
        var blobs = new HashSet<string>(StringComparer.Ordinal);
        var packs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, content) in await ListAsync(accountId, container, ct))
            foreach (var r in content.Records)
                (r.Kind == "pack" ? packs : blobs).Add(r.Ref);
        return blobs.Count == 0 && packs.Count == 0 ? ActiveJournalRefs.Empty : new ActiveJournalRefs(blobs, packs);
    }

    public void Delete(int accountId, string container, string runId)
    {
        try { File.Delete(PathFor(accountId, container, runId)); } catch { /* can't delete it; try again next time */ }
        // The marker goes with the journal: leave it behind and a later run colliding on the same runId would read the previous reason.
        try { File.Delete(MarkPathFor(accountId, container, runId)); } catch { /* same as above */ }
    }

    /// <summary>Backstop for config deletion: throw away every journal for this container.</summary>
    public void DeleteAll(int accountId, string container)
    {
        try
        {
            var dir = DirFor(accountId, container);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch { /* same as above */ }
    }
}
