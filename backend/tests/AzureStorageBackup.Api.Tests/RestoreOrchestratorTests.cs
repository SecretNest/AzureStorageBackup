using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>同步收集 phase 上报（Progress&lt;T&gt; 会把回调排到同步上下文，测试里读不到）。</summary>
internal sealed class SyncProgress : IProgress<string>
{
    public List<string> Messages { get; } = [];
    public void Report(string value) { lock (Messages) Messages.Add(value); }
}

[Trait("Category", "Integration")]
public sealed class RestoreOrchestratorTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _base;
    private readonly string _src;
    private readonly string _dst;
    private readonly string _temp;

    public RestoreOrchestratorTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "asb-restore-" + Guid.NewGuid().ToString("N"));
        _src = Path.Combine(_base, "src");
        _dst = Path.Combine(_base, "dst");
        _temp = Path.Combine(_base, "temp");
        Directory.CreateDirectory(_src);
        Directory.CreateDirectory(_temp);
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

    private void WriteSrc(string rel, string content)
    {
        var full = Path.Combine(_src, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <param name="restoreCompressor">还原侧注入的压缩器，默认 null 时用真的 <see cref="SevenZipCompressor"/>。
    /// 备份侧固定用真的——这个口子只为了让某些测试在**解压**这一步接一个假的（例如探测
    /// "解压这一刻在途标记是否已经摘掉"），不影响打包过程本身。</param>
    /// <param name="restoreClock">还原侧注入给内部 <see cref="StageTracker"/> 的时间源，见
    /// <see cref="RestoreOrchestrator.Clock"/> 上的注释——只为让节流窗口失效，不影响下载/解压本身。</param>
    private (BackupOrchestrator Backup, RestoreOrchestrator Restore, IBackupInfoStore Store, BlobClientFactory Factory) Build(
        IFileCompressor? restoreCompressor = null, Func<long>? restoreClock = null)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(Path.Combine(_temp, "c"), Path.Combine(_temp, "s"), () => 200_000_000);
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging, new RetentionCleaner(factory, store, new RetentionEvaluator()), new FileHasher());
        var restore = new RestoreOrchestrator(
            factory, store, restoreCompressor ?? new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "restore"))
        { Clock = restoreClock };
        return (backup, restore, store, factory);
    }

    private BackupRequest BackupReq(Account account, string container, string? password = null) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _src,
        Name = "photos",
        Password = password,
        Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 } },
    };

    [SkippableFact]
    public async Task Restore_Rehydrates_Archived_Blob_Then_Restores()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rhyd-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("a.txt", "archived content");
            await backup.RunAsync(BackupReq(account, name) with
            {
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
            });

            // 把 data blob 设为 Archive；若 Azurite 不支持则跳过。
            try
            {
                await foreach (var b in container.GetBlobsAsync(Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", CancellationToken.None))
                    await container.GetBlobClient(b.Name).SetAccessTierAsync(Azure.Storage.Blobs.Models.AccessTier.Archive);
            }
            catch (Azure.RequestFailedException)
            {
                Skip.If(true, "Azurite does not support Archive tier");
            }

            // 还原应自动活化后取回内容（轮询间隔设小）。
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst, RehydratePollSeconds = 1,
            });

            Assert.Equal("archived content", File.ReadAllText(Path.Combine(_dst, "a.txt")));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Restore_Substitutes_Unrecoverable_File_From_Chosen_Version()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rsub-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("a.txt", "version one");
            WriteSrc("keep.txt", "unchanged file");
            await backup.RunAsync(BackupReq(account, name));   // v1
            WriteSrc("a.txt", "version two");
            await backup.RunAsync(BackupReq(account, name));   // v2

            // 把 v2 的 a.txt 标记为不可恢复（模拟修复后无法从本地恢复）。
            var info = await store.ReadInfoAsync(account, name, null);
            var v2 = info!.Versions[^1];
            var idx = await store.ReadIndexAsync(account, name, v2.IndexBlob, null);
            idx.UnrecoverablePaths.Add("a.txt");
            await store.WriteIndexAsync(account, name, v2.Version, idx, null);

            // 不给替代 → a.txt 跳过（其余照常）。
            await restore.RunAsync(new RestoreRequest { Account = account, Container = name, TargetRoot = _dst, Version = 2 });
            Assert.False(File.Exists(Path.Combine(_dst, "a.txt")));
            Assert.True(File.Exists(Path.Combine(_dst, "keep.txt")));

            // 指定用 v1 替代 → a.txt 还原为 v1 内容。
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst, Version = 2,
                Substitutions = new Dictionary<string, int> { ["a.txt"] = 1 },
            });
            Assert.Equal("version one", File.ReadAllText(Path.Combine(_dst, "a.txt")));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Substitution_To_Missing_Version_Skips_Path_Without_Failing_Whole_Restore()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rsubm-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("a.txt", "version one");
            WriteSrc("b.txt", "unchanged file");
            await backup.RunAsync(BackupReq(account, name));   // v1
            WriteSrc("a.txt", "version two");
            await backup.RunAsync(BackupReq(account, name));   // v2

            // 把 v2 的 a.txt 标记为不可恢复（模拟修复后无法从本地恢复）。
            var info = await store.ReadInfoAsync(account, name, null);
            var v2 = info!.Versions[^1];
            var idx = await store.ReadIndexAsync(account, name, v2.IndexBlob, null);
            idx.UnrecoverablePaths.Add("a.txt");
            await store.WriteIndexAsync(account, name, v2.Version, idx, null);

            // 声明替代到一个不存在的版本（如已被保留清理删除）→ 应回落跳过，而不是整体报错。
            var result = await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst, Version = 2,
                Substitutions = new Dictionary<string, int> { ["a.txt"] = 99 }, // 不存在的版本
            });

            Assert.True(result.SkippedFiles >= 1);                    // a.txt 回落跳过
            Assert.False(File.Exists(Path.Combine(_dst, "a.txt")));
            Assert.True(File.Exists(Path.Combine(_dst, "b.txt")));    // b.txt 正常还原
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Encrypted_Keyed_Backup_RoundTrips_Through_Restore()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rste-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("dir/small.txt", "grouped");       // pack 成员
            WriteSrc("big.bin", new string('y', 6_000_000)); // 密钥化寻址的单文件 data blob

            await backup.RunAsync(BackupReq(account, name, password: "pw"));
            var result = await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst, Password = "pw",
            });

            Assert.Equal(2, result.RestoredFiles);
            Assert.Equal("grouped", File.ReadAllText(Path.Combine(_dst, "dir", "small.txt")));
            Assert.Equal(6_000_000, new FileInfo(Path.Combine(_dst, "big.bin")).Length);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [SkippableFact]
    public async Task Staged_Backup_By_Removing_Ignores_Final_Version_Equals_Whole()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rststg-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("a/1.txt", "alpha");
            WriteSrc("b/2.txt", "bravo");
            WriteSrc("c/3.txt", "charlie");

            BackupRequest Req(params string[] ignore) => BackupReq(account, name) with
            {
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
                    Ignore = new IgnoreRuleSet(ignore),
                },
            };

            var v1 = await backup.RunAsync(Req("b/", "c/")); // 阶段1：只 a
            var v2 = await backup.RunAsync(Req("c/"));       // 阶段2：去掉 b → 加 b
            var v3 = await backup.RunAsync(Req());           // 阶段3：去掉 c → 加 c（完整）

            // 各阶段只处理新解禁的文件，旧的结转不重传。
            Assert.Equal(1, v1.ChangedFiles);
            Assert.Equal(1, v2.ChangedFiles); // 只 b/2.txt
            Assert.Equal(1, v3.ChangedFiles); // 只 c/3.txt

            var info = await store.ReadInfoAsync(account, name, null);
            var idx1 = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);
            var idx3 = await store.ReadIndexAsync(account, name, info.Versions[^1].IndexBlob, null);
            Assert.Equal(["a/1.txt"], idx1.Entries.Select(e => e.Path).OrderBy(x => x)); // v1 不全
            Assert.Equal(["a/1.txt", "b/2.txt", "c/3.txt"], idx3.Entries.Select(e => e.Path).OrderBy(x => x)); // v3 完整

            // 还原最终版本 = 全部文件。
            var result = await restore.RunAsync(new RestoreRequest { Account = account, Container = name, TargetRoot = _dst });
            Assert.Equal(3, result.RestoredFiles);
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(_dst, "a", "1.txt")));
            Assert.Equal("bravo", File.ReadAllText(Path.Combine(_dst, "b", "2.txt")));
            Assert.Equal("charlie", File.ReadAllText(Path.Combine(_dst, "c", "3.txt")));
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [SkippableFact]
    public async Task Deduped_Identical_Files_Both_Restore()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        foreach (var storeOnly in new[] { false, true }) // 非 raw(7z) 与 raw 两种单文件 blob
        {
            var (backup, restore, _, factory) = Build();
            var account = AzuriteAccount();
            var name = RandomName("rstdup-");
            var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
            await container.CreateIfNotExistsAsync();
            var dst = Path.Combine(_dst, storeOnly ? "raw" : "z");

            try
            {
                WriteSrc("x.txt", "identical content");
                WriteSrc("y.txt", "identical content"); // 同内容 → 同 hash → 共享一个 data blob（去重）

                await backup.RunAsync(BackupReq(account, name) with
                {
                    Options = new BackupEngineOptions
                    {
                        Plan = new PlanOptions { SingleFileThresholdBytes = 1 }, // 单文件 blob
                        DontCompress = storeOnly ? new IgnoreRuleSet(["*"]) : null,
                    },
                });

                var result = await restore.RunAsync(new RestoreRequest { Account = account, Container = name, TargetRoot = dst });

                Assert.Equal(2, result.RestoredFiles); // 两个引用同一 blob 的文件都还原
                Assert.Equal("identical content", File.ReadAllText(Path.Combine(dst, "x.txt")));
                Assert.Equal("identical content", File.ReadAllText(Path.Combine(dst, "y.txt")));
            }
            finally
            {
                await container.DeleteIfExistsAsync();
            }
        }
    }

    [SkippableFact]
    public async Task Raw_Stored_File_RoundTrips_Through_Restore()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstraw-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("keep.bin", "raw bytes not compressed");
            await backup.RunAsync(BackupReq(account, name) with
            {
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
                    DontCompress = new IgnoreRuleSet(["*"]), // store-only → 原始直传
                },
            });

            var result = await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });

            Assert.Equal(1, result.RestoredFiles);
            Assert.Equal("raw bytes not compressed", File.ReadAllText(Path.Combine(_dst, "keep.bin")));
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [SkippableFact]
    public async Task Restores_Files_And_Empty_Dirs_To_Target()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rst-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("a.txt", "alpha");
            WriteSrc("dir/b.txt", "bravo");
            WriteSrc("big.bin", new string('x', 6_000_000)); // > 5M -> data blob
            Directory.CreateDirectory(Path.Combine(_src, "emptydir"));

            await backup.RunAsync(BackupReq(account, name));

            var result = await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });

            Assert.Equal(1, result.Version);
            Assert.Equal(3, result.RestoredFiles);
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(_dst, "a.txt")));
            Assert.Equal("bravo", File.ReadAllText(Path.Combine(_dst, "dir", "b.txt")));
            Assert.Equal(6_000_000, new FileInfo(Path.Combine(_dst, "big.bin")).Length);
            Assert.True(Directory.Exists(Path.Combine(_dst, "emptydir")));
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [SkippableFact]
    public async Task Volume_Split_Backup_RoundTrips_Through_Restore()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstv-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("big.bin", new string('x', 60_000));
            var req = BackupReq(account, name) with
            {
                Options = new BackupEngineOptions
                {
                    // 阈值调低 → big.bin 走单文件 blob；不压缩 + 20KB 分卷 → 多卷
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1000 },
                    DontCompress = new IgnoreRuleSet(["*.bin"]),
                    VolumeBytes = 20_000,
                },
            };
            await backup.RunAsync(req);

            // 应产出多卷 data blob（data/{hash}.001 存在）
            var volumeBlobs = new List<string>();
            await foreach (var b in container.GetBlobsAsync(
                Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", default))
                volumeBlobs.Add(b.Name);
            Assert.Contains(volumeBlobs, n => n.EndsWith(".001"));

            var result = await restore.RunAsync(new RestoreRequest { Account = account, Container = name, TargetRoot = _dst });

            Assert.Equal(1, result.RestoredFiles);
            Assert.Equal(60_000, new FileInfo(Path.Combine(_dst, "big.bin")).Length);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 端到端兜底：走真实备份/还原全流程产出一个真正的多卷 7z 归档，断言 Restoring 阶段最终
    /// 累计的字节数与云端归档的真实（压缩后）大小一致。
    /// <para>
    /// 这条测不出"工厂被换成共用实例"这个缺陷本身——按 mutation 验证过：本项目 7z 分卷天然是
    /// "除末卷外各卷等大、末卷最小"的大小序列，<c>DeltaProgress</c> 的回退判定（见
    /// <see cref="StageTracker"/>）在这种序列下会自我纠正，共享一个实例照样能凑出与本测试相同的
    /// 正确总数，不会让这条断言变红。真正钉死 Part 1 契约（"每卷各调一次工厂、拿到各不相同的
    /// 实例"）的是 <c>VolumeBlobIOTests.DownloadAsync_Calls_Progress_Factory_Once_Per_Volume_With_A_Fresh_Instance</c>——
    /// 那条直接测 <c>DownloadAsync</c> 本身、不经过分卷大小序列这层间接性。这条测试留着是为了兜住
    /// "整条还原链路的字节账目没被这次改动弄错"这个更朴素的不变量，与 mutation-检测无关。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Multi_Volume_Restore_Sums_Downloaded_Bytes_Without_Compounding()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstb-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // 小分卷逼出一个真正的多卷归档（5+ 卷）——只有卷数足够多，"每卷各要一个进度实例"
            // 与"全程共用一个实例"两条路径的累计结果才会显著分岔。
            WriteSrc("big.bin", new string('x', 100_000));
            var req = BackupReq(account, name) with
            {
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1000 },
                    DontCompress = new IgnoreRuleSet(["*.bin"]),
                    VolumeBytes = 20_000,
                },
            };
            await backup.RunAsync(req);

            // 云端归档的真实大小：下载会实打实传过网线的字节数，即改动之后 Restoring 阶段
            // 应该累计到的数字。
            long archivedBytes = 0;
            var volumeCount = 0;
            await foreach (var b in container.GetBlobsAsync(
                Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", CancellationToken.None))
            {
                archivedBytes += b.Properties.ContentLength ?? 0;
                volumeCount++;
            }
            Assert.True(volumeCount >= 4, "fixture should produce several volumes, or this test doesn't exercise the per-volume factory");

            var reports = new List<StageProgress>();
            var result = await restore.RunAsync(
                new RestoreRequest { Account = account, Container = name, TargetRoot = _dst },
                CancellationToken.None, phase: null, onProgress: p => { lock (reports) reports.Add(p); });

            Assert.Equal(1, result.RestoredFiles);

            var restoring = reports.Where(r => r.Stage == "Restoring").ToList();
            Assert.NotEmpty(restoring);
            Assert.Equal(archivedBytes, restoring[^1].Bytes);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Second_Restore_Skips_Unchanged_Files()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rst2-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("a.txt", "alpha");
            WriteSrc("dir/b.txt", "bravo");
            await backup.RunAsync(BackupReq(account, name));

            var req = new RestoreRequest { Account = account, Container = name, TargetRoot = _dst };
            var first = await restore.RunAsync(req);
            Assert.Equal(2, first.RestoredFiles);

            var second = await restore.RunAsync(req); // 本地已相同 → 全部跳过
            Assert.Equal(0, second.RestoredFiles);
            Assert.Equal(2, second.SkippedFiles);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [SkippableFact]
    public async Task Selective_Restore_Writes_Only_Selected_Pack_Members()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstsel-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // 三个小文件 → 同一个 pack（默认阈值 5M 以下走分组）。
            WriteSrc("dir/a.txt", "alpha");
            WriteSrc("dir/b.txt", "bravo");
            WriteSrc("dir/c.txt", "charlie");
            await backup.RunAsync(BackupReq(account, name));

            // 确认确实打成了一个 pack（一次下载语义的前提）。
            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            var packRefs = idx.Entries.Where(e => e.Storage?.Kind == "pack").Select(e => e.Storage!.Ref).Distinct().ToList();
            Assert.Single(packRefs); // 三个成员同属一个 pack

            // 只选中 pack 内一个成员 → 只该成员落地，其余不 over-restore。
            var result = await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
                SelectedPaths = ["dir/a.txt"],
            });

            Assert.Equal(1, result.RestoredFiles);
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(_dst, "dir", "a.txt")));
            Assert.False(File.Exists(Path.Combine(_dst, "dir", "b.txt"))); // 未选成员不落地
            Assert.False(File.Exists(Path.Combine(_dst, "dir", "c.txt")));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 钉住 RestoreGroupAsync 里那对内层 try/finally：下载一结束就把 <c>EndItem</c> 摘掉，解压/写盘
    /// 这段本地 CPU 工作不该继续算"在途"。这不是不痛不痒的细节——测速分母只认在途窗口，
    /// 解压一个大 pack 能有几十秒，多算进去会把显示的速度腰斩。
    /// <para>
    /// 直接读 <c>onProgress</c> 收到的"最近一次发布"靠不住：发布有 200ms 节流，真实时钟下载一个
    /// 几十字节的测试包全程往往就几十毫秒，下载中 SDK 至少报一次进度（首次调用必发布），
    /// 随后 EndItem/BeginPacking 各自的发布多半被这同一个节流窗口吞掉——于是无论 fix 还是
    /// mutant，观察到的"最近一次"都可能还停在"下载中"的快照上，测试测不出任何东西
    /// （已用 Diagnostic 探针实测验证过这个失效模式）。
    /// </para>
    /// <para>
    /// 用注入的假时钟绕开它：每查一次时间就往前跳一大步，节流条件 <c>now - last &lt; 200ms</c>
    /// 因此永远不成立，每一次状态变化都会被发布——不是赌真实时钟恰好跨过节流窗口，
    /// 是让节流窗口对这个测试彻底失效。不涉及 Thread.Sleep/Task.Delay，下载/解压仍是对
    /// Azurite 的真实调用，只是"现在几点"这一件事被接管了。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Extraction_Starts_After_Item_Is_Removed_From_ActiveItems()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var probe = new ActiveItemsProbeCompressor(new SevenZipCompressor());
        long fakeNow = 0;
        var (backup, restore, _, factory) = Build(probe, restoreClock: () => Interlocked.Add(ref fakeNow, 1000));
        var account = AzuriteAccount();
        var name = RandomName("rstextract-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // 三个小文件同目录 → 单个 pack、单个组：只有一件在途项，断言不必按名字过滤。
            WriteSrc("dir/a.txt", "alpha");
            WriteSrc("dir/b.txt", "bravo");
            WriteSrc("dir/c.txt", "charlie");
            await backup.RunAsync(BackupReq(account, name));

            var result = await restore.RunAsync(
                new RestoreRequest { Account = account, Container = name, TargetRoot = _dst },
                onProgress: d => probe.LatestPublished = d);

            Assert.Equal(3, result.RestoredFiles);
            Assert.True(probe.ExtractCallCount > 0, "fake compressor's ExtractAsync should have been invoked");
            // 解压这一刻，假时钟已经保证了下载结束时那次 EndItem 触发的发布没被节流吞掉——
            // 拿到的就是解压开始那一瞬间真正的在途集合，不是碰运气捞到的一张旧快照。
            Assert.Empty(probe.ActiveItemsAtExtractCall ?? ["<no snapshot captured>"]);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>包一层真压缩器，只在 <see cref="ExtractAsync"/> 这一步截住，记下调用那一刻
    /// 最近一次发布的 <see cref="StageProgress.ActiveItems"/>——解压本身仍然照常委托给内层真的
    /// <see cref="SevenZipCompressor"/> 完成，被测的只是"调用顺序"，不是解压结果。</summary>
    private sealed class ActiveItemsProbeCompressor(IFileCompressor inner) : IFileCompressor
    {
        public StageProgress? LatestPublished { get; set; }
        public IReadOnlyList<string>? ActiveItemsAtExtractCall { get; private set; }
        public int ExtractCallCount { get; private set; }

        public Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken ct = default) =>
            inner.CompressAsync(request, ct);

        public Task ExtractAsync(string firstVolumePath, string outputDir, string? password, CancellationToken ct = default)
        {
            ExtractCallCount++;
            ActiveItemsAtExtractCall = LatestPublished?.ActiveItems;
            return inner.ExtractAsync(firstVolumePath, outputDir, password, ct);
        }

        public Task<CompressionResult> CompressStreamAsync(
            StreamCompressionRequest request, Func<Stream, CancellationToken, Task<long>> writeSource,
            CancellationToken ct = default) => inner.CompressStreamAsync(request, writeSource, ct);

        public Task<IReadOnlyList<ArchiveEntry>> ListEntriesAsync(
            string firstVolumePath, string? password, CancellationToken ct = default) =>
            inner.ListEntriesAsync(firstVolumePath, password, ct);

        public Task<long> ExtractToStreamAsync(
            string firstVolumePath, string? entryName, string? password, Stream destination,
            CancellationToken ct = default) => inner.ExtractToStreamAsync(firstVolumePath, entryName, password, destination, ct);
    }

    [SkippableFact]
    public async Task Conflict_Skip_Leaves_Existing_File_Untouched()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstskip-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("a.txt", "cloud content");
            await backup.RunAsync(BackupReq(account, name));

            // 目标已存在且内容不同 → Skip 模式应原样保留，不覆盖，不新增。
            Directory.CreateDirectory(_dst);
            File.WriteAllText(Path.Combine(_dst, "a.txt"), "local content");

            var result = await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
                Conflict = RestoreConflictMode.Skip,
            });

            Assert.Equal(0, result.RestoredFiles);
            Assert.Equal(1, result.SkippedFiles);
            Assert.Equal("local content", File.ReadAllText(Path.Combine(_dst, "a.txt"))); // 未被覆盖
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Conflict_RenameKeep_Preserves_Existing_And_Writes_Restored()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstrk-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("a.txt", "cloud content");
            await backup.RunAsync(BackupReq(account, name));

            // 目标已存在且内容不同 → RenameKeep：旧内容改名保留，还原内容落原名。
            Directory.CreateDirectory(_dst);
            File.WriteAllText(Path.Combine(_dst, "a.txt"), "local content");

            var result = await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
                Conflict = RestoreConflictMode.RenameKeep,
            });

            Assert.Equal(1, result.RestoredFiles);
            Assert.Equal("cloud content", File.ReadAllText(Path.Combine(_dst, "a.txt"))); // 原名 = 还原内容
            var baks = Directory.GetFiles(_dst, "a.txt.bak-*");
            Assert.Single(baks);
            Assert.Equal("local content", File.ReadAllText(baks[0])); // 旧内容永不丢失
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Restore_Skips_Index_Entry_That_Would_Escape_Target_Root()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstesc-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // 阈值调到 1 字节 → a.txt 走单文件 data blob（而非 pack），
            // 使得还原时内容按 blob 整体复用、不依赖归档内的条目名——
            // 这样恶意条目才会真的把内容写到它自己声明的（越界）路径上，
            // 忠实重现 /import 场景下可信度为零的索引所能触发的写入。
            WriteSrc("a.txt", "safe content");
            await backup.RunAsync(BackupReq(account, name) with
            {
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
            });

            // 模拟一条被篡改/来自不可信容器（/import 可导入任意容器）的索引：追加一条
            // Path 含 .. 的条目，复用 a.txt 的 Storage（同一个下载分组），
            // 验证写入前的越界检查会拦下它，而不是让它落到 TargetRoot 之外。
            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = info!.Versions[^1];
            var idx = await store.ReadIndexAsync(account, name, v1.IndexBlob, null);
            var aEntry = idx.Entries.Single(e => e.Path == "a.txt");
            idx.Entries.Add(aEntry with { Path = "../pwned.txt" });
            await store.WriteIndexAsync(account, name, v1.Version, idx, null);

            var result = await restore.RunAsync(new RestoreRequest { Account = account, Container = name, TargetRoot = _dst });

            Assert.True(File.Exists(Path.Combine(_dst, "a.txt")));       // 正常条目照常还原
            Assert.False(File.Exists(Path.Combine(_base, "pwned.txt"))); // 越界条目未写到目标根之外
            Assert.Equal(1, result.FailedFiles);                         // 计入失败数，其余照常，不中断整次还原
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// C1（Critical）：还原**先**建 symlink 条目、**后**写文件条目。索引里一条
    /// <c>evil -&gt; &lt;根外&gt;</c> 加一条 <c>evil/x</c>，词法判定看 <c>&lt;root&gt;/evil/x</c> 完全在根内，
    /// 而 File.Copy 会跟随那条链接把内容落到根外——没有竞态，纯靠还原自身的顺序。
    /// 判定必须作用在解析后的真实路径上才拦得住。
    /// </summary>
    [SkippableFact]
    public async Task Restore_Does_Not_Write_Through_A_Symlink_Entry_That_Points_Outside_The_Target()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstsym1-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        var outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(outside);

        try
        {
            // 阈值 1 → a.txt 走单文件 data blob，内容按条目声明的路径整体复制，
            // 于是恶意条目真的会把内容写到它声明的位置（忠实重现 /import 场景）。
            WriteSrc("a.txt", "safe content");
            await backup.RunAsync(BackupReq(account, name) with
            {
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
            });

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = info!.Versions[^1];
            var idx = await store.ReadIndexAsync(account, name, v1.IndexBlob, null);
            var aEntry = idx.Entries.Single(e => e.Path == "a.txt");

            // 恶意索引：一条指向根外目录的软链条目 + 一条「在它下面」的文件条目。
            idx.Entries.Add(aEntry with { Path = "evil", Kind = "symlink", Target = outside, Storage = null });
            idx.Entries.Add(aEntry with { Path = "evil/x" });
            await store.WriteIndexAsync(account, name, v1.Version, idx, null);

            var result = await restore.RunAsync(new RestoreRequest { Account = account, Container = name, TargetRoot = _dst });

            // 核心断言：目标根之外不能出现任何东西。
            Assert.False(Path.Exists(Path.Combine(outside, "x")));
            Assert.Empty(Directory.GetFileSystemEntries(outside));

            Assert.Equal("safe content", File.ReadAllText(Path.Combine(_dst, "a.txt"))); // 合法条目照常
            Assert.Equal(1, result.FailedFiles);                                          // 穿链写入被计入失败
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>C6：symlink 条目自身的路径越界（<c>../</c>）必须被拦下，并计入失败而不是静默跳过（C3）。</summary>
    [SkippableFact]
    public async Task Restore_Rejects_Symlink_Entry_Whose_Own_Path_Escapes_The_Target_Root()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstsym2-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        var outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(outside);

        try
        {
            WriteSrc("a.txt", "safe content");
            await backup.RunAsync(BackupReq(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = info!.Versions[^1];
            var idx = await store.ReadIndexAsync(account, name, v1.IndexBlob, null);
            var aEntry = idx.Entries.Single(e => e.Path == "a.txt");
            idx.Entries.Add(aEntry with { Path = "../evil-link", Kind = "symlink", Target = outside, Storage = null });
            await store.WriteIndexAsync(account, name, v1.Version, idx, null);

            var reports = new SyncProgress();
            var result = await restore.RunAsync(
                new RestoreRequest { Account = account, Container = name, TargetRoot = _dst },
                phase: reports);

            Assert.False(Path.Exists(Path.Combine(_base, "evil-link"))); // 根外没建出链接
            Assert.Equal(1, result.FailedFiles);                         // 计入失败，不是跳过
            Assert.True(File.Exists(Path.Combine(_dst, "a.txt")));

            // C3：安全检查被触发必须可见，不能和「未变」一样静默。
            Assert.Contains(reports.Messages, m => m.Contains("../evil-link", StringComparison.Ordinal));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// C6：空目录条目的两条越界路线——词法 <c>../</c>，以及穿过一条**上次还原留下**的指向根外的软链。
    /// 同时钉住 C4：RestoredDirs 报的是真正创建成功的数量。
    /// </summary>
    [SkippableFact]
    public async Task Restore_Skips_Empty_Dir_Entries_That_Would_Escape_Target_Root()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstdir-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        var outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(outside);

        try
        {
            WriteSrc("a.txt", "safe content");
            Directory.CreateDirectory(Path.Combine(_src, "emptydir"));
            await backup.RunAsync(BackupReq(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = info!.Versions[^1];
            var idx = await store.ReadIndexAsync(account, name, v1.IndexBlob, null);
            Assert.Contains("emptydir", idx.EmptyDirs);
            idx.EmptyDirs.Add("../pwned-dir");   // 词法越界
            idx.EmptyDirs.Add("leftover/sub");   // 穿过既有软链越界
            await store.WriteIndexAsync(account, name, v1.Version, idx, null);

            // 真实软链：模拟上一次还原（或用户自己）在根内留下的、指向根外的链接。
            Directory.CreateDirectory(_dst);
            Directory.CreateSymbolicLink(Path.Combine(_dst, "leftover"), outside);

            var result = await restore.RunAsync(new RestoreRequest { Account = account, Container = name, TargetRoot = _dst });

            Assert.False(Directory.Exists(Path.Combine(_base, "pwned-dir")));
            Assert.False(Directory.Exists(Path.Combine(outside, "sub")));
            Assert.Empty(Directory.GetFileSystemEntries(outside));
            Assert.True(Directory.Exists(Path.Combine(_dst, "emptydir")));
            Assert.Equal(1, result.RestoredDirs);  // C4：三条条目只成功创建了一条
            Assert.Equal(2, result.FailedFiles);   // M1：两条越界的空目录条目也要算进失败，不能只走 phase 上报
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>M1：一份只含越界 EmptyDirs 的恶意索引，此前 FailedFiles 冻在 0——唯一信号是 phase 流。
    /// 与 symlink 越界（C3）同一原则：安全检查触发必须计入 FailedFiles，操作者的汇总才是他们真正会看的东西。</summary>
    [SkippableFact]
    public async Task Restore_With_Only_Escaping_Empty_Dirs_Reports_Nonzero_FailedFiles()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstdironly-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("a.txt", "safe content");
            await backup.RunAsync(BackupReq(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = info!.Versions[^1];
            var idx = await store.ReadIndexAsync(account, name, v1.IndexBlob, null);
            idx.EmptyDirs.Clear();               // 只留恶意越界条目，没有任何合法空目录条目
            idx.EmptyDirs.Add("../pwned-dir-only");
            await store.WriteIndexAsync(account, name, v1.Version, idx, null);

            var result = await restore.RunAsync(new RestoreRequest { Account = account, Container = name, TargetRoot = _dst });

            Assert.False(Directory.Exists(Path.Combine(_base, "pwned-dir-only")));
            Assert.Equal(0, result.RestoredDirs);
            Assert.Equal(1, result.FailedFiles); // 唯一条目就是被拦下的越界目录 —— 不能是 0
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// C2：越界条目在「是否需要还原」阶段就被拦下。此前它会先对根外路径做 File.Exists +
    /// 全量 hash（存在性/内容旁道），且根外已有同内容文件时被判成「跳过」——
    /// 于是走不到写入处的检查，既不计失败也不上报，一次被拦下的越界完全不可见。
    /// </summary>
    [SkippableFact]
    public async Task Escaping_Entry_Whose_Out_Of_Root_Twin_Exists_Is_Counted_As_Failed_Not_Skipped()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstorc-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("a.txt", "safe content");
            await backup.RunAsync(BackupReq(account, name) with
            {
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
            });

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = info!.Versions[^1];
            var idx = await store.ReadIndexAsync(account, name, v1.IndexBlob, null);
            var aEntry = idx.Entries.Single(e => e.Path == "a.txt");
            idx.Entries.Add(aEntry with { Path = "../twin.txt" });
            await store.WriteIndexAsync(account, name, v1.Version, idx, null);

            // 根外已存在同内容的「孪生」文件 → 旧代码会判定「无需还原」并计成跳过。
            Directory.CreateDirectory(_base);
            File.WriteAllText(Path.Combine(_base, "twin.txt"), "safe content");

            var result = await restore.RunAsync(new RestoreRequest { Account = account, Container = name, TargetRoot = _dst });

            Assert.Equal(1, result.FailedFiles);
            Assert.Equal(0, result.SkippedFiles);
            Assert.Equal("safe content", File.ReadAllText(Path.Combine(_base, "twin.txt"))); // 根外文件未被动过
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 目标根**自身**经软链到达时还原必须照常工作——判定同时解析根，否则每一条合法条目
    /// 都会被误判成越界。这是把判定改成「解析后路径」的主要回归风险。
    /// </summary>
    [SkippableFact]
    public async Task Restore_Into_A_Target_Root_Reached_Through_A_Symlink_Still_Works()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstlnkroot-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        var real = Path.Combine(_base, "real-dst");
        var link = Path.Combine(_base, "link-dst");
        Directory.CreateDirectory(real);
        Directory.CreateSymbolicLink(link, real); // 真实软链，非 mock

        try
        {
            WriteSrc("a.txt", "alpha");
            WriteSrc("dir/b.txt", "bravo");
            Directory.CreateDirectory(Path.Combine(_src, "emptydir"));
            await backup.RunAsync(BackupReq(account, name));

            var result = await restore.RunAsync(new RestoreRequest { Account = account, Container = name, TargetRoot = link });

            Assert.Equal(0, result.FailedFiles);
            Assert.Equal(2, result.RestoredFiles);
            Assert.Equal(1, result.RestoredDirs);
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(real, "a.txt")));
            Assert.Equal("bravo", File.ReadAllText(Path.Combine(real, "dir", "b.txt")));
            Assert.True(Directory.Exists(Path.Combine(real, "emptydir")));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 合法备份会如实记录指向根外的**绝对**软链，还原它是正确行为（被禁止的只是穿过链接写）。
    /// 重复还原同一条链接必须仍判「未变」，不能因为末段已是那条链接就被判成越界。
    /// </summary>
    [SkippableFact]
    public async Task Symlink_Entry_Targeting_Outside_The_Root_Restores_And_Second_Restore_Is_A_No_Op()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstsym3-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        var outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(outside);

        try
        {
            WriteSrc("a.txt", "safe content");
            await backup.RunAsync(BackupReq(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = info!.Versions[^1];
            var idx = await store.ReadIndexAsync(account, name, v1.IndexBlob, null);
            var aEntry = idx.Entries.Single(e => e.Path == "a.txt");
            idx.Entries.Add(aEntry with { Path = "dir/link", Kind = "symlink", Target = outside, Storage = null });
            await store.WriteIndexAsync(account, name, v1.Version, idx, null);

            var req = new RestoreRequest { Account = account, Container = name, TargetRoot = _dst };

            var first = await restore.RunAsync(req);
            Assert.Equal(0, first.FailedFiles);
            Assert.Equal(outside, new FileInfo(Path.Combine(_dst, "dir", "link")).LinkTarget);

            var second = await restore.RunAsync(req); // 幂等：链接未变 → 跳过，不是越界失败
            Assert.Equal(0, second.FailedFiles);
            Assert.Equal(outside, new FileInfo(Path.Combine(_dst, "dir", "link")).LinkTarget);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// M3：symlink 条目缺 Target（云端索引损坏/被篡改）此前被判成 <c>SymlinkOutcome.Unchanged</c>——
    /// 与「链接已经是对的，无事发生」同一个结果，但畸形条目从没成功还原过，操作者应该看得见、
    /// 应该算进 FailedFiles，不能套上「未变」的名义悄悄计成 Skipped。
    /// </summary>
    [SkippableFact]
    public async Task Restore_Symlink_Entry_With_Missing_Target_Fails_That_Entry_Not_Marked_Unchanged()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstmalsym-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("a.txt", "safe content");
            await backup.RunAsync(BackupReq(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = info!.Versions[^1];
            var idx = await store.ReadIndexAsync(account, name, v1.IndexBlob, null);
            var aEntry = idx.Entries.Single(e => e.Path == "a.txt");
            // 畸形 symlink 条目：Kind=symlink 但 Target 缺失。
            idx.Entries.Add(aEntry with { Path = "malformed-link", Kind = "symlink", Target = null, Storage = null });
            await store.WriteIndexAsync(account, name, v1.Version, idx, null);

            var reports = new SyncProgress();
            var result = await restore.RunAsync(
                new RestoreRequest { Account = account, Container = name, TargetRoot = _dst },
                phase: reports);

            Assert.False(Path.Exists(Path.Combine(_dst, "malformed-link"))); // 没能还原，什么都没建出来
            Assert.Equal(1, result.FailedFiles); // 算失败，不是 SkippedFiles——「未变」语义不适用于「从没成功过」
            Assert.True(File.Exists(Path.Combine(_dst, "a.txt")));
            Assert.Contains(reports.Messages, m => m.Contains("malformed-link", StringComparison.Ordinal));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// M6：索引里两条条目共享同一个 Path（/import 可导入任意容器，索引本身可自相矛盾）。
    /// 此前 <c>index.Entries.ToDictionary(e =&gt; e.Path, ...)</c> 直接抛 <see cref="ArgumentException"/>，
    /// 中止整次还原——包括 keep.txt 这样完全无关、完全正常的条目也一并遭殃。
    /// 决策：两条互相矛盾，无法判断哪条权威，选择两条都不写（不猜 last-wins/first-wins），
    /// 该 Path 只算一次失败，其余条目照常还原。
    /// </summary>
    [SkippableFact]
    public async Task Restore_With_Duplicate_Index_Entry_Path_Fails_That_One_Entry_Not_The_Whole_Run()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstdup2-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("a.txt", "safe content");
            WriteSrc("keep.txt", "unrelated file");
            await backup.RunAsync(BackupReq(account, name) with
            {
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
            });

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = info!.Versions[^1];
            var idx = await store.ReadIndexAsync(account, name, v1.IndexBlob, null);
            var aEntry = idx.Entries.Single(e => e.Path == "a.txt");
            // 重复 Path：索引本身自相矛盾。此前这里的 ToDictionary 直接抛出，整次还原被中止。
            idx.Entries.Add(aEntry with { Path = "dup.txt" });
            idx.Entries.Add(aEntry with { Path = "dup.txt" });
            await store.WriteIndexAsync(account, name, v1.Version, idx, null);

            var result = await restore.RunAsync(new RestoreRequest { Account = account, Container = name, TargetRoot = _dst });

            Assert.False(File.Exists(Path.Combine(_dst, "dup.txt"))); // 两条都不写——无法判断哪条权威
            Assert.True(File.Exists(Path.Combine(_dst, "a.txt")));    // 其余条目未被中止的整次还原拖累
            Assert.True(File.Exists(Path.Combine(_dst, "keep.txt")));
            Assert.Equal(1, result.FailedFiles); // 一个重复 Path 只算一次失败，不是每条重复各算一次
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// C5：畸形条目（Path 为 "" / "." → 目标就是 TargetRoot 本身）只能失败它自己。
    /// 此前文件条目会让**整组**合法条目一起判失败，symlink 条目更是让**整次还原**抛出中止。
    /// </summary>
    [SkippableFact]
    public async Task Degenerate_Entry_Path_Fails_Only_That_Entry()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstdeg-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // 两个小文件同属一个 pack：畸形条目若冒泡到组处理器，会把它们一起判失败。
            WriteSrc("dir/a.txt", "alpha");
            WriteSrc("dir/b.txt", "bravo");
            await backup.RunAsync(BackupReq(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = info!.Versions[^1];
            var idx = await store.ReadIndexAsync(account, name, v1.IndexBlob, null);
            var aEntry = idx.Entries.Single(e => e.Path == "dir/a.txt");
            idx.Entries.Add(aEntry with { Path = "" });                                            // 文件条目 → 目标 = 根（目录）
            idx.Entries.Add(aEntry with { Path = ".", Kind = "symlink", Target = "/tmp", Storage = null }); // symlink 条目 → 抛出
            await store.WriteIndexAsync(account, name, v1.Version, idx, null);

            var result = await restore.RunAsync(new RestoreRequest { Account = account, Container = name, TargetRoot = _dst });

            Assert.Equal(2, result.RestoredFiles); // 合法条目全部还原
            Assert.Equal(2, result.FailedFiles);   // 两条畸形条目各算一条
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(_dst, "dir", "a.txt")));
            Assert.Equal("bravo", File.ReadAllText(Path.Combine(_dst, "dir", "b.txt")));
            Assert.True(Directory.Exists(_dst));   // 根仍是目录，没被覆写
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Restores_Encrypted_Backup_With_Password()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rste-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("secret.txt", "classified");
            await backup.RunAsync(BackupReq(account, name, password: "pw"));

            var result = await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst, Password = "pw",
            });

            Assert.Equal(1, result.RestoredFiles);
            Assert.Equal("classified", File.ReadAllText(Path.Combine(_dst, "secret.txt")));
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    /// <summary>
    /// 覆盖判定要先读一遍目标位置已有的文件，看它是不是已经等于要还原的内容。此前那次读取没有保护，
    /// 而它抛出后会被**整组**的 catch 接住——同一个 pack 里的其它文件因此一个都还原不了。
    /// 一个文件的权限问题不该有这么大的爆炸半径：它自己失败即可，同伴照常落地。
    /// <para>三个小文件同目录 → 同一个 pack。目标位置预先放一个读不开的 b.txt（权限位清零，
    /// 不是替身抛的假异常）。修复前：整组失败，a 和 c 根本不会出现在目标目录里。</para>
    /// </summary>
    [SkippableFact]
    public async Task An_Unreadable_Target_File_Does_Not_Sink_Its_Whole_Restore_Group()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");
        Skip.If(OperatingSystem.IsWindows(), "Relies on Unix permission bits.");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rst-unread-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        var blocked = Path.Combine(_dst, "d", "b.txt");

        try
        {
            WriteSrc("d/a.txt", "alpha");
            WriteSrc("d/b.txt", "bravo");
            WriteSrc("d/c.txt", "charlie");
            await backup.RunAsync(BackupReq(account, name)); // 默认阈值 5MB → 三个小文件同成一个 pack

            // 目标位置已有一个同名文件，且读不开——覆盖判定第一步就撞上它。
            Directory.CreateDirectory(Path.GetDirectoryName(blocked)!);
            await File.WriteAllTextAsync(blocked, "pre-existing and unreadable");
            File.SetUnixFileMode(blocked, UnixFileMode.None);

            var result = await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });

            // 同伴照常落地——修复前这两个文件连碰都碰不到。
            Assert.Equal("alpha", await File.ReadAllTextAsync(Path.Combine(_dst, "d", "a.txt")));
            Assert.Equal("charlie", await File.ReadAllTextAsync(Path.Combine(_dst, "d", "c.txt")));
            Assert.Equal(2, result.RestoredFiles);

            // 读不开的那个自己失败，恰好一个——不是整组三个。
            Assert.Equal(1, result.FailedFiles);
        }
        finally
        {
            try { File.SetUnixFileMode(blocked, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best effort */ }
            await container.DeleteIfExistsAsync();
        }
    }

    /// <summary>还原此前只有一个自由文本的 phase 字段，而它承载的其实是**错误流**
    /// （Failed to restore…／Skipped unsafe…），且是单值覆盖：说不出"还剩多少组"，
    /// 逐文件失败也只剩最后一条。这里验证结构化进度确实报出来了。</summary>
    [SkippableFact]
    public async Task Restore_Reports_Structured_Progress_For_Each_Group()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rst-progress-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // 三个分处不同目录的文件 → 三个 pack → 三组，进度有多步可报。
            WriteSrc("a/1.txt", "alpha");
            WriteSrc("b/2.txt", "bravo");
            WriteSrc("c/3.txt", "charlie");
            await backup.RunAsync(BackupReq(account, name));

            var snapshots = new List<StageProgress>();
            var result = await restore.RunAsync(
                new RestoreRequest { Account = account, Container = name, TargetRoot = _dst },
                onProgress: d => { lock (snapshots) snapshots.Add(d); });

            Assert.Equal(3, result.RestoredFiles);
            Assert.NotEmpty(snapshots);

            // 收尾必须产出终态：所有组都完成，否则进度永远差最后一下。
            var final = snapshots[^1];
            Assert.Equal(final.Total, final.Processed);
            Assert.Equal(100, final.Percent);
            Assert.True(final.Bytes > 0, "restored bytes should accumulate for the speed readout");
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 单文件 blob 直接从归档流到目标，不再落一份临时解压件——于是"写出来的东西对不对"
    /// 必须自己核对：<c>7z x -so</c> 取不到内容时输出为空却**退出码 0**（本项目已经踩过一次），
    /// 光靠"没抛异常"会把一个空文件或别人的内容当成还原成功盖到用户文件上。
    /// 这里把 data blob 换成另一个**合法**归档（内容与长度都不同）：解压这一步会成功，
    /// 只有长度/hash 这道关能拦住它。
    /// </summary>
    [SkippableFact]
    public async Task Streamed_Blob_Whose_Content_Does_Not_Match_The_Index_Fails_That_Entry_And_Leaves_No_Debris()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var good = RandomName("rst-mismatch-");
        var other = RandomName("rst-other-");
        var goodContainer = factory.CreateServiceClient(account).GetBlobContainerClient(good);
        var otherContainer = factory.CreateServiceClient(account).GetBlobContainerClient(other);
        await goodContainer.CreateIfNotExistsAsync();
        await otherContainer.CreateIfNotExistsAsync();

        try
        {
            var blobOnly = BackupReq(account, good) with
            {
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
            };

            WriteSrc("victim.txt", "the content the index describes");
            await backup.RunAsync(blobOnly);

            // 另一次备份产出一个内容不同的合法 7z 单文件归档，拿它顶掉上面那条的 data blob。
            File.Delete(Path.Combine(_src, "victim.txt"));
            WriteSrc("decoy.txt", "a different payload of a different length entirely");
            await backup.RunAsync(blobOnly with { Container = other });

            var decoy = await FirstDataBlobAsync(otherContainer);
            var victim = await FirstDataBlobAsync(goodContainer);
            var bytes = (await otherContainer.GetBlobClient(decoy).DownloadContentAsync()).Value.Content;
            await goodContainer.GetBlobClient(victim).UploadAsync(bytes, overwrite: true);

            var phase = new SyncProgress();
            var result = await restore.RunAsync(
                new RestoreRequest { Account = account, Container = good, TargetRoot = _dst },
                phase: phase);

            Assert.Equal(0, result.RestoredFiles);
            Assert.Equal(1, result.FailedFiles);
            Assert.Contains(phase.Messages, m => m.Contains("victim.txt", StringComparison.Ordinal));

            // 内容对不上就一个字节都不该落到目标上——半截件、临时件都不留。
            Assert.False(File.Exists(Path.Combine(_dst, "victim.txt")));
            Assert.False(File.Exists(Path.Combine(_dst, "victim.txt.asb-part")));
        }
        finally
        {
            await goodContainer.DeleteIfExistsAsync();
            await otherContainer.DeleteIfExistsAsync();
        }
    }

    private static async Task<string> FirstDataBlobAsync(Azure.Storage.Blobs.BlobContainerClient container)
    {
        await foreach (var b in container.GetBlobsAsync(
            Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", CancellationToken.None))
            return b.Name;
        throw new InvalidOperationException("no data blob was produced by the backup");
    }
}
