namespace AzureStorageBackup.Api.Services;

/// <summary>How the two retention limits combine (PRD 3.2).</summary>
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
/// Retention evaluation (M4 design §10): pick the version numbers to delete from the maximum count, the
/// maximum age and the combination mode.
/// The newest version is always kept (so a backup can never be emptied). Which data blobs and packs to
/// delete is worked out separately by the orchestrator from what the remaining versions reference.
/// </summary>
public sealed class RetentionEvaluator
{
    public IReadOnlyList<int> VersionsToDelete(
        IReadOnlyList<VersionRef> versions, RetentionPolicy policy, DateTimeOffset now)
    {
        var ordered = versions.OrderBy(v => v.Version).ToList(); // oldest → newest
        if (ordered.Count == 0)
            return [];

        var newest = ordered[^1].Version;
        var cutoff = now.AddDays(-policy.MaxAgeDays);
        var toDelete = new List<int>();

        for (var i = 0; i < ordered.Count; i++)
        {
            var v = ordered[i];
            var rankFromNewest = ordered.Count - i; // oldest = Count, newest = 1
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
