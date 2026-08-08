namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 从若干卷还作数的 journal 里建起来的查找表：回答"这份内容上一轮是不是已经传上去了"。
/// <para>
/// 判据一律是**路径 + 内容双对**。光凭路径不行——中断之后文件完全可能被改过；光凭内容 hash
/// 也不行——journal 是按路径记的，同内容不同路径在索引里是两条不同的条目。
/// </para>
/// <para>
/// 多卷之间同一条路径重复时新的胜，判据见 <see cref="FromVolumes"/>。
/// </para>
/// <para>
/// 纯内存、纯本地，不读云端。记录能进 journal 的前提就是"上传已经确认返回"，所以这里不需要
/// （也不应该）再去云上核对一次——那会违反"备份期间零云读"这条底线。
/// </para>
/// </summary>
public sealed class JournalResume(IReadOnlyList<JournalRecord> records)
{
    public static readonly JournalResume Empty = new([]);

    /// <summary>
    /// 从若干卷 journal 建表。**按开跑时刻从新到旧**把记录串起来，让下面的"先命中者胜"落成"新的胜"。
    /// <para>
    /// 不排的话顺序来自 <see cref="BackupJournalStore.ListAsync"/>，那是按文件名的序数序，而文件名是
    /// runId——每轮新生成的 GUID 前缀。于是"同一条路径在两卷里记着不同内容"（两次挂起之间文件被改过）
    /// 时谁胜是掷骰子。掷输了不会漏传：被盖住的那条记录从 <see cref="FindBlob"/> 和
    /// <see cref="ConfirmedBlobs"/> 都够不着，四项内容判据对不上就当没有，照传不误；
    /// 而清理器那边 <c>LoadActiveRefsAsync</c> 是逐条过所有记录的，被盖住的块照样受保护。
    /// 代价只是"明明上一轮传过的那一版，这一轮又传一遍"，且随运行不同时有时无——这种不确定性
    /// 本身就不该留在恢复路径上。
    /// </para>
    /// </summary>
    public static JournalResume FromVolumes(IReadOnlyList<JournalContent> volumes)
        => volumes.Count == 0
            ? Empty
            : new JournalResume([..
                volumes.OrderByDescending(v => v.Header.StartedAt).SelectMany(v => v.Records)]);

    /// <summary>按路径索引的单文件 blob 记录。重复路径先命中者胜；调用方
    /// （<see cref="FromVolumes"/>）已把各卷按开跑时刻从新到旧排好，所以胜出的是最新那一卷记的。</summary>
    private readonly Dictionary<string, JournalRecord> _blobs = BuildBlobs(records);

    /// <summary>按成员集合的规范化键索引的 pack 记录。</summary>
    private readonly Dictionary<string, JournalRecord> _packs = BuildPacks(records);

    public bool IsEmpty => _blobs.Count == 0 && _packs.Count == 0;

    public int RecordCount => _blobs.Count + _packs.Count;

    private static Dictionary<string, JournalRecord> BuildBlobs(IReadOnlyList<JournalRecord> records)
    {
        var map = new Dictionary<string, JournalRecord>(StringComparer.Ordinal);
        foreach (var r in records)
            if (r.Kind == "blob" && r.Path is { } p && r.FullHash is not null)
                map.TryAdd(p, r);
        return map;
    }

    private static Dictionary<string, JournalRecord> BuildPacks(IReadOnlyList<JournalRecord> records)
    {
        var map = new Dictionary<string, JournalRecord>(StringComparer.Ordinal);
        foreach (var r in records)
            if (r.Kind == "pack" && r.Members.Count > 0)
                map.TryAdd(MemberKey(r.Members), r);
        return map;
    }

    /// <summary>
    /// 成员集合的规范化键：按序拼 路径 + 全文 hash + 长度。
    /// <para>
    /// **故意不含 <see cref="JournalMember.EntryName"/>**。在本仓库里它恒等于成员自己的路径
    /// （<c>new PackEntry(f.Path, f.Path, …)</c>，见 <c>BackupOrchestrator.ProcessPackAsync</c>；
    /// <c>RestoreOrchestrator</c> 里那段"按 EntryName 而不是 Path 取"说的是**跨版本去重**时
    /// 一个新路径指向老包里的老成员名，不是装箱时会另起一套编号），因此把它拼进键里除了让
    /// 同一份内容多算一遍路径之外没有任何区分力。
    /// </para>
    /// <para>
    /// 顺序也算数：<c>PackInfo.Members</c> 是一串按序排下来的成员 hash，键不认顺序就会让
    /// 同一组成员的两种排法互相命中，而记进信息文件的那串顺序与归档里的实际内容对不上。
    /// </para>
    /// </summary>
    private static string MemberKey(IReadOnlyList<JournalMember> members)
        => string.Join('\n', members.Select(m => $"{m.Path}\0{m.FullHash}\0{m.Length}"));

    /// <summary>精确匹配一个单文件 blob。四项内容判据全对上才认。</summary>
    public JournalRecord? FindBlob(string path, string fullHash, long length, string headHash, string tailHash)
        => _blobs.TryGetValue(path, out var r)
            && string.Equals(r.FullHash, fullHash, StringComparison.Ordinal)
            && r.Length == length
            && string.Equals(r.HeadHash, headHash, StringComparison.Ordinal)
            && string.Equals(r.TailHash, tailHash, StringComparison.Ordinal)
            ? r
            : null;

    /// <summary>
    /// 把这些单文件记录按**内容身份**交出去，喂给 <see cref="LocalDedupResolver.Build"/>。
    /// <para>
    /// 恢复本身是按路径认账的（见类注释），但这些块在云上的处境和索引里的块一模一样：
    /// 传完了、地址占着。不告诉去重表的话，一个**同内容不同路径**的文件会认不出它，
    /// 重压之后 ResolveAsync 给回同一个地址，上传前那一步清残卷就会把上一轮的成果删掉再传一遍。
    /// 详见 <c>LocalDedupResolver.Build</c> 的 <c>confirmed</c> 参数说明。
    /// </para>
    /// <para>四项内容判据缺一不可，缺的记录直接跳过——身份不全就不该参与去重。</para>
    /// </summary>
    public IReadOnlyList<ConfirmedBlob> ConfirmedBlobs()
    {
        var list = new List<ConfirmedBlob>(_blobs.Count);
        foreach (var r in _blobs.Values)
            if (r is { FullHash: { } full, HeadHash: { } head, TailHash: { } tail })
                list.Add(new ConfirmedBlob(
                    full, r.Length, head, tail,
                    new ResolvedBlob(r.Ref, r.Raw, Math.Max(1, r.Volumes), r.VolumeSizes)));
        return list;
    }

    /// <summary>
    /// 精确匹配一箱 pack。成员集合必须逐一相同，宽松不得。
    /// <para>
    /// 理由不在名字上（归档里的成员名就是成员自己的路径，见 <see cref="MemberKey"/>），
    /// 而在**记账与归档必须严丝合缝**：命中之后走的 <c>RecordPackAsync</c> 会拿**本轮这一组**
    /// 的成员表去写 <c>PackInfo.Members</c> / <c>OriginalBytes</c>，并给每个成员写一条指向
    /// 这个包的索引条目。允许部分匹配（本轮这组是上一轮那箱的超集）就等于宣称归档里有一些
    /// 它根本没有的成员：还原时解不出文件、检查时报缺失，而索引一口咬定它在。
    /// 子集同样不行——<c>OriginalBytes</c> 会算少，死重压实据此判断这箱还剩多少活肉。
    /// </para>
    /// <para>
    /// 分组本身是确定性的（同样的基线、同样的源、同样的界），所以严格相等在实际中命中率并不低；
    /// 对不上就重压——一箱都是小文件，重压很便宜。
    /// </para>
    /// </summary>
    public JournalRecord? FindPack(IReadOnlyList<JournalMember> members)
        => members.Count > 0 && _packs.TryGetValue(MemberKey(members), out var r) ? r : null;
}
