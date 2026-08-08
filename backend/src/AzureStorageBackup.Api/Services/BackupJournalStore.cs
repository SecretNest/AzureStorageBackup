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

    /// <summary>列出该容器上每卷 journal 的概览。头读不通的直接跳过（= 这卷作废）。</summary>
    public async Task<IReadOnlyList<JournalSummary>> PeekAsync(int accountId, string container, CancellationToken ct)
    {
        var dir = DirFor(accountId, container);
        if (!Directory.Exists(dir))
            return [];

        var result = new List<JournalSummary>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal))
        {
            JournalHeader? header;
            var lines = 0;
            try
            {
                using var reader = new StreamReader(
                    new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8);
                var first = await reader.ReadLineAsync(ct);
                if (first is null)
                    continue;
                try { header = JsonSerializer.Deserialize<JournalHeader>(first, JournalJson.Options); }
                catch (JsonException) { continue; }
                if (header is null)
                    continue;
                while (await reader.ReadLineAsync(ct) is { } line)
                    if (line.Length > 0)
                        lines++;
            }
            catch (IOException)
            {
                continue;   // 正在被写的那一卷偶尔读不开；下次轮询再说
            }
            result.Add(new JournalSummary(
                Path.GetFileNameWithoutExtension(file), header, lines, new FileInfo(file).Length));
        }
        return result;
    }

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
