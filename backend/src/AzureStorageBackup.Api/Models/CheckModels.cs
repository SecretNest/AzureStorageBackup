using Azure.Storage.Blobs.Models;

namespace AzureStorageBackup.Api.Models;

/// <summary>云端检查深度（分级，用户逐次选择；PRD 检查）。</summary>
public enum CloudCheckLevel
{
    /// <summary>不检查云端。</summary>
    None = 0,

    /// <summary>只读云端信息/版本文件并与本地缓存比对，报告漂移（不查数据 blob）。</summary>
    Metadata = 1,

    /// <summary>数据 blob/分卷「存在 + 尺寸」（HEAD，不下载；尺寸缺失则仅验存在）。默认。</summary>
    ExistenceSize = 2,

    /// <summary>下载并重算 hash 校验内容（Archive 需活化）。</summary>
    Content = 3,
}

/// <summary>本地源文件检查深度（分级）。</summary>
public enum LocalCheckLevel
{
    /// <summary>不检查本地。</summary>
    None = 0,

    /// <summary>存在 + 尺寸 + 权限。</summary>
    Attributes = 1,

    /// <summary>内容 hash（＝可从本地修复的判据）。默认。</summary>
    Content = 2,
}

/// <summary>一次检查的选项：云端/本地两条独立深度轴 + Archive 活化 tier。</summary>
public sealed record CheckOptions
{
    public CloudCheckLevel Cloud { get; init; } = CloudCheckLevel.ExistenceSize;
    public LocalCheckLevel Local { get; init; } = LocalCheckLevel.Content;

    /// <summary>Content 级遇 Archive blob 时的活化目标 tier（null=不活化，遇 Archive 记为待活化）。</summary>
    public AccessTier? RehydrateTier { get; init; }
}

/// <summary>某文件的云端状态。</summary>
public enum CloudState { NotChecked = 0, Ok = 1, MissingOrBad = 2 }

/// <summary>某文件的本地状态。</summary>
public enum LocalState { NotChecked = 0, Ok = 1, Missing = 2, Changed = 3 }

/// <summary>单个文件的检查结论。</summary>
public sealed record FileFinding(string Path, string? Ref, CloudState Cloud, LocalState Local)
{
    /// <summary>云端已坏且本地内容一致 → 可从本地修复。</summary>
    public bool Repairable => Cloud == CloudState.MissingOrBad && Local == LocalState.Ok;
}

/// <summary>检查报告：按文件结论 + 可选元数据漂移说明。</summary>
public sealed record CheckReport(int Version, IReadOnlyList<FileFinding> Findings, string? MetadataIssue = null)
{
    public bool Ok => MetadataIssue is null && Findings.All(f => f.Cloud != CloudState.MissingOrBad);

    /// <summary>坏掉的 blob 名（去重，兼容旧前端）。</summary>
    public IReadOnlyList<string> MissingRefs =>
        Findings.Where(f => f.Cloud == CloudState.MissingOrBad && f.Ref is not null)
            .Select(f => f.Ref!).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>坏掉的文件路径（兼容旧前端）。</summary>
    public IReadOnlyList<string> CorruptedPaths =>
        Findings.Where(f => f.Cloud == CloudState.MissingOrBad).Select(f => f.Path).ToList();

    /// <summary>可从本地修复的文件路径。</summary>
    public IReadOnlyList<string> RepairablePaths =>
        Findings.Where(f => f.Repairable).Select(f => f.Path).ToList();
}
