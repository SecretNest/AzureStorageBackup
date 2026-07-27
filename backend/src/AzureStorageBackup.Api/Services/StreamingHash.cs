using System.IO.Hashing;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 单遍流式三段 hash：head（前 N 字节）/ full（全部）/ tail（后 N 字节）。
/// <para>
/// 产出的字符串与 <see cref="FileHasher"/> **逐字节一致**（XxHash128，"xxh128:" + 小写十六进制）：
/// 同一份内容既可能由按路径读的 FileHasher 算出、也可能由这里算出，索引里两者混用，
/// 差一点格式就等于全库比对失效。
/// </para>
/// <para>
/// tail 用环形缓冲：流不能回退，"最后 N 字节是哪些"要到 EOF 才知道，只能一路留着最近的 N 字节。
/// </para>
/// </summary>
public sealed class StreamingHasher(int headBytes, int tailBytes)
{
    private readonly XxHash128 _full = new();
    private readonly byte[] _head = new byte[Math.Max(0, headBytes)];
    private readonly byte[] _tail = new byte[Math.Max(0, tailBytes)];
    private int _headFilled;
    private int _tailWritePos;   // 环形缓冲下一个写入位置
    private int _tailFilled;     // 环形缓冲中的有效字节数

    /// <summary>迄今喂入的总字节数。流式读取**必须**核对它与索引记录的长度——
    /// `7z x -so` 取一个不存在的成员时输出为空却退出码 0，只有长度和 hash 能识破。</summary>
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

        // 一次就写满整个缓冲：先前留着的都会被覆盖，直接取末尾一段即可。
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
/// 只写流：把写入的字节喂给一个 <see cref="StreamingHasher"/>，可选同时转发给下游。
/// 下游为 null＝只算 hash 不落盘（检查用）；给下游＝边写边算（还原/压缩用）。
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
/// 只写流：把写入的字节按事先给定的**段长序列**切开，逐段算 hash 并回调。
/// <para>
/// 用来一次解压整个归档、按成员尺寸还原出每个成员的 hash：7z 归档是固实的，
/// 逐成员各调一次 `x -so` 会让第 k 个成员把前 k-1 个也解一遍，退化成 O(N²)。
/// </para>
/// </summary>
public sealed class SegmentHashingStream(
    IReadOnlyList<(string Name, long Length)> segments,
    Action<string, long, string> onSegment) : Stream
{
    private int _index;
    private long _consumed;
    private StreamingHasher? _current;

    /// <summary>全部段都填满之后仍然收到的字节数。不为 0 说明归档实际内容比列举出来的多，
    /// 切段的依据（列举顺序＝输出顺序）不成立——调用方据此放弃快路径。</summary>
    public long ExtraBytes { get; private set; }

    /// <summary>已完整填满并已回调的段数。</summary>
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
                return;  // 这一段还没满，等下一次写入

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

    /// <summary>流结束后调用：把尾部那些长度为 0 的段（空文件）补上回调——
    /// 它们不消耗任何字节，光靠 <see cref="Write"/> 永远推不动。
    /// 未填满的段**不**回调，调用方查不到即视为不符。</summary>
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
