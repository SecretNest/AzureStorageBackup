using System.IO.Hashing;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Single-pass streaming three-segment hash: head (first N bytes) / full (everything) / tail (last N bytes).
/// <para>
/// The strings it produces are **byte-for-byte identical** to <see cref="FileHasher"/> (XxHash128, "xxh128:" + lowercase hex):
/// the same content may be hashed either by FileHasher reading by path or by this class, the index mixes the two, and being
/// off by even a little in the format means comparison across the whole store stops working.
/// </para>
/// <para>
/// tail uses a ring buffer: a stream cannot rewind, "which bytes are the last N" is not known until EOF, so the only option is to keep the most recent N bytes as you go.
/// </para>
/// </summary>
public sealed class StreamingHasher(int headBytes, int tailBytes)
{
    private readonly XxHash128 _full = new();
    private readonly byte[] _head = new byte[Math.Max(0, headBytes)];
    private readonly byte[] _tail = new byte[Math.Max(0, tailBytes)];
    private int _headFilled;
    private int _tailWritePos;   // Next write position in the ring buffer
    private int _tailFilled;     // Number of valid bytes in the ring buffer

    /// <summary>Total bytes fed in so far. A streaming read **must** check this against the length recorded in the index —
    /// `7z x -so` on a member that does not exist produces empty output yet exits 0, and only the length and the hash can catch that.</summary>
    public long Length { get; private set; }

    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return;

        Length += data.Length;
        _full.Append(data);

        var toHead = Math.Min(_head.Length - _headFilled, data.Length);
        if (toHead > 0)
        {
            data[..toHead].CopyTo(_head.AsSpan(_headFilled));
            _headFilled += toHead;
        }

        AppendTail(data);
    }

    private void AppendTail(ReadOnlySpan<byte> data)
    {
        if (_tail.Length == 0)
            return;

        // This single write fills the whole buffer: everything kept before is overwritten, so just take the trailing slice.
        if (data.Length >= _tail.Length)
        {
            data[^_tail.Length..].CopyTo(_tail);
            _tailWritePos = 0;
            _tailFilled = _tail.Length;
            return;
        }

        var first = Math.Min(_tail.Length - _tailWritePos, data.Length);
        data[..first].CopyTo(_tail.AsSpan(_tailWritePos));
        if (data.Length > first)
            data[first..].CopyTo(_tail.AsSpan(0));
        _tailWritePos = (_tailWritePos + data.Length) % _tail.Length;
        _tailFilled = Math.Min(_tail.Length, _tailFilled + data.Length);
    }

    public string FullHash => Format(_full.GetCurrentHash());

    public string HeadHash => Format(XxHash128.Hash(_head.AsSpan(0, _headFilled)));

    public string TailHash
    {
        get
        {
            if (_tailFilled == 0)
                return Format(XxHash128.Hash([]));

            var buffer = new byte[_tailFilled];
            var start = (_tailWritePos - _tailFilled + _tail.Length) % _tail.Length;
            var first = Math.Min(_tailFilled, _tail.Length - start);
            _tail.AsSpan(start, first).CopyTo(buffer);
            if (_tailFilled > first)
                _tail.AsSpan(0, _tailFilled - first).CopyTo(buffer.AsSpan(first));
            return Format(XxHash128.Hash(buffer));
        }
    }

    private static string Format(byte[] hash) => "xxh128:" + Convert.ToHexString(hash).ToLowerInvariant();
}

/// <summary>
/// Write-only stream: feeds the written bytes to a <see cref="StreamingHasher"/>, optionally forwarding them downstream as well.
/// A null downstream = hash only, nothing written to disk (used by check); a downstream given = hash while writing (used by restore/compression).
/// </summary>
public sealed class HashingStream(StreamingHasher hasher, Stream? inner = null) : Stream
{
    public StreamingHasher Hasher { get; } = hasher;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => Hasher.Length;
    public override long Position
    {
        get => Hasher.Length;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
        => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Hasher.Append(buffer);
        inner?.Write(buffer);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        Hasher.Append(buffer.Span);
        if (inner is not null)
            await inner.WriteAsync(buffer, ct);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Flush() => inner?.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner?.FlushAsync(ct) ?? Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// Write-only stream: splits the written bytes by a **sequence of segment lengths** given up front, hashing each segment and invoking a callback.
/// <para>
/// Used to extract a whole archive in one go and recover each member's hash from the member sizes: 7z archives are solid, so
/// calling `x -so` once per member makes the k-th member re-extract the preceding k-1 as well, degrading to O(N²).
/// </para>
/// </summary>
public sealed class SegmentHashingStream(
    IReadOnlyList<(string Name, long Length)> segments,
    Action<string, long, string> onSegment) : Stream
{
    private int _index;
    private long _consumed;
    private StreamingHasher? _current;

    /// <summary>Bytes still received after every segment has been filled. Nonzero means the archive holds more content than was
    /// listed, so the premise for splitting (listing order = output order) does not hold — the caller drops the fast path on that basis.</summary>
    public long ExtraBytes { get; private set; }

    /// <summary>Number of segments completely filled and already called back.</summary>
    public int CompletedSegments => _index;

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (true)
        {
            if (_index >= segments.Count)
            {
                ExtraBytes += buffer.Length;
                return;
            }

            var (name, length) = segments[_index];
            _current ??= new StreamingHasher(0, 0);
            var take = (int)Math.Min(length - _consumed, buffer.Length);
            if (take > 0)
            {
                _current.Append(buffer[..take]);
                _consumed += take;
                buffer = buffer[take..];
            }

            if (_consumed < length)
                return;  // This segment isn't full yet, wait for the next write

            onSegment(name, _consumed, _current.FullHash);
            _index++;
            _consumed = 0;
            _current = null;
            if (buffer.IsEmpty)
                return;
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Write(buffer.Span);
        await Task.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    /// <summary>Called after the stream ends: fire the callback for the trailing zero-length segments (empty files) —
    /// they consume no bytes, so <see cref="Write"/> on its own can never move past them.
    /// Segments that were not filled get **no** callback; the caller treats a lookup miss as a mismatch.</summary>
    public void Finish()
    {
        while (_index < segments.Count && segments[_index].Length == 0 && _consumed == 0)
        {
            onSegment(segments[_index].Name, 0, new StreamingHasher(0, 0).FullHash);
            _index++;
        }
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
