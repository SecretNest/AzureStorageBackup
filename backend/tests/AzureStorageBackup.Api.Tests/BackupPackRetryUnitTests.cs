using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 闸门重试 pack 时，重试的**单位**必须是一组，不是一整个池。
/// <para>
/// 一件 pack 活是一个池，<c>ProcessPackAsync</c> 按 GroupIsFull 把它切成若干组，每组各领一个包号。
/// 整件重试就等于第 9 组的一次抖动把前 8 组全部推倒重来，而且重来时领的是**新**包号：前 8 组
/// 已经传上去的归档从此没有任何索引引用得到，只在容器里占着地方（保留清理要到下一轮才收），
/// info.Packs 里还各留一条指向孤儿的记录，进度也跟着多销几笔。
/// </para>
/// <para>
/// 这里的池靠"压缩期间成员变了"切成两组——这是编排器自己就有的那条路（变化成员以新 hash 重新
/// 入队，自然进入下一组），不是为测试造的机关：装箱在 diff 那侧按同样的三条界封箱，所以正常
/// 情况下一个池就是一组，多组只可能这么来。
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class BackupPackRetryUnitTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;
    private readonly BackupJournalStore _journals;

    public BackupPackRetryUnitTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-pack-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "src");
        _temp = Path.Combine(baseDir, "temp");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_temp);
        _journals = new BackupJournalStore(Path.Combine(_temp, "journal"));
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { /* best effort */ }
    }

    private static Account AzuriteAccount() => new()
    {
        Id = 42,
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

    private const string TargetLeaf = "m3.bin";

    /// <summary>一个目录、6 个小文件：按三条界这是**一个**池、一组。</summary>
    private void WritePool()
    {
        Directory.CreateDirectory(Path.Combine(_root, "d"));
        for (var i = 1; i <= 6; i++)
            File.WriteAllBytes(Path.Combine(_root, "d", $"m{i}.bin"), new byte[20_000 + i]);
    }

    /// <summary>
    /// 压 6 个成员那一下（也只有那一下）把其中一个成员改掉：编排器的压缩后重校验会把它排除出
    /// 归档、以新 hash 重新入队，于是同一个池被切成两组——**不改动被测代码**就得到多组现场。
    /// 顺带把每个包号被压了几次记下来，用来回答"已经传好的那组有没有被重压"。
    /// </summary>
    private sealed class MutatingCompressor(IFileCompressor inner, string root) : IFileCompressor
    {
        private readonly List<string> _compressed = [];
        private int _mutations;

        /// <summary>按调用顺序记下每次压缩的包号（同一个包号可以出现多次）。</summary>
        public IReadOnlyList<string> Compressed
        {
            get { lock (_compressed) return [.. _compressed]; }
        }

        public Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken ct = default)
        {
            var packId = Path.GetFileNameWithoutExtension(request.OutputArchivePath);
            lock (_compressed) _compressed.Add(packId);

            // 只在"整组一起压"那一下动手：剔除变化成员后的重压只剩 5 个成员、后面那一组只剩 1 个，
            // 都不含目标，于是不会没完没了地"又变了"（那会一路撞到 ProcessingMaxAttempts 降级成单文件）。
            var target = request.Entries.FirstOrDefault(
                e => e.EndsWith(TargetLeaf, StringComparison.Ordinal));
            if (request.Entries.Count > 1 && target is not null)
            {
                // 每次改成**不同的长度**。压缩后重校验的第一道是元数据比对，只改内容不改长度的话，
                // 同一秒内重写会得到相同的 (mtime, length)，比对就说"这个成员没变"——那样整件重试
                // 的旧行为会伪装成正确的（一次抖动之后只剩一组），测试就失去了鉴别力。
                var n = Interlocked.Increment(ref _mutations);
                File.WriteAllBytes(
                    Path.Combine(root, target.Replace('/', Path.DirectorySeparatorChar)),
                    new byte[33_000 + (n * 1_000)]);
            }

            return inner.CompressAsync(request, ct);
        }

        public Task ExtractAsync(string firstVolumePath, string outputDir, string? password, CancellationToken ct = default)
            => inner.ExtractAsync(firstVolumePath, outputDir, password, ct);

        public Task<CompressionResult> CompressStreamAsync(
            StreamCompressionRequest request, Func<Stream, CancellationToken, Task<long>> writeSource,
            CancellationToken ct = default)
            => inner.CompressStreamAsync(request, writeSource, ct);

        public Task<IReadOnlyList<ArchiveEntry>> ListEntriesAsync(
            string firstVolumePath, string? password, CancellationToken ct = default)
            => inner.ListEntriesAsync(firstVolumePath, password, ct);

        public Task<long> ExtractToStreamAsync(
            string firstVolumePath, string? entryName, string? password, Stream destination,
            CancellationToken ct = default)
            => inner.ExtractToStreamAsync(firstVolumePath, entryName, password, destination, ct);
    }

    /// <summary>第 N 个**包**的第一次上传抖一下（只抖一次）。N=2 就是"前面那组已经传好了才出事"。</summary>
    private sealed class FlakyOnNthPack(IBlobUploader inner, int nth) : IBlobUploader
    {
        private readonly HashSet<string> _packs = new(StringComparer.Ordinal);
        private int _thrown;

        private Task<bool> GateAsync(string blobName, Func<Task<bool>> call)
        {
            if (blobName.StartsWith("packs/", StringComparison.Ordinal))
            {
                bool trip;
                lock (_packs)
                {
                    var id = blobName[..(blobName.IndexOf(".7z", StringComparison.Ordinal) + 3)];
                    trip = _packs.Add(id) && _packs.Count == nth;
                }
                if (trip && Interlocked.Exchange(ref _thrown, 1) == 0)
                    throw new AggregateException("Retry failed after 6 tries.", new TaskCanceledException("timeout"));
            }
            return call();
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
            => GateAsync(blobName, () => inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata));

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry, CancellationToken ct, IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
            => GateAsync(blobName, () => inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata, progress));

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
            => GateAsync(blobName, async () =>
            {
                await inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
                return true;
            });
    }

    /// <summary>每一个**包**的第一次上传各抖一下：两次抖动之间必定夹着一次成功。</summary>
    private sealed class FlakyOnEveryPackOnce(IBlobUploader inner) : IBlobUploader
    {
        private readonly HashSet<string> _packs = new(StringComparer.Ordinal);
        private int _thrown;

        public int Thrown => _thrown;

        private Task<bool> GateAsync(string blobName, Func<Task<bool>> call)
        {
            if (blobName.StartsWith("packs/", StringComparison.Ordinal))
            {
                bool trip;
                lock (_packs)
                    trip = _packs.Add(blobName[..(blobName.IndexOf(".7z", StringComparison.Ordinal) + 3)]);
                if (trip)
                {
                    Interlocked.Increment(ref _thrown);
                    throw new AggregateException("Retry failed after 6 tries.", new TaskCanceledException("timeout"));
                }
            }
            return call();
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
            => GateAsync(blobName, () => inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata));

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry, CancellationToken ct, IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
            => GateAsync(blobName, () => inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata, progress));

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
            => GateAsync(blobName, async () =>
            {
                await inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
                return true;
            });
    }

    /// <summary>只记下每次压缩用的包号,不改动内容、不改动行为——用来数"这一组被压了几次"。</summary>
    private sealed class CountingCompressor(IFileCompressor inner) : IFileCompressor
    {
        private readonly List<string> _compressed = [];

        public IReadOnlyList<string> Compressed
        {
            get { lock (_compressed) return [.. _compressed]; }
        }

        public Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken ct = default)
        {
            var packId = Path.GetFileNameWithoutExtension(request.OutputArchivePath);
            lock (_compressed) _compressed.Add(packId);
            return inner.CompressAsync(request, ct);
        }

        public Task ExtractAsync(string firstVolumePath, string outputDir, string? password, CancellationToken ct = default)
            => inner.ExtractAsync(firstVolumePath, outputDir, password, ct);

        public Task<CompressionResult> CompressStreamAsync(
            StreamCompressionRequest request, Func<Stream, CancellationToken, Task<long>> writeSource,
            CancellationToken ct = default)
            => inner.CompressStreamAsync(request, writeSource, ct);

        public Task<IReadOnlyList<ArchiveEntry>> ListEntriesAsync(
            string firstVolumePath, string? password, CancellationToken ct = default)
            => inner.ListEntriesAsync(firstVolumePath, password, ct);

        public Task<long> ExtractToStreamAsync(
            string firstVolumePath, string? entryName, string? password, Stream destination,
            CancellationToken ct = default)
            => inner.ExtractToStreamAsync(firstVolumePath, entryName, password, destination, ct);
    }

    /// <summary>取消令牌一按就抛 OperationCanceledException 的上传器：用来验证取消没有被闸门吞掉。</summary>
    private sealed class CancellingUploader(IBlobUploader inner, CancellationTokenSource cts) : IBlobUploader
    {
        private Task<bool> GateAsync(string blobName, Func<Task<bool>> call)
        {
            if (blobName.StartsWith("packs/", StringComparison.Ordinal))
            {
                cts.Cancel();
                // 形状与"传到一半被取消"一模一样：真实的取消就是从这里抛出来的。
                throw new OperationCanceledException(cts.Token);
            }
            return call();
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
            => GateAsync(blobName, () => inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata));

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry, CancellationToken ct, IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
            => GateAsync(blobName, () => inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata, progress));

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
            => GateAsync(blobName, async () =>
            {
                await inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
                return true;
            });
    }

    private (BackupOrchestrator Orchestrator, BlobClientFactory Factory, IBackupInfoStore Store) Build(
        IBlobUploader uploader, IFileCompressor compressor, VerboseFileLog? verboseLog = null)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var compactor = new DeadWeightCompactor(
            new BlobUploader(factory), new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "compact"),
            staging);
        var authority = new TestLocalAuthority(store);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            compressor, uploader, factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), compactor,
                indexCache: authority.IndexCache, trackedInfo: authority.Tracked),
            new FileHasher(), authority.IndexCache, authority.Tracked,
            verboseLog: verboseLog);
        return (orchestrator, factory, store);
    }

    private BackupRequest Request(Account account, string container) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _root,
        Name = "photos",
        Password = null,
        Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 } },
    };

    /// <summary>容器里的 pack 归档（.7z 基名），与索引真正引用到的那些。两者必须相等。</summary>
    private static async Task<(HashSet<string> InContainer, HashSet<string> Referenced)> PacksAsync(
        Azure.Storage.Blobs.BlobContainerClient cc, IBackupInfoStore store, Account account, string container)
    {
        var inContainer = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var b in cc.GetBlobsAsync(BlobTraits.None, BlobStates.None, "packs/", default))
            inContainer.Add(b.Name[..(b.Name.IndexOf(".7z", StringComparison.Ordinal) + 3)]);

        var info = await store.ReadInfoAsync(account, container, null);
        var index = await store.ReadIndexAsync(account, container, info!.Versions[^1].IndexBlob, null);
        var referenced = index.Entries
            .Where(e => e.Storage is { Kind: "pack" })
            .Select(e => $"packs/{e.Storage!.Ref}.7z")
            .ToHashSet(StringComparer.Ordinal);
        return (inContainer, referenced);
    }

    /// <summary>
    /// 第 2 组上传抖一次：只有第 2 组重来，第 1 组既不重压也不重传，包号不变，容器里不留孤儿。
    /// </summary>
    [SkippableFact]
    public async Task A_blip_in_the_second_group_reruns_only_that_group()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("pack");
        var compressor = new MutatingCompressor(new SevenZipCompressor(), _root);
        var flaky = new FlakyOnNthPack(new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), nth: 2);
        var (orchestrator, factory, store) = Build(flaky, compressor);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WritePool();
            await using var control = new BackupRunControl(_journals, 5, "run-pack", new PauseGate(
                schedule: [TimeSpan.FromMilliseconds(20)], steady: TimeSpan.FromMilliseconds(20),
                patience: TimeSpan.FromSeconds(5)));

            var result = await orchestrator.RunAsync(Request(account, name), null, default, control);
            Assert.Equal(1, result.Version);

            var compressed = compressor.Compressed;
            var packs = compressed.Distinct(StringComparer.Ordinal).ToList();
            // 这个现场的前提：池确实被切成了两组。切不出来的话下面几条断言都是空转。
            Assert.Equal(2, packs.Count);

            // 第 1 组：整组压一次 + 剔掉变化成员后重压一次，就这两次。第 2 组抖完重来时若把整个池
            // 推倒重来，这里会冒出第 3 次（而且是挂在一个**新**包号上）。
            Assert.Equal(2, compressed.Count(p => p == packs[0]));
            // 第 2 组：抖了一次，压了两次——**同一个包号**。号变了就等于在云上多留一份没人引用的归档。
            Assert.Equal(2, compressed.Count(p => p == packs[1]));

            var (inContainer, referenced) = await PacksAsync(cc, store, account, name);
            Assert.Equal(referenced, inContainer);   // 容器里没有索引引用不到的孤儿包
            Assert.Equal(2, referenced.Count);
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 进度销账每组恰好一次，与抖了几次无关。整件重试会让已经销过账的那一组再销一次，
    /// uploaded 就此虚高（越过 total 之后速度与剩余时间一起失真）。
    /// </summary>
    [SkippableFact]
    public async Task Each_group_reports_progress_exactly_once_however_many_retries()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("pack");
        var compressor = new MutatingCompressor(new SevenZipCompressor(), _root);
        var flaky = new FlakyOnNthPack(new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), nth: 2);
        var (orchestrator, factory, _) = Build(flaky, compressor);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WritePool();
            await using var control = new BackupRunControl(_journals, 5, "run-once", new PauseGate(
                schedule: [TimeSpan.FromMilliseconds(20)], steady: TimeSpan.FromMilliseconds(20),
                patience: TimeSpan.FromSeconds(5)));

            var peak = 0;
            var progress = new Progress<BackupProgress>(p => peak = Math.Max(peak, p.UploadedItems));
            await orchestrator.RunAsync(Request(account, name), progress, default, control);

            // 两组 → 恰好两笔。整件重试时第 1 组会被再销一次，这里就成了 3。
            Assert.Equal(2, peak);
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>用户按的取消不是"网络抖了一下"：必须原样上抛，不能被闸门等成挂起。</summary>
    [SkippableFact]
    public async Task User_cancellation_still_propagates_through_the_group_retry()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("pack");
        using var cts = new CancellationTokenSource();
        var uploader = new CancellingUploader(new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), cts);
        var (orchestrator, factory, _) = Build(uploader, new SevenZipCompressor());
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WritePool();
            await using var control = new BackupRunControl(_journals, 5, "run-cancel", new PauseGate(
                schedule: [TimeSpan.FromMilliseconds(20)], steady: TimeSpan.FromMilliseconds(20),
                patience: TimeSpan.FromSeconds(5)));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => orchestrator.RunAsync(Request(account, name), null, cts.Token, control));

            // 光看异常类型不够：真正要守的是"取消根本没进过闸门"。瞬时判据若拿到的不是运行本身
            // 那个令牌，取消就会被当成抖动，在闸门前一等再等，直到耐心耗尽把这轮判成挂起——
            // 那时用户按的是取消，界面上出现的却是"已挂起，稍后自动接着跑"。
            Assert.False(control.Gate.IsDowngraded, "取消被闸门吞成了自动挂起。");
            Assert.Null(control.Gate.Current);
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>没有 control 的运行（定时任务之外的老路径）行为不变：照样两组、照样不留孤儿。</summary>
    [SkippableFact]
    public async Task Runs_without_a_control_behave_exactly_as_before()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("pack");
        var compressor = new MutatingCompressor(new SevenZipCompressor(), _root);
        var (orchestrator, factory, store) = Build(
            new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), compressor);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WritePool();
            var result = await orchestrator.RunAsync(Request(account, name), null, default);

            Assert.Equal(1, result.Version);
            Assert.Equal(2, compressor.Compressed.Distinct(StringComparer.Ordinal).Count());
            var (inContainer, referenced) = await PacksAsync(cc, store, account, name);
            Assert.Equal(referenced, inContainer);
            Assert.Equal(2, referenced.Count);
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 上传已确认之后才撞上的瞬时错误（journal append / oplog 那一步）不再拖着整组重来一遍。
    /// <para>
    /// 用一把独占文件锁常驻卡住当天的 verbose 日志文件：<c>LogFileAsync</c> 一写就撞
    /// <see cref="IOException"/>（真实的共享冲突，<see cref="TransientErrors"/> 判它为瞬时）。锁全程
    /// 不放，逼出"重不重试"的差异——重试的话每次撞锁都会先把整组重新压缩、重新上传一遍，压缩
    /// 次数会跟着撞锁次数一起涨；不重试的话，压缩只可能发生在成功上传的那一次，之后这一步
    /// 自己撞上的错误原样往外抛，压缩次数永远停在 1。
    /// </para>
    /// <para>
    /// 压缩次数正是「上传字节」的账本：<c>state.AddUploaded</c> 与每一次成功的
    /// <c>UploadStagedPackAsync</c> 一一对应，而后者又与每一次压缩一一对应。压缩只跑一次，
    /// 上传字节就只可能记一次——这正是本组要守住的"不双计"。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_failure_after_upload_confirm_does_not_retry_the_group()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("pack");
        var compressor = new CountingCompressor(new SevenZipCompressor());
        var verboseRoot = Path.Combine(_temp, "verbose");
        var verboseLog = new VerboseFileLog(verboseRoot);
        var (orchestrator, factory, _) = Build(
            new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), compressor, verboseLog);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WritePool();   // 一个池、一组（见类注释）

            // 提前把今天这份 verbose 日志文件锁死：独占打开之后，AppendAsync 内部的
            // File.AppendAllTextAsync 一开就撞共享冲突，抛出裸 IOException。锁在 using 里，
            // 直到这个测试方法结束才放开——不给重试留任何"这次就成了"的窗口。
            var logDir = Path.Combine(verboseRoot, name);
            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir, DateTimeOffset.UtcNow.ToString("yyyyMMdd") + ".log");
            File.WriteAllText(logFile, "");
            using var block = new FileStream(logFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var request = Request(account, name) with
            {
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
                    VerboseLogging = true,
                },
            };

            await using var control = new BackupRunControl(_journals, 5, "run-record", new PauseGate(
                schedule: [TimeSpan.FromMilliseconds(20)], steady: TimeSpan.FromMilliseconds(20),
                patience: TimeSpan.FromSeconds(2)));

            // 记账阶段的失败现在原样往外抛：不再经过挂起闸门那套"等一等再来"。
            await Assert.ThrowsAnyAsync<IOException>(
                () => orchestrator.RunAsync(request, null, default, control));

            // 压缩只跑了一次——上传已经确认过的那一组没有被重新压、重新传。大于 1 就说明记账阶段
            // 的失败仍然拖着整组重试，state.AddUploaded 会跟着多算一遍（本组要守的双计 bug）。
            Assert.Single(compressor.Compressed);
            // 记账阶段的失败不该经过闸门：它已经在重试范围之外了，闸门连一次连败都不该记到。
            Assert.False(control.Gate.IsDowngraded, "记账阶段的失败被闸门当成了瞬时抖动去等。");
            Assert.Null(control.Gate.Current);
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 干成一段活就把闸门的连败清零。<c>ReportSuccess</c> 从前没有任何测试守着——把那一行删掉，
    /// 上面几个测试照样全绿。
    /// <para>
    /// 它守的是这件事：闸门的耐心是"从第一次不顺算起还没好过"。中间成功过却不清零的话，一天里
    /// 零星抖几下就会攒够耐心，把一轮从头到尾都在正常传的备份判成自动挂起——而且抖得越久越像
    /// 网络坏了，其实每一次都当场自愈了。
    /// </para>
    /// <para>
    /// 现场用的是**同一个池里的两组**，不是两件并发的活：一个池由一个消费者顺序处理，所以
    /// "第 1 组重试成功 → 清零 → 第 2 组才出事"这个次序由程序顺序保证，不靠等待时间去碰运气。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_success_between_two_blips_resets_the_gates_failure_count()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("pack");
        var compressor = new MutatingCompressor(new SevenZipCompressor(), _root);
        var flaky = new FlakyOnEveryPackOnce(new BlobUploader(new BlobClientFactory(TestSecrets.Reader)));
        var (orchestrator, factory, _) = Build(flaky, compressor);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WritePool();
            // 退避 400ms：挂起现场要在闸门上挂足够久，下面 10ms 一次的取样才看得见它的连败数。
            // 耐心给足，这个测试问的不是"耐心会不会用尽"。
            await using var control = new BackupRunControl(_journals, 5, "run-reset", new PauseGate(
                schedule: [TimeSpan.FromMilliseconds(400)], steady: TimeSpan.FromMilliseconds(400),
                patience: TimeSpan.FromSeconds(30)));

            var run = orchestrator.RunAsync(Request(account, name), null, default, control);
            var peak = 0;
            while (!run.IsCompleted)
            {
                if (control.Gate.Current is { } paused) peak = Math.Max(peak, paused.Failures);
                await Task.Delay(10);
            }
            var result = await run;

            Assert.Equal(1, result.Version);
            Assert.Equal(2, flaky.Thrown);   // 确实抖了两次，中间夹着一次成功
            // 第 2 次抖动开闸时的连败数：清零了就还是 1，没清零就是 2。
            Assert.Equal(1, peak);
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }
}
