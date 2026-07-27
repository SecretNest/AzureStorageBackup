using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 差分与「压缩 + 上传」流水线化之后的验收（第 3 期）。三个承重点：
/// 产出必须与"先 diff 完再上传"时代一致（每个文件的落位与 hash 一一对应，pack 编号可以不同）；
/// 两条流真的在同时跑（diff 还没完就已经在传了）；
/// 一侧出事时另一侧要收得干净——上传失败要把原始异常抛出去，取消要让两条流都停下。
/// </summary>
[Trait("Category", "Integration")]
public sealed class PipelinedBackupTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _base;
    private readonly string _root;
    private readonly string _temp;

    public PipelinedBackupTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "asb-pipe-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_base, "src");
        _temp = Path.Combine(_base, "temp");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
    }

    private static Account AzuriteAccount() => new()
    {
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

    private static bool SevenZip() => SevenZipArchiveCodec.TryResolveExecutable() is not null;
    private static string RandomName(string p) => p + Guid.NewGuid().ToString("N")[..8];

    private void WriteFile(string rel, int size)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        File.WriteAllBytes(full, bytes);
    }

    private BackupOrchestrator Build(
        BlobClientFactory factory, IBackupInfoStore store,
        IFileHasher? hasher = null, IBlobUploader? uploader = null)
    {
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        return new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(hasher ?? new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), uploader ?? new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator()), new FileHasher());
    }

    private BackupRequest Request(Account account, string container, BackupEngineOptions options) => new()
    {
        Account = account, Container = container, LocalRoot = _root, Name = "pipe", Options = options,
    };

    /// <summary>
    /// 装箱结果必须与"等 diff 全部跑完再一次装箱"完全一致。这里不去复刻旧代码，而是拿装箱那个
    /// **纯函数**当基准：同一批变更文件喂给 <see cref="GroupingPlanner.Plan"/>，得到的成员集合
    /// 必须与流水线实际产出的 pack 成员集合逐一对应（编号可以不同，成员分组不行）。
    /// </summary>
    [SkippableFact]
    public async Task Packing_Matches_What_The_Planner_Would_Have_Produced()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("pipep-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // 三类都要有：超阈值的单文件、按目录合并的小文件（跨越封箱上限，会切成多箱）、
            // 以及命中跨目录规则、散落在很多目录里的小文件。
            WriteFile("big.bin", 40_000);
            WriteFile("also/big2.bin", 40_000);
            foreach (var dir in new[] { "docs", "docs/deep", "notes" })
                for (var i = 0; i < 6; i++)
                    WriteFile($"{dir}/f{i}.txt", 3_000);
            for (var i = 0; i < 12; i++)
                WriteFile($"shard/{i:D2}/blob.dat", 2_500);

            var options = new BackupEngineOptions
            {
                CrossDirGroup = new IgnoreRuleSet(["shard/"]),
                Plan = new PlanOptions
                {
                    SingleFileThresholdBytes = 10_000,
                    GroupCapBytes = 9_000, // 故意小：每个目录会被切成多箱
                },
            };
            await Build(factory, store).RunAsync(Request(account, name, options));

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);

            // 基准：把本轮全部条目按索引记录的长度/hash 喂给纯函数装箱。
            var expected = new GroupingPlanner().Plan(
                [.. idx.Entries
                    .OrderBy(e => e.Path, StringComparer.Ordinal)
                    .Select(e => new PlannedFile(e.Path, e.Length, e.FullHash!))],
                options.Plan with { CrossDirGroup = options.CrossDirGroup });

            // 先确认这份数据真的把三条路都走到了，否则下面的相等断言可能只是"两边都只有一箱"。
            Assert.Equal(2, expected.Blobs.Count);
            Assert.True(expected.Packs.Count >= 6, $"expected several packs, got {expected.Packs.Count}");

            Assert.Equal(
                expected.Blobs.Select(b => b.Path).OrderBy(p => p, StringComparer.Ordinal),
                idx.Entries.Where(e => e.Storage!.Kind == "blob").Select(e => e.Path)
                    .OrderBy(p => p, StringComparer.Ordinal));

            // pack 编号可以不同，成员的分组不行：把两边都归一化成"成员路径集合的集合"再比。
            static IEnumerable<string> Signature(IEnumerable<IEnumerable<string>> packs) =>
                packs.Select(m => string.Join('\n', m.OrderBy(p => p, StringComparer.Ordinal)))
                    .OrderBy(s => s, StringComparer.Ordinal);

            var actualPacks = idx.Entries.Where(e => e.Storage!.Kind == "pack")
                .GroupBy(e => e.Storage!.Ref, StringComparer.Ordinal)
                .Select(g => g.Select(e => e.Path));
            Assert.Equal(
                Signature(expected.Packs.Select(p => p.Members.Select(m => m.Path))),
                Signature(actualPacks));

            foreach (var pack in info.Packs)
                Assert.True(await container.GetBlobClient(pack.Value.Blob).ExistsAsync());
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>diff 每读完一个文件就慢一下，并数一数已经判完几个。
    /// 首次备份里差分对每个文件恰好调一次 <c>FullHashAsync</c>，所以这个计数就是"判到第几个了"。</summary>
    private sealed class SlowHasher(IFileHasher inner, int delayMs) : IFileHasher
    {
        private int _hashed;
        public int Hashed => Volatile.Read(ref _hashed);

        public Task<string> HeadHashAsync(string path, int headBytes, CancellationToken ct = default) =>
            inner.HeadHashAsync(path, headBytes, ct);

        public Task<string> TailHashAsync(string path, int tailBytes, CancellationToken ct = default) =>
            inner.TailHashAsync(path, tailBytes, ct);

        public async Task<string> FullHashAsync(string path, CancellationToken ct = default)
        {
            var hash = await inner.FullHashAsync(path, ct);
            await Task.Delay(delayMs, ct);
            Interlocked.Increment(ref _hashed);
            return hash;
        }
    }

    /// <summary>记下每次上传发生时"diff 是否还在跑"。</summary>
    private sealed class OverlapWatchingUploader(IBlobUploader inner, Func<bool> diffRunning) : IBlobUploader
    {
        public int UploadsWhileDiffing { get; private set; }

        public async Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            if (diffRunning())
                UploadsWhileDiffing++;
            return await inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
    }

    /// <summary>这一期的全部意义所在：diff 还在读盘的时候，网络就已经在传了。
    /// 从前 Plan 是一道全局屏障，首次备份要等每个文件都哈希完才发出第一个字节。</summary>
    [SkippableFact]
    public async Task Uploading_Starts_While_The_Diff_Is_Still_Running()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("pipeo-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // 全是超阈值的单文件：判定一出来就该立刻上传，不必等任何组。
            for (var i = 0; i < 8; i++)
                WriteFile($"f{i:D2}.bin", 20_000);

            const int files = 8;
            var hasher = new SlowHasher(new FileHasher(), delayMs: 120);
            var uploader = new OverlapWatchingUploader(new BlobUploader(factory), () => hasher.Hashed < files);
            var orchestrator = Build(factory, store, hasher, uploader);

            await orchestrator.RunAsync(Request(account, name, new BackupEngineOptions
            {
                Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
            }));

            Assert.True(uploader.UploadsWhileDiffing > 0,
                "no upload happened while the diff was still running — the pipeline is still serialised");

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);
            Assert.Equal(8, idx.Entries.Count);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>关掉重叠：回到"先全部判完再传"。给的是一条退路——机械盘的 NAS 上两股读
    /// 互相拖慢时，用户在界面上就能把它关掉，不需要任何诊断。产出必须与开着时一模一样。</summary>
    [SkippableFact]
    public async Task Overlap_Can_Be_Turned_Off_Without_Changing_The_Result()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("pipes-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteFile("big.bin", 40_000);
            for (var i = 0; i < 5; i++)
                WriteFile($"docs/f{i}.txt", 3_000);

            const int files = 6;
            var hasher = new SlowHasher(new FileHasher(), delayMs: 60);
            var uploader = new OverlapWatchingUploader(new BlobUploader(factory), () => hasher.Hashed < files);
            var orchestrator = Build(factory, store, hasher, uploader);

            await orchestrator.RunAsync(Request(account, name, new BackupEngineOptions
            {
                OverlapDiffAndUpload = false,
                Plan = new PlanOptions { SingleFileThresholdBytes = 10_000 },
            }));

            Assert.Equal(0, uploader.UploadsWhileDiffing); // 一个字节都没有在 diff 期间传出去

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);
            Assert.Equal(6, idx.Entries.Count);
            Assert.Equal("blob", idx.Entries.Single(e => e.Path == "big.bin").Storage!.Kind);
            Assert.All(idx.Entries.Where(e => e.Path.StartsWith("docs/", StringComparison.Ordinal)),
                e => Assert.Equal("pack", e.Storage!.Kind));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    private sealed class AlwaysFailingUploader : IBlobUploader
    {
        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null) =>
            throw new InvalidOperationException("upload refused by the test");

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null) =>
            throw new InvalidOperationException("upload refused by the test");
    }

    /// <summary>上传在 diff 还没跑完时就失败：这是流水线化才有的状态组合。
    /// 必须把**上传那边的原始异常**抛出去（而不是 diff 被叫停后看到的那个取消），
    /// 而且不能留下一个新版本——否则一次什么都没传成功的备份会被记成一次成功。</summary>
    [SkippableFact]
    public async Task An_Upload_Failure_While_Diffing_Surfaces_The_Real_Error()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("pipef-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            for (var i = 0; i < 12; i++)
                WriteFile($"f{i:D2}.bin", 20_000);

            var hasher = new SlowHasher(new FileHasher(), delayMs: 80);
            var orchestrator = Build(factory, store, hasher, new AlwaysFailingUploader());

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                orchestrator.RunAsync(Request(account, name, new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
                })));

            Assert.Contains("upload refused by the test", ex.Message);
            Assert.Null(await store.ReadInfoAsync(account, name, null)); // 没有留下版本
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>用户按下停止：两条流都要收尾，RunAsync 才返回。它一返回，调用方就会释放忙碌锁——
    /// 早一步返回等于把一堆压缩/上传丢在锁外面继续跑。</summary>
    [SkippableFact]
    public async Task Canceling_Mid_Run_Stops_Both_Streams()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("pipec-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            for (var i = 0; i < 40; i++)
                WriteFile($"f{i:D2}.bin", 20_000);

            var orchestrator = Build(factory, store, new SlowHasher(new FileHasher(), delayMs: 100));
            using var cts = new CancellationTokenSource();
            var run = orchestrator.RunAsync(Request(account, name, new BackupEngineOptions
            {
                Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
            }), progress: null, cts.Token);

            await Task.Delay(400);
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.True(run.IsCompleted); // 返回即已收尾，不留后台余波
            Assert.Null(await store.ReadInfoAsync(account, name, null));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>进度：两条流同时在跑的时候，明细必须同时给出两条——只报一条，界面上另一行就死住了。</summary>
    [SkippableFact]
    public async Task Progress_Carries_Both_Stages_While_They_Overlap()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("pipeg-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            for (var i = 0; i < 10; i++)
                WriteFile($"f{i:D2}.bin", 20_000);

            var reports = new List<BackupProgress>();
            var progress = new CollectingProgress(reports);
            await Build(factory, store, new SlowHasher(new FileHasher(), delayMs: 100))
                .RunAsync(Request(account, name, new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
                }), progress);

            lock (reports)
            {
                Assert.Contains(reports, r =>
                    r.Details.Count == 2
                    && r.Details.Any(d => d.Stage == "Diffing")
                    && r.Details.Any(d => d.Stage == "Uploading"));
                // 单值字段仍然可用（只看一条的调用方不必先判断有没有第二条）。
                Assert.Contains(reports, r => r.Detail is not null);
            }
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    private sealed class CollectingProgress(List<BackupProgress> sink) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) { lock (sink) sink.Add(value); }
    }
}
