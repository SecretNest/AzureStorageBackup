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

    /// <summary>压缩一次之后立刻改写其中一个成员的内容——模拟"分组重校验发现内容在压缩期间变了"
    /// （而非读不开）：该成员会被排除出本次归档、以新内容重新处理，走 foreach(changed) 里"内容变化"
    /// 而非"读不开"的分支，为 Finding 1 的回归测试构造"源读取已经全部成功"的前提。</summary>
    private sealed class MutateAfterFirstCompressCompressor(
        IFileCompressor inner, string rootPath, string relPath, string newContent) : IFileCompressor
    {
        private int _fired;

        public async Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken ct = default)
        {
            var result = await inner.CompressAsync(request, ct);
            if (Interlocked.Exchange(ref _fired, 1) == 0)
            {
                var full = Path.Combine(rootPath, relPath.Replace('/', Path.DirectorySeparatorChar));
                File.WriteAllText(full, newContent);
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

    /// <summary>捕获每次 progress?.Report 调用，供断言"完工时进度确实到了 100%"（Finding 2）。</summary>
    private sealed class CapturingProgress : IProgress<BackupProgress>
    {
        public List<BackupProgress> Reports { get; } = [];
        public void Report(BackupProgress value) { lock (Reports) Reports.Add(value); }
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

    /// <summary>Finding 2：一整个 pack 的成员全部在 diff 之后读不开时（stable.Count == 0），
    /// 此前 HandleBlobAsync 的 catch 会 onItem()，但 ProcessDirectoryAsync 里 foreach(changed) 的
    /// 姊妹 catch 不会——而 stable.Count == 0 时 "if (stable.Count > 0)" 这唯一的另一个 onItem()
    /// 调用点也被跳过，于是这个在 total 里占了一个槽位的 pack，整轮下来 onItem() 被调用零次。
    /// uploaded 从此永远比 total 少 1，完工时进度报告也到不了 100%——即使备份其实已经跑完。
    /// 本测试直接盯着 progress 上报：备份完工后最后一次上报必须是 Stage=Completed 且 Percent=100。</summary>
    [SkippableFact]
    public async Task A_Whole_Pack_Unreadable_After_The_Diff_Still_Reports_Full_Progress()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var notifier = new CapturingNotifier();
        var progress = new CapturingProgress();

        var account = AzuriteAccount();
        var name = RandomName("unreadprog-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // 同目录两个小文件 → 规划成同一个 pack，占 total 里的一个槽位。
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
                Request(account, name, singleFileThresholdBytes: 5_000_000), progress); // 走分组打包

            Assert.Equal(1, result.Version);
            Assert.Equal(2, result.UnreadableFiles); // 两个成员都读不开——这个 pack 整包失败

            var completed = Assert.Single(progress.Reports, p => p.Stage == BackupStage.Completed);
            Assert.Equal(completed.TotalItems, completed.UploadedItems); // uploaded 追上了 total
            Assert.Equal(100, completed.Percent); // 完工必须显示 100%，不能永远差一

            // 反过来也要确认没有矫枉过正到重复计数：整个运行过程中任何一次上报都不该超过 total。
            Assert.All(progress.Reports, p => Assert.True(p.UploadedItems <= p.TotalItems));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Finding 1 回归测试：修复前，ProcessDirectoryAsync 的 foreach(changed) 用一个 try
    /// 圈住了整个 HandleBlobAsync(...) 调用——而该方法自己的处理早已成功把内容上传到云端之后，
    /// 还会做与源读取无关的下游工作（这里用 verbose logging 真实触发磁盘 IOException 来还原：
    /// 令 VerboseFileLog 的日志根目录路径中有一段是文件而非目录，Directory.CreateDirectory 在这种
    /// 路径下必然抛 IOException——不是靠假抛异常的替身模拟，是真实的文件系统调用失败）。
    /// 修复前，这个下游失败会被那层过宽的 try 接住，文件被误判成"读不开"、已经成功上传的
    /// blob 在索引里凭空消失，备份本身却"成功"收尾——这才是最坏的情形：数据丢失但无人报警。
    /// 修复后，foreach(changed) 的 try 只圈住了 hasher/BuildOverrideAsync 这段真正的源读取；
    /// HandleBlobAsync 自己也没有再把这段下游工作纳入它自己的 catch。于是这个下游失败必须
    /// 如实地从 RunAsync 抛出来——响亮地失败，而不是悄悄把一个已经传成功的文件当作读不开。</summary>
    [SkippableFact]
    public async Task A_Downstream_Failure_After_Successful_Upload_Is_Not_Misreported_As_Unreadable()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var notifier = new CapturingNotifier();

        // verbose 日志根目录里有一段路径其实是个普通文件——Directory.CreateDirectory 在这种路径下
        // 必然报 ENOTDIR（IOException），这是货真价实的文件系统失败，不是替身抛的假异常。
        var logBlockerFile = Path.Combine(_temp, "log-root-blocker");
        await File.WriteAllTextAsync(logBlockerFile, "not a directory");
        var verboseLog = new VerboseFileLog(Path.Combine(logBlockerFile, "logs"));

        var account = AzuriteAccount();
        var name = RandomName("unreaddownstream-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // 目录 d 下只有这一个小文件（长度 22 字节 < 30 字节阈值）→ 规划阶段单独成一个只有
            // 1 个成员的 pack（GroupingPlanner 按目录分组，目录内哪怕只有一个可分组文件也会
            // 成一个 pack）。故意只放一个文件：如果还有其它"稳定"成员，它们会先被
            // ProcessDirectoryAsync 里 "if (stable.Count > 0)" 分支的 LogFileAsync 撞上同一个坏
            // 日志目录，抢先失败，测试就测不到 foreach(changed) 这条路径了。
            // x.txt 会在首次压缩后被改写成一段超过阈值长度的新内容（模拟"处理中变化"，而非
            // 读不开)。分组重校验发现它变化后排除出归档（此时 stable.Count == 0，不产生任何
            // LogFileAsync 调用），foreach(changed) 见其新长度 ≥ 阈值 → 走"超阈值→单文件"分支，
            // 递归调用 HandleBlobAsync 走单文件上传路径——这正是 Finding 1 命中的那条调用路径
            // （调用方 try 曾经圈住这整个调用，包括调用内部成功上传之后的 LogFileAsync）。
            WriteText("d/x.txt", "original content of x"); // 22 字节，< 30，规划时入 pack

            var compressor = new MutateAfterFirstCompressCompressor(
                new SevenZipCompressor(), _root, "d/x.txt",
                "mutated content of x, now much longer than the 30-byte threshold"); // > 30 字节

            var orchestrator = new BackupOrchestrator(
                new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
                compressor, new BlobUploader(factory), factory, store, staging,
                new RetentionCleaner(factory, store, new RetentionEvaluator()), new FileHasher(),
                notifier: notifier, verifier: new ProcessingVerifier(new FileHasher()), verboseLog: verboseLog);

            var request = Request(account, name, singleFileThresholdBytes: 30) with
            {
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 30 },
                    VerboseLogging = true,
                },
            };

            // 内容已经成功压缩、上传到云端之后，verbose 日志写入才失败——这个失败必须如实抛出来，
            // 绝不能被悄悄吞掉、把 x.txt 误判成"读不开"（那样的话数据在云端却在索引里凭空消失，
            // 而且备份还会"成功"收尾，是比崩溃更糟的静默数据丢失）。DirectoryNotFoundException
            // 是 IOException 的子类，用 ThrowsAnyAsync 兼容 .NET 对 ENOTDIR 的具体映射类型。
            await Assert.ThrowsAnyAsync<IOException>(() => orchestrator.RunAsync(request));

            // 修复前的错误处置会先写一条"读不开"告警再吞掉异常；修复后异常真实向外传播，
            // 不会有任何文件被误判成"读不开"进而通知。
            Assert.DoesNotContain(notifier.Notifications, n => n.Title.Contains("d/x.txt"));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
