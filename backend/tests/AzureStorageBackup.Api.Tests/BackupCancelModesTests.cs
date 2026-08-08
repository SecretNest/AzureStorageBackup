using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

[Trait("Category", "Integration")]
public sealed class BackupCancelModesTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;
    private readonly BackupJournalStore _journals;

    public BackupCancelModesTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-stop-" + Guid.NewGuid().ToString("N"));
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
        Id = 43,
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

    private void WriteBytes(string rel, int size)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[size]);
    }

    /// <summary>写不可压缩的内容（定种子，可复现）。全零的文件 7z 能压到几 KB，
    /// 设了 VolumeBytes 也切不出几卷来——要多卷就必须让压缩压不动。</summary>
    private void WriteIncompressible(string rel, int size, int seed)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        File.WriteAllBytes(full, bytes);
    }

    /// <summary>把 AppendAsync 收进列表的 IOperationLog 替身（durable 一并记下：取消绝不能长存）。
    /// 项目里已有的几份都是各自文件私有的嵌套类，这里照同一形状再写一份。</summary>
    private sealed class RecordingOperationLog : IOperationLog
    {
        public List<(OperationLogLevel Level, string Source, string Message, bool? Durable)> Entries { get; } = [];

        public Task AppendAsync(
            OperationLogLevel level, string source, string message, CancellationToken ct = default,
            bool? durable = null)
        {
            lock (Entries) Entries.Add((level, source, message, durable));
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

    private sealed class RecordingNotifier : INotifier
    {
        public List<(NotificationEvents Event, string Title, string Body)> Sent { get; } = [];

        public Task NotifyAsync(NotificationEvents evt, string title, string body, CancellationToken ct = default)
        {
            lock (Sent) Sent.Add((evt, title, body));
            return Task.CompletedTask;
        }
    }

    /// <param name="reuse">跨两次 Build 共用同一份本地权威（信息文件 + 索引缓存）。
    /// "先跑成功一轮、再在第二轮读版本索引时叫停"这种用例非它不可：每次新建一份的话，
    /// 第二轮根本看不到第一轮写下的版本，也就没有索引可读。</param>
    /// <param name="wrapIndexCache">只包**编排器**手上那一份索引缓存；保留清理仍拿真的那份。</param>
    /// <param name="compactUploader">死重压实自己那份 uploader（与主备份上传用的那份彻底分开）。
    /// 默认造一个真的；钉"清理尾巴上的停止意愿"那条用例要塞一个人为拖延的替身进来，
    /// 借它的耗时把"压实真的被叫起来了"和"压实被跳过了"这两种结局分得开。</param>
    private (BackupOrchestrator Orchestrator, BlobClientFactory Factory, TestLocalAuthority Authority) Build(
        IBlobUploader uploader, IOperationLog? opLog = null, INotifier? notifier = null,
        TestLocalAuthority? reuse = null, Func<ILocalIndexCache, ILocalIndexCache>? wrapIndexCache = null,
        IBlobUploader? compactUploader = null)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var compactor = new DeadWeightCompactor(
            compactUploader ?? new BlobUploader(factory), new SevenZipCompressor(), new FileHasher(),
            Path.Combine(_temp, "compact"), staging);
        var authority = reuse ?? new TestLocalAuthority(store);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), uploader, factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), compactor,
                indexCache: authority.IndexCache, trackedInfo: authority.Tracked),
            new FileHasher(), wrapIndexCache?.Invoke(authority.IndexCache) ?? authority.IndexCache, authority.Tracked,
            notifier: notifier, opLog: opLog);
        return (orchestrator, factory, authority);
    }

    private BackupRequest Request(Account account, string container, long? volumeBytes = null) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _root,
        Name = "photos",
        Password = null,
        Options = new BackupEngineOptions
        {
            // 上传额度 1＝任一时刻只有一卷在传，第几次上传因此是确定的，
            // "第 2 次上传时叫停"这句话才说得准。
            UploadConcurrency = 1,
            Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
            VolumeBytes = volumeBytes,
        },
    };

    /// <summary>第 N 次上传时按指定停法叫停；<paramref name="thenThrow"/> 用来模拟"在途被打断"。
    /// <para>
    /// 真实 <see cref="IBlobUploader"/> 的参数顺序是 (tier, retry, ct, metadata[, progress])，
    /// 两个 UploadIfMissing 重载都要接：带 progress 的那个在接口上**有默认实现**，
    /// 而备份主路径（VolumeUploadScope 在场时）走的恰恰是它——只接不带 progress 的那个，
    /// 这个替身就一次都拦不到备份的上传。
    /// </para></summary>
    private sealed class StopAt(IBlobUploader inner, int at, Action stop, bool thenThrow) : IBlobUploader
    {
        private int _count;

        /// <summary>实际打到 uploader 上的上传次数。停法生效之后一次都不该再有。</summary>
        public int Calls => Volatile.Read(ref _count);

        private async Task<T> RunAsync<T>(Func<Task<T>> call)
        {
            var n = Interlocked.Increment(ref _count);
            var result = await call();
            if (n == at)
            {
                stop();
                if (thenThrow)
                    throw new OperationCanceledException("aborted mid-flight");
            }
            return result;
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => RunAsync(() => inner.UploadIfMissingAsync(
                account, container, blobName, filePath, tier, retry, ct, metadata));

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry, CancellationToken ct,
            IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
            => RunAsync(() => inner.UploadIfMissingAsync(
                account, container, blobName, filePath, tier, retry, ct, metadata, progress));

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => RunAsync<bool>(async () =>
            {
                await inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
                return true;
            });
    }

    private static async Task<List<string>> DataBlobsAsync(Azure.Storage.Blobs.BlobContainerClient container)
    {
        var names = new List<string>();
        await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, "data/", default))
            names.Add(b.Name);
        return names;
    }

    // 下面四条不碰 Azurite：两个令牌的分工是整套停止语义的地基，必须钉死。
    // 集成测试证明不了"Suspend 没有去碰 AbortToken"——它只看得见落盘结果，
    // 而落盘那一段本来就走 CancellationToken.None，把 AbortToken 一起点着也照样绿。
    private BackupRunControl NewControl() => new(_journals, 8, "run-" + Guid.NewGuid().ToString("N")[..8]);

    [Theory]
    [InlineData(StopKind.Suspend)]
    [InlineData(StopKind.FinishCurrentFiles)]
    public async Task Suspend_and_finish_current_files_stop_the_diff_but_never_abort(StopKind kind)
    {
        await using var c = NewControl();
        Assert.Equal(StopKind.None, c.Stop);

        c.RequestStop(kind);

        Assert.Equal(kind, c.Stop);
        Assert.True(c.StopToken.IsCancellationRequested);      // diff 该停
        Assert.False(c.AbortToken.IsCancellationRequested);    // 在途上传绝不能被打断
    }

    [Fact]
    public async Task Stop_now_fires_both_tokens()
    {
        await using var c = NewControl();

        c.RequestStop(StopKind.StopNow);

        Assert.Equal(StopKind.StopNow, c.Stop);
        Assert.True(c.StopToken.IsCancellationRequested);
        Assert.True(c.AbortToken.IsCancellationRequested);
    }

    [Fact]
    public async Task None_is_a_no_op()
    {
        await using var c = NewControl();

        c.RequestStop(StopKind.None);

        Assert.Equal(StopKind.None, c.Stop);
        Assert.False(c.StopToken.IsCancellationRequested);
        Assert.False(c.AbortToken.IsCancellationRequested);
    }

    /// <summary>用户点了 Suspend、发现卡在一个巨大的多卷文件后面动不了，改点 Stop now：
    /// 这次升级必须真的生效，否则 API 会为一个从没应用过的停法返回成功。</summary>
    [Fact]
    public async Task A_stronger_stop_kind_escalates_and_fires_abort()
    {
        await using var c = NewControl();

        c.RequestStop(StopKind.Suspend);
        Assert.False(c.AbortToken.IsCancellationRequested);

        c.RequestStop(StopKind.StopNow);

        Assert.Equal(StopKind.StopNow, c.Stop);
        Assert.True(c.StopToken.IsCancellationRequested);
        Assert.True(c.AbortToken.IsCancellationRequested);   // 升级的全部意义就在这一句
    }

    [Fact]
    public async Task Finish_current_files_escalates_over_suspend()
    {
        await using var c = NewControl();

        c.RequestStop(StopKind.Suspend);
        c.RequestStop(StopKind.FinishCurrentFiles);

        Assert.Equal(StopKind.FinishCurrentFiles, c.Stop);
        Assert.False(c.AbortToken.IsCancellationRequested);   // 它仍然不许打断在途上传
    }

    /// <summary>反方向不成立：已经打断、已经删掉的残留卷不会因为一次更温和的下达而复活。</summary>
    [Theory]
    [InlineData(StopKind.Suspend)]
    [InlineData(StopKind.FinishCurrentFiles)]
    public async Task A_weaker_stop_kind_after_stop_now_is_ignored(StopKind weaker)
    {
        await using var c = NewControl();

        c.RequestStop(StopKind.StopNow);
        c.RequestStop(weaker);

        Assert.Equal(StopKind.StopNow, c.Stop);
    }

    [Fact]
    public async Task The_same_stop_kind_twice_is_a_no_op()
    {
        await using var c = NewControl();

        c.RequestStop(StopKind.Suspend);
        c.RequestStop(StopKind.Suspend);

        Assert.Equal(StopKind.Suspend, c.Stop);
        Assert.False(c.AbortToken.IsCancellationRequested);
    }

    /// <summary>并发升级不许把 <c>_abort.Cancel()</c> 丢掉：点火归赢下 CAS 的那个线程，
    /// 只要 Stop now 赢了一次，AbortToken 就一定被点着。</summary>
    [Fact]
    public async Task Concurrent_escalation_never_loses_the_abort()
    {
        for (var round = 0; round < 200; round++)
        {
            await using var c = NewControl();
            using var start = new ManualResetEventSlim(false);
            var racers = new[] { StopKind.Suspend, StopKind.StopNow, StopKind.FinishCurrentFiles }
                .Select(k => Task.Run(() => { start.Wait(); c.RequestStop(k); }))
                .ToArray();

            start.Set();
            await Task.WhenAll(racers);

            Assert.Equal(StopKind.StopNow, c.Stop);
            Assert.True(c.AbortToken.IsCancellationRequested);
        }
    }

    [SkippableFact]
    public async Task Suspend_keeps_the_journal_and_ends_as_suspended()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("stop");
        BackupRunControl? control = null;
        var uploader = new StopAt(
            new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), at: 1,
            stop: () => control!.RequestStop(StopKind.Suspend), thenThrow: false);
        var (orchestrator, factory, _) = Build(uploader);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteBytes("big1.bin", 6_000_000);
            WriteBytes("big2.bin", 6_000_001);
            WriteBytes("big3.bin", 6_000_002);
            await using var c = new BackupRunControl(_journals, 8, "run-suspend");
            control = c;

            var ex = await Assert.ThrowsAsync<BackupSuspendedException>(
                () => orchestrator.RunAsync(Request(account, name), null, default, c));
            Assert.Equal(SuspendReason.UserRequested, ex.Reason);

            // 第一件活是做完了的：journal 留着它，云上也留着它。
            var journal = Assert.Single(await _journals.ListAsync(account.Id, name, default));
            Assert.NotEmpty(journal.Content.Records);
            Assert.NotEmpty(await DataBlobsAsync(container));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>在第一件活的第 2 卷上挂住，等测试放行；放行之后**先问一次递进来的令牌**再真传。
    /// <para>
    /// 那一问就是这条用例的全部力气所在：Suspend / Finish current files 之后，递到上传这一层的
    /// 令牌必须仍然干净。编排器要是把消费者的 working 令牌接到 StopToken 上（而不是 AbortToken），
    /// 这里当场抛取消，第 2 卷之后一卷都传不成——"做完当前这件，含它的全部分卷"就成了空话。
    /// </para></summary>
    private sealed class BlockOnSecondVolume(IBlobUploader inner) : IBlobUploader
    {
        private readonly Lock _lock = new();
        private readonly List<string> _uploaded = [];
        private string? _first;

        /// <summary>第 2 卷已经进来、正挂着。</summary>
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>测试下达停止之后放行。</summary>
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>真正传上去的 blob 名（按完成顺序）。</summary>
        public IReadOnlyList<string> Uploaded
        {
            get { lock (_lock) return [.. _uploaded]; }
        }

        /// <summary>去掉 .001 这种卷号后缀，得到这一族的基名。</summary>
        private static string BaseOf(string name)
        {
            var dot = name.LastIndexOf('.');
            return dot > 0 && name.Length - dot == 4 && name.AsSpan(dot + 1).ToString().All(char.IsAsciiDigit)
                ? name[..dot]
                : name;
        }

        private async Task<T> RunAsync<T>(string blobName, CancellationToken ct, Func<Task<T>> call)
        {
            bool hold;
            lock (_lock)
            {
                _first ??= BaseOf(blobName);
                hold = BaseOf(blobName) == _first && blobName.EndsWith(".002", StringComparison.Ordinal);
            }
            if (hold)
            {
                Blocked.TrySetResult();
                await Release.Task;
            }
            ct.ThrowIfCancellationRequested();
            var result = await call();
            lock (_lock) _uploaded.Add(blobName);
            return result;
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => RunAsync(blobName, ct, () => inner.UploadIfMissingAsync(
                account, container, blobName, filePath, tier, retry, ct, metadata));

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry, CancellationToken ct,
            IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
            => RunAsync(blobName, ct, () => inner.UploadIfMissingAsync(
                account, container, blobName, filePath, tier, retry, ct, metadata, progress));

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => RunAsync<bool>(blobName, ct, async () =>
            {
                await inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
                return true;
            });
    }

    /// <summary>
    /// "做完当前这件，含它的全部分卷，再停"——这条承诺整个落在编排器**怎么用**那两个令牌上：
    /// 消费者的 working 令牌必须接 AbortToken，绝不能接 StopToken。
    /// <para>
    /// 上面那几条单元用例钉的是 BackupRunControl 自己，把这两个令牌在编排器里混成一个，
    /// 它们一条都不会红。这条从外面钉：一个切成好几卷的大文件传到第 2 卷时按下停止，
    /// 放行之后剩下的卷必须一卷不少地传完，而且这件活要落进 journal——落了 journal 才算"做完"，
    /// 下一轮才复用得上。
    /// </para>
    /// </summary>
    [SkippableTheory]
    [InlineData(StopKind.Suspend)]
    [InlineData(StopKind.FinishCurrentFiles)]
    public async Task A_gentle_stop_finishes_every_volume_of_the_item_in_flight(StopKind kind)
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("stop");
        var uploader = new BlockOnSecondVolume(new BlobUploader(new BlobClientFactory(TestSecrets.Reader)));
        var (orchestrator, factory, _) = Build(uploader);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            // 6 MB 不可压缩 + 1 MB 一卷 = 七卷上下；单文件阈值 5 MB 以下，走的是单文件 blob 那条路。
            WriteIncompressible("big.bin", 6_000_000, seed: 20260808);
            await using var c = new BackupRunControl(_journals, 8, "run-gentle-" + (int)kind);
            var run = orchestrator.RunAsync(Request(account, name, volumeBytes: 1_000_000), null, default, c);
            try
            {
                await uploader.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(30));
                c.RequestStop(kind);                 // 第 2 卷正挂在半空中
                uploader.Release.TrySetResult();

                await RunAndCatchAsync(run.WaitAsync(TimeSpan.FromSeconds(30)), kind);

                // 这件活整个做完了：journal 里有它，卷数与它自报的一致。
                var journal = Assert.Single(await _journals.ListAsync(account.Id, name, default));
                var record = Assert.Single(journal.Content.Records);
                Assert.True(record.Volumes >= 3, $"expected a multi-volume item, got {record.Volumes}");

                // 云上一卷不少——这才是"含它的全部分卷"那半句话的实际含义。
                var expected = VolumeBlobIO.VolumeNames(record.Ref, record.Volumes).Order(StringComparer.Ordinal);
                Assert.Equal(expected, (await DataBlobsAsync(container)).Order(StringComparer.Ordinal));
                Assert.Equal(record.Volumes, uploader.Uploaded.Count);
            }
            finally
            {
                // Blocked 信号没等到（正是回归会长成的样子）时，run 还在后台挂在 Release 上：
                // 这里放行、等它收尾，不然它会带着一个没人观察的异常在 Dispose() 删掉临时目录之后
                // 继续跑，白烧 120s 才让用例变红。TrySetResult 幂等，正常路径下重复调用无副作用。
                uploader.Release.TrySetResult();
                await run.ContinueWith(_ => { });
            }
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>读版本索引时挂住不返回，直到**递进来的那个令牌**被取消。
    /// 50 万条目的索引真读起来要好几秒（本仓库实测过），几十个版本累起来就是几分钟；
    /// 令牌若没接进这一步（用的还是运行自己的 ct），这个替身会一直挂着，用例超时红掉。</summary>
    private sealed class BlockingIndexCache(ILocalIndexCache inner) : ILocalIndexCache
    {
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<VersionIndex> ReadAsync(
            Account account, string container, int version, long identityTicks,
            string indexBlob, string? password, CancellationToken ct = default)
        {
            Reading.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);   // 只有令牌救得了它
            return await inner.ReadAsync(account, container, version, identityTicks, indexBlob, password, ct);
        }

        public Task PutAsync(int accountId, string container, int version, long identityTicks, VersionIndex index,
            CancellationToken ct = default) => inner.PutAsync(accountId, container, version, identityTicks, index, ct);

        public Task RemoveAsync(int accountId, string container, int version, CancellationToken ct = default)
            => inner.RemoveAsync(accountId, container, version, ct);

        public Task RemoveForContainerAsync(int accountId, string container, CancellationToken ct = default)
            => inner.RemoveForContainerAsync(accountId, container, ct);
    }

    private static async Task<Exception> RunAndCatchAsync(Task run, StopKind kind) =>
        kind == StopKind.Suspend
            ? await Assert.ThrowsAsync<BackupSuspendedException>(() => run)
            : await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

    /// <summary>死重压实重传那一步人为拖住不放，直到给定的令牌被取消（或拖延期满）。
    /// <para>
    /// 只在 <c>UploadOverwriteAsync</c> 上拖——<see cref="VolumeBlobIO.ReplaceAsync"/> 用的正是它，
    /// 而这正是压实"传新卷"落地的那一刻。<c>UploadIfMissingAsync</c> 原样放过：压实不走那条路，
    /// 拖它只会拖累无关的东西。
    /// </para>
    /// <param name="onFirstCall">第一次真的落到这一步时同步触发一次，跑在调用方（压实自己）的
    /// 线程上。钉"压实进行中途"那条用例专用：从这里面下停止意愿，"压实已经在跑"就是强制出来的
    /// 时序，不是指望进度回调赶巧排在它前面。</param>
    /// </summary>
    private sealed class DelayedOverwriteUploader(IBlobUploader inner, TimeSpan delay, Action? onFirstCall = null)
        : IBlobUploader
    {
        private int _overwriteCalls;

        /// <summary>真正落到重传这一步的次数——用来确认压实到底有没有被叫起来过。</summary>
        public int OverwriteCalls => Volatile.Read(ref _overwriteCalls);

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata);

        public async Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            if (Interlocked.Increment(ref _overwriteCalls) == 1)
                onFirstCall?.Invoke();
            // 观察传进来的 ct：修复前这里收到的是运行自己的 ct（永不因用户停止而触发），
            // 拖延期满才会往下走；修复后收到的是 stopProducing.Token，用户一按停止这里就被打断。
            await Task.Delay(delay, ct);
            await inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }
    }

    /// <summary>某个阶段一进入就同步触发回调。用直接实现 <see cref="IProgress{T}"/>而不是用便利类
    /// <c>Progress&lt;T&gt;</c>：后者靠 SynchronizationContext.Post/线程池转发，在测试里没有同步上下文时
    /// 会排到线程池异步执行——回调这时才跑，编排器早就已经往下走了，"停止意愿必须先于清理决策落定"
    /// 这件事就钉不住。直接实现这个接口，Report 就在编排器自己的线程上同步跑完。</summary>
    private sealed class StopOnStage(BackupStage stage, Action stop) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value)
        {
            if (value.Stage == stage)
                stop();
        }
    }

    /// <summary>造一份"v1 提交后删掉一个成员、v2 一提交 v1 就该退役"的现场：两条清理尾巴用例共用。
    /// 三个小文件同目录，按目录装箱进同一个 pack，内容各不相同（不同种子）——全零内容会被本地
    /// 去重收敛成同一份物理成员，那样删一个不会产生任何死重。返回时 a 已经删掉、request 已经把
    /// MaxVersions 摁到 1（v2 一提交，v1 立刻该退役），调用方接着跑 v2 即可让死重压实被叫起来。</summary>
    private async Task<BackupRequest> SeedPackDueForCompactionAsync(BackupOrchestrator orchestrator, Account account, string name)
    {
        WriteIncompressible("pack/a.bin", 2000, seed: 1);
        WriteIncompressible("pack/b.bin", 2000, seed: 2);
        WriteIncompressible("pack/c.bin", 2000, seed: 3);
        var baseRequest = Request(account, name);
        var request = baseRequest with
        {
            Options = baseRequest.Options with
            {
                Retention = new RetentionPolicy { Mode = RetentionMode.VersionOnly, MaxVersions = 1 },
            },
        };
        Assert.Equal(1, (await orchestrator.RunAsync(request, null, default, null)).Version);

        // v2：删掉 a——b、c 没变，仍沿用 v1 那个 pack；那个 pack 里 a 那份变成死重
        // （2000 / 6000 ≈ 33% > 30% 阈值），触发原地重压。
        File.Delete(Path.Combine(_root, "pack", "a.bin"));
        return request;
    }

    /// <summary>清理尾巴上的停止意愿，Task 9 交接时漏掉的那一段：版本索引与信息文件都已提交，
    /// 编排器随即径直调 <c>cleaner.CleanupAsync</c>，两个令牌都不接进去——死重压实该下载、
    /// 重压、重传照样跑到底，而 CancelAsync/SuspendAsync 是等到终态才返回的，用户按下按钮的
    /// 那个 HTTP 请求就跟着一起挂那么久。
    /// <para>
    /// 这条钉的是**跳过**那半——停止意愿在进清理之前就已经落定（<c>BackupOrchestrator.cs:1028</c>），
    /// <c>cleaner.CleanupAsync</c> 整个没被叫起来。压实进行中途才被打断的那半在
    /// <see cref="A_stop_requested_mid_compaction_lets_the_run_finish_promptly"/>：那条才用得上
    /// 会拖住重传的替身，这条用不上——拖也拖不到从未被叫起来的调用上，硬塞一个只会让人误以为
    /// 这里也测了压实进行中途，所以这里就用一份不拖延的替身，靠 <c>OverwriteCalls</c> 直接钉
    /// "压实一次都没被叫起来"。
    /// </para></summary>
    [SkippableTheory]
    [InlineData(StopKind.Suspend)]
    [InlineData(StopKind.FinishCurrentFiles)]
    [InlineData(StopKind.StopNow)]
    public async Task A_stop_pending_before_cleanup_skips_it_and_finishes_promptly(StopKind kind)
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("stop");
        var real = new BlobUploader(new BlobClientFactory(TestSecrets.Reader));
        // 没有拖延——压实压根不该被叫起来，OverwriteCalls 留着只为验证这一点。
        var compact = new DelayedOverwriteUploader(real, TimeSpan.Zero);
        var (orchestrator, factory, _) = Build(real, compactUploader: compact);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            var request = await SeedPackDueForCompactionAsync(orchestrator, account, name);

            await using var c = new BackupRunControl(_journals, 8, "run-cleanup-skip-" + (int)kind);
            // 在 CleaningUp 这一帧同步下停止意愿：跑在编排器自己的线程上，严格早于它判断
            // "要不要调 CleanupAsync"的那一步，不留时间窗口。
            var progress = new StopOnStage(BackupStage.CleaningUp, () => c.RequestStop(kind));

            var run = orchestrator.RunAsync(request, progress, default, c);
            try
            {
                var result = await run.WaitAsync(TimeSpan.FromSeconds(3));

                // 版本已经提交：这仍然是一次成功的备份，不是 Suspended/Canceled——
                // 清理只是顺手维护，跳过它不改变"这一轮备份成功"这个事实。
                Assert.Equal(2, result.Version);
                // 压实一次都没被叫起来——这才是"跳过"这个词的实际含义。
                Assert.Equal(0, compact.OverwriteCalls);
            }
            finally
            {
                await run.ContinueWith(_ => { });
            }
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>清理尾巴的另一半，也是危险的那一半：死重压实已经真被叫起来、正在下载/重压/重传
    /// 半路上，用户这时候才按下停止。<c>BackupOrchestrator.cs:1050</c> 那个
    /// <c>catch (OperationCanceledException) when (...)</c> 接的正是这一刻的取消——接漏了，
    /// 一次已经提交的成功备份会被倒扣成 Suspended/Canceled；接错了 guard，会连宿主真关停的
    /// 取消也一并吞掉（见 <see cref="A_genuine_cancellation_mid_compaction_still_propagates_as_a_failure"/>）。
    /// <para>
    /// 停止意愿从 <see cref="DelayedOverwriteUploader"/> 的 <c>onFirstCall</c> 钩子里下达——那正是
    /// <c>UploadOverwriteAsync</c>（<see cref="VolumeBlobIO.ReplaceAsync"/> 落地重传新卷的那一步）
    /// 真被叫到的时刻，早已经在 <c>cleaner.CleanupAsync</c> 内部：这样"压实已经在跑"是强制出来的
    /// 时序，不是指望某个 progress 回调赶巧排在它前面。8 秒的人为拖延摆在那儿：真正被打断的收尾
    /// 用不了这么久，3 秒的收尾期限只有它跨得过去。
    /// </para></summary>
    [SkippableTheory]
    [InlineData(StopKind.Suspend)]
    [InlineData(StopKind.FinishCurrentFiles)]
    [InlineData(StopKind.StopNow)]
    public async Task A_stop_requested_mid_compaction_lets_the_run_finish_promptly(StopKind kind)
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("stop");
        var real = new BlobUploader(new BlobClientFactory(TestSecrets.Reader));
        BackupRunControl? control = null;
        var slowCompact = new DelayedOverwriteUploader(
            real, TimeSpan.FromSeconds(8), onFirstCall: () => control!.RequestStop(kind));
        var (orchestrator, factory, _) = Build(real, compactUploader: slowCompact);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            var request = await SeedPackDueForCompactionAsync(orchestrator, account, name);

            await using var c = new BackupRunControl(_journals, 8, "run-cleanup-mid-" + (int)kind);
            control = c;

            var run = orchestrator.RunAsync(request, null, default, c);
            try
            {
                // 8 秒的人为拖延摆在那儿：真正被打断的收尾用不了这么久。
                var result = await run.WaitAsync(TimeSpan.FromSeconds(3));

                // 版本已经提交：压实被半路打断不改变"这一轮备份成功"这个事实，不是
                // Suspended/Canceled——被打断的这份清理留给下一轮 cleaner 补上。
                Assert.Equal(2, result.Version);
                // 压实真的被叫起来过一次——不是像跳过路径那样一次都没碰到 UploadOverwriteAsync。
                Assert.Equal(1, slowCompact.OverwriteCalls);
            }
            finally
            {
                await run.ContinueWith(_ => { });
            }
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>压实进行中途取消的却是**调用方自己的令牌**（宿主关停那一路），不是用户点的停止
    /// 按钮：<c>BackupOrchestrator.cs:1050</c> 那个 guard 里 <c>!ct.IsCancellationRequested</c>
    /// 半句就是为了不吞下这一种。没有 <see cref="BackupRunControl"/>——这条要钉的正是"没人按停止，
    /// 是外面把令牌砍了"——这次取消必须原样透出去，被 <c>RunAsync</c> 顶上兜底的
    /// <c>catch (Exception)</c> 当成一次真实失败记下来，而不是被那个 catch 悄悄咽成
    /// <c>CleanupReport.Empty</c>、报一次"成功"。</summary>
    [SkippableFact]
    public async Task A_genuine_cancellation_mid_compaction_still_propagates_as_a_failure()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("stop");
        var real = new BlobUploader(new BlobClientFactory(TestSecrets.Reader));
        using var hostShutdown = new CancellationTokenSource();
        var slowCompact = new DelayedOverwriteUploader(
            real, TimeSpan.FromSeconds(8), onFirstCall: () => hostShutdown.Cancel());
        var (orchestrator, factory, _) = Build(real, compactUploader: slowCompact);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            var request = await SeedPackDueForCompactionAsync(orchestrator, account, name);

            // 无 control：v2 本身用的就是这个会被砍掉的令牌，不是某个 BackupRunControl 的 StopToken。
            var run = orchestrator.RunAsync(request, null, hostShutdown.Token, null);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>扫描之前就下达停止：扫描必须当场看得见它，而且要**走收尾路径**退出——
    /// 按停法产出对的异常类型，而不是让一个裸取消逃出去被 BackupRunner 记成 Failed。
    /// <para>
    /// "没开卷"就是"扫描确实被截断了"的判据：journal 开卷排在扫描与读索引**之后**，
    /// 令牌若没接进扫描，这一轮会一路跑到开卷才被 diff 拦下，盘上就会留下一卷 journal。
    /// </para></summary>
    [SkippableTheory]
    [InlineData(StopKind.Suspend)]
    [InlineData(StopKind.FinishCurrentFiles)]
    [InlineData(StopKind.StopNow)]
    public async Task A_stop_before_the_scan_settles_without_ever_opening_a_journal(StopKind kind)
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("stop");
        var uploader = new StopAt(
            new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), at: -1, stop: () => { }, thenThrow: false);
        var (orchestrator, factory, _) = Build(uploader);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteBytes("big1.bin", 6_000_000);
            WriteBytes("big2.bin", 6_000_001);
            await using var c = new BackupRunControl(_journals, 8, "run-early-" + (int)kind);
            c.RequestStop(kind);

            await RunAndCatchAsync(orchestrator.RunAsync(Request(account, name), null, default, c), kind);

            Assert.Empty(await _journals.ListAsync(account.Id, name, default));
            Assert.Equal(0, uploader.Calls);
            Assert.Empty(await DataBlobsAsync(container));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>读版本索引读到一半按下停止：必须当场有反应，而不是等每一版索引都读完。
    /// 用户按下停止之后 SuspendAsync/CancelAsync 一直等到终态才返回，不当场反应就是 HTTP 挂死。</summary>
    [SkippableTheory]
    [InlineData(StopKind.Suspend)]
    [InlineData(StopKind.StopNow)]
    public async Task A_stop_during_the_version_index_load_settles_instead_of_waiting_for_it(StopKind kind)
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("stop");
        var real = new BlobUploader(new BlobClientFactory(TestSecrets.Reader));
        var (first, factory, authority) = Build(real);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteBytes("small1.bin", 1024);
            WriteBytes("small2.bin", 2048);
            // 先跑成功一轮：第二轮才有版本索引可读。
            Assert.Equal(1, (await first.RunAsync(Request(account, name), null, default)).Version);

            BlockingIndexCache? blocking = null;
            var (second, _, _) = Build(
                real, reuse: authority, wrapIndexCache: inner => blocking = new BlockingIndexCache(inner));

            await using var c = new BackupRunControl(_journals, 8, "run-index-" + (int)kind);
            // 这条用例唯一的"放行"手段就是这个令牌：BlockingIndexCache 挂在 Task.Delay(Infinite, ct)
            // 上，没有另外的 Release 信号。令牌没等到 Reading，或者停止意愿没能传进这个 ct（正是
            // 本用例要钉的回归），run 都可能还在后台挂着——finally 里用测试自己的 CTS 兜底取消。
            using var runCts = new CancellationTokenSource();
            var run = second.RunAsync(Request(account, name), null, runCts.Token, c);
            try
            {
                await blocking!.Reading.Task.WaitAsync(TimeSpan.FromSeconds(30));
                c.RequestStop(kind);

                // 令牌没接进读索引那一步的话，这一句会一直挂到超时——那正是这条用例要钉的回归。
                await RunAndCatchAsync(run.WaitAsync(TimeSpan.FromSeconds(30)), kind);

                // 停在开卷之前，所以盘上不该留下 journal；也不该写出第二个版本。
                Assert.Empty(await _journals.ListAsync(account.Id, name, default));
                var info = await authority.Tracked.LoadAsync(account, name, null, default);
                Assert.Single(info!.Versions);
            }
            finally
            {
                // 不管上面成没成功都保证收尾：run 若还挂着，这里直接取消它自己的外部令牌，
                // 不再依赖被测的那条链路，然后等它真正跑完，不让它带着未观察的异常在
                // Dispose() 删掉临时目录之后继续跑。
                runCts.Cancel();
                await run.ContinueWith(_ => { });
            }
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>用户按的取消不是事故：不留长存 Error，也不发失败 webhook。
    /// Task 9 之前这条靠的是"Cancel 取消了 ct，于是 Record 自己抛出、什么都没记"这个巧合；
    /// 现在 ct 不再被碰，必须有东西钉着。</summary>
    [SkippableFact]
    public async Task Cancel_leaves_no_error_audit_entry_and_fires_no_failure_webhook()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("stop");
        BackupRunControl? control = null;
        var opLog = new RecordingOperationLog();
        var notifier = new RecordingNotifier();
        var uploader = new StopAt(
            new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), at: 1,
            stop: () => control!.RequestStop(StopKind.StopNow), thenThrow: false);
        var (orchestrator, factory, _) = Build(uploader, opLog, notifier);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteBytes("big1.bin", 6_000_000);
            WriteBytes("big2.bin", 6_000_001);
            await using var c = new BackupRunControl(_journals, 8, "run-cancel-audit");
            control = c;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => orchestrator.RunAsync(Request(account, name), null, default, c));

            List<(OperationLogLevel Level, string Source, string Message, bool? Durable)> entries;
            lock (opLog.Entries) entries = [.. opLog.Entries];
            List<(NotificationEvents Event, string Title, string Body)> sent;
            lock (notifier.Sent) sent = [.. notifier.Sent];

            Assert.DoesNotContain(entries, e => e.Level >= OperationLogLevel.Warning);
            Assert.DoesNotContain(entries, e => e.Message.Contains("Backup failed", StringComparison.Ordinal));
            Assert.DoesNotContain(sent, n => n.Event == NotificationEvents.BackupFailure);

            // 但审计上要留得下"这一轮是被人停掉的，不是跑挂了"：Info，且短存。
            var canceled = Assert.Single(
                entries, e => e.Message.Contains("Backup canceled", StringComparison.Ordinal));
            Assert.Equal(OperationLogLevel.Info, canceled.Level);
            Assert.False(canceled.Durable);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Stop_now_deletes_the_in_flight_residue_but_keeps_finished_blocks()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("stop");
        BackupRunControl? control = null;
        // 第 2 次上传：传上去了，但随即"在途中断"——上传确认没能返回，所以它没进 journal，
        // 在途登记也没销。这正是 Stop now 要清掉的那种残留。
        var uploader = new StopAt(
            new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), at: 2,
            stop: () => control!.RequestStop(StopKind.StopNow), thenThrow: true);
        var (orchestrator, factory, _) = Build(uploader);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteBytes("big1.bin", 6_000_000);
            WriteBytes("big2.bin", 6_000_001);
            WriteBytes("big3.bin", 6_000_002);
            await using var c = new BackupRunControl(_journals, 8, "run-stopnow");
            control = c;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => orchestrator.RunAsync(Request(account, name), null, default, c));

            // Stop now 打断在途上传：第三件活连上传都不该开始。
            Assert.Equal(2, uploader.Calls);

            var journal = Assert.Single(await _journals.ListAsync(account.Id, name, default));
            var kept = Assert.Single(journal.Content.Records);       // 只有第一件确认完成
            var blobs = await DataBlobsAsync(container);
            // 完整传完的留着（下次复用），在途那个的残留被删干净。
            Assert.Equal([kept.Ref], blobs);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
