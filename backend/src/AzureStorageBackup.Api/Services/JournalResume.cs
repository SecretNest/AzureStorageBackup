namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 从若干卷还作数的 journal 里建起来的查找表：回答"这份内容上一轮是不是已经传上去了"。
/// <para>
/// 判据一律是**路径 + 内容双对**。光凭路径不行——中断之后文件完全可能被改过；光凭内容 hash
/// 也不行——journal 是按路径记的，同内容不同路径在索引里是两条不同的条目。
/// </para>
/// <para>
/// 纯内存、纯本地，不读云端。记录能进 journal 的前提就是"上传已经确认返回"，所以这里不需要
/// （也不应该）再去云上核对一次——那会违反"备份期间零云读"这条底线。
/// </para>
/// </summary>
public sealed class JournalResume(IReadOnlyList<JournalRecord> records)
{
    public static readonly JournalResume Empty = new([]);

    /// <summary>按路径索引的单文件 blob 记录。重复路径先命中者胜（多卷 journal 会有重复）。</summary>
    private readonly Dictionary<string, JournalRecord> _blobs = BuildBlobs(records);

    /// <summary>按成员集合的规范化键索引的 pack 记录。</summary>
    private readonly Dictionary<string, JournalRecord> _packs = BuildPacks(records);

    /// <summary>预筛用：(路径, 长度, head hash)。三样齐了才值得把整个文件读一遍算全文 hash。</summary>
    private readonly HashSet<string> _prescreen = BuildPrescreen(records);

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

    private static HashSet<string> BuildPrescreen(IReadOnlyList<JournalRecord> records)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in records)
            if (r.Kind == "blob" && r.Path is { } p && r.HeadHash is { } h)
                set.Add(PrescreenKey(p, r.Length, h));
        return set;
    }

    /// <summary>分隔符一律用 NUL：路径里什么都可能有（空格、竖线、制表符），换个可打印字符就会撞键。</summary>
    private static string PrescreenKey(string path, long length, string headHash)
        => $"{path}\0{length}\0{headHash}";

    /// <summary>成员集合的规范化键：按序拼 路径 + 全文 hash + 长度。顺序也算数——entryName 的编号跟着它走。</summary>
    private static string MemberKey(IReadOnlyList<JournalMember> members)
        => string.Join('\n', members.Select(m => $"{m.Path}\0{m.FullHash}\0{m.Length}"));

    /// <summary>
    /// 预筛：只用（路径 + 长度 + head hash）问一句"值不值得把整个文件读一遍"。
    /// 这一关必须存在——恢复时那份内容还没进任何版本索引，本地去重表认不出它，
    /// 不在这里放行的话整轮的活会一件不落地重做一遍。
    /// </summary>
    public bool MayResumeBlob(string path, long length, string headHash)
        => _prescreen.Contains(PrescreenKey(path, length, headHash));

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
    /// 精确匹配一箱 pack。成员集合必须逐一相同，宽松不得：
    /// entryName 的编号是跟着分组走的，成员对不上就会让索引指向箱里根本不存在的条目。
    /// </summary>
    public JournalRecord? FindPack(IReadOnlyList<JournalMember> members)
        => members.Count > 0 && _packs.TryGetValue(MemberKey(members), out var r) ? r : null;
}
