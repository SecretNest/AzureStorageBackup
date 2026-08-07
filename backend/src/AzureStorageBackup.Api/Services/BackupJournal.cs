using System.Text;
using System.Text.Json;

namespace AzureStorageBackup.Api.Services;

/// <summary>journal 的头一行：恢复前置校验要用的一切都在这。</summary>
public sealed record JournalHeader
{
    public required string RunId { get; init; }
    public required int ConfigId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>本次运行差异比对的基线版本号。基线变了（别人跑完了一轮），这卷 journal 作废。</summary>
    public required int BaselineVersion { get; init; }

    /// <summary>本地源根。改过根目录，路径含义就变了，作废。</summary>
    public required string LocalRoot { get; init; }

    /// <summary>加密身份指纹（<see cref="BlobAddressScheme.Identity"/>）。换了密码，地址空间就变了，作废。</summary>
    public required string EncryptionIdentity { get; init; }
}

/// <summary>pack 里的一个成员。恢复时要靠它重建 <c>PackInfo</c> 与每个成员的 StorageRef。</summary>
public sealed record JournalMember(string Path, string EntryName, string FullHash, long Length);

/// <summary>一条"这块内容已经在云上确认了"。</summary>
public sealed record JournalRecord
{
    /// <summary>"blob" 或 "pack"。</summary>
    public required string Kind { get; init; }

    /// <summary>blob：data blob 的基名（如 <c>data/abc</c>）；pack：packId。</summary>
    public required string Ref { get; init; }

    // 以下 blob 用
    public string? Path { get; init; }
    public string? FullHash { get; init; }
    public string? HeadHash { get; init; }
    public string? TailHash { get; init; }
    public long Length { get; init; }
    public bool Raw { get; init; }

    // 以下 pack 用
    public bool StoreOnly { get; init; }
    public IReadOnlyList<JournalMember> Members { get; init; } = [];

    public int Volumes { get; init; } = 1;
    public IReadOnlyList<long> VolumeSizes { get; init; } = [];
}

/// <summary>读出来的整卷 journal。</summary>
public sealed record JournalContent(JournalHeader Header, IReadOnlyList<JournalRecord> Records);

/// <summary>
/// 一次备份运行的恢复日志：append-only 的 JSONL，头一行是 <see cref="JournalHeader"/>，
/// 后面每行一条 <see cref="JournalRecord"/>。
/// <para>
/// **时序是这个文件的全部意义**：压缩 → 上传 → 上传确认返回 → 才追加一行。
/// 顺序反了就会记下一块其实不在云上的内容，下次恢复直接跳过它 —— 数据丢失。
/// </para>
/// <para>
/// **不逐条 fsync**：代价不对称。少记一条 = 下次多传一个文件；每条都 fsync = 每个文件
/// 多一次磁盘同步。所以崩溃后最后一行可能是半截的，<see cref="ReadAsync"/> 跳过解析不了的行。
/// 只有主动挂起收尾时才真 fsync（那一刻我们承诺"落盘成功再返回"）。
/// </para>
/// </summary>
public sealed class BackupJournal : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly FileStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private BackupJournal(FileStream stream) => _stream = stream;

    /// <summary>建一卷新 journal 并写下头一行。父目录不存在会自动建。</summary>
    public static async Task<BackupJournal> CreateAsync(string path, JournalHeader header, CancellationToken ct)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var journal = new BackupJournal(stream);
        await journal.WriteLineAsync(JsonSerializer.Serialize(header, Json), ct);
        await journal.FlushAsync(fsync: true, ct);   // 头写不下去，后面全是白搭，这一次同步值得
        return journal;
    }

    /// <summary>追加一条。调用点必须在**上传确认返回之后**。</summary>
    public async Task AppendAsync(JournalRecord record, CancellationToken ct)
        => await WriteLineAsync(JsonSerializer.Serialize(record, Json), ct);

    /// <param name="fsync">true 时连同操作系统缓冲一起刷到盘上（主动挂起收尾用）。</param>
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
            await _stream.FlushAsync(ct);   // 只刷到 OS，不落盘；见类注释
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>读整卷。文件不在、空的、或头坏了都返回 null（= 这卷作废，当没有恢复点）。</summary>
    public static async Task<JournalContent?> ReadAsync(string path, CancellationToken ct)
    {
        JournalHeader? header = null;
        var records = new List<JournalRecord>();

        // 不先 File.Exists 再打开——那两步之间有个真实的缺口：清理器在扫某个容器的活动 journal 时，
        // 另一轮备份可能正好跑完并删掉自己那卷。缺口里删掉就会抛 FileNotFoundException，把整轮清理
        // 掀掉。直接开、接住"不在了"，与"本来就不在"归到同一个答案：这卷作废。
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
                try { header = JsonSerializer.Deserialize<JournalHeader>(line, Json); }
                catch (JsonException) { return null; }   // 头坏了，整卷作废
                if (header is null)
                    return null;
                continue;
            }
            try
            {
                if (JsonSerializer.Deserialize<JournalRecord>(line, Json) is { } record)
                    records.Add(record);
            }
            catch (JsonException)
            {
                // 崩溃留下的半截行。正常只可能出现在最后一行；真出现在中间也只是少认几条，
                // 后果是多传几个文件，不是数据丢失。继续读完。
            }
        }

        return header is null ? null : new JournalContent(header, records);
    }

    public async ValueTask DisposeAsync()
    {
        try { await _stream.FlushAsync(); } catch { /* 关的时候刷不动就算了 */ }
        await _stream.DisposeAsync();
        _writeLock.Dispose();
    }
}
