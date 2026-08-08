using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class VolumeBlobIOTests
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

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

    private static string RandomName(string p) => p + Guid.NewGuid().ToString("N")[..8];

    /// <summary>记录上传顺序的假 uploader。</summary>
    private sealed class RecordingUploader : IBlobUploader
    {
        public List<string> Order { get; } = [];

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            Order.Add(blobName);
            return Task.FromResult(true);
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            Order.Add(blobName);
            return Task.CompletedTask;
        }
    }

    private static Account Acc() => new() { Name = "a", BlobEndpoint = "http://x", AccountKeyProtected = TestSecrets.Protect("k") };

    /// <summary>并发峰值探针：达到 <paramref name="expectPeak"/> 个同时在传才放行。
    /// 不用 sleep 猜时序——并行真没发生的话这里会一直等到超时，<c>Max</c> 停在 1，断言自然失败。</summary>
    private sealed class ConcurrencyProbe(int expectPeak) : IBlobUploader
    {
        private readonly TaskCompletionSource _peak = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _gate = new();
        private int _current;

        public int Max { get; private set; }
        public List<string> Order { get; } = [];

        public async Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            var now = Interlocked.Increment(ref _current);
            lock (_gate)
            {
                Max = Math.Max(Max, now);
                Order.Add(blobName);
            }
            if (now >= expectPeak)
                _peak.TrySetResult();
            await Task.WhenAny(_peak.Task, Task.Delay(TimeSpan.FromSeconds(5), ct));
            Interlocked.Decrement(ref _current);
            return true;
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => throw new NotSupportedException();
    }

    private static VolumeUploadScope Scope(VolumeUploadGate gate, int perItem) =>
        new(gate, new StageTracker("Uploading", 0, static _ => { }), perItem);

    /// <summary>记录每一卷开始上传的先后，并让上传慢到足以让两族卷真的抢起来。</summary>
    private sealed class OrderProbe : IBlobUploader
    {
        private readonly Lock _gate = new();
        public List<string> Started { get; } = [];

        public async Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            lock (_gate) Started.Add(blobName);
            await Task.Delay(20, ct);
            return true;
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// 两族卷同时抢额度时，老的那一族**基本上是整族连着传完的**，新的那族插不进来。
    /// <para>
    /// 这条守的是中断代价。整族传完、云端确认返回之后才记 journal、才销在途账，所以「同时半完成
    /// 的族数」就是一次 <c>Stop now</c> / 挂起 / 崩溃白扔掉多少已经压好传上去的字节。先到先得会让
    /// 额度摊薄到所有在传的族上，几族一起卡在半路；按件龄仲裁之后基本上只有一族。
    /// </para>
    /// <para>
    /// **断言为什么不是「一卷都插不进来」**：一卷传完是在 <c>RunAsync</c> 的 finally 里放行的，
    /// 而这一族的下一卷要等 <c>WhenAny</c> 的续体跑起来才排得上队——这两件事之间有一道缝。
    /// <c>WindowPerItem</c> 多排的那一卷（接力棒）盖住了常见时序：换卷那一瞬本族在闸门上还留着
    /// 一个等待者，凭更小的票号把额度接住。但线程池被饿着时续体可能迟到几百毫秒，那一缝里新族
    /// 确实可能捡走一卷。生产上这道缝以微秒计、而一卷上传以秒计，所以它是可以忍的漏，不是错误。
    /// 界限取 2：先到先得下这里稳定是 3 卷（一半），修好之后正常是 0、最坏 1，两边离得很开。
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_Older_Archive_Is_Not_Interleaved_With_A_Newer_One()
    {
        var up = new OrderProbe();
        var gate = new VolumeUploadGate(2);
        var scope = Scope(gate, perItem: 2);
        var older = Enumerable.Range(1, 6).Select(i => $"/tmp/a.{i:000}").ToList();
        var newer = Enumerable.Range(1, 6).Select(i => $"/tmp/b.{i:000}").ToList();

        // 票号在 UploadAsync 的同步段就领掉了，所以先调的这一族一定更老——不靠 sleep 猜时序。
        var first = VolumeBlobIO.UploadAsync(
            up, Acc(), "c", "data/older", older, AccessTier.Hot, scope: scope);
        var second = VolumeBlobIO.UploadAsync(
            up, Acc(), "c", "data/newer", newer, AccessTier.Hot, scope: scope);
        await Task.WhenAll(first, second);

        Assert.Equal(12, up.Started.Count);
        var lastOfOlder = up.Started.FindLastIndex(n => n.StartsWith("data/older", StringComparison.Ordinal));
        var newerBeforeOlderFinished = up.Started
            .Take(lastOfOlder)
            .Count(n => n.StartsWith("data/newer", StringComparison.Ordinal));

        Assert.True(newerBeforeOlderFinished < 2,
            $"新族在老族传完之前抢走了 {newerBeforeOlderFinished} 卷：{string.Join(", ", up.Started)}");
        Assert.Equal(2, gate.Free);
    }

    /// <summary>
    /// 每一卷都传上去了，顺序不作要求。首卷曾经是最后单独传的「整族齐全」提交标记，那个语义
    /// 已经随云端存在性去重一并删掉——它换来的是 2–5 卷的文件上传耗时翻倍（收尾那一趟只有一条流
    /// 在动），而去重现在只看本地权威索引，根本不问云端有什么。
    /// </summary>
    [Fact]
    public async Task Every_Volume_Goes_Up_Regardless_Of_Order()
    {
        var up = new RecordingUploader();

        await VolumeBlobIO.UploadAsync(
            up, Acc(), "c", "data/h", ["/tmp/a.001", "/tmp/a.002", "/tmp/a.003"], AccessTier.Hot);

        Assert.Equal(["data/h.001", "data/h.002", "data/h.003"], [.. up.Order.Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// 同一归档的分卷必须并行上传，且在途流数受闸门约束。
    /// <para>
    /// 这条测试守的是那个真实故障：一个大文件切出上千卷时，从前它整段只占**一个**槽位，
    /// 一卷传完才轮下一卷——设置里的「并发 5」在传大文件时形同虚设，实测只跑出单条 TCP
    /// 到 Azure 的 4–6 MB/s。额度改按卷发放之后，在途流数与队列里是一个大文件还是一万个
    /// 小文件无关。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Volumes_Of_One_Archive_Ride_The_Gate_In_Parallel()
    {
        var up = new ConcurrencyProbe(expectPeak: 2);
        var gate = new VolumeUploadGate(2);
        var files = Enumerable.Range(1, 7).Select(i => $"/tmp/a.{i:000}").ToList();

        await VolumeBlobIO.UploadAsync(
            up, Acc(), "c", "data/h", files, AccessTier.Hot, scope: Scope(gate, perItem: 4));

        Assert.Equal(2, up.Max);              // 既确实并行了（>1），又没越过闸门（≤2）
        Assert.Equal(7, up.Order.Count);
        Assert.Equal(2, gate.Free);   // 额度全数归还
    }

    /// <summary>
    /// 两卷也要真并发——这正是首卷单独收尾最贵的地方。
    /// <para>
    /// 默认卷 100 MB、并发 5，一个 100–500 MB 的文件切成 2–5 卷。首卷留到最后单传的话，这样的
    /// 文件永远是"其余几卷并行一轮，再单独一轮传首卷"，整件耗时翻倍，而这个尺寸段在真实备份里
    /// 是大头。探针要等到 2 卷同时在传才放行：旧实现下峰值只会是 1，这条会卡到超时后失败。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_Volumes_Go_Up_At_The_Same_Time()
    {
        var up = new ConcurrencyProbe(expectPeak: 2);
        var gate = new VolumeUploadGate(5);

        await VolumeBlobIO.UploadAsync(
            up, Acc(), "c", "data/h", ["/tmp/a.001", "/tmp/a.002"], AccessTier.Hot,
            scope: Scope(gate, perItem: 5));

        Assert.Equal(2, up.Max);
        Assert.Equal(5, gate.Free);
    }

    /// <summary>没给 scope 的调用（修复/替换等非备份主路径）保持老样子：串行、一次一卷。</summary>
    [Fact]
    public async Task Without_A_Scope_Volumes_Still_Go_Up_One_At_A_Time()
    {
        var up = new ConcurrencyProbe(expectPeak: 1);

        await VolumeBlobIO.UploadAsync(
            up, Acc(), "c", "data/h", ["/tmp/a.001", "/tmp/a.002", "/tmp/a.003"], AccessTier.Hot);

        Assert.Equal(1, up.Max);
    }

    [Fact]
    public async Task Single_Volume_Uploads_Base_Name()
    {
        var up = new RecordingUploader();

        await VolumeBlobIO.UploadAsync(up, Acc(), "c", "data/h", ["/tmp/a.7z"], AccessTier.Hot);

        Assert.Equal(["data/h"], up.Order);
    }

    /// <summary>记录每次调用返回的实例是否互不相同的假进度回调。</summary>
    private sealed class SpyProgress : IProgress<long>
    {
        public long LastReported { get; private set; } = -1;
        public void Report(long value) => LastReported = value;
    }

    /// <summary>
    /// 用户实际会看到的症状（修复前）：<c>DownloadAsync</c> 若把进度回调只拿一次、多卷共用，
    /// 后一卷的字节会被 <see cref="StageTracker"/> 里的 <c>DeltaProgress</c> 误判成"前一卷的
    /// 回退重传"而错记账，还原/校验速度读数因此失真（见 <c>VolumeBlobIO.DownloadAsync</c> 方法头
    /// 注释）。
    /// <para>
    /// 直接钉死 Part 1 的字面契约——"工厂每卷各调一次、拿到的是各不相同的实例"——而不是拐个弯去看
    /// 下游 <c>StageTracker</c> 累计的总字节数：后者经过 mutation 验证过，对本项目 7z 分卷天然产生
    /// 的"除末卷外各卷等大、末卷最小"这种大小序列并不敏感（<c>DeltaProgress</c> 的回退判定在这种
    /// 序列下会自我纠正，共享实例照样能凑出正确的总数，测不出这个缺陷）。工厂调用次数/实例身份
    /// 是本次改动唯一能保证在 mutation 下必现的信号。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task DownloadAsync_Calls_Progress_Factory_Once_Per_Volume_With_A_Fresh_Instance()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var account = AzuriteAccount();
        var name = RandomName("vbio-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            const string baseRef = "data/multi";
            var sizes = new[] { 5_000, 7_000, 3_000, 1_234 };
            for (var i = 0; i < sizes.Length; i++)
                await container.GetBlobClient($"{baseRef}.{i + 1:D3}")
                    .UploadAsync(new BinaryData(new byte[sizes[i]]), overwrite: true);

            var instances = new List<SpyProgress>();
            Func<IProgress<long>> makeProgress = () =>
            {
                var spy = new SpyProgress();
                instances.Add(spy);
                return spy;
            };

            var workDir = Path.Combine(Path.GetTempPath(), "asb-vbio-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);
            try
            {
                await VolumeBlobIO.DownloadAsync(container, baseRef, workDir, CancellationToken.None, makeProgress);
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
            }

            // 工厂调用次数＝卷数：若实现把 progress() 提到循环外只调一次，这里会是 1 而不是 4。
            Assert.Equal(sizes.Length, instances.Count);
            // 每个实例互不相同——ReferenceEquals 意义上真的是"各卷各要一个"，不是同一个引用重复入列。
            Assert.Equal(instances.Count, instances.Distinct().Count());
            // 每个实例确实收到了对应那一卷的最终累计字节，证明工厂返回值真被接到了那一卷的下载上，
            // 不是造了个没人用的实例、实际下载另外共享着别的回调。
            for (var i = 0; i < sizes.Length; i++)
                Assert.Equal(sizes[i], instances[i].LastReported);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>一卷挂住不动，其余卷照常放行；记下谁传完了。</summary>
    private sealed class OneStuckVolume(string stuck, int expectOthers) : IBlobUploader
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _others = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _done;

        public Task OthersFinished => _others.Task;
        public void Release() => _release.TrySetResult();
        public List<string> Order { get; } = [];

        public async Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            if (blobName == stuck)
                await _release.Task.WaitAsync(ct);
            lock (Order) Order.Add(blobName);
            if (blobName != stuck && Interlocked.Increment(ref _done) >= expectOthers)
                _others.TrySetResult();
            return true;
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// 卷的并发额度是**滑动窗口**：完成一卷立刻补一卷，不等同批的其它卷。
    /// <para>
    /// 从前是一批一批来（<c>Task.WhenAll</c> 每 N 卷一个栅栏），一批里最慢的那一卷会让其余几条流
    /// 全程空转等它。卷与卷的耗时本来就不齐——重试、分块并行度、服务端限流各不相同——所以界面上
    /// 看到的是"5 条流一条条减到 0，然后又冒出 5 条"，而不是稳稳保持 5 条。
    /// </para>
    /// <para>
    /// 这条测试专挑那个故障：窗口 3、共 10 卷，第二卷挂死不动。补位生效的话，剩下 9 卷照样能全部
    /// 传完（慢卷只占住一个位子）；换回分批实现，第一批就整批卡在慢卷上，最多传完 2 卷，
    /// 下面这个等待会超时。
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_Slow_Volume_Does_Not_Stall_The_Others()
    {
        // 十卷全进窗口（首卷不再是最后单独传的提交标记），卡住的是 .002，其余 9 卷该照跑。
        var up = new OneStuckVolume("data/h.002", expectOthers: 9);
        var gate = new VolumeUploadGate(3);
        var files = Enumerable.Range(1, 10).Select(i => $"/tmp/a.{i:D3}").ToList();

        var upload = VolumeBlobIO.UploadAsync(
            up, Acc(), "c", "data/h", files, AccessTier.Hot, scope: Scope(gate, 3));

        await up.OthersFinished.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(upload.IsCompleted, "慢卷还挂着，整件不该已经收工");

        up.Release();
        await upload.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(10, up.Order.Count);
    }

    /// <summary>某一卷倒了。</summary>
    private sealed class FailingVolume(string bad) : IBlobUploader
    {
        public List<string> Order { get; } = [];
        public int Finished;

        public async Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            await Task.Yield();
            lock (Order) Order.Add(blobName);
            if (blobName == bad)
                throw new IOException("volume died");
            Interlocked.Increment(ref Finished);
            return true;
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// 有卷倒了时：不再往上起新的卷，而且抛出之前要把已经起飞的等完。半路撒手会留下没人观察的
    /// 孤儿任务，它们还占着闸门额度、还在读临时盘上的卷文件——而上层收到异常后就要去释放暂存区了。
    /// </summary>
    [Fact]
    public async Task A_Dead_Volume_Stops_New_Ones_And_Leaves_Nothing_Running()
    {
        var up = new FailingVolume("data/h.004");
        var gate = new VolumeUploadGate(3);
        var files = Enumerable.Range(1, 8).Select(i => $"/tmp/a.{i:D3}").ToList();

        await Assert.ThrowsAsync<IOException>(() => VolumeBlobIO.UploadAsync(
            up, Acc(), "c", "data/h", files, AccessTier.Hot, scope: Scope(gate, 3)));

        // 倒在第 4 卷，后面几卷就不该再起飞了——8 卷不可能都跑过一遍。
        Assert.True(up.Order.Count < files.Count, $"倒了之后还在起新卷：{up.Order.Count} 卷都跑了");
        // 闸门额度全数归还＝没有卷还攥在手里。抛出时仍在跑的话，这里会少。
        Assert.Equal(3, gate.Free);
    }

    /// <summary>
    /// 进度回调坏掉时，那一份上传额度必须还回闸门。
    /// <para>
    /// 进度上报不是旁路——<c>EndItem</c>/<c>EndWait</c> 都会直接调用方给的 publish，而那是外部代码
    /// （写库、推 SSE），抛异常的概率不为零，<c>StageProgress</c> 也明说非心跳路径故意让它往外传。
    /// 从前 <c>gate.Release()</c> 跟 <c>EndItem</c> 排在同一个 <c>finally</c> 里的后一句，前一句抛出
    /// 就把它整个跳过去了：额度一去不回。这种泄漏还不响——异常往上撞到"文件读不开"那条兜底
    /// （<c>MarkPostDiffUnreadableAsync</c> 收 IOException）就被吞了，备份照跑，只是少一条流。
    /// 攒够设定的并发数，全部上传就永远停在闸门上：界面上是「什么都没在传，暂存池却压着一堆」，
    /// 而且不会自愈。
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_Broken_Progress_Sink_Does_Not_Swallow_The_Upload_Slot()
    {
        var gate = new VolumeUploadGate(1);
        var tracker = new StageTracker("Uploading", 0, static _ => throw new IOException("progress sink broke"))
        {
            Clock = () => 0,
        };
        var scope = new VolumeUploadScope(gate, tracker, 1);

        await Assert.ThrowsAsync<IOException>(() =>
            scope.RunAsync("data/h.001", _ => Task.CompletedTask, CancellationToken.None));

        Assert.Equal(1, gate.Free);
    }

    /// <summary>
    /// 同上，但坏在**刚排到队**那一下：额度已经到手、<c>EndWait</c> 才抛。
    /// 这一段在另一个 <c>finally</c> 里，与上面那处是两条独立的路。
    /// </summary>
    [Fact]
    public async Task A_Broken_Progress_Sink_Does_Not_Swallow_The_Slot_It_Just_Waited_For()
    {
        var gate = new VolumeUploadGate(1);
        var publishes = 0;
        // 头一次是 BeginWait——那时额度还没到手，抛出去不带走任何东西，放过它。
        // 第二次是 EndWait，闸门已经放行，那一份额度正攥在手里。
        var tracker = new StageTracker("Uploading", 0, _ =>
        {
            if (Interlocked.Increment(ref publishes) >= 2)
                throw new IOException("progress sink broke");
        })
        {
            Clock = () => 0,
        };
        var scope = new VolumeUploadScope(gate, tracker, 1);

        await gate.AcquireAsync(0, 0, CancellationToken.None);  // 把唯一那份额度占住，逼它去排队
        var run = scope.RunAsync("data/h.001", _ => Task.CompletedTask, CancellationToken.None);
        gate.Release();          // 放行：等待者醒来，随即撞上坏掉的 sink

        await Assert.ThrowsAsync<IOException>(() => run);
        Assert.Equal(1, gate.Free);
    }

    [Theory]
    // 自身卷：基名、卷后缀（含 >3 位数）
    [InlineData("data/abc", "data/abc", true)]
    [InlineData("data/abc", "data/abc.001", true)]
    [InlineData("data/abc", "data/abc.1000", true)]
    [InlineData("packs/1.7z", "packs/1.7z.002", true)]
    // 碰撞避让兄弟：同前缀但内容不同，必须排除（ReplaceAsync 删残留卷时不得误删）
    [InlineData("data/abc", "data/abc~1", false)]
    [InlineData("data/abc", "data/abc~1.001", false)]
    [InlineData("data/abc~1", "data/abc~10", false)]
    [InlineData("data/abc~1", "data/abc~1.001", true)]
    // 其它同前缀噪声
    [InlineData("data/abc", "data/abcd", false)]
    [InlineData("data/abc", "data/abc.00x", false)]
    [InlineData("data/abc", "data/abc.", false)]
    public void IsVolumeOf_Matches_Only_Own_Volumes_Not_Collision_Siblings(string baseRef, string name, bool expected)
        => Assert.Equal(expected, VolumeBlobIO.IsVolumeOf(baseRef, name));
}
