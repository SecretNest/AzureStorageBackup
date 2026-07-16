using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>完整性检查结果。MissingRefs 为索引引用但 container 中缺失的 blob 名。</summary>
public sealed record CheckResult(int Version, int CheckedRefs, IReadOnlyList<string> MissingRefs)
{
    public bool Ok => MissingRefs.Count == 0;
}

/// <summary>
/// 备份完整性检查（M5、PRD 2.3）：读某版本索引，校验其引用的 data blob 与 pack 是否都存在。
/// 存在性检查快且不下载；深度校验（下载+重算 hash）留后续。
/// </summary>
public sealed class BackupChecker(
    IBlobClientFactory factory, IBackupInfoStore store, INotifier? notifier = null, IOperationLog? opLog = null)
{
    public async Task<CheckResult> CheckAsync(
        Account account, string container, string? password, int? version, CancellationToken ct = default)
    {
        var source = $"check:{container}";
        await Record(NotificationEvents.CheckStart, source, $"Check started: {container}", "", ct);
        try
        {
            var result = await CheckCoreAsync(account, container, password, version, ct);
            await Record(
                result.Ok ? NotificationEvents.CheckSuccess : NotificationEvents.CheckFailure, source,
                $"Check {(result.Ok ? "passed" : "failed")}: {container}",
                result.Ok ? $"{result.CheckedRefs} object(s) OK" : $"{result.MissingRefs.Count} missing object(s)", ct);
            return result;
        }
        catch (Exception ex)
        {
            await Record(NotificationEvents.CheckFailure, source, $"Check failed: {container}", ex.Message, ct);
            throw;
        }
    }

    private async Task Record(NotificationEvents evt, string source, string title, string body, CancellationToken ct)
    {
        if (opLog is not null)
            await opLog.AppendAsync(EventLog.LevelOf(evt), source, $"{title} — {body}", ct);
        if (notifier is not null)
            await notifier.NotifyAsync(evt, title, body, ct);
    }

    private async Task<CheckResult> CheckCoreAsync(
        Account account, string container, string? password, int? version, CancellationToken ct)
    {
        var info = await store.ReadInfoAsync(account, container, password, ct)
            ?? throw new InvalidOperationException("No backup found in container.");
        if (info.Versions.Count == 0)
            throw new InvalidOperationException("Backup has no versions.");

        var ver = version is { } v
            ? info.Versions.FirstOrDefault(x => x.Version == v)
              ?? throw new InvalidOperationException($"Version {v} not found.")
            : info.Versions[^1];

        var index = await store.ReadIndexAsync(account, container, ver.IndexBlob, password, ct);

        // 去重收集引用的 blob 名
        var refs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in index.Entries)
        {
            if (e.Storage is not { } s)
                continue;
            refs.Add(s.Kind == "pack" ? $"packs/{s.Ref}.7z" : s.Ref);
        }

        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(container);
        var missing = new List<string>();
        foreach (var name in refs)
        {
            if (!(await cc.GetBlobClient(name).ExistsAsync(ct)).Value)
                missing.Add(name);
        }

        return new CheckResult(ver.Version, refs.Count, missing);
    }
}
