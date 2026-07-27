using System.Net.Sockets;
using Azure.Storage.Blobs;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// diff 通过之后，源文件还会被再次打开——7z 压缩打包成员时、原样存储单文件上传时。
/// 一个在 diff 时可读、随后被锁住（占用/权限收回）的文件，此前会让 hasher.FullHashAsync
/// 在分组重校验的"已排除成员"处理里第二次抛出且无人接住，从而让整轮备份崩溃。
/// 本文件验证：diff 之后才发生的读失败，与 diff 时就读不开一样，被当作"读不开"处理——
/// 不产生 blob、索引沿用旧条目（无则缺席）、记一条告警、计入 UnreadableFiles，绝不让整轮备份失败。
/// </summary>
[Trait("Category", "Integration")]
public sealed class UnreadableDuringUploadTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;

    public UnreadableDuringUploadTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-unreadupload-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "src");
        _temp = Path.Combine(baseDir, "temp");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        // 恢复权限，否则某些平台上递归删除会因子文件不可读而失败。
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                try { File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best effort */ }
        }
        catch { /* best effort */ }
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { /* best effort */ }
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

    private void WriteText(string rel, string content)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>
    /// 模拟"diff 时能读、随后立刻被锁住"：包一层真实 hasher，diff 用它对目标路径算完真实的
    /// fullHash 之后（也就是 diff 认定该文件可读、可分类为 Added/Modified 的那一刻），
    /// 立刻把该文件的 Unix 权限位清零。此后 orchestrator 自己的（同样是真实的）hasher/7z
    /// 再去读它，会撞上真正的操作系统权限拒绝——不是靠假异常模拟，是真的读不开。
    /// 之所以不用假抛异常的替身：本进程不是 root（chmod 000 在这台机器上真实生效），
    /// 用真权限验证的是"生产环境下的操作系统调用是否真被正确捕获"，比替身更贴近真实故障。
    /// </summary>
    private sealed class LockAfterDiffHasher(IFileHasher inner, string relPath) : IFileHasher
    {
        private int _locked;

        public Task<string> HeadHashAsync(string path, int headBytes, CancellationToken ct = default) =>
            inner.HeadHashAsync(path, headBytes, ct);

        public Task<string> TailHashAsync(string path, int tailBytes, CancellationToken ct = default) =>
            inner.TailHashAsync(path, tailBytes, ct);

        public async Task<string> FullHashAsync(string path, CancellationToken ct = default)
        {
            var hash = await inner.FullHashAsync(path, ct);
            if (path.EndsWith(relPath.Replace('/', Path.DirectorySeparatorChar), StringComparison.Ordinal)
                && Interlocked.Exchange(ref _locked, 1) == 0)
                File.SetUnixFileMode(path, UnixFileMode.None); // diff 之后立即锁住——模拟"随后被占用/权限收回"
            return hash;
        }
    }

    /// <summary>把一组文件（分组打包成员）压缩一次之后立刻整批锁住——模拟"整个目录忽然读不开"：
    /// 分组重校验会发现每个成员的权限位都变了，逐一重算 hash 全部失败，"已排除成员"处理必须
    /// 在第一个成员就吞下失败、继续处理其余成员，而不是抛出未接住的异常让整轮备份崩溃。</summary>
    private sealed class LockAllAfterFirstCompressCompressor(
        IFileCompressor inner, string rootPath, IReadOnlyList<string> relPaths) : IFileCompressor
    {
        private int _fired;

        public async Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken ct = default)
        {
            var result = await inner.CompressAsync(request, ct);
            if (Interlocked.Exchange(ref _fired, 1) == 0)
            {
                foreach (var rel in relPaths)
                {
                    var full = Path.Combine(rootPath, rel.Replace('/', Path.DirectorySeparatorChar));
                    File.SetUnixFileMode(full, UnixFileMode.None);
                }
            }
            return result;
        }

        public Task ExtractAsync(string firstVolumePath, string outputDir, string? password, CancellationToken ct = default)
            => inner.ExtractAsync(firstVolumePath, outputDir, password, ct);
    }

    /// <summary>捕获 NotifyAsync 调用，供断言"读不开的文件推送了通知"。</summary>
    private sealed class CapturingNotifier : INotifier
    {
        public List<(NotificationEvents Event, string Title, string Body)> Notifications { get; } = [];
        public Task NotifyAsync(NotificationEvents evt, string title, string body, CancellationToken ct = default)
        {
            lock (Notifications) Notifications.Add((evt, title, body));
            return Task.CompletedTask;
        }
    }

    private BackupRequest Request(Account account, string container, long singleFileThresholdBytes) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _root,
        Name = "photos",
        Password = null,
        Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = singleFileThresholdBytes } },
    };

    /// <summary>本次修复的核心断言：diff 之后才发生的读失败（压缩/上传阶段再次打开源文件时撞上）
    /// 不能让整轮备份崩溃——必须完工，该文件按"读不开"降级处理，其余文件正常上传。</summary>
    [SkippableFact]
    public async Task A_File_Locked_After_The_Diff_Does_Not_Abort_The_Run()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var notifier = new CapturingNotifier();

        // 单文件阈值压到 1：locked.bin 与 plain.txt 各自成一个 data/{hash} blob（单文件路径，
        // 即 HandleBlobAsync/ProcessAsync），而不是走分组打包——本测试专盯"原样单文件"这条路径。
        var account = AzuriteAccount();
        var name = RandomName("unreadupl-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("locked.bin", "will be locked right after diff reads it");
            WriteText("plain.txt", "ordinary file, uploads fine");

            // differ 用会在读完之后锁文件的 hasher；orchestrator 自身用真实 hasher/真实 7z——
            // 它们撞上的是货真价实的操作系统权限拒绝，不是替身抛出的假异常。
            var differ = new BackupDiffer(new LockAfterDiffHasher(new FileHasher(), "locked.bin"));
            var orchestrator = new BackupOrchestrator(
                new LocalFileScanner(), differ, new GroupingPlanner(),
                new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
                new RetentionCleaner(factory, store, new RetentionEvaluator()), new FileHasher(),
                notifier: notifier);

            var result = await orchestrator.RunAsync(Request(account, name, singleFileThresholdBytes: 1));

            Assert.Equal(1, result.Version); // 备份完工，产出新版本——没有因这一个文件崩掉整轮
            Assert.Equal(1, result.UnreadableFiles); // 复用既有计数

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);

            // locked.bin 是全新文件、从未成功备份过——没有旧条目可沿用，编造一条是撒谎，须整条缺席。
            Assert.DoesNotContain(idx.Entries, e => e.Path == "locked.bin");

            // plain.txt 完全不受影响，正常上传、正常出现在索引里。
            var plain = Assert.Single(idx.Entries, e => e.Path == "plain.txt");
            Assert.Equal("blob", plain.Storage!.Kind);
            Assert.True(await container.GetBlobClient(plain.Storage.Ref).ExistsAsync());

            // 复用既有的 UnrecoverableError 通知通道，而非另起一套。
            var notification = Assert.Single(notifier.Notifications, n => n.Event == NotificationEvents.UnrecoverableError);
            Assert.Contains("locked.bin", notification.Title);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>诊断报告点名的最坏情形：整个目录的成员一起变得读不开。分组重校验会把它们
    /// 全部判定为"已排除"，此前"changed"成员处理里对第一个成员重算 hash 会再次抛出且无人接住，
    /// 于是在同目录的其余成员被处理之前就让整轮备份崩溃。验证这条路径现在能扛住并全须全尾地完工。</summary>
    [SkippableFact]
    public async Task A_Whole_Directory_Locked_After_The_Diff_Does_Not_Abort_The_Run()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var notifier = new CapturingNotifier();

        var account = AzuriteAccount();
        var name = RandomName("unreaddir-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // 同目录两个小文件 → 默认分组阈值下会被规划进同一个 pack。
            WriteText("d/x.txt", "xxxx");
            WriteText("d/y.txt", "yyyy");

            var compressor = new LockAllAfterFirstCompressCompressor(
                new SevenZipCompressor(), _root, ["d/x.txt", "d/y.txt"]);

            var orchestrator = new BackupOrchestrator(
                new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
                compressor, new BlobUploader(factory), factory, store, staging,
                new RetentionCleaner(factory, store, new RetentionEvaluator()), new FileHasher(),
                notifier: notifier, verifier: new ProcessingVerifier(new FileHasher()));

            var result = await orchestrator.RunAsync(
                Request(account, name, singleFileThresholdBytes: 5_000_000)); // 走分组打包，不走单文件路径

            Assert.Equal(1, result.Version); // 备份完工——两个成员一起读不开也不能让整轮崩溃
            Assert.Equal(2, result.UnreadableFiles); // 两个都计入

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);

            // 两个都是全新文件，没有旧条目可沿用，整条缺席。
            Assert.DoesNotContain(idx.Entries, e => e.Path == "d/x.txt");
            Assert.DoesNotContain(idx.Entries, e => e.Path == "d/y.txt");

            // 两个各有一条告警，都复用既有通知通道。
            Assert.Contains(notifier.Notifications, n => n.Event == NotificationEvents.UnrecoverableError && n.Title.Contains("d/x.txt"));
            Assert.Contains(notifier.Notifications, n => n.Event == NotificationEvents.UnrecoverableError && n.Title.Contains("d/y.txt"));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
