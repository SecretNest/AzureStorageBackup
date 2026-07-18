namespace AzureStorageBackup.Api.Services;

/// <summary>还原时的冲突处理模式（决策 3）。默认 <see cref="OverwriteIfChanged"/> = 现状行为。</summary>
public enum RestoreConflictMode
{
    /// <summary>覆盖仅当变更时：本地已是相同内容则跳过，否则覆盖写入（现状）。</summary>
    OverwriteIfChanged = 0,

    /// <summary>目标已存在（无论内容异同）即跳过，不写入。</summary>
    Skip = 1,

    /// <summary>目标存在且内容不同 → 先把现有本地文件改名为 {name}.bak-{ts} 再写还原内容（旧内容永不丢失）。</summary>
    RenameKeep = 2,
}

/// <summary>Archive blob 活化优先级（透传 Azure <c>RehydratePriority</c>）。</summary>
public enum RestoreRehydratePriority
{
    /// <summary>标准优先级（默认，最长约 15 小时）。</summary>
    Standard = 0,

    /// <summary>高优先级（通常 &lt; 1 小时，费用更高）。</summary>
    High = 1,
}

/// <summary>还原冲突「重命名保留」（决策 3）：把现有本地文件改名为 {name}.bak-{yyyyMMdd-HHmmss}
/// （冲突追加 -1/-2…），腾出原名供还原写入。旧内容永不丢失。</summary>
public static class RestoreConflict
{
    public static string RenameExisting(string dest, DateTimeOffset now)
    {
        var stamp = now.ToString("yyyyMMdd-HHmmss");
        var baseBak = dest + ".bak-" + stamp;
        var target = baseBak;
        var n = 1;
        while (File.Exists(target) || Directory.Exists(target))
            target = baseBak + "-" + n++;
        File.Move(dest, target);
        return target;
    }
}
