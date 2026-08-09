using System.Text;
using System.Threading.Channels;

namespace AzureStorageBackup.Api.Services;

/// <summary>One item of work on the pipeline: a single-file blob, or one sealed pack's worth of members.</summary>
/// <param name="StoreOnly">This pack is stored, not compressed (<c>-mx0</c>). Packing already split by
/// compressibility, so the compression mode rides with the pack all the way to the compression step and the consumer
/// side no longer derives it itself — what it gets is a member list, while the rules match on paths, and deriving it
/// again means writing the same judgement twice.
/// The single-file path ignores this value: <c>HandleBlobAsync</c> derives it from the path itself (same rules, same
/// method).</param>
internal readonly record struct WorkItem(
    PlannedFile? Single, IReadOnlyList<PlannedFile>? Pack, bool StoreOnly = false)
{
    /// <summary>How many <see cref="PlannedFile"/> this item carries.</summary>
    public int Members => Single is not null ? 1 : Pack?.Count ?? 0;

    /// <summary>Whichever shape it takes, treat it as a member sequence (spill serialisation and read-back share this one view).</summary>
    public IReadOnlyList<PlannedFile> AsMembers => Single is { } single ? [single] : Pack ?? [];

    /// <summary>
    /// Roughly how many bytes this item weighs on the managed heap. The queue's byte limit accumulates this.
    /// <para>
    /// Deliberately an overestimate: in items that have **not yet spilled**, the path strings share instances with the
    /// scan results, so strictly speaking that part is not incremental; but what comes back from disk is a fresh
    /// string, and then this queue really is the one holding it. With both kinds of item mixed under the same limit,
    /// only counting them as "held by us" avoids underestimating — and an underestimate makes the limit meaningless.
    /// </para>
    /// </summary>
    public long EstimatedBytes
    {
        get
        {
            long total = 0;
            foreach (var m in AsMembers)
                total += 48 + StringBytes(m.Path) + StringBytes(m.FullHash);
            return total;
        }
    }

    /// <summary>Strings are sized by the CLR's actual layout: 24-byte object header + 2 bytes per char (UTF-16), aligned to 8 bytes.</summary>
    private static long StringBytes(string? s) =>
        s is null ? 0 : (24 + (2L * s.Length) + 7) & ~7L;
}

/// <summary>
/// The per-segment limits for <see cref="DiffWorkQueue"/>. All configurable (see <c>Backup:DiffQueue*</c> in Program.cs).
/// </summary>
/// <param name="MaxCachedItems">r segment: how many **items** of work may pile up in memory. The item is the unit the
/// pipeline speaks in, and it is what the UI's processed/queued/total counts too, so this is the main knob.</param>
/// <param name="MaxCachedBytes">r segment's byte backstop. Item count alone cannot hold memory down: one item can be a
/// single-file blob, or one pack of twenty thousand small files (fill a 100 MB pack with 5 KB files and that is the
/// number) — four orders of magnitude apart.</param>
/// <param name="WriteBatchItems">w segment: how many items to accumulate before flushing a batch into the temp file.</param>
/// <param name="WriteBatchBytes">w segment's byte backstop. **This one cannot be dropped**: w is in memory too, and
/// bounding it by item count alone means 200 full packs in the small-file case are several GB — the limit set for the
/// r segment gets bypassed right here.</param>
/// <param name="RefillBatchItems">How many items to fetch from the temp file at a time. Fetching in batches amortises
/// IO: fetching one at a time costs a lock and a Flush per item, and the read-back sits on the consumer side's
/// critical path.</param>
/// <param name="FileBufferBytes">The temp file's <see cref="FileStream"/> buffer. The write side's batching actually
/// happens mostly right here — each item's Write only writes into this buffer, and only a full buffer issues a real
/// syscall.</param>
public sealed record DiffQueueLimits(
    int MaxCachedItems = 2_000,
    long MaxCachedBytes = 64L * 1024 * 1024,
    int WriteBatchItems = 200,
    long WriteBatchBytes = 8L * 1024 * 1024,
    int RefillBatchItems = 1_000,
    int FileBufferBytes = 256 * 1024);

/// <summary>
/// The queue between the diff and compress-and-upload. The write side (diff) **never blocks**.
/// <para>
/// The whole queue is three segments, head on the left, tail on the right:
/// </para>
/// <code>
///   rrrrr | fffffffffff | www
///     r  = in memory, waiting to be picked up by a consumer
///     f  = in the temp file
///     w  = in memory, waiting to be written into the temp file in a batch
/// </code>
/// <para>
/// A "limit" is a **number fixed in advance** (<see cref="DiffQueueLimits"/>), not "wait until memory runs out". This
/// queue does not watch the process's memory level, and should not: it manages its own share only, and puts the excess
/// on disk. The r segment and the w segment are **both** counted in the memory budget — w is in memory too, and
/// leaving it out makes the budget a lie.
/// </para>
/// <para>
/// Why the write side must not block: the upload stage's remaining time cannot be computed until
/// <c>StageTracker.SetTotal</c> (see the first line of <c>StageProgress.Eta</c>: <c>_total &lt;= 0</c> returns null
/// outright), and that total is only settled once the diff has finished. The moment the queue holds the write side up,
/// the diff can only inch forward at the upload's pace — so "the diff is done" = "there is only one queue depth of work
/// left", and the remaining time refuses to appear until the tail end of the whole backup.
/// No queue size escapes this; the only answer is to never stop the write side at all.
/// </para>
/// <para>
/// <b>At least one item ready</b>: when the r segment is empty the next item is admitted unconditionally, even if that
/// item alone exceeds the entire limit (fill a 100 MB pack with 1-byte files and that is hundreds of millions of
/// members). This exception must exist on **both the write side and the read-back side** — keep it only on the write
/// side and, once that oversized item has spilled, the limit blocks it again on read-back and the pump and the
/// consumers stall in place together.
/// The price is that the real memory peak = max(limit, the largest single item): the limit is a soft floor, not a hard
/// ceiling. To actually bound "how big one item can get" you have to add a per-pack member cap at the
/// <c>GroupingPlanner</c> sealing layer; this queue cannot decide it.
/// </para>
/// <para>
/// FIFO holds across all three segments: as long as f or w is non-empty, a newly arriving item always goes into w,
/// otherwise it would jump ahead of everything already queued. Order does not actually matter for correctness (pack
/// numbers are assigned at processing time, see <c>RunState.NextPackId</c>),
/// but out-of-order work makes the UI's "current file" jump back and forth between directories, and there is no reason
/// to sacrifice that for nothing.
/// </para>
/// <para>
/// <b>When f is empty, w goes straight into r without touching the disk.</b> Once the consumer side catches up there
/// is no point writing w's items out and reading them back — the half-batch at the end of the diff is the most obvious
/// case, and skipping this shortcut is a pure wasted round trip to disk.
/// </para>
/// </summary>
internal sealed class DiffWorkQueue : IDisposable
{
    private readonly Lock _gate = new();
    /// <summary>The r segment. The Channel itself is unbounded — the real bounds are
    /// <see cref="DiffQueueLimits.MaxCachedItems"/> and <see cref="DiffQueueLimits.MaxCachedBytes"/> enforced on the
    /// write side and the read-back side; the Channel is here only to get its wait/completion semantics for free,
    /// not to serve as the upper bound.</summary>
    private readonly Channel<WorkItem> _cache = Channel.CreateUnbounded<WorkItem>();
    /// <summary>The w segment. A Queue rather than a List: read-back takes from the front, and removing from the front of a List is O(n).</summary>
    private readonly Queue<WorkItem> _pendingWrite = new();
    /// <summary>Wakes the read-back pump: a new item entered w, r consumed one (freeing space), the write side finished, or we are disposing.</summary>
    private readonly SemaphoreSlim _wake = new(0);
    private readonly string? _spillPath;
    private readonly DiffQueueLimits _limits;
    private readonly Task _pump;

    // Two handles on the same file: the write side only appends, the read side only moves forward. Both move inside
    // _gate, so the positions never fight.
    private FileStream? _writeStream;
    private BinaryWriter? _writer;
    private FileStream? _readStream;
    private BinaryReader? _reader;

    private int _cachedItems;
    private long _cachedBytes;
    private long _pendingWriteBytes;
    private long _onDisk;        // f segment: items already spilled and not yet read back
    private long _spilledTotal;  // cumulative items really written into the file (monotonic, for the UI)
    private bool _addingDone;
    private int _disposed;

    /// <param name="spillPath">Full path of the spill file; pass null = no spilling, unbounded memory (the fallback for tests and for when no temp disk is configured).</param>
    /// <param name="limits">The per-segment limits.</param>
    public DiffWorkQueue(string? spillPath, DiffQueueLimits limits)
    {
        _spillPath = spillPath;
        _limits = limits with
        {
            MaxCachedItems = Math.Max(1, limits.MaxCachedItems),
            MaxCachedBytes = Math.Max(1, limits.MaxCachedBytes),
            WriteBatchItems = Math.Max(1, limits.WriteBatchItems),
            WriteBatchBytes = Math.Max(1, limits.WriteBatchBytes),
            RefillBatchItems = Math.Max(1, limits.RefillBatchItems),
            FileBufferBytes = Math.Max(4096, limits.FileBufferBytes),
        };
        _pump = spillPath is null ? Task.CompletedTask : Task.Run(PumpAsync);
    }

    /// <summary>How many items of work in total were really written into the temp file. For the UI — it is a direct
    /// readout of "how much faster the diff is running than the upload".
    /// It only grows when the w segment flushes: items that sat in w and later went straight into r never touched the
    /// disk and must not be counted.</summary>
    public long SpilledItems => Interlocked.Read(ref _spilledTotal);

    /// <summary>How many items r holds right now, their estimated bytes, and how many w is still holding back. For diagnostics and tests.</summary>
    public (int Items, long Bytes, int PendingWrite) Cached
    {
        get { lock (_gate) return (_cachedItems, _cachedBytes, _pendingWrite.Count); }
    }

    /// <summary>
    /// Clears, at process startup, the spill files left behind by a previous abnormal exit.
    /// <para>
    /// It may only be done at **process startup**, never at the start of each backup: several backups can run at once,
    /// and clearing per run would delete files someone else is using. Each run uses its own random file name and
    /// deletes only its own on a normal shutdown (see <see cref="Dispose"/>); this is the backstop only for what the
    /// process-was-killed path leaves behind.
    /// </para>
    /// </summary>
    public static void ClearStale(string spillDir)
    {
        try
        {
            Directory.CreateDirectory(spillDir);
            foreach (var file in Directory.EnumerateFiles(spillDir, "*.spill"))
            {
                try { File.Delete(file); }
                catch { /* if it will not delete, let it be: a bit of wasted disk does not affect correctness, blocking startup would be the real problem */ }
            }
        }
        catch { /* same as above */ }
    }

    /// <summary>Push one item of work in. Single writer (the diff advances on one thread), and it **never blocks**.</summary>
    public void Enqueue(WorkItem item)
    {
        var bytes = item.EstimatedBytes;
        var wake = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);

            // Both earlier segments empty and r still has room → straight into r, without spending one extra copy or
            // one extra syscall. When f or w is non-empty it **must** go through w, otherwise this item jumps ahead of them.
            if (_spillPath is null || (_onDisk == 0 && _pendingWrite.Count == 0 && HasRoomLocked(bytes)))
            {
                AdmitLocked(item, bytes);
            }
            else
            {
                _pendingWrite.Enqueue(item);
                _pendingWriteBytes += bytes;
                // Once w is full, flush the whole batch into the file. Whichever of item count and bytes trips first
                // counts — bound by item count alone, 200 full packs in the small-file case are several GB, and the r
                // segment's limit is bypassed through the back door.
                if (_pendingWrite.Count >= _limits.WriteBatchItems || _pendingWriteBytes >= _limits.WriteBatchBytes)
                    FlushPendingWritesLocked();
                wake = true;
            }
        }
        if (wake)
            _wake.Release();
    }

    /// <summary>The write side is done. Whatever is left in f and w still gets delivered in full; only after that does the read side get null.</summary>
    public void CompleteAdding()
    {
        lock (_gate) { _addingDone = true; }
        if (_spillPath is null)
            _cache.Writer.TryComplete();
        else
            _wake.Release(); // the pump is responsible for closing the gate only once f and w are both empty
    }

    /// <summary>Take one item of work; null = the write side is done and all three segments are empty. Called concurrently by multiple consumers.</summary>
    public async ValueTask<WorkItem?> DequeueAsync(CancellationToken ct)
    {
        while (true)
        {
            if (_cache.Reader.TryRead(out var item))
            {
                bool wake;
                lock (_gate)
                {
                    _cachedItems--;
                    _cachedBytes -= item.EstimatedBytes;
                    // Do not wake the pump when there is nothing behind, otherwise every single consumption wakes it for nothing.
                    wake = _onDisk > 0 || _pendingWrite.Count > 0;
                }
                if (wake)
                    _wake.Release();
                return item;
            }
            if (!await _cache.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                return null;
        }
    }

    /// <summary>The read-back pump: the moment r frees space it refills from f (and then w), and closes the gate once all three segments are empty and the write side is done.</summary>
    private async Task PumpAsync()
    {
        while (true)
        {
            // Aborted mid-run (the backup was cancelled or threw): whatever is left no longer matters, pull out now.
            // Otherwise the pump sits waiting on an r segment nobody consumes and that never frees space, while
            // Dispose is waiting for it to exit.
            if (Volatile.Read(ref _disposed) != 0)
                return;

            int moved;
            bool done;
            lock (_gate)
            {
                moved = RefillLocked();
                done = _onDisk == 0 && _pendingWrite.Count == 0 && _addingDone;
            }
            if (done)
            {
                _cache.Writer.TryComplete();
                return;
            }
            if (moved == 0)
                await _wake.WaitAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Refills the r segment in batches, inside <see cref="_gate"/>. Returns how many items this round moved.</summary>
    private int RefillLocked()
    {
        var moved = 0;

        // f before w: f sits ahead of w, and the other way round breaks the order.
        if (_onDisk > 0)
        {
            // Flush once per batch: unless the write-side buffer is flushed to the kernel, the other handle cannot read the items just written.
            _writer!.Flush();
            while (moved < _limits.RefillBatchItems && _onDisk > 0)
            {
                if (!HasRoomForNextLocked())
                    return moved;
                var item = ReadSpill();
                _onDisk--;
                moved++;
                // The gate is already closed (aborted mid-run by Dispose): read it and drop it, purely to drive _onDisk to zero so the pump can get out.
                if (_cache.Writer.TryWrite(item))
                {
                    _cachedItems++;
                    _cachedBytes += item.EstimatedBytes;
                }
            }
        }

        // f is empty → w's items go straight into r, no need to write them out and read them back.
        if (_onDisk == 0)
        {
            while (moved < _limits.RefillBatchItems && _pendingWrite.Count > 0)
            {
                if (!HasRoomForNextLocked())
                    return moved;
                var item = _pendingWrite.Dequeue();
                var bytes = item.EstimatedBytes;
                _pendingWriteBytes -= bytes;
                moved++;
                AdmitLocked(item, bytes);
            }
        }

        return moved;
    }

    /// <summary>Whether the r segment still has room for the next item. Always true when r is empty — that is the "at least one item ready" exception.</summary>
    private bool HasRoomForNextLocked() =>
        _cachedItems == 0 || (_cachedItems < _limits.MaxCachedItems && _cachedBytes < _limits.MaxCachedBytes);

    /// <summary>Whether the r segment has room for this item (used by the write side; it asks whether adding it would go over).</summary>
    private bool HasRoomLocked(long bytes) =>
        _cachedItems == 0
        || (_cachedItems + 1 <= _limits.MaxCachedItems && _cachedBytes + bytes <= _limits.MaxCachedBytes);

    private void AdmitLocked(WorkItem item, long bytes)
    {
        if (!_cache.Writer.TryWrite(item))
            return; // the gate is closed (aborted mid-run by Dispose)
        _cachedItems++;
        _cachedBytes += bytes;
    }

    private void FlushPendingWritesLocked()
    {
        EnsureSpillOpenLocked();
        while (_pendingWrite.Count > 0)
        {
            WriteSpill(_pendingWrite.Dequeue());
            _onDisk++;
            Interlocked.Increment(ref _spilledTotal);
        }
        _pendingWriteBytes = 0;
    }

    private void EnsureSpillOpenLocked()
    {
        if (_writer is not null)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(_spillPath!)!);
        // FileShare.ReadWrite: a second, read-only handle on the same file reads forward through it.
        // A large buffer: the write side's batching mostly happens right here, and each item's Write only writes into it.
        _writeStream = new FileStream(
            _spillPath!, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, _limits.FileBufferBytes);
        _writer = new BinaryWriter(_writeStream, Encoding.UTF8, leaveOpen: true);
        _readStream = new FileStream(
            _spillPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, _limits.FileBufferBytes);
        _reader = new BinaryReader(_readStream, Encoding.UTF8, leaveOpen: true);
    }

    private void WriteSpill(WorkItem item)
    {
        // Length-prefixed binary, not line-delimited text: a Linux path may contain newlines and any non-NUL byte, so
        // splitting on lines is bound to split wrong on some user's directory, and the symptom of splitting wrong is a
        // backup that uploads fewer files — nobody will ever notice.
        // Each item is one complete record; a read either yields the whole item or leaves it alone, so "half a pack was read" cannot happen.
        var members = item.AsMembers;
        _writer!.Write(item.Single is not null);
        _writer.Write(item.StoreOnly);
        _writer.Write(members.Count);
        foreach (var m in members)
        {
            _writer.Write(m.Path);
            _writer.Write(m.Length);
            _writer.Write(m.FullHash is not null);
            if (m.FullHash is not null)
                _writer.Write(m.FullHash);
        }
    }

    private WorkItem ReadSpill()
    {
        var single = _reader!.ReadBoolean();
        var storeOnly = _reader.ReadBoolean();
        var count = _reader.ReadInt32();
        var members = new List<PlannedFile>(count);
        for (var i = 0; i < count; i++)
        {
            var path = _reader.ReadString();
            var length = _reader.ReadInt64();
            var hash = _reader.ReadBoolean() ? _reader.ReadString() : null;
            members.Add(new PlannedFile(path, length, hash));
        }
        return single ? new WorkItem(members[0], null, storeOnly) : new WorkItem(null, members, storeOnly);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Close the gate before waking the pump: the pump sees _disposed and exits outright instead of trying to deliver the rest.
        _cache.Writer.TryComplete();
        lock (_gate) { _addingDone = true; }
        _wake.Release();
        try { _pump.Wait(TimeSpan.FromSeconds(10)); }
        catch { /* however the pump ends up, it must not stand in the way of releasing the handles */ }

        lock (_gate)
        {
            _pendingWrite.Clear();
            _pendingWriteBytes = 0;
            _writer?.Dispose();
            _writeStream?.Dispose();
            _reader?.Dispose();
            _readStream?.Dispose();
            _writer = null;
            _writeStream = null;
            _reader = null;
            _readStream = null;
        }

        if (_spillPath is not null)
        {
            try { File.Delete(_spillPath); }
            catch { /* ClearStale backs this up at the next process startup */ }
        }
        _wake.Dispose();
    }
}

/// <summary>
/// Each backup run opens a queue of its own. The spill file takes a random name per run — concurrent backups each
/// write their own, and none of them can ever delete another's (see <see cref="DiffWorkQueue.ClearStale"/>).
/// </summary>
public sealed class DiffWorkQueueFactory(string spillDirectory, DiffQueueLimits limits)
{
    public string SpillDirectory => spillDirectory;

    public DiffQueueLimits Limits => limits;

    internal DiffWorkQueue Create() =>
        new(Path.Combine(spillDirectory, $"{Guid.NewGuid():N}.spill"), limits);
}
