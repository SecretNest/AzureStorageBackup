using System.Net.Sockets;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

[Trait("Category", "Integration")]
public sealed class BackupResumeTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;
    private readonly BackupJournalStore _journals;

    public BackupResumeTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-resume-" + Guid.NewGuid().ToString("N"));
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
        Id = 44,
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
        var bytes = new byte[size];
        // 每个文件的内容必须互不相同，否则三个文件会去重成一个 blob，上传次数就说明不了问题。
        for (var i = 0; i < bytes.Length; i += 4096) bytes[i] = (byte)rel.Length;
        File.WriteAllBytes(full, bytes);
    }

    /// <summary>写不可压缩的内容（定种子，可复现）。装箱那条路要的是"每个成员各不相同"，
    /// 全零的小文件会被本地去重收敛成同一份，箱与箱之间就分不出上传次数了。</summary>
    private void WriteIncompressible(string rel, int size, int seed)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        File.WriteAllBytes(full, bytes);
    }

    /// <summary>数一数真正发起了多少次内容上传，顺带支持"第 N 次之后叫停"。
    /// <para>
    /// 两个 UploadIfMissing 重载都要接、都要过计数器：带 progress 的那个在接口上**有默认实现**，
    /// 而备份主路径（VolumeUploadScope 一直在场）走的恰恰是它。只接不带 progress 的那个，
    /// 这个替身一次都拦不到备份的上传，<see cref="Uploads"/> 会恒等于 0——断言就成了空话。
    /// </para></summary>
    private sealed class CountingUploader(IBlobUploader inner, int stopAt = 0, Func<StopKind>? stop = null)
        : IBlobUploader
    {
        private int _count;

        public int Uploads => Volatile.Read(ref _count);

        private async Task<T> RunAsync<T>(Func<Task<T>> call)
        {
            var n = Interlocked.Increment(ref _count);
            var result = await call();
            if (stopAt > 0 && n == stopAt) stop!();
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
                await inner.UploadOverwriteAsync(
                    account, container, blobName, filePath, tier, retry, ct, metadata);
                return true;
            });
    }

    private (BackupOrchestrator Orchestrator, BackupInfoStore Store, BlobClientFactory Factory) Build(
        IBlobUploader uploader)
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
        return (orchestrator, store, factory);
    }

    private BackupRequest Request(
        Account account, string container, string? password = null, long? volumeBytes = null) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _root,
        Name = "photos",
        Password = password,
        Options = new BackupEngineOptions
        {
            // 上传额度 1＝任一时刻只有一卷在传，所以"第 1 次上传之后叫停"这个**下达时刻**是准的。
            // 但它并不保证停下来时只做完了一件：编排器起的是 Math.Max(2, UploadConcurrency + 1) 个
            // 工作者，第二件完全可能已经在半路上（详见下面用例里那段说明）。
            UploadConcurrency = 1,
            VolumeBytes = volumeBytes,
            Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
        },
    };

    /// <summary>某个 ref 名下现存的分卷：名字 + ETag。重传过一遍的话两样都会变。</summary>
    private static async Task<List<(string Name, string ETag)>> VolumesOfAsync(
        BlobContainerClient container, string blobRef)
    {
        var list = new List<(string Name, string ETag)>();
        await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, blobRef, default))
            if (VolumeBlobIO.IsVolumeOf(blobRef, b.Name))
                list.Add((b.Name, b.Properties.ETag?.ToString() ?? ""));
        list.Sort((x, y) => string.CompareOrdinal(x.Name, y.Name));
        return list;
    }

    [SkippableFact]
    public async Task Second_run_reuses_what_the_suspended_run_already_uploaded()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("resume");
        var factory0 = new BlobClientFactory(TestSecrets.Reader);
        var container = factory0.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteBytes("a.bin", 6_000_000);
            WriteBytes("b.bin", 6_000_001);
            WriteBytes("c.bin", 6_000_002);

            // 第一轮：传完一个就挂起。
            BackupRunControl? first = null;
            var stopping = new CountingUploader(
                new BlobUploader(factory0), stopAt: 1,
                stop: () => { first!.RequestStop(StopKind.Suspend); return StopKind.Suspend; });
            await using (var c = new BackupRunControl(_journals, 9, "run-a"))
            {
                first = c;
                var (o1, _, _) = Build(stopping);
                await Assert.ThrowsAsync<BackupSuspendedException>(
                    () => o1.RunAsync(Request(account, name), null, default, c));
            }
            // 挂起时到底做完了几件，不由这条用例说了算：编排器起的是 UploadConcurrency + 1 个
            // 工作者（最少 2 个），停止意愿落下的那一刻，第二件完全可能已经在半路上。所以不写死
            // 数字——做完了几件，第二轮就该正好少传几件，这才是这条用例真正要钉的东西。
            var done = (await _journals.ListAsync(account.Id, name, default))[0].Content.Records;
            Assert.NotEmpty(done);
            Assert.True(done.Count < 3, $"the first run was supposed to be interrupted, it did all {done.Count}");

            // 第二轮：同一个配置、同样的钥匙和根目录 → 采纳旧卷，只补剩下的。
            var resuming = new CountingUploader(new BlobUploader(factory0));
            var (o2, store2, _) = Build(resuming);
            await using (var c2 = new BackupRunControl(_journals, 9, "run-b"))
            {
                var result = await o2.RunAsync(Request(account, name), null, default, c2);
                Assert.Equal(1, result.Version);
            }
            Assert.Equal(3 - done.Count, resuming.Uploads);   // 复用来的那些一个字节都没重传

            // 索引三条齐全，且复用来的那几条指的正是上一轮传上去的那个 blob。
            var info = await store2.ReadInfoAsync(account, name, null, default);
            var index = await store2.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null, default);
            Assert.Equal(3, index.Entries.Count(e => e.Storage is not null));
            foreach (var r in done)
                Assert.Equal(r.Ref, index.Entries.Single(e => e.Path == r.Path).Storage!.Ref);

            // journal 全都功成身退了——自己那卷和采纳来的那卷一起。
            Assert.Empty(await _journals.ListAsync(account.Id, name, default));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// pack 那条路——恢复里最容易出错的一段，上面两条一个字都没碰到它。
    /// <para>
    /// 命中的一箱仍然要走 <c>RecordPackAsync</c>（只是不上传）：<c>info.Packs</c> 要有这个包，
    /// 每个成员的索引条目要指回箱里的 <c>entryName</c>。跳过这一步，索引里这一箱就整个不见了。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_resumed_pack_is_reused_whole_and_still_lands_in_the_index()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("resume");
        var factory0 = new BlobClientFactory(TestSecrets.Reader);
        var container = factory0.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            // 一目录一箱（不跨目录装箱），每箱两个小文件；内容各不相同，免得被本地去重收敛掉。
            for (var d = 1; d <= 3; d++)
            {
                WriteIncompressible($"d{d}/x.bin", 2000, seed: d * 10);
                WriteIncompressible($"d{d}/y.bin", 2000, seed: d * 10 + 1);
            }

            BackupRunControl? first = null;
            var stopping = new CountingUploader(
                new BlobUploader(factory0), stopAt: 1,
                stop: () => { first!.RequestStop(StopKind.Suspend); return StopKind.Suspend; });
            await using (var c = new BackupRunControl(_journals, 9, "run-a"))
            {
                first = c;
                var (o1, _, _) = Build(stopping);
                await Assert.ThrowsAsync<BackupSuspendedException>(
                    () => o1.RunAsync(Request(account, name), null, default, c));
            }

            var done = (await _journals.ListAsync(account.Id, name, default))[0].Content.Records;
            Assert.NotEmpty(done);
            Assert.All(done, r => Assert.Equal("pack", r.Kind));
            Assert.True(done.Count < 3, $"the first run was supposed to be interrupted, it did all {done.Count}");

            var resuming = new CountingUploader(new BlobUploader(factory0));
            var (o2, store2, _) = Build(resuming);
            await using (var c2 = new BackupRunControl(_journals, 9, "run-b"))
                Assert.Equal(1, (await o2.RunAsync(Request(account, name), null, default, c2)).Version);

            Assert.Equal(3 - done.Count, resuming.Uploads);   // 复用来的那几箱一箱都没重压重传

            var info = await store2.ReadInfoAsync(account, name, null, default);
            var index = await store2.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null, default);
            Assert.Equal(6, index.Entries.Count(e => e.Storage is not null));
            foreach (var r in done)
            {
                // RecordPackAsync 真的跑过：info.Packs 里有这一箱，成员表也是原样那一份。
                Assert.True(info.Packs.ContainsKey(r.Ref), $"pack {r.Ref} missing from the info file");
                foreach (var m in r.Members)
                {
                    var storage = index.Entries.Single(e => e.Path == m.Path).Storage!;
                    Assert.Equal("pack", storage.Kind);
                    Assert.Equal(r.Ref, storage.Ref);
                    Assert.Equal(m.EntryName, storage.EntryName);
                }
            }
            Assert.Empty(await _journals.ListAsync(account.Id, name, default));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 同内容不同路径：上一轮传完了 a.bin 就挂起，本轮多出一个与它逐字节相同的 b.bin。
    /// b.bin 绝不能把 a.bin 那几卷删了重传一遍。
    /// <para>
    /// 恢复是按**路径**认账的，b.bin 在 journal 里查无此人。它重压之后拿到的却是**同一个**
    /// 地址（内容寻址，同内容必同址），而多卷上传前那一步 <c>ClearLeftoverVolumesAsync</c>
    /// 会无条件把该地址名下的分卷全删掉再传（7z 的 <c>-si</c> 不是逐字节确定的，新旧卷混在一起
    /// 拼不出归档）。删了再传的这个窗口里被 Stop now 打断或进程崩掉，云上就只剩半套卷；
    /// 下一轮采纳同一卷 journal，a.bin 照样复用、照样提交索引，指向的却是一份缺卷的内容——
    /// 错要到还原或检查时才看得见。
    /// </para>
    /// <para>
    /// 所以采纳来的块要一并喂进本地去重表（<c>LocalDedupResolver.Build</c> 的 confirmed 参数），
    /// 让 b.bin 走跨版本去重那条路：不压、不传，那几卷根本没有被碰的机会。
    /// 这条用例钉的正是"没被碰过"——分卷的名字与 ETag 一个都不许变。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_duplicate_of_a_resumed_file_reuses_its_volumes_instead_of_rewriting_them()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("resume");
        var factory0 = new BlobClientFactory(TestSecrets.Reader);
        var container = factory0.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            // 不可压缩 + 2 MB 一卷 → 稳定压出多卷。单卷不清残留，这条用例就不成立了。
            WriteIncompressible("a.bin", 6_000_000, seed: 7);

            // 第一轮只有 a.bin：Suspend 不打断在途上传（只有 Stop now 才碰 AbortToken），
            // 所以它的全部分卷都会传完、journal 也会记上，然后整轮以 Suspended 收场。
            BackupRunControl? first = null;
            var stopping = new CountingUploader(
                new BlobUploader(factory0), stopAt: 1,
                stop: () => { first!.RequestStop(StopKind.Suspend); return StopKind.Suspend; });
            await using (var c = new BackupRunControl(_journals, 9, "run-a"))
            {
                first = c;
                var (o1, _, _) = Build(stopping);
                await Assert.ThrowsAsync<BackupSuspendedException>(
                    () => o1.RunAsync(Request(account, name, volumeBytes: 2_000_000), null, default, c));
            }

            var done = (await _journals.ListAsync(account.Id, name, default))[0].Content.Records;
            var record = Assert.Single(done);
            Assert.Equal("a.bin", record.Path);
            Assert.True(record.Volumes > 1, $"this test needs a multi-volume blob, got {record.Volumes}");
            var before = await VolumesOfAsync(container, record.Ref);
            Assert.Equal(record.Volumes, before.Count);

            // 第二轮多一个与 a.bin 逐字节相同的 b.bin。
            File.Copy(Path.Combine(_root, "a.bin"), Path.Combine(_root, "b.bin"));

            var resuming = new CountingUploader(new BlobUploader(factory0));
            var (o2, store2, _) = Build(resuming);
            await using (var c2 = new BackupRunControl(_journals, 9, "run-b"))
                Assert.Equal(1, (await o2.RunAsync(
                    Request(account, name, volumeBytes: 2_000_000), null, default, c2)).Version);

            // 那几卷原封未动：删了再传的话名字（7z 卷号）和 ETag 都会变。这是本条用例的正题。
            Assert.Equal(before, await VolumesOfAsync(container, record.Ref));
            // a.bin 从 journal 复用，b.bin 从去重表复用 → 一个字节都没再传。
            Assert.Equal(0, resuming.Uploads);

            var info = await store2.ReadInfoAsync(account, name, null, default);
            var index = await store2.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null, default);
            foreach (var path in new[] { "a.bin", "b.bin" })
            {
                var storage = index.Entries.Single(e => e.Path == path).Storage!;
                Assert.Equal(record.Ref, storage.Ref);
                Assert.Equal(record.Volumes, storage.Volumes);
            }
            Assert.Empty(await _journals.ListAsync(account.Id, name, default));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task A_changed_key_voids_the_journal_instead_of_reusing_it()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("resume");
        var factory0 = new BlobClientFactory(TestSecrets.Reader);
        var container = factory0.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteBytes("a.bin", 6_000_000);
            WriteBytes("b.bin", 6_000_001);
            WriteBytes("c.bin", 6_000_002);

            BackupRunControl? first = null;
            var stopping = new CountingUploader(
                new BlobUploader(factory0), stopAt: 1,
                stop: () => { first!.RequestStop(StopKind.Suspend); return StopKind.Suspend; });
            await using (var c = new BackupRunControl(_journals, 9, "run-a"))
            {
                first = c;
                var (o1, _, _) = Build(stopping);
                await Assert.ThrowsAsync<BackupSuspendedException>(
                    () => o1.RunAsync(Request(account, name), null, default, c));
            }

            // 换了密码 → 寻址身份变了 → 旧卷里的引用全对不上号，必须整卷作废，三个文件全部重传。
            var again = new CountingUploader(new BlobUploader(factory0));
            var (o2, _, _) = Build(again);
            await using (var c2 = new BackupRunControl(_journals, 9, "run-b"))
                await o2.RunAsync(Request(account, name, password: "pw"), null, default, c2);

            Assert.Equal(3, again.Uploads);
            Assert.Empty(await _journals.ListAsync(account.Id, name, default));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
