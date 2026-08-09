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
    public static FileStream OpenRead(string path) => Open(path);

    private static FileStream Open(string path)
    {
        if (OperatingSystem.IsWindows())
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

        var fd = open(path, O_RDONLY | (OperatingSystem.IsMacOS() ? O_NONBLOCK_BSD : O_NONBLOCK_LINUX));
        if (fd < 0)
            throw new IOException(
                $"Cannot open '{path}' for reading (errno {Marshal.GetLastPInvokeError()}).");

        var stream = new FileStream(new SafeFileHandle(fd, ownsHandle: true), FileAccess.Read, 81920);
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
