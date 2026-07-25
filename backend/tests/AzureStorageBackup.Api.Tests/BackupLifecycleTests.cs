using System.Net.Sockets;
using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 备份生命周期端到端链（真实 Azurite + 真实 7-Zip，不 mock）：
/// 全新备份 → 增量（实测去重）→ 死重压实 → 只读检查 → 人为破坏云端 → 从本地修复 → 逐字节还原。
/// 一棵真实文件树按顺序走完整条链，每一阶段断言可观察的业务结果；终点比对还原字节与最初写入完全一致。
/// 加密与不加密各跑一遍——密文入库 + 咽喉解密重构后，加密路径风险最高。
/// </summary>
[Trait("Category", "Integration")]
public sealed class BackupLifecycleTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    /// <summary>源文件的基准 mtime：每次写入递增一分钟，保证「等长改写」也一定被差异检测识别为变更。</summary>
    private static readonly DateTime MtimeBase = new(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _base;
    private readonly string _root;
    private readonly string _temp;
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly RecordingUploader _uploader;
    private int _mtimeSeq;

    public BackupLifecycleTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "asb-life-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_base, "src");
        _temp = Path.Combine(_base, "temp");
        Directory.CreateDirectory(_root);

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _uploader = new RecordingUploader(new BlobUploader(new BlobClientFactory(TestSecrets.Reader)));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
    }

    private static Account AzuriteAccount() => new()
    {
        Id = 1,
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

    /// <summary>记录每一次 data/pack 对象上传的 blob 名——增量阶段据此实测「未改动文件没有重传」。</summary>
    private sealed class RecordingUploader(IBlobUploader inner) : IBlobUploader
    {
        private readonly List<string> _names = [];

        public IReadOnlyList<string> Uploads { get { lock (_names) return [.. _names]; } }

        public void Reset() { lock (_names) _names.Clear(); }

        private void Note(string blobName) { lock (_names) _names.Add(blobName); }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            Note(blobName);
            return inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            Note(blobName);
            return inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }
    }

    private sealed record Rig(
        BackupOrchestrator Backup,
        BackupChecker Checker,
        BackupRepairer Repairer,
        RestoreOrchestrator Restore,
        IBackupInfoStore Store,
        BlobClientFactory Factory);

    /// <summary>按生产接线组装整条链：本地权威状态机（TrackedInfoStore + LocalIndexCache）贯穿备份/清理/压实/修复。</summary>
    private Rig Build()
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var hasher = new FileHasher();
        var tracked = new TrackedInfoStore(store, new LocalBackupStateStore(_db));
        var indexCache = new LocalIndexCache(_db, store);
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var compactor = new DeadWeightCompactor(
            _uploader, new SevenZipCompressor(), hasher, Path.Combine(_temp, "compact"));
        var cleaner = new RetentionCleaner(
            factory, store, new RetentionEvaluator(), compactor, indexCache, tracked);
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(hasher), new GroupingPlanner(),
            new SevenZipCompressor(), _uploader, factory, store, staging, cleaner, hasher,
            indexCache: indexCache, trackedInfo: tracked);
        var checker = new BackupChecker(
            factory, store, new SevenZipCompressor(), hasher, Path.Combine(_temp, "check"), trackedInfo: tracked);
        var repairer = new BackupRepairer(
            factory, store, new SevenZipCompressor(), hasher, _uploader, Path.Combine(_temp, "repair"),
            checker: checker, trackedInfo: tracked, indexCache: indexCache);
        var restore = new RestoreOrchestrator(
            factory, store, new SevenZipCompressor(), hasher, Path.Combine(_temp, "restore"));
        return new Rig(backup, checker, repairer, restore, store, factory);
    }

    private BackupRequest Request(Account account, string container, string? password, int maxVersions) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _root,
        Name = "lifecycle",
        Description = "end-to-end lifecycle fixture",
        Password = password,
        Options = new BackupEngineOptions
        {
            // 20K 阈值：docs/ 的小文件成组进 pack，media/ 的大文件走单文件 data blob——两条存储路径都覆盖。
            Plan = new PlanOptions { SingleFileThresholdBytes = 20_000 },
            Retention = new RetentionPolicy { Mode = RetentionMode.VersionOnly, MaxVersions = maxVersions },
        },
    };

    // ───────────────────────── 源树与快照 ─────────────────────────

    /// <summary>写文件并赋予唯一递增的 mtime。等长改写若不动 mtime 会被差异检测判为未变，故必须显式推进。</summary>
    private void Write(string rel, byte[] content)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        File.SetLastWriteTimeUtc(full, MtimeBase.AddMinutes(++_mtimeSeq));
    }

    private void WriteText(string rel, string text) => Write(rel, Encoding.UTF8.GetBytes(text));

    /// <summary>确定性的不可压缩内容——使 pack/blob 体积与成员数成正比，压实的体积回收才可观测。</summary>
    private static byte[] Rand(int size, int seed)
    {
        var buf = new byte[size];
        new Random(seed).NextBytes(buf);
        return buf;
    }

    /// <summary>当前源树快照：相对路径 → (内容字节, mtime)。还原后据此逐字节比对。</summary>
    private Dictionary<string, (byte[] Bytes, DateTime Mtime)> Snapshot()
    {
        var map = new Dictionary<string, (byte[], DateTime)>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            map[Rel(_root, f)] = (File.ReadAllBytes(f), File.GetLastWriteTimeUtc(f));
        return map;
    }

    private static string Rel(string root, string full) =>
        Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>还原树必须与快照完全一致：文件集合相同、每个文件逐字节相同、mtime 由索引元数据复原。</summary>
    private static void AssertTreeEquals(
        Dictionary<string, (byte[] Bytes, DateTime Mtime)> expected, string target, string label)
    {
        var actual = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Rel(target, f), StringComparer.Ordinal);

        // 目录结构一致：多一个文件或少一个文件都失败。
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());

        foreach (var (rel, exp) in expected)
        {
            var got = File.ReadAllBytes(actual[rel]);
            Assert.True(exp.Bytes.AsSpan().SequenceEqual(got),
                $"{label}: restored content differs for {rel} ({exp.Bytes.Length} vs {got.Length} bytes)");
            Assert.Equal(exp.Mtime, File.GetLastWriteTimeUtc(actual[rel]));
        }
    }

    /// <summary>还原某版本到全新空目录并全量比对。FailedFiles 必须为 0——组下载/解压失败只计数不抛异常。</summary>
    private async Task<string> RestoreAndAssertAsync(
        Rig rig, Account account, string container, string? password, int version,
        Dictionary<string, (byte[] Bytes, DateTime Mtime)> expected, string label)
    {
        var target = Path.Combine(_base, "restore", label);
        Directory.CreateDirectory(target);

        var result = await rig.Restore.RunAsync(new RestoreRequest
        {
            Account = account,
            Container = container,
            TargetRoot = target,
            Password = password,
            Version = version,
        });

        Assert.Equal(version, result.Version);
        Assert.Equal(0, result.FailedFiles);   // 失败被吞成计数，不断言就等于没断言
        Assert.Equal(0, result.SkippedFiles);  // 目标是空目录，不该有跳过
        Assert.Equal(expected.Count, result.RestoredFiles);
        AssertTreeEquals(expected, target, label);
        return target;
    }

    // ───────────────────────── 云端探针与破坏手段 ─────────────────────────

    private static StorageRef StorageOf(VersionIndex index, string path) =>
        index.Entries.Single(e => e.Path == path).Storage
        ?? throw new InvalidOperationException($"{path} has no storage ref");

    private static string BlobNameOf(StorageRef s) => s.Kind == "pack" ? $"packs/{s.Ref}.7z" : s.Ref;

    private static async Task AssertReferencedBlobsExistAsync(BlobContainerClient cc, VersionIndex index)
    {
        foreach (var e in index.Entries)
            Assert.True(
                await VolumeBlobIO.ExistsAsync(cc, BlobNameOf(e.Storage!), CancellationToken.None),
                $"missing blob {BlobNameOf(e.Storage!)} for {e.Path}");
    }

    /// <summary>容器全量快照（名字 → 长度 + ETag）：用于断言「不带修复的检查是只读的」。</summary>
    private static async Task<Dictionary<string, (long Length, string ETag)>> BlobFingerprintAsync(BlobContainerClient cc)
    {
        var map = new Dictionary<string, (long, string)>(StringComparer.Ordinal);
        await foreach (var b in cc.GetBlobsAsync())
            map[b.Name] = (b.Properties.ContentLength ?? -1, b.Properties.ETag?.ToString() ?? "");
        return map;
    }

    /// <summary>破坏手段一：整块删除某归档的全部分卷（模拟对象被误删/生命周期策略清掉）。</summary>
    private static async Task DeleteArchiveAsync(BlobContainerClient cc, string baseRef)
    {
        var deleted = 0;
        await foreach (var b in cc.GetBlobsAsync(BlobTraits.None, BlobStates.None, baseRef, CancellationToken.None))
        {
            if (!VolumeBlobIO.IsVolumeOf(baseRef, b.Name))
                continue;
            await cc.GetBlobClient(b.Name).DeleteIfExistsAsync();
            deleted++;
        }
        Assert.True(deleted > 0, $"nothing deleted for {baseRef} — the damage step itself is broken");
    }

    /// <summary>破坏手段二：**等长**改写内容（位腐）。体积不变故 HEAD 级检查看不见，只有下载重算 hash 才能发现。</summary>
    private static async Task CorruptInPlaceAsync(BlobContainerClient cc, string blobName)
    {
        var blob = cc.GetBlobClient(blobName);
        var props = (await blob.GetPropertiesAsync()).Value;
        var junk = Rand((int)props.ContentLength, 9_001);
        await blob.UploadAsync(
            BinaryData.FromBytes(junk),
            new BlobUploadOptions { Metadata = new Dictionary<string, string>(props.Metadata) });

        var after = (await blob.GetPropertiesAsync()).Value.ContentLength;
        Assert.Equal(props.ContentLength, after); // 破坏必须不改变体积，否则这一步没有区分度
    }

    /// <summary>断言某归档确实是加密归档：不给密码解不开、给密码能解开。</summary>
    private async Task AssertArchiveIsEncryptedAsync(BlobContainerClient cc, string baseRef, string password)
    {
        var dir = Path.Combine(_temp, "encprobe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var first = await VolumeBlobIO.DownloadAsync(cc, baseRef, dir, CancellationToken.None);
        var codec = new SevenZipCompressor();

        await Assert.ThrowsAnyAsync<Exception>(
            () => codec.ExtractAsync(first, Path.Combine(dir, "nopw"), null));
        await codec.ExtractAsync(first, Path.Combine(dir, "withpw"), password);
    }

    // ───────────────────────── 生命周期主链 ─────────────────────────

    [SkippableTheory]
    [InlineData("correct horse battery staple")] // 加密：本轮改动风险最高的路径，必须走完整条链
    [InlineData(null)]                           // 不加密
    public async Task Full_Lifecycle_From_First_Backup_Through_Compaction_Damage_Repair_And_Restore(string? password)
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var rig = Build();
        var account = AzuriteAccount();
        var name = RandomName(password is null ? "lifeplain-" : "lifeenc-");
        var cc = rig.Factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            // ═══ 阶段 1：全新备份 ═══
            Write("docs/a.txt", Rand(4000, 11));   // 同目录 5 个小文件 → 合成一个 pack
            Write("docs/b.txt", Rand(4000, 12));
            Write("docs/c.txt", Rand(4000, 13));
            Write("docs/d.txt", Rand(4000, 14));
            Write("docs/e.txt", Rand(4000, 15));
            Write("media/photo.bin", Rand(40_000, 21)); // ≥20K 阈值 → 单文件 data blob
            Write("media/clip.bin", Rand(30_000, 22));
            WriteText("notes/deep/readme.txt", "nested note, first revision");
            WriteText("top.txt", "root level file, first revision");
            Directory.CreateDirectory(Path.Combine(_root, "empty")); // 空目录须入索引并在还原时重建

            var snap1 = Snapshot();
            var r1 = await rig.Backup.RunAsync(Request(account, name, password, maxVersions: 2));

            Assert.Equal(1, r1.Version);
            Assert.Equal(snap1.Count, r1.ChangedFiles); // 首次备份全部文件皆为变更

            var info1 = await rig.Store.ReadInfoAsync(account, name, password);
            Assert.NotNull(info1);
            Assert.Equal(password is not null, info1!.Backup.Encrypted);
            Assert.Single(info1.Versions);
            // 加密/非加密走不同的信息文件 blob 名。
            Assert.True(await cc.GetBlobClient(password is null
                ? BackupDiscovery.IndexBlobName
                : BackupDiscovery.EncryptedIndexBlobName).ExistsAsync());

            var idx1 = await rig.Store.ReadIndexAsync(account, name, info1.Versions[0].IndexBlob, password);
            Assert.Equal(snap1.Count, idx1.Entries.Count);                             // 条目数 == 本地文件数
            Assert.Equal(snap1.Keys.Order(), idx1.Entries.Select(e => e.Path).Order()); // 且逐一对应
            Assert.Contains("empty", idx1.EmptyDirs);
            await AssertReferencedBlobsExistAsync(cc, idx1);

            var docsPack = StorageOf(idx1, "docs/a.txt");
            Assert.Equal("pack", docsPack.Kind);
            foreach (var p in new[] { "docs/b.txt", "docs/c.txt", "docs/d.txt", "docs/e.txt" })
                Assert.Equal(docsPack.Ref, StorageOf(idx1, p).Ref); // 5 个成员同属一个 pack
            var clip1 = StorageOf(idx1, "media/clip.bin");
            Assert.Equal("blob", clip1.Kind);

            // 全新备份写入的碰撞检测元数据基线（len/head/tail，或加密时的不透明 v）——修复后须与此完全一致。
            var clipMetaBaseline = (await cc.GetBlobClient(clip1.Ref).GetPropertiesAsync()).Value.Metadata;
            Assert.NotEmpty(clipMetaBaseline);

            // 加密备份的数据对象是密钥化地址，明文 data/{fullHash} 不得存在（防指纹识别）。
            if (password is not null)
            {
                var clipHash = idx1.Entries.Single(e => e.Path == "media/clip.bin").FullHash!;
                Assert.DoesNotContain(clipHash, clip1.Ref);
                Assert.False(await cc.GetBlobClient($"data/{clipHash}").ExistsAsync());
            }

            // ═══ 阶段 2：增量备份（改 3 个、增 1 个、删 1 棵子树）═══
            _uploader.Reset();
            Write("docs/a.txt", Rand(4000, 111)); // 等长不同内容
            Write("docs/b.txt", Rand(4000, 112));
            Write("docs/c.txt", Rand(4000, 113));
            Write("media/photo.bin", Rand(40_000, 121));
            Write("media/copy.bin", snap1["media/clip.bin"].Bytes); // 新增，内容与 clip.bin 完全相同
            Directory.Delete(Path.Combine(_root, "notes"), recursive: true);

            var snap2 = Snapshot();
            var r2 = await rig.Backup.RunAsync(Request(account, name, password, maxVersions: 2));

            Assert.Equal(2, r2.Version);
            Assert.Equal(5, r2.ChangedFiles); // a/b/c/photo 改 + copy 增；删除不计入变更

            var info2 = await rig.Store.ReadInfoAsync(account, name, password);
            Assert.Equal([1, 2], info2!.Versions.Select(v => v.Version));
            var idx2 = await rig.Store.ReadIndexAsync(account, name, info2.Versions[^1].IndexBlob, password);
            Assert.Equal(snap2.Keys.Order(), idx2.Entries.Select(e => e.Path).Order()); // 删掉的文件已不在索引
            await AssertReferencedBlobsExistAsync(cc, idx2);

            // 去重实测（增量的核心价值）：未改动文件仍指向 v1 的同一存储对象……
            foreach (var p in new[] { "docs/d.txt", "docs/e.txt" })
                Assert.Equal(docsPack.Ref, StorageOf(idx2, p).Ref);
            Assert.Equal(clip1.Ref, StorageOf(idx2, "media/clip.bin").Ref);
            // ……新增的同内容文件也命中既有对象（跨版本内容寻址去重）……
            Assert.Equal(clip1.Ref, StorageOf(idx2, "media/copy.bin").Ref);

            // ……而且本轮上传的对象**恰好只有**变更内容产生的新对象：任何一次多余重传都会让这条断言失败。
            var docsPack2 = StorageOf(idx2, "docs/a.txt");
            var photo2 = StorageOf(idx2, "media/photo.bin");
            Assert.NotEqual(docsPack.Ref, docsPack2.Ref);
            Assert.Equal(
                new[] { $"packs/{docsPack2.Ref}.7z", photo2.Ref }.Order(),
                _uploader.Uploads.Order());

            // ═══ 阶段 6a：还原版本 1 与版本 2 ═══
            // 版本 1 稍后会被保留策略退役，故在此先验证它可逐字节还原。
            var v1Dir = await RestoreAndAssertAsync(rig, account, name, password, 1, snap1, "v1");
            Assert.True(Directory.Exists(Path.Combine(v1Dir, "empty")), "empty directory was not recreated");
            await RestoreAndAssertAsync(rig, account, name, password, 2, snap2, "v2");

            // ═══ 阶段 3：死重压实 ═══
            // docsPack 有 5 个成员；a/b/c 自 v2 起改到新 pack，d 在 v3 再改。v1 退役后 docsPack 仅剩
            // d（v2 引用）与 e（v2/v3 引用）有效 → 死重 3/5 = 60% > 30% 阈值 → 原地重压。
            // 注意 d 的本地文件此时已是 v3 内容，与 pack 内的 v1 内容不符 → 压实必须下载旧 pack 解压补齐
            // （加密备份即在此走「下载 + 用密码解压」路径）。
            var packBlob = $"packs/{docsPack.Ref}.7z";
            var packSizeBefore = (await cc.GetBlobClient(packBlob).GetPropertiesAsync()).Value.ContentLength;

            Write("docs/d.txt", Rand(4000, 114));
            WriteText("top.txt", "root level file, second revision");
            var snap3 = Snapshot();
            var r3 = await rig.Backup.RunAsync(Request(account, name, password, maxVersions: 2));
            Assert.Equal(3, r3.Version);

            var info3 = await rig.Store.ReadInfoAsync(account, name, password);
            Assert.Equal([2, 3], info3!.Versions.Select(v => v.Version)); // v1 已退役

            var compacted = info3.Packs[docsPack.Ref];
            Assert.Equal(2, compacted.Members.Count);   // 死重成员 a/b/c 被丢弃，只留 d、e
            Assert.Equal(0, compacted.DeadBytes);
            Assert.Equal(8000L, compacted.OriginalBytes);

            var packSizeAfter = (await cc.GetBlobClient(packBlob).GetPropertiesAsync()).Value.ContentLength;
            Assert.True(packSizeAfter < packSizeBefore,
                $"dead weight was not physically reclaimed: {packSizeBefore} → {packSizeAfter} bytes");
            Assert.Equal(packSizeAfter, compacted.VolumeSizes[0]); // 信息文件记录的尺寸与云端实际一致

            // 压实最危险的失败模式是回收掉**仍被引用**的数据：把两个保留版本整棵还原并逐字节比对。
            await RestoreAndAssertAsync(rig, account, name, password, 2, snap2, "v2-after-compaction");
            await RestoreAndAssertAsync(rig, account, name, password, 3, snap3, "v3-after-compaction");

            // ═══ 阶段 4：检查（不带修复）═══
            var deep = new CheckOptions
            {
                Cloud = CloudCheckLevel.Content,
                Local = LocalCheckLevel.Content,
                ListOrphans = true,
            };

            var healthy = await rig.Checker.CheckAsync(account, name, password, null, deep, _root);
            Assert.True(healthy.Ok);
            Assert.Null(healthy.MetadataIssue);
            Assert.Equal(snap3.Count, healthy.Findings.Count);
            Assert.All(healthy.Findings, f => Assert.Equal(CloudState.Ok, f.Cloud));
            Assert.All(healthy.Findings, f => Assert.Equal(LocalState.Ok, f.Local)); // 本地树与 v3 完全一致
            Assert.Empty(healthy.OrphanBlobs);                                        // 退役 + 压实后无残留

            // 人为破坏云端：① 整块删除 clip 的 data blob；② 等长改写 docs/a·b·c 所在的 pack。
            var idx3 = await rig.Store.ReadIndexAsync(account, name, info3.Versions[^1].IndexBlob, password);
            var clip3 = StorageOf(idx3, "media/clip.bin");
            var abcPack = StorageOf(idx3, "docs/a.txt");
            Assert.Equal(docsPack2.Ref, abcPack.Ref); // v3 未改 a/b/c，仍沿用 v2 的 pack
            Assert.Equal(clip1.Ref, clip3.Ref); // media/clip.bin 内容全程未变，地址稳定，可拿 v1 元数据基线做比对

            await DeleteArchiveAsync(cc, clip3.Ref);
            await CorruptInPlaceAsync(cc, $"packs/{abcPack.Ref}.7z");

            var fingerprintBefore = await BlobFingerprintAsync(cc);

            // 「存在+尺寸」级只看得见被删的那个：等长位腐体积未变，这一级按设计发现不了。
            var shallow = await rig.Checker.CheckAsync(account, name, password, null,
                new CheckOptions { Cloud = CloudCheckLevel.ExistenceSize, Local = LocalCheckLevel.Content }, _root);
            Assert.False(shallow.Ok);
            Assert.Equal(
                new[] { "media/clip.bin", "media/copy.bin" }.Order(),
                shallow.CorruptedPaths.Order()); // 共享同一 data blob 的两条路径都被如实报告

            // 「内容」级下载解压重算 hash，把等长位腐也揪出来。
            var damaged = await rig.Checker.CheckAsync(account, name, password, null, deep, _root);
            Assert.False(damaged.Ok);
            Assert.Equal(
                new[] { "docs/a.txt", "docs/b.txt", "docs/c.txt", "media/clip.bin", "media/copy.bin" }.Order(),
                damaged.CorruptedPaths.Order());
            // 本地源文件都还在且内容一致 → 全部可从本地修复。
            Assert.Equal(damaged.CorruptedPaths.Order(), damaged.RepairablePaths.Order());

            // 不带修复的检查必须是**只读**的：两轮检查（含下载解压）后，云端每个 blob 的长度与 ETag 都没变。
            Assert.Equal(fingerprintBefore, await BlobFingerprintAsync(cc));

            // ═══ 阶段 5：检查 + 修复 ═══
            var repair = await rig.Repairer.RepairAsync(
                account, name, password, _root, version: null,
                deep with { ListOrphans = true }, AccessTier.Hot, volumeBytes: null);

            Assert.Equal(damaged.CorruptedPaths.Order(), repair.Repaired.Order());
            Assert.Empty(repair.Unrecoverable);
            Assert.Empty(repair.DeletedOrphans); // 修复只替换内容，不该造出或回收孤儿

            // 修复重造的碰撞检测元数据必须与全新备份逐键相等——不是「存在就行」，值不同会让去重误判碰撞
            // （defect 2：以前修复会整个丢掉 len/head/tail，静默关闭碰撞防护）。
            var clipMetaAfterRepair = (await cc.GetBlobClient(clip3.Ref).GetPropertiesAsync()).Value.Metadata;
            Assert.Equal(
                clipMetaBaseline.OrderBy(kv => kv.Key, StringComparer.Ordinal),
                clipMetaAfterRepair.OrderBy(kv => kv.Key, StringComparer.Ordinal));

            var afterRepair = await rig.Checker.CheckAsync(account, name, password, null, deep, _root);
            Assert.True(afterRepair.Ok);
            Assert.All(afterRepair.Findings, f => Assert.Equal(CloudState.Ok, f.Cloud));
            Assert.Empty(afterRepair.OrphanBlobs);

            // ═══ 阶段 6b：还原（受损版本与共享对象的旧版本）═══
            // 「修好了」的判据不是文件存在，而是内容逐字节等于当初写入的内容。
            await RestoreAndAssertAsync(rig, account, name, password, 3, snap3, "v3-after-repair");
            await RestoreAndAssertAsync(rig, account, name, password, 2, snap2, "v2-after-repair");
        }
        finally
        {
            await cc.DeleteIfExistsAsync();
        }
    }

    /// <summary>
    /// 加密备份的机密性必须跨越修复：从本地重造并替换一个单文件 data blob 之后，
    /// 云端对象仍须是**加密**归档。
    /// <para>
    /// 回归背景：<see cref="BackupRepairer"/> 的 <c>ReplaceBlobAsync</c> 曾把
    /// <c>CompressionRequest.Password</c> 硬编码为 <c>null</c>（同类的 <c>RepairPackAsync</c>
    /// 却正确传了密码），于是加密备份一经修复，该 data blob 就以明文 7z 落到云端。
    /// 该缺陷**功能上毫无症状**——7z 对未加密归档忽略 <c>-p</c>，检查与还原照样通过——
    /// 所以只能在存储层探测，不能靠「还原得出来」来判定。本用例即为此守护。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Repair_Of_Encrypted_Backup_Keeps_The_Data_Blob_Encrypted()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        const string password = "correct horse battery staple";
        var rig = Build();
        var account = AzuriteAccount();
        var name = RandomName("liferepenc-");
        var cc = rig.Factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            Write("media/clip.bin", Rand(30_000, 22)); // ≥20K → 单文件 data blob
            await rig.Backup.RunAsync(Request(account, name, password, maxVersions: 5));

            var info = await rig.Store.ReadInfoAsync(account, name, password);
            var idx = await rig.Store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, password);
            var clip = StorageOf(idx, "media/clip.bin");

            // 基线：备份路径写出的对象确实是加密归档（同时证明这个探针有区分度）。
            await AssertArchiveIsEncryptedAsync(cc, clip.Ref, password);

            // 云端对象丢失 → 从仍在的本地源文件修复。
            await DeleteArchiveAsync(cc, clip.Ref);
            var repair = await rig.Repairer.RepairAsync(
                account, name, password, _root, version: null,
                new CheckOptions { Cloud = CloudCheckLevel.ExistenceSize }, AccessTier.Hot, volumeBytes: null);
            Assert.Equal(["media/clip.bin"], repair.Repaired);

            // 修复写回的对象仍须加密。
            await AssertArchiveIsEncryptedAsync(cc, clip.Ref, password);
        }
        finally
        {
            await cc.DeleteIfExistsAsync();
        }
    }
}
