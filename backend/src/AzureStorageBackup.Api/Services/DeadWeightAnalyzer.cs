namespace AzureStorageBackup.Api.Services;

/// <summary>pack 成员及其原始尺寸（编排器从索引提供）。</summary>
public sealed record PackMember(string FullHash, long OriginalBytes);

/// <summary>现存 pack 的状态。</summary>
public sealed record PackState(string PackId, IReadOnlyList<PackMember> Members);

public sealed record DeadWeightOptions
{
    /// <summary>死重比例阈值（默认 30%，M4 §6），严格大于才触发重组。</summary>
    public double Threshold { get; init; } = 0.30;
}

/// <summary>某 pack 需重组：其仍有效成员应重新参与规划，旧 pack 备份完成后删除。</summary>
public sealed record RepackDecision(
    string PackId,
    IReadOnlyList<PackMember> LiveMembers,
    long DeadBytes,
    long OriginalBytes,
    double DeadRatio);

/// <summary>
/// 死重压实分析（M4 设计 §6）：成员的 hash 不再被任何有效版本引用即为死重。
/// pack 死重比例（原始尺寸）严格超过阈值时标记重组，给出仍有效成员。
/// </summary>
public sealed class DeadWeightAnalyzer
{
    public IReadOnlyList<RepackDecision> Analyze(
        IEnumerable<PackState> packs, ISet<string> referencedHashes, DeadWeightOptions? options = null)
    {
        options ??= new DeadWeightOptions();
        var decisions = new List<RepackDecision>();

        foreach (var pack in packs)
        {
            long original = 0, dead = 0;
            var live = new List<PackMember>();

            foreach (var member in pack.Members)
            {
                original += member.OriginalBytes;
                if (referencedHashes.Contains(member.FullHash))
                    live.Add(member);
                else
                    dead += member.OriginalBytes;
            }

            var ratio = original == 0 ? 0 : (double)dead / original;
            if (ratio > options.Threshold)
                decisions.Add(new RepackDecision(pack.PackId, live, dead, original, ratio));
        }

        return decisions;
    }
}
