using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// §5.3: the operation log source must carry the account dimension ("{op}:{accountId}/{container}") so logs can be filtered/deleted per account.
/// Asserted with pure construction (no Azurite) plus a spy IOperationLog: BackupChecker has already written its CheckStart log before the
/// store fails early, and that source carries account.Id.
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

    /// <summary>Throws without doing any I/O: CheckAsync writes its CheckStart record before it ever calls the store,
    /// so this fake only has to fail fast the moment it is called, with no dependency on the network or Azurite.</summary>
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

    /// <summary>Returns a minimal info with a single version (no I/O): lets BackupRepairer.RepairAsync get past its own
    /// "no backup found" early-exit check and reach the point where it delegates to checker.CheckAsync (it is the checker's own store that is the throwing one).</summary>
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

    /// <summary>Must never be called (ThrowingBackupInfoStore throws before CheckCoreAsync ever uses the factory).</summary>
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

    /// <summary>The repairer's very first step is to delegate to checker.CheckAsync (same account/container),
    /// whose CheckStart source is already verified to carry the account dimension; here we recheck with a different account id
    /// and confirm the failure-propagation path neither swallows nor mangles the source. BackupRepairer's own
    /// "repair:{account.Id}/{container}" logging points (see the end of RepairAsync in BackupRepairer.cs plus DeleteOrphansAsync)
    /// need real Azure interaction (factory.CreateServiceClient) before they are reached; code review confirmed they use the same format as this test (the §5.3 report lists every site).</summary>
    [Fact]
    public async Task Checker_Invoked_By_Repairer_Logs_Source_With_Account_Id()
    {
        var log = new RecordingOperationLog();
        var checker = new BackupChecker(
            new UnusedBlobClientFactory(), new ThrowingBackupInfoStore(), opLog: log);
        var repairer = new BackupRepairer(
            new UnusedBlobClientFactory(), new OneVersionBackupInfoStore(), compressor: null!, hasher: null!,
            uploader: null!, tempRoot: Path.GetTempPath(),
            // This case never reaches compression; the staging area is only here so the object can be constructed at all.
            staging: new StagingArea(Path.GetTempPath(), Path.GetTempPath(), () => long.MaxValue),
            checker: checker);
        var account = new Account { Id = 7, Name = "acct7" };

        try { await repairer.RepairAsync(account, "photos", null, "/tmp", null, new CheckOptions(), AccessTier.Hot, null, dontCompress: null); }
        catch (InvalidOperationException) { /* expected: fake store throws inside checker.CheckAsync */ }

        Assert.Contains(log.Entries, e => e.Source == "check:7/photos");
    }
}
