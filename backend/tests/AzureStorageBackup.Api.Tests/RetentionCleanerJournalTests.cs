using System.Net.Sockets;
using System.Text;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage;
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

    private RetentionCleaner Cleaner(IBlobClientFactory factory, DeadWeightCompactor? compactor = null)
        => new(factory, new BackupInfoStore(factory, new SevenZipArchiveCodec()), new RetentionEvaluator(),
            compactor, journals: _journals);

    /// <summary>真的一个云端请求都没发过——"没让它扫就不该发 LIST"这句话，只有数出来才算钉住。</summary>
    private sealed class CountingFactory(BlobClientFactory inner) : IBlobClientFactory
    {
        private int _requests;
        private int _lists;

        /// <summary>发出去的请求总数（含 LIST、HEAD、DELETE、PUT）。</summary>
        public int Requests => Volatile.Read(ref _requests);

        /// <summary>其中的列举请求（<c>comp=list</c>）。孤儿扫描的代价就摊在这里。</summary>
        public int Lists => Volatile.Read(ref _lists);

        public BlobServiceClient CreateServiceClient(Account account)
        {
            var uri = new Uri(account.BlobEndpoint);
            var credential = new StorageSharedKeyCredential(
                BlobClientFactory.ParseAccountName(uri), TestSecrets.Reader.RevealAccountKey(account));
            var options = new BlobClientOptions();
            options.AddPolicy(new CountingPolicy(this), HttpPipelinePosition.PerCall);
            return new BlobServiceClient(uri, credential, options);
        }

        public Task<ConnectionResult> TestConnectionAsync(Account account, CancellationToken ct = default)
            => inner.TestConnectionAsync(account, ct);

        private sealed class CountingPolicy(CountingFactory owner) : HttpPipelineSynchronousPolicy
        {
            public override void OnSendingRequest(HttpMessage message)
            {
                Interlocked.Increment(ref owner._requests);
                if (message.Request.Uri.Query.Contains("comp=list", StringComparison.Ordinal))
                    Interlocked.Increment(ref owner._lists);
            }
        }
    }

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

    private static CleanupOptions Options(string? localRoot = null) => new()
    {
        Retention = new RetentionPolicy { MaxVersions = 50, MaxAgeDays = 365, Mode = RetentionMode.EitherTriggers },
        LocalRoot = localRoot,
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
            // pack 也要带上分卷。"基名保住了、分卷被扫走"是这条判据最容易出的错，而 pack 那侧的
            // 名字比 data 侧更绕：PackIdOf 是切在 ".7z" 上的，不是切在三位数字后缀上。
            await PutAsync(container, "packs/pkeep.7z", "kept pack");
            await PutAsync(container, "packs/pkeep.7z.001", "kept pack volume");
            await PutAsync(container, "packs/pgone.7z", "orphan pack");
            await PutAsync(container, "packs/pgone.7z.001", "orphan pack volume");
            await WriteJournalAsync(account.Id, name, "run-x",
                new JournalRecord { Kind = "blob", Ref = "data/keep", Path = "a.bin", FullHash = "keep", Volumes = 2 },
                new JournalRecord { Kind = "pack", Ref = "pkeep", VolumeSizes = [5] });

            // 一个版本都没退役，但仍要扫：取消留下的块正是这种情形。
            var report = await Cleaner(factory).CleanupAsync(
                account, name, null, Options(),
                new BackupInfoFile { Backup = new BackupMeta { Name = name, CreatedAt = DateTimeOffset.UnixEpoch } },
                default, sweepOrphans: true);

            Assert.Equal(["data/keep", "data/keep.001"], await NamesAsync(container, "data/"));
            Assert.Equal(["packs/pkeep.7z", "packs/pkeep.7z.001"], await NamesAsync(container, "packs/"));
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

            // 没有版本退役、也没让它扫 → 一个云端请求都不该发。几十万对象的容器上，那两趟 LIST
            // 不是白干的——而"孤儿还在"这一条本身分不出"列过一遍又留下了"和"根本没列"，
            // 所以这里数的是真发出去的请求（计数器套在 HTTP 管道上）。
            var counting = new CountingFactory(factory);
            var report = await Cleaner(counting).CleanupAsync(
                account, name, null, Options(),
                new BackupInfoFile { Backup = new BackupMeta { Name = name, CreatedAt = DateTimeOffset.UnixEpoch } },
                default);

            Assert.Equal(0, counting.Requests);
            Assert.True(report.IsEmpty);
            Assert.Equal(["data/gone"], await NamesAsync(container, "data/"));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// journal 判据也管着 <c>info.Packs</c> 那一行。删了那一行，箱子本身在云上还好端端的
    /// （上面那条用例保着），但信息文件里查无此人：下一轮拿 journal 复用这一箱时
    /// <c>RecordPackAsync</c> 会照原样把它写回去，而这中间任何一次检查/还原都认为这一箱不存在。
    /// </summary>
    [SkippableFact]
    public async Task A_journalled_pack_keeps_its_row_in_the_info_file()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");

        var account = AzuriteAccount();
        var name = RandomName("cleanj");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            await PutAsync(container, "packs/pkeep.7z", "kept pack");
            await PutAsync(container, "packs/pgone.7z", "orphan pack");
            await WriteJournalAsync(account.Id, name, "run-x",
                new JournalRecord { Kind = "pack", Ref = "pkeep", VolumeSizes = [5] });

            var info = new BackupInfoFile
            {
                Backup = new BackupMeta { Name = name, CreatedAt = DateTimeOffset.UnixEpoch },
                Packs =
                {
                    ["pkeep"] = new PackInfo { Blob = "packs/pkeep.7z", OriginalBytes = 5 },
                    ["pgone"] = new PackInfo { Blob = "packs/pgone.7z", OriginalBytes = 5 },
                },
            };

            await Cleaner(factory).CleanupAsync(account, name, null, Options(), info, default, sweepOrphans: true);

            // 一个保留版本都没有，两箱都不被索引引用——分开它们的只有 journal。
            Assert.Equal(["pkeep"], info.Packs.Keys.ToList());
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 纯孤儿扫描（没有任何版本退役）不得把死重压实和信息文件重写一起带起来。
    /// <para>
    /// 死重只在版本退役时增加，而压实失败或放弃时 <see cref="DeadWeightCompactor"/> 只是把同一个
    /// DeadBytes 原样写回去——下一轮的判断因此一模一样。挂在每晚一次的定时清理上，就是同一批包
    /// 每晚下载、重压、重传一遍，永远如此。信息文件同理：什么都没变还要重写一次，是白付一次
    /// 带 If-Match 的条件写。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_sweep_with_no_retirement_neither_compacts_nor_rewrites_the_info_file()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");

        var account = AzuriteAccount();
        var name = RandomName("cleanj");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            await PutAsync(container, "packs/p1.7z", "a pack that is 99.9% dead weight on paper");
            await PutAsync(container, "data/gone", "orphan");

            // 存活成员在本地实打实放一份、hash 也是真算的：压实一旦被叫起来，它走的就是**成功**
            // 那一支（本地取料、重压、覆盖上传），而不是"放弃/抛异常"那两支。这一点是这条测试的
            // 判据能不能分辨"没跑过"的关键——放弃与失败会把 DeadBytes 写成 999，一眼可见；
            // 唯独成功那一支把 DeadBytes 写回 0，与"从来没跑过"长得一模一样。
            var localRoot = Path.Combine(_temp, "src");
            Directory.CreateDirectory(localRoot);
            await File.WriteAllTextAsync(Path.Combine(localRoot, "a.bin"), "x");
            var liveHash = await new FileHasher().FullHashAsync(Path.Combine(localRoot, "a.bin"), default);

            // 版本 1 只引用 p1 里的一个 1 字节成员，而这一箱记着 1000 字节原始尺寸 →
            // 死重 99.9%，远超默认 30% 阈值。压实一旦被叫起来，一定会动这一箱。
            var indexBlob = await store.WriteIndexAsync(account, name, 1, new VersionIndex
            {
                Version = 1,
                Entries =
                [
                    new IndexEntry
                    {
                        Path = "a.bin", Kind = "file", Length = 1, Permissions = "644", FullHash = liveHash,
                        Storage = new StorageRef { Kind = "pack", Ref = "p1", EntryName = "a.bin" },
                    },
                ],
            }, password: null);

            var info = new BackupInfoFile
            {
                Backup = new BackupMeta { Name = name, CreatedAt = DateTimeOffset.UnixEpoch },
                Versions =
                {
                    new BackupVersion
                    {
                        Version = 1, CreatedAt = DateTimeOffset.UtcNow, IndexBlob = indexBlob,
                        Stats = new VersionStats(1, 1, 1, 1),
                    },
                },
                Packs = { ["p1"] = new PackInfo { Blob = "packs/p1.7z", OriginalBytes = 1000, Members = [liveHash] } },
            };

            var staging = new StagingArea(
                Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
            var compactor = new DeadWeightCompactor(
                new BlobUploader(factory), new SevenZipCompressor(), new FileHasher(),
                Path.Combine(_temp, "compact"), staging);

            // 保留 50 版 → 唯一那个版本一个都不退役，但仍要求扫孤儿。
            var report = await Cleaner(factory, compactor).CleanupAsync(
                account, name, null, Options(localRoot), info, default, sweepOrphans: true);

            Assert.Equal(1, report.DeletedBlobs);                       // 扫确实做了
            Assert.Equal(0, report.RetiredVersions);                    // 而且没有任何版本退役
            Assert.Empty(await NamesAsync(container, "data/"));
            // 判据落在**只有压实会写**的两项上。不看 DeadBytes：成功压实把它写成 0，与从没跑过
            // 一模一样，这条测试等于分辨不出自己有没有生效。OriginalBytes 则一定从 1000 掉到 1
            //（只剩那个存活成员），成员表也会被整份换掉。
            Assert.Equal(1000, info.Packs["p1"].OriginalBytes);
            Assert.Equal([liveHash], info.Packs["p1"].Members);
            Assert.Empty(info.Packs["p1"].VolumeSizes);
            // 放弃与失败那两支写的是 DeadBytes=999，顺带一并挡住。
            Assert.Equal(0, info.Packs["p1"].DeadBytes);
            // 信息文件也一个字节都没写过：这个容器里从头到尾就没有过信息文件 blob。
            Assert.False((await container.GetBlobClient(BackupDiscovery.IndexBlobName).ExistsAsync()).Value);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
