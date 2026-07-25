using System.Net.Sockets;
using System.Text;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

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

    private (BackupOrchestrator Backup, RestoreOrchestrator Restore, IBackupInfoStore Store, BlobClientFactory Factory) Build()
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(Path.Combine(_temp, "c"), Path.Combine(_temp, "s"), () => 200_000_000);
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging, new RetentionCleaner(factory, store, new RetentionEvaluator()), new FileHasher());
        var restore = new RestoreOrchestrator(
            factory, store, new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "restore"));
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
}
