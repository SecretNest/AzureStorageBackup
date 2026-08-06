using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 用户实际遭遇：新建备份后界面停在 `Diffing 0% (0 changed)` 很久，无从判断在干什么、是否卡死。
/// 原因是每个阶段只在**进入**时上报一次，而首次备份的 diff 要把每个文件完整读一遍算 hash
/// （无 previous 的文件走 AddedAsync → HeadHash + FullHash），可以跑几小时；
/// 且 `TotalItems=0` 让百分比恒为 0。扫描阶段同样如此。
/// </summary>
[Trait("Category", "Integration")]
public sealed class BackupProgressDetailTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;

    public BackupProgressDetailTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-progress-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "src");
        _temp = Path.Combine(baseDir, "temp");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { /* best effort */ }
    }

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static bool SevenZip() => SevenZipArchiveCodec.TryResolveExecutable() is not null;

    private sealed class CapturingProgress : IProgress<BackupProgress>
    {
        public List<BackupProgress> Reports { get; } = [];
        public void Report(BackupProgress value) { lock (Reports) Reports.Add(value); }
    }

    [SkippableFact]
    public async Task Scanning_And_Diffing_Report_What_They_Are_Working_On()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var progress = new CapturingProgress();

        var account = new Account
        {
            Name = "azurite",
            BlobEndpoint = "http://127.0.0.1:10000/devstoreaccount1",
            AccountKeyProtected = TestSecrets.Protect(AzuriteKey),
            Region = AzureRegion.Global,
        };
        var name = "progress-" + Guid.NewGuid().ToString("N")[..8];
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // 若干个文件，足以让 diff 有多步可报。
            for (var i = 0; i < 40; i++)
            {
                var dir = Path.Combine(_root, "d" + (i % 4));
                Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(Path.Combine(dir, $"f{i:D3}.txt"), new string('x', 500 + i));
            }

            var authority = new TestLocalAuthority(store);
            var orchestrator = new BackupOrchestrator(
                new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
                new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
                new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked);

            await orchestrator.RunAsync(new BackupRequest
            {
                Account = account, Container = name, LocalRoot = _root, Name = "progress-test",
            }, progress);

            var diffing = progress.Reports.Where(r => r.Stage == BackupStage.Diffing && r.Detail is not null).ToList();
            var scanning = progress.Reports.Where(r => r.Stage == BackupStage.Scanning && r.Detail is not null).ToList();

            // 核心：diff 阶段必须报出「正在处理哪个文件」——卡住时这是唯一能说明卡在哪的信息。
            Assert.NotEmpty(diffing);
            Assert.Contains(diffing, r => !string.IsNullOrEmpty(r.Detail!.CurrentItem));

            // 而且必须走到底：修复前它一次都不报，百分比恒为 0。
            var lastDiff = diffing[^1].Detail!;
            Assert.Equal(40, lastDiff.Total);
            Assert.Equal(40, lastDiff.Processed);
            Assert.Equal(100, lastDiff.Percent);

            // 扫描阶段总数未知（总数正是它要算出来的）→ 不编造百分比，但要报当前目录与已扫条目数。
            Assert.NotEmpty(scanning);
            Assert.Null(scanning[^1].Detail!.Percent);
            Assert.Equal(40, scanning[^1].Detail!.Processed);
            Assert.Contains(scanning, r => !string.IsNullOrEmpty(r.Detail!.CurrentItem));

            // 上传阶段：已传字节要累计起来（测速的依据），且收尾必须强制产出终态——
            // 否则最后一批字节会被压在节流窗口里再也发不出来。
            // 这里**不**断言"某次快照恰好看到在途项"：那取决于 200ms 节流窗口是否恰好落在
            // BeginItem 与 EndItem 之间，本地 Azurite 上传太快时不可靠。在途项的机制由
            // StageProgressTests 的单测确定性地覆盖，集成测试只验证接线与终态。
            var uploading = progress.Reports.Where(r => r.Stage == BackupStage.Uploading && r.Detail is not null).ToList();
            Assert.NotEmpty(uploading);
            // 字节现在只有一个来源：Azure SDK 的 ProgressHandler 边传边报。这条断言因此顺带守住了
            // 整条字节级链路（VolumeBlobIO → IBlobUploader → BlobUploadOptions.ProgressHandler）——
            // 任何一环断了，速度读数就会永远是 0，正是修复前用户看到的现象。
            Assert.True(uploading[^1].Detail!.Bytes > 0, "uploaded bytes should accumulate for the speed readout");

            // 槽位计数恰好一次：绝不能超过 total（在途项的起止不得参与计数）。
            Assert.All(uploading, r => Assert.True(r.Detail!.Processed <= r.Detail.Total));

            // 队列必须排空。BeginWork/EndWork 不配对（失败路径漏了 finally）会让界面永远挂着
            // "N preparing"，而那时其实什么都没在跑；入队计数漏一笔则会挂着 "N queued"。
            Assert.Equal(0, uploading[^1].Detail!.Preparing);
            Assert.Equal(0, uploading[^1].Detail!.Queued);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 读盘核对那几段要真的接上线。用户遭遇：屏幕上半分钟纹丝不动的
    /// <c>686 of 11,004 objects · 1 object starting upload · 10,317 objects queued</c>——
    /// 那一件活当时在逐成员 <c>Stat</c>／整读算 hash，既没在 starting 也没在 upload，
    /// 而这几段一个进度事件都不发，心跳又只在有流在传时才跑，于是界面冻在旧快照上。
    /// <para>
    /// 这里断言的是**接线**（四处调用点确实登记了、且配对没漏）；计数语义与发布时机由
    /// <c>UploadWaitVisibilityTests</c> 确定性地覆盖。能这么断言正是因为 <c>BeginChecking</c>
    /// 强制发布——若它跟着 200ms 节流走，这条断言就得看运气，而那也正说明界面看不看得见得看运气。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Local_Checking_Work_Shows_Up_In_The_Upload_Stage()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var progress = new CapturingProgress();

        var account = new Account
        {
            Name = "azurite",
            BlobEndpoint = "http://127.0.0.1:10000/devstoreaccount1",
            AccountKeyProtected = TestSecrets.Protect(AzuriteKey),
            Region = AzureRegion.Global,
        };
        var name = "checking-" + Guid.NewGuid().ToString("N")[..8];
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // 小文件成箱（装箱前 stat + 压缩后逐成员重校验），大文件走单文件路径（去重预筛整读算
            // 三段 hash）——四处登记里的三处都在这一趟里，第四处（加密多卷清残留）另有其测。
            Directory.CreateDirectory(Path.Combine(_root, "pack"));
            for (var i = 0; i < 8; i++)
                await File.WriteAllTextAsync(Path.Combine(_root, "pack", $"s{i:D2}.txt"), new string('x', 200 + i));
            await File.WriteAllTextAsync(Path.Combine(_root, "big.bin"), new string('y', 8_000));

            var authority = new TestLocalAuthority(store);
            var orchestrator = new BackupOrchestrator(
                new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
                new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
                new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked);

            await orchestrator.RunAsync(new BackupRequest
            {
                Account = account, Container = name, LocalRoot = _root, Name = "checking-test",
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1_000 } },
            }, progress);

            var uploading = progress.Reports
                .Where(r => r.Stage == BackupStage.Uploading && r.Detail is not null)
                .Select(r => r.Detail!)
                .ToList();

            Assert.NotEmpty(uploading);
            // 接线：这一段至少被看见过一次。看不见就是回到了"屏幕上一动不动的 starting upload"。
            Assert.Contains(uploading, d => d.Checking > 0);
            // 细分关系：checking 是从 uploading 里拆出来的，越不过它——越过了说明有一段登记跑到
            // 暂存段里去了，那条件数恒等式就破了，界面上会算出负数的 "starting upload"。
            Assert.All(uploading, d => Assert.True(
                d.Checking <= d.Uploading, $"checking ({d.Checking}) must stay within uploading ({d.Uploading})"));
            // 配对：终态必须归零。漏一次 EndChecking，这一栏就在余下的运行里卡在虚高的数字上——
            // preparing 在这个项目里正是这么栽过一次。
            Assert.Equal(0, uploading[^1].Checking);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
