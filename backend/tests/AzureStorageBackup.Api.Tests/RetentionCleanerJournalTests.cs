using System.Net.Sockets;
using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

[Trait("Category", "Integration")]
public sealed class RetentionCleanerJournalTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "asb-cleanj-" + Guid.NewGuid().ToString("N"));
    private readonly BackupJournalStore _journals;

    public RetentionCleanerJournalTests()
    {
        Directory.CreateDirectory(_temp);
        _journals = new BackupJournalStore(Path.Combine(_temp, "journal"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* best effort */ }
    }

    private static Account AzuriteAccount() => new()
    {
        Id = 45,
        Name = "azurite",
        BlobEndpoint = "http://127.0.0.1:10000/devstoreaccount1",
        AccountKeyProtected = TestSecrets.Protect(AzuriteKey),
        Region = AzureRegion.Global,
    };

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static string RandomName(string p) => p + Guid.NewGuid().ToString("N")[..8];

    private static async Task PutAsync(BlobContainerClient container, string name, string body)
        => await container.GetBlobClient(name).UploadAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(body)), overwrite: true);

    private static async Task<List<string>> NamesAsync(BlobContainerClient container, string prefix)
    {
        var names = new List<string>();
        await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, default))
            names.Add(b.Name);
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private RetentionCleaner Cleaner(BlobClientFactory factory)
        => new(factory, new BackupInfoStore(factory, new SevenZipArchiveCodec()), new RetentionEvaluator(),
            journals: _journals);

    private async Task WriteJournalAsync(int accountId, string container, string runId, params JournalRecord[] records)
    {
        await using var j = await _journals.CreateAsync(accountId, container, runId, new JournalHeader
        {
            RunId = runId, ConfigId = 1, StartedAt = DateTimeOffset.UnixEpoch, BaselineVersion = 0,
            LocalRoot = "/data/src", EncryptionIdentity = "plain",
        }, default);
        foreach (var r in records)
            await j.AppendAsync(r, default);
    }

    private static CleanupOptions Options() => new()
    {
        Retention = new RetentionPolicy { MaxVersions = 50, MaxAgeDays = 365, Mode = RetentionMode.EitherTriggers },
    };

    [SkippableFact]
    public async Task Journalled_blocks_survive_the_orphan_sweep()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");

        var account = AzuriteAccount();
        var name = RandomName("cleanj");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            await PutAsync(container, "data/keep", "kept");
            await PutAsync(container, "data/keep.001", "kept volume");
            await PutAsync(container, "data/gone", "orphan");
            await PutAsync(container, "packs/pkeep.7z", "kept pack");
            await PutAsync(container, "packs/pgone.7z", "orphan pack");
            await WriteJournalAsync(account.Id, name, "run-x",
                new JournalRecord { Kind = "blob", Ref = "data/keep", Path = "a.bin", FullHash = "keep", Volumes = 2 },
                new JournalRecord { Kind = "pack", Ref = "pkeep", VolumeSizes = [5] });

            // 一个版本都没退役，但仍要扫：取消留下的块正是这种情形。
            var report = await Cleaner(factory).CleanupAsync(
                account, name, null, Options(),
                new BackupInfoFile { Backup = new BackupMeta { Name = name, CreatedAt = DateTimeOffset.UnixEpoch } },
                default, sweepOrphans: true);

            Assert.Equal(["data/keep", "data/keep.001"], await NamesAsync(container, "data/"));
            Assert.Equal(["packs/pkeep.7z"], await NamesAsync(container, "packs/"));
            Assert.Equal(1, report.DeletedBlobs);
            Assert.Equal(1, report.DeletedPacks);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Once_the_journal_is_gone_the_blocks_are_swept()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");

        var account = AzuriteAccount();
        var name = RandomName("cleanj");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            await PutAsync(container, "data/keep", "kept");
            await WriteJournalAsync(account.Id, name, "run-x",
                new JournalRecord { Kind = "blob", Ref = "data/keep", Path = "a.bin", FullHash = "keep" });
            _journals.DeleteAll(account.Id, name);   // 删配置兜底做的就是这一步

            await Cleaner(factory).CleanupAsync(
                account, name, null, Options(),
                new BackupInfoFile { Backup = new BackupMeta { Name = name, CreatedAt = DateTimeOffset.UnixEpoch } },
                default, sweepOrphans: true);

            Assert.Empty(await NamesAsync(container, "data/"));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Without_the_sweep_flag_a_no_op_cleanup_touches_nothing()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");

        var account = AzuriteAccount();
        var name = RandomName("cleanj");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            await PutAsync(container, "data/gone", "orphan");

            // 没有版本退役、也没让它扫 → 一个 LIST 都不该发。几十万对象的容器上这不是白干的。
            var report = await Cleaner(factory).CleanupAsync(
                account, name, null, Options(),
                new BackupInfoFile { Backup = new BackupMeta { Name = name, CreatedAt = DateTimeOffset.UnixEpoch } },
                default);

            Assert.True(report.IsEmpty);
            Assert.Equal(["data/gone"], await NamesAsync(container, "data/"));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
