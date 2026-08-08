using System.Text;
using System.Text.Json;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 一卷 journal 的概览。<b>不解析记录体</b>：只反序列化头一行，剩下的只数行数。
/// 界面列"有哪些中途停下的运行"会反复调它，而一卷 journal 可能有几十万行——
/// 逐行 JSON 反序列化只为显示一个数字，代价不值。
/// </summary>
public sealed record JournalSummary(string RunId, JournalHeader Header, int Records, long SizeBytes);

/// <summary>
/// 某个容器上所有**活动** journal 引用到的 blob 基名与 packId。
/// 清理器拿它当"别删我"的名单：这些内容云上有、索引里还没有，
/// 只有 journal 记着它们存在，删了就等于让恢复白跑。
/// </summary>
public sealed record ActiveJournalRefs(IReadOnlySet<string> Blobs, IReadOnlySet<string> Packs)
{
    public static readonly ActiveJournalRefs Empty =
        new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
}

/// <summary>
/// journal 的目录：<c>{root}/{accountId}/{container}/{runId}.jsonl</c>。
/// <para>
/// 按 (accountId, container) 分目录而不是按 configId——清理器就是按这两样定位容器的，
/// 手上根本没有 configId。configId 记在 journal 头里，需要时从头读。
/// </para>
/// </summary>
public sealed class BackupJournalStore(string rootDir)
{
    /// <summary>
    /// 容器名理论上不含斜杠，但别把这条当保证：拼路径前一律做一次扁平化。
    /// <para>
    /// 全是点的名字（<c>.</c>、<c>..</c>）也一并换掉。它们不含任何非法字符，却是路径段而不是名字，
    /// 拼出来会往上跳一层——而 <see cref="DeleteAll"/> 是 <c>Directory.Delete(recursive: true)</c>，
    /// 跳错一层删掉的就是别的容器的恢复点。今天到不了这里（Azure 容器名根本不许有点，runId 是自己
    /// 生成的），这一条纯粹是不指望上游永远守规矩。
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

    /// <summary>挂起标记：与 journal 同名同目录，只多一个后缀。<c>ListAsync</c>/<c>PeekAsync</c>
    /// 只枚举 <c>*.jsonl</c>，所以它天然不会被当成一卷 journal 去解析。</summary>
    private string MarkPathFor(int accountId, string container, string runId)
        => PathFor(accountId, container, runId) + ".suspend";

    /// <summary>
    /// 记下这一卷是**为什么**停的。自动恢复只认其中一种：读出
    /// <see cref="SuspendReason.ShuttingDown"/> 才可以不问自取地接着跑（那是一次计划内的重启或升级），
    /// 别的一律等操作员按 Resume。
    /// <para>
    /// 标记**不在**的含义要说准，它不等于"被 kill"：可能是被 kill、可能是进程崩了、可能是关机等
    /// 落盘超时后这一卷被丢在半路、可能是操作员自己按了 Cancel（取消路径照样落盘，但不写标记）、
    /// 也可能就是这次写文件本身失败了。这几种谁也不该被自动重开——所以判据是"只在读到
    /// ShuttingDown 时才动手"，而不是"没有标记就当它是崩溃"。
    /// </para>
    /// <para>
    /// 耐久性上还有一层不对称，别指望它：<c>SettleStopAsync</c> 那条路上标记是在 journal
    /// fsync **之后**写的，所以标记不会比它描述的记录更"新"；但闸门降级（
    /// <see cref="SuspendReason.AutoSuspended"/>）那条是从流水线深处直接抛上来的，标记先落、journal
    /// 随 control 释放才关，而这里用的是 <c>File.WriteAllText</c>，没有 fsync。崩溃安全，掉电不安全：
    /// 最坏情况是标记活下来、它描述的那几条记录没有，代价是下一轮多传一个文件。
    /// </para>
    /// <para>
    /// 单开一个文件而不是往 journal 里加一条记录：journal 是只追加的记录流，而
    /// <see cref="LoadActiveRefsAsync"/> 是 <c>r.Kind == "pack" ? packs : blobs</c> 的二分——
    /// 多出来的第三种 Kind 会被静默丢进 blobs 桶，污染清理器的"别删我"名单。
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
        catch { /* 写不下就当没标记：后果是多跑一轮，不是丢数据 */ }
    }

    /// <summary>
    /// 抹掉这一卷的挂起标记。给"这一卷换了主人"用：标记描述的是**某一轮运行**为什么停下，
    /// 而一卷 journal 被新一轮采纳之后，写下那个理由的那一轮已经被顶替了，它的理由随之作废。
    /// <para>
    /// 与 <see cref="Delete"/> 的差别是 journal 本身还留着：采纳是只读的，旧卷要一直等到新一轮
    /// 成功提交索引才删（<see cref="BackupRunControl.CompleteAsync"/>）。中间这段时间里，
    /// 它的标记该由**接手的那一轮**重新写（见 <see cref="BackupRunControl.MarkSuspended"/>），
    /// 而不是继续拿着上一轮的旧值。
    /// </para>
    /// </summary>
    public void ClearSuspendMark(int accountId, string container, string runId)
    {
        try { File.Delete(MarkPathFor(accountId, container, runId)); } catch { /* 删不掉下次再说 */ }
    }

    /// <summary>读挂起理由。文件不在、读不动、或内容不认识都返回 null（= 当没标记）。</summary>
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

    /// <summary>接着往一卷已经存在的 journal 后面写。只在"本轮 runId 与盘上那卷重名"时用，
    /// 原委见 <see cref="BackupRunControl.OpenJournalAsync"/>。</summary>
    public Task<BackupJournal> AppendAsync(int accountId, string container, string runId, CancellationToken ct)
        => BackupJournal.OpenForAppendAsync(PathFor(accountId, container, runId), ct);

    /// <summary>列出该容器上所有能读通的 journal。读不通的（头坏了）直接当不存在。</summary>
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
    /// 一卷 journal 已经数过的行数。<see cref="PeekAsync"/> 靠它避免每次轮询都把整卷重走一遍。
    /// </summary>
    /// <param name="StartedAt">数这一份时那卷的头里写的开跑时刻。见 <see cref="PeekAsync"/> 里的作废判据。</param>
    /// <param name="Length">数完时的文件长度。</param>
    /// <param name="SafeOffset">最后一个换行符之后的偏移。只有它之前的行才算数完了。</param>
    /// <param name="CompleteLines">SafeOffset 之前的非空行数（含头一行）。</param>
    /// <param name="TotalLines">连同末尾那截没换行的残行一起算的非空行数（含头一行）。</param>
    private sealed record LineMemo(
        DateTimeOffset StartedAt, long Length, long SafeOffset, int CompleteLines, int TotalLines);

    private readonly Dictionary<string, LineMemo> _lineMemos = new(StringComparer.Ordinal);
    private readonly Lock _memoLock = new();

    /// <summary>测试用：这个实例至今为了数行数真正读过的字节数。备忘生不生效，只有数出来才算钉住。
    /// 挂在实例上而不是静态字段上：测试类之间是并行跑的，静态计数会被别的类的 <see cref="PeekAsync"/> 搅浑。</summary>
    internal long BytesScanned;

    /// <summary>
    /// 列出该容器上每卷 journal 的概览。头读不通的直接跳过（= 这卷作废）。
    /// <para>
    /// 行数是**增量**数出来的，不是每次重走一遍。界面开着的时候这个端点每 5 秒被每个配置各调一次，
    /// 而一卷 journal 在一次二十万文件的运行里能长到几百 MB——重走一遍就是每分钟几百 MB 的读，
    /// 抢的还是备份自己正在读的那块盘；挂起的那一卷停在盘上不动，这份开销还永远不会停。
    /// </para>
    /// <para>
    /// 备忘作废判据（journal 是只追加的，这三条合起来就够）：
    /// <list type="bullet">
    /// <item>头里的 <see cref="JournalHeader.StartedAt"/> 变了 → 这卷被<b>另起一轮重写</b>过
    /// （<see cref="BackupJournal.CreateAsync"/> 是 <c>FileMode.Create</c>，会截断），旧计数全不作数，从头数。
    /// 单看长度挡不住这一条：重写出来的长度完全可能与旧的相等或更长。</item>
    /// <item>文件比记下的短 → 同样是被换过，从头数。</item>
    /// <item>长度没变且 StartedAt 没变 → 只追加的文件长度不变就是内容不变，直接交出上次的数。</item>
    /// </list>
    /// 长了就只数新增的那一段，且只从**上一次数到的最后一个换行符**接着数：这个文件不逐条 fsync，
    /// 快照可能正落在半行中间，从文件末尾接着数会把那半行的后半截再当成一行算一遍。
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

                // 长度从已经打开的这个句柄上取，不另开 FileInfo：数行数与记长度必须是**同一个**快照，
                // 两次取样之间那卷可能又长了一截，记下的备忘就会声称"这个长度我数到过这么多行"。
                length = stream.Length;
                lines = await CountLinesAsync(file, stream, header, length, ct);
            }
            catch (IOException)
            {
                continue;   // 正在被写的那一卷偶尔读不开；下次轮询再说
            }
            // 头一行不是记录，从总行数里扣掉。
            result.Add(new JournalSummary(
                Path.GetFileNameWithoutExtension(file), header, Math.Max(0, lines - 1), length));
        }
        return result;
    }

    /// <summary>数出这一卷到 <paramref name="length"/> 为止的非空行数（含头一行），能接着上次数就接着数。</summary>
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
            // 行尾按字节找。UTF-8 里 0x0A 不可能出现在多字节字符的续字节上，所以按字节扫与按字符扫
            // 结果相同，而不必为了数一个数字把几百 MB 解码成字符串。IndexOf 走的是向量化的那条路，
            // 逐字节自己比要慢好几倍——而这段代码要吃的正是几百 MB。
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

        // 末尾那截没换行的残行照 StreamReader.ReadLine 的老规矩算一行，但**不进备忘**：
        // 它随时可能被后面的字节补全成一整行，记下来下次就会连着新行重复算。
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

    /// <summary>这一段里有没有正文。空行不算一行，只有 <c>\r</c> 的也不算（<c>ReadLine</c> 把 CRLF 当一个行尾）。</summary>
    private static bool HasContent(ReadOnlySpan<byte> segment) => segment.IndexOfAnyExcept((byte)'\r') >= 0;

    /// <summary>汇总该容器上所有活动 journal 引用到的内容。清理判据的一半。</summary>
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
        try { File.Delete(PathFor(accountId, container, runId)); } catch { /* 删不掉下次再说 */ }
        // 标记跟着 journal 一起走：留着它，下一轮撞上同名 runId 会读到上一次的理由。
        try { File.Delete(MarkPathFor(accountId, container, runId)); } catch { /* 同上 */ }
    }

    /// <summary>删配置兜底用：这个容器的 journal 全不要了。</summary>
    public void DeleteAll(int accountId, string container)
    {
        try
        {
            var dir = DirFor(accountId, container);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch { /* 同上 */ }
    }
}
