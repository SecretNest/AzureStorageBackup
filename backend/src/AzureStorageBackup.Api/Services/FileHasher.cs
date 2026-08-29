using System.IO.Hashing;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Two-level file hashing. Uses XxHash128 (non-cryptographic, extremely fast, 16 bytes) — faster and shorter than SHA-256, which keeps the index smaller;
/// at 128 bits the content-addressed dedup collision probability is negligible at personal-backup scale (far better than CRC).
/// headHash covers a small leading segment of the file for fast diff prefiltering; fullHash covers the whole file and doubles as the dedup key.
/// </summary>
public interface IFileHasher
{
    Task<string> HeadHashAsync(string path, int headBytes, CancellationToken ct = default);

    /// <summary>Hash of the file's tail segment (at most tailBytes bytes). Together with headHash it is the hardening metadata for dedup collision detection:
    /// a residual collision where the content differs yet fullHash + length + head all match is caught by the tail hash, as long as the difference is not in the middle of the file.</summary>
    Task<string> TailHashAsync(string path, int tailBytes, CancellationToken ct = default);

    /// <param name="onRead">
    /// Reporting of bytes read (incremental). Computing a large file's full hash means reading the whole thing — a 10 GB file
    /// on a NAS is a dozen-odd seconds, and change detection **has to** read all of it (when the length is the same but mtime
    /// changed, only the full hash can tell "the content really changed" from "it was just touched"). Without progress
    /// reporting, the UI shows a motionless file name, indistinguishable from a hang.
    /// </param>
    Task<string> FullHashAsync(string path, CancellationToken ct = default, IProgress<long>? onRead = null);

    /// <summary>
    /// Hash of the file's first <paramref name="length"/> bytes, formatted identically to <see cref="FullHashAsync"/>.
    /// That identity is what lets repair recognize an append-only grown file: the old version's recorded FullHash
    /// is the live file's prefix hash. Throws <see cref="IOException"/> when the file holds fewer bytes than asked —
    /// a race shrank it, and hashing fewer bytes must not impersonate the recorded content.
    /// <para>
    /// The default implementation throws — the same reasoning as <see cref="ContentIdentityAsync"/>'s default,
    /// pointed the other way: the many fake hashers in the tests intercept the three classic methods and never
    /// reach this one, so they must not each be forced to write a stub; but a fake that DOES wander into the
    /// prefix path has no honest generic answer, and a loud throw beats a silently wrong hash.
    /// </para>
    /// </summary>
    Task<string> PrefixHashAsync(string path, long length, CancellationToken ct = default, IProgress<long>? onRead = null)
        => throw new NotSupportedException($"{GetType().Name} does not hash prefixes; use {nameof(FileHasher)}.");

    /// <summary>
    /// Compute the complete content identity (three-segment hash + length) in a single read pass.
    /// <para>
    /// Reading three separate times pays for two extra IO passes for nothing: the full-file pass already goes past the head
    /// and the tail. Judging one changed file in the diff stage used to open the same file three times, so a first backup of
    /// a few hundred thousand small files meant a few hundred thousand redundant open + seek calls — on a NAS with spinning
    /// disks that is not a small number.
    /// </para>
    /// <para>
    /// The default implementation still makes three calls, so the fake hashers in the tests don't each have to override it
    /// (they intercept these three methods to simulate "can't be opened", and that semantics must be preserved verbatim).
    /// The real implementation (<see cref="FileHasher"/>) overrides it to a single read pass.
    /// </para>
    /// </summary>
    async Task<ContentIdentity> ContentIdentityAsync(
        string path, int segmentBytes, CancellationToken ct = default) =>
        new(await FullHashAsync(path, ct),
            await HeadHashAsync(path, segmentBytes, ct),
            await TailHashAsync(path, segmentBytes, ct),
            new FileInfo(path).Length);
}

/// <summary>The content identity obtained in a single read pass: three-segment hash + length. The four pieces of evidence behind dedup and collision decisions.</summary>
public sealed record ContentIdentity(string FullHash, string HeadHash, string TailHash, long Length);

public sealed class FileHasher : IFileHasher
{
    public async Task<string> HeadHashAsync(string path, int headBytes, CancellationToken ct = default)
    {
        await using var stream = Open(path);
        var buffer = new byte[headBytes];
        var total = 0;
        while (total < headBytes)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, headBytes - total), ct);
            if (read == 0)
                break;
            total += read;
        }
        return Format(XxHash128.Hash(buffer.AsSpan(0, total)));
    }

    public async Task<string> TailHashAsync(string path, int tailBytes, CancellationToken ct = default)
    {
        await using var stream = Open(path);
        var len = stream.Length;
        var take = (int)Math.Min(tailBytes, len);
        if (take > 0)
            stream.Seek(-take, SeekOrigin.End);
        var buffer = new byte[take];
        var total = 0;
        while (total < take)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, take - total), ct);
            if (read == 0)
                break;
            total += read;
        }
        return Format(XxHash128.Hash(buffer.AsSpan(0, total)));
    }

    public async Task<string> FullHashAsync(
        string path, CancellationToken ct = default, IProgress<long>? onRead = null)
    {
        await using var stream = Open(path);
        var hash = new XxHash128();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            hash.Append(buffer.AsSpan(0, read));
            onRead?.Report(read);   // Incremental: the caller accumulates as it sees fit, without needing to know which chunk it started at
        }
        return Format(hash.GetCurrentHash());
    }

    public async Task<string> PrefixHashAsync(
        string path, long length, CancellationToken ct = default, IProgress<long>? onRead = null)
    {
        await using var stream = Open(path);
        var hash = new XxHash128();
        var buffer = new byte[81920];
        var remaining = length;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
            if (read == 0)
                throw new IOException($"'{path}' holds fewer than the {length} bytes asked of its prefix.");
            hash.Append(buffer.AsSpan(0, read));
            onRead?.Report(read);
            remaining -= read;
        }
        return Format(hash.GetCurrentHash());
    }

    /// <summary>Single read pass: the bytes are fed into all three segment hashers at once, with head and tail picked up along the way.</summary>
    public async Task<ContentIdentity> ContentIdentityAsync(
        string path, int segmentBytes, CancellationToken ct = default)
    {
        var streaming = new StreamingHasher(segmentBytes, segmentBytes);
        await using var stream = Open(path);
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            streaming.Append(buffer.AsSpan(0, read));
        return new ContentIdentity(streaming.FullHash, streaming.HeadHash, streaming.TailHash, streaming.Length);
    }

    /// <summary>
    /// Open a file for hashing. On Unix, O_NONBLOCK is **mandatory**.
    /// <para>
    /// A FIFO (named pipe) makes an ordinary <c>File.OpenRead</c> **block forever** inside open(), waiting for a writer.
    /// That block sits inside a syscall where <c>CancellationToken</c> cannot reach it: the whole backup run wedges and cannot
    /// be canceled, the busy lock is held forever, that config then refuses every operation, all its scheduled tasks are
    /// skipped, and the UI is left with a percentage that never moves. And .NET on Unix **cannot** recognize one — measured:
    /// a FIFO's <c>FileAttributes</c> is likewise Normal and its <c>Length</c> is likewise 0, indistinguishable from an ordinary empty file.
    /// </para>
    /// <para>
    /// So: a non-blocking open makes a FIFO return immediately instead of hanging, and <c>CanSeek</c> then tells it apart from
    /// an ordinary file (ordinary files — empty ones included — are always true, FIFOs/pipes are false; sockets fail a step
    /// earlier, with open failing outright as ENXIO). Anything judged not to be a regular file throws IOException and falls
    /// into the existing "can't be opened" handling: it never enters the upload plan, and therefore 7z never opens it either —
    /// 7z is a separate process without this flag, and would hang just the same if it ran into one.
    /// O_NONBLOCK has no effect on read semantics for regular files (guaranteed by POSIX), so the normal path is untouched.
    /// </para>
    /// <para>
    /// It is exposed publicly (<see cref="OpenRead"/>) because streaming backup reads the source files too: a FIFO is just as
    /// fatal there, and "reading a source file" must have exactly one way of opening in this project, or this protection will get bypassed sooner or later.
    /// </para>
    /// </summary>
    public static FileStream OpenRead(string path) => Open(path, StreamingBufferBytes);

    /// <summary>
    /// The buffer for the hashing reads inside this class: head, tail and the full-hash pass. Each of those asks for
    /// a segment of its own size, so the buffer only has to not get in their way.
    /// </summary>
    private const int HashingBufferBytes = 81920;

    /// <summary>
    /// The buffer for <see cref="OpenRead"/>, the whole-file sequential route: the upload's read of a staged volume
    /// or of a source file on the raw route, the raw route's hash-only pass, and the feed into 7z.
    /// <para>
    /// Fifty times the hashing one, because on the upload path this number sets the **network** rate, not the disk's.
    /// A volume under the SDK's single-shot threshold is one Put Blob over one connection, and the SDK copies our
    /// stream into the socket in 80 KB slices, so the loop is strictly serial: read a slice, wait for the disk, write
    /// it, read the next. Every disk read is dead air on the socket, and 80 KB per write is far under what a link with
    /// any real round-trip needs in flight to stay at line rate — measured, five streams could not fill an uplink that
    /// another tool held at its ceiling, while the staging disk sat at 200-400 read IOPS with latency to spare. The
    /// disk was answering exactly what was asked of it and no more.
    /// </para>
    /// <para>
    /// The SDK's 80 KB slices stay 80 KB; what changes is where they come from. Fifty of them in a row are served out
    /// of this buffer as memory copies, and only the fifty-first waits for a platter, so the socket gets its writes
    /// back to back instead of one per seek. The cost is one buffer per open file — at the default upload concurrency
    /// that is the five uploaders plus whatever the compression side has open.
    /// </para>
    /// <para>
    /// The hashing reads above deliberately do **not** get this. Head and tail hashing read one small segment and
    /// close; a buffer this size would pull megabytes off the disk to satisfy a request for kilobytes, on every file
    /// in the diff pass — the exact opposite trade, and that pass runs over the whole tree.
    /// </para>
    /// <para>
    /// It is a **ceiling**, not a size — see <see cref="BufferFor"/> for why a file smaller than it must not get it.
    /// </para>
    /// </summary>
    private const int StreamingBufferBytes = 4 * 1024 * 1024;

    /// <summary>
    /// The buffer an open of this length actually gets: never below <see cref="HashingBufferBytes"/>, never above the
    /// caller's ceiling, and never above the file itself.
    /// <para>
    /// The last clause is the one that matters. Anything past 85 KB is a large-object-heap allocation, and
    /// <c>ArrayPool</c> does not pool past 1 MB, so a fixed ceiling-sized buffer would put a fresh 4 MB array on the
    /// LOH for **every open** — and <see cref="OpenRead"/> is per file, not per volume: the raw route opens every
    /// single-file item and the feed into 7z opens every member. A tree of a few hundred thousand small files would
    /// pay a few hundred thousand 4 MB allocations to buffer files of a few KB, which is a gen2 collector running
    /// flat out to buy nothing at all — the read that buffer exists to smooth does not exist below one buffer's worth.
    /// </para>
    /// <para>
    /// Length 0 lands on the floor, which covers both an empty file and the "cannot tell" of a handle that will not
    /// answer — a FIFO, rejected a few lines below anyway.
    /// </para>
    /// </summary>
    private static int BufferFor(long length, int ceiling)
        => length <= HashingBufferBytes ? HashingBufferBytes : (int)Math.Min(length, ceiling);

    /// <summary>The length behind a handle, or 0 when it will not say — anything not a regular file.</summary>
    private static long LengthOrZero(SafeFileHandle handle)
    {
        try { return RandomAccess.GetLength(handle); }
        catch { return 0; }
    }

    private static FileStream Open(string path, int bufferCeiling = HashingBufferBytes)
    {
        if (OperatingSystem.IsWindows())
        {
            var length = 0L;
            try { length = new FileInfo(path).Length; } catch { /* fall back to the floor */ }
            return new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                BufferFor(length, bufferCeiling), useAsync: true);
        }

        var fd = open(path, O_RDONLY | (OperatingSystem.IsMacOS() ? O_NONBLOCK_BSD : O_NONBLOCK_LINUX));
        if (fd < 0)
            throw new IOException(
                $"Cannot open '{path}' for reading (errno {Marshal.GetLastPInvokeError()}).");

        // The handle is asked for its length **before** it is handed to the FileStream, because that is the only
        // moment the size is known and the buffer has to be chosen at construction. The FileStream takes ownership
        // either way, so a throw from here on still closes the fd through the stream's own disposal.
        var handle = new SafeFileHandle(fd, ownsHandle: true);
        var stream = new FileStream(handle, FileAccess.Read, BufferFor(LengthOrZero(handle), bufferCeiling));
        if (stream.CanSeek)
            return stream;

        // Non-seekable things like pipes/FIFOs: there is no "file content" to speak of, and reading one only waits for a writer that may never come.
        stream.Dispose();
        throw new IOException($"'{path}' is not a regular file (named pipe, device or similar).");
    }

    private const int O_RDONLY = 0;
    private const int O_NONBLOCK_LINUX = 0x800;  // Linux: 0o4000
    private const int O_NONBLOCK_BSD = 0x4;      // macOS/BSD

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int open(string pathname, int flags);

    private static string Format(byte[] hash) => "xxh128:" + Convert.ToHexString(hash).ToLowerInvariant();
}
