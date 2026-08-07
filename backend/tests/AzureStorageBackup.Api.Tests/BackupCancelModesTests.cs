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

    private (BackupOrchestrator Orchestrator, BlobClientFactory Factory) Build(IBlobUploader uploader)
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
            new SevenZipCompressor(), uploader, factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), compactor,
                indexCache: authority.IndexCache, trackedInfo: authority.Tracked),
            new FileHasher(), authority.IndexCache, authority.Tracked);
        return (orchestrator, factory);
    }

    private BackupRequest Request(Account account, string container) => new()
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
            var start = new ManualResetEventSlim(false);
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
        var (orchestrator, factory) = Build(uploader);
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
        var (orchestrator, factory) = Build(uploader);
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
