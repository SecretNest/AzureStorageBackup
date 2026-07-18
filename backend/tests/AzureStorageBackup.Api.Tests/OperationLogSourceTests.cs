using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// §5.3：操作日志 source 须含 account 维度（"{op}:{accountId}/{container}"），便于按 account 过滤/删除。
/// 用纯构造（无 Azurite）+ spy IOperationLog 断言：BackupChecker 在 store 早期失败前就已写入 CheckStart 日志，
/// 其 source 携带 account.Id。
/// </summary>
public sealed class OperationLogSourceTests
{
    private sealed class RecordingOperationLog : IOperationLog
    {
        public List<(OperationLogLevel Level, string Source, string Message)> Entries { get; } = [];

        public Task AppendAsync(OperationLogLevel level, string source, string message, CancellationToken ct = default, bool? durable = null)
        {
            lock (Entries) Entries.Add((level, source, message));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LogEntry>> QueryAsync(
            OperationLogLevel? minLevel, string? source, DateTimeOffset? from, DateTimeOffset? to, int limit,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LogEntry>>([]);

        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteForContainerAsync(int accountId, string container, CancellationToken ct = default) => Task.CompletedTask;
        public Task PurgeBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default) => Task.CompletedTask;
        public Task TrimAsync(int? maxAgeDays, DateTimeOffset now, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>抛出而不做任何 I/O：CheckAsync 的 CheckStart 记录发生在 store 调用之前，
    /// 故此 fake 只需保证一旦被调用就快速失败，不依赖网络/Azurite。</summary>
    private sealed class ThrowingBackupInfoStore : IBackupInfoStore
    {
        public Task<BackupInfoFile?> ReadInfoAsync(Account account, string container, string? password, CancellationToken ct = default)
            => throw new InvalidOperationException("no backup found (test fake)");

        public Task<(BackupInfoFile Info, string ETag)?> ReadInfoWithETagAsync(Account account, string container, string? password, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task WriteInfoAsync(Account account, string container, BackupInfoFile info, string? password, AccessTier? tier = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<string> WriteInfoConditionalAsync(Account account, string container, BackupInfoFile info, string? password, AccessTier? tier, string? ifMatch, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<VersionIndex> ReadIndexAsync(Account account, string container, string indexBlob, string? password, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<string> WriteIndexAsync(Account account, string container, int version, VersionIndex index, string? password, AccessTier? tier = null, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    /// <summary>返回一个带单个版本的最小 info（不做 I/O）：让 BackupRepairer.RepairAsync 越过其自身的
    /// "no backup found" 早退检查、走到委派 checker.CheckAsync 那一步（checker 自己的 store 才是 Throwing 的）。</summary>
    private sealed class OneVersionBackupInfoStore : IBackupInfoStore
    {
        private static BackupInfoFile Info() => new()
        {
            Backup = new BackupMeta { Name = "photos" },
            Versions = [new BackupVersion { Version = 1, IndexBlob = "v1.json", Stats = new VersionStats(0, 0, 0, 0) }],
        };

        public Task<BackupInfoFile?> ReadInfoAsync(Account account, string container, string? password, CancellationToken ct = default)
            => Task.FromResult<BackupInfoFile?>(Info());

        public Task<(BackupInfoFile Info, string ETag)?> ReadInfoWithETagAsync(Account account, string container, string? password, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task WriteInfoAsync(Account account, string container, BackupInfoFile info, string? password, AccessTier? tier = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<string> WriteInfoConditionalAsync(Account account, string container, BackupInfoFile info, string? password, AccessTier? tier, string? ifMatch, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<VersionIndex> ReadIndexAsync(Account account, string container, string indexBlob, string? password, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<string> WriteIndexAsync(Account account, string container, int version, VersionIndex index, string? password, AccessTier? tier = null, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    /// <summary>永不应被调用（ThrowingBackupInfoStore 在 CheckCoreAsync 使用 factory 之前就已抛出）。</summary>
    private sealed class UnusedBlobClientFactory : IBlobClientFactory
    {
        public BlobServiceClient CreateServiceClient(Account account) => throw new NotImplementedException();
        public Task<ConnectionResult> TestConnectionAsync(Account account, CancellationToken ct = default) => throw new NotImplementedException();
    }

    [Fact]
    public async Task Check_Log_Source_Includes_Account_Id()
    {
        var log = new RecordingOperationLog();
        var checker = new BackupChecker(
            new UnusedBlobClientFactory(), new ThrowingBackupInfoStore(), opLog: log);
        var account = new Account { Id = 3, Name = "acct3" };

        try { await checker.CheckAsync(account, "photos", null, null, new CheckOptions()); }
        catch (InvalidOperationException) { /* expected: fake store throws */ }

        Assert.Contains(log.Entries, e => e.Source == "check:3/photos");
    }

    /// <summary>Repairer 的第一步就是委派给 checker.CheckAsync（同一 account/container），
    /// 其记录的 CheckStart source 已验证携带 account 维度；这里换一个 account id 复核，
    /// 并确认失败传播路径不吞掉/篡改 source。BackupRepairer 自身的 "repair:{account.Id}/{container}"
    /// 记录点（见 BackupRepairer.cs RepairAsync 末尾 + DeleteOrphansAsync）在到达前需要真实 Azure 交互
    /// （factory.CreateServiceClient），已通过代码审查确认与本测试同一格式一致（§5.3 报告有逐处列表）。</summary>
    [Fact]
    public async Task Checker_Invoked_By_Repairer_Logs_Source_With_Account_Id()
    {
        var log = new RecordingOperationLog();
        var checker = new BackupChecker(
            new UnusedBlobClientFactory(), new ThrowingBackupInfoStore(), opLog: log);
        var repairer = new BackupRepairer(
            new UnusedBlobClientFactory(), new OneVersionBackupInfoStore(), compressor: null!, hasher: null!,
            uploader: null!, tempRoot: Path.GetTempPath(), checker: checker);
        var account = new Account { Id = 7, Name = "acct7" };

        try { await repairer.RepairAsync(account, "photos", null, "/tmp", null, new CheckOptions(), AccessTier.Hot, null); }
        catch (InvalidOperationException) { /* expected: fake store throws inside checker.CheckAsync */ }

        Assert.Contains(log.Entries, e => e.Source == "check:7/photos");
    }
}
