namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 一个不入箱的后到者：内容与某个 leader 相同，索引条目将直接指向 leader 在包里的那个成员。
/// <para>
/// 带齐四项内容身份，是为了 leader 走岔时能把它当作一个普通待处理文件重新跑一遍
/// （见编排器收尾处的悬空重跑）——那时手上必须有它自己的长度与 hash。
/// </para>
/// </summary>
public sealed record PlannedAlias(
    string Path, long Length, string FullHash, string HeadHash, string TailHash);

/// <summary>
/// 同一轮备份内、跨箱的打包成员去重。
/// <para>
/// 打包的小文件从前只有两条去重：同一箱内靠 7z 的 solid 归档（字典跨成员匹配），跨版本靠
/// <see cref="LocalDedupResolver.TryFindPackMember"/>。缺的是**本轮之内、跨箱**那一段——
/// 不同箱之间压缩不共享字典，同一份内容会实打实地存两遍，而 <c>_packMembers</c> 只从历史
/// 版本索引构建，本轮新封的箱不进那张表。
/// </para>
/// <para>
/// 这张表只登记"谁是第一份"，**不**登记"它最后存到哪儿去了"——那要等消费者全部收工才知道
/// （leader 可能在压缩窗口里被改写、可能读不开、可能变大到改走单文件 blob）。回填因此放在
/// 收尾统一做，判断只看最终态。代价是别名要多等一会儿，换来的是这里一个并发原语都不需要。
/// </para>
/// <para>
/// diff 单线程独占，不加锁——与编排器里的 <c>dirPending</c>/<c>crossPending</c> 同一条约束。
/// </para>
/// </summary>
public sealed class PackAliasTable
{
    // 四项内容身份 → 第一个见到这份内容的路径。首次备份时每个变更小文件各占一条
    // （约 150 字节），20 万个约 30 MB——与装箱本身的在途状态同一个量级，可以接受。
    private readonly Dictionary<string, string> _leaderByContent = new(StringComparer.Ordinal);

    // leader 路径 → 挂在它身上的别名。**只有真有别名的 leader 才建列表**：
    // 一次首备有几十万个 leader，给每个都建一个空 List 是白占几十 MB。
    private readonly Dictionary<string, List<PlannedAlias>> _aliasesByLeader = new(StringComparer.Ordinal);

    /// <summary>只含真有别名的 leader。收尾回填遍历它。</summary>
    public IReadOnlyDictionary<string, List<PlannedAlias>> AliasesByLeader => _aliasesByLeader;

    /// <summary>
    /// 本轮这份内容是不是已经有 leader 了。
    /// <para>
    /// 返回 <c>true</c>：已有，<paramref name="candidate"/> 已登记为那个 leader 的别名，
    /// 调用方**不要**入箱。返回 <c>false</c>：这是第一份（或四项不全，不参与去重），照旧入箱。
    /// </para>
    /// <para>
    /// 四项**严格**相等，缺失也算不等——与 <see cref="LocalDedupResolver.TryFindPackMember"/>
    /// 同一套判据。同样是"这份内容是不是已经有了"的判断，两条路各有一套标准是说不通的：
    /// 判错就让索引指向别人的内容、还原时出来错误数据。
    /// </para>
    /// </summary>
    public bool TryClaim(
        string? fullHash, long length, string? headHash, string? tailHash, PlannedAlias candidate)
    {
        if (fullHash is null || headHash is null || tailHash is null)
            return false;

        var key = LocalDedupResolver.ContentKey(fullHash, length, headHash, tailHash);
        if (_leaderByContent.TryGetValue(key, out var leader))
        {
            if (!_aliasesByLeader.TryGetValue(leader, out var list))
                _aliasesByLeader[leader] = list = [];
            list.Add(candidate);
            return true;
        }

        _leaderByContent[key] = candidate.Path;
        return false;
    }
}
