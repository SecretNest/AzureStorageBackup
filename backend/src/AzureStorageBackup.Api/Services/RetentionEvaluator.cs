namespace AzureStorageBackup.Api.Services;

/// <summary>超量判断方式（PRD 3.2）。</summary>
public enum RetentionMode
{
    VersionOnly,
    TimeOnly,
    EitherTriggers,
    BothRequired,
}

public sealed record RetentionPolicy
{
    public int MaxVersions { get; init; } = 100;
    public int MaxAgeDays { get; init; } = 180;
    public RetentionMode Mode { get; init; } = RetentionMode.EitherTriggers;
}

public sealed record VersionRef(int Version, DateTimeOffset CreatedAt);

/// <summary>
/// 版本保留评估（M4 设计 §10）：按最大版本数 + 最长时间 + 组合模式选出要删的版本号。
/// 始终保留最新版本（避免清空备份）。删除数据 blob/pack 由编排器据剩余版本引用另算。
/// </summary>
public sealed class RetentionEvaluator
{
    public IReadOnlyList<int> VersionsToDelete(
        IReadOnlyList<VersionRef> versions, RetentionPolicy policy, DateTimeOffset now)
    {
        var ordered = versions.OrderBy(v => v.Version).ToList(); // 旧→新
        if (ordered.Count == 0)
            return [];

        var newest = ordered[^1].Version;
        var cutoff = now.AddDays(-policy.MaxAgeDays);
        var toDelete = new List<int>();

        for (var i = 0; i < ordered.Count; i++)
        {
            var v = ordered[i];
            var rankFromNewest = ordered.Count - i; // 最旧 = Count，最新 = 1
            var excess = rankFromNewest > policy.MaxVersions;
            var tooOld = v.CreatedAt < cutoff;

            var delete = policy.Mode switch
            {
                RetentionMode.VersionOnly => excess,
                RetentionMode.TimeOnly => tooOld,
                RetentionMode.EitherTriggers => excess || tooOld,
                RetentionMode.BothRequired => excess && tooOld,
                _ => false,
            };

            if (delete && v.Version != newest)
                toDelete.Add(v.Version);
        }

        return toDelete;
    }
}
