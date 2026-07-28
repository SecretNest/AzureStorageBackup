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

            var orchestrator = new BackupOrchestrator(
                new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
                new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
                new RetentionCleaner(factory, store, new RetentionEvaluator()), new FileHasher());

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
}
