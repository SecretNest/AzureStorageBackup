using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 暂存区的账必须**在任何路径上**归还，包括抛出的那一条。
/// <para>
/// <see cref="StagingArea"/> 是 DI 单例、跨备份共享，它那个字节数是进程内内存计数——漏掉一次
/// 就永远挂在那里，只有重启才清。而它同时是压缩/打包的背压闸门（<c>HasRoom</c>）：账虚高到上限，
/// 所有运行的产出都会被卡在 <c>WaitForRoomAsync</c> 上，整条流水线退化成"一件传完才放行下一件"。
/// 界面上还看不出来——那一栏显示的是本次运行**席位**的占用，不是全局账。
/// </para>
/// <para>
/// <c>ProcessPackAsync</c> 里从拿到 <c>staged</c> 到用完它之间那一段（压缩后逐成员重校验）从前
/// 没有 <c>finally</c> 兜着：那里的 <c>catch</c> 只收 <c>IOException</c> / <c>UnauthorizedAccessException</c>，
/// 别的异常（取消最典型）一穿出去，这一箱的字节和临时文件就都留下了。单文件那条路一直有
/// （<c>HandleBlobAsync</c> 的 finally），只有 pack 漏。
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class StagedReleaseTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;

    public StagedReleaseTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-staged-" + Guid.NewGuid().ToString("N"));
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

    [SkippableFact]
    public async Task A_Pack_That_Throws_After_Compression_Still_Gives_Its_Staged_Bytes_Back()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);

        var account = new Account
        {
            Name = "azurite",
            BlobEndpoint = "http://127.0.0.1:10000/devstoreaccount1",
            AccountKeyProtected = TestSecrets.Protect(AzuriteKey),
            Region = AzureRegion.Global,
        };
        var name = "staged-" + Guid.NewGuid().ToString("N")[..8];
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // 一箱小文件。目标成员在压缩之后被"改过"（mtime 变），于是重校验会去重算它的 hash——
            // 而那一次重算抛的是 InvalidOperationException：不在那层 catch 的收集范围里，
            // 一路穿出 ProcessPackAsync，正是取消（OperationCanceledException）走的同一条路。
            var dir = Path.Combine(_root, "pack");
            Directory.CreateDirectory(dir);
            for (var i = 0; i < 6; i++)
                await File.WriteAllTextAsync(Path.Combine(dir, $"m{i}.txt"), new string('x', 300 + i));
            var target = Path.Combine(dir, "m3.txt");

            var authority = new TestLocalAuthority(store);
            // 门闩用"这一箱压完了"而不是调用次数：diff 阶段对同一个文件算几次 full hash 不是这条
            // 测试该知道的事，绑上去它就会随 diff 的实现漂移。
            var packed = new PackedGate();
            var orchestrator = new BackupOrchestrator(
                new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
                new TouchesAMemberAfterPacking(new SevenZipCompressor(), target, packed),
                new BlobUploader(factory), factory, store, staging,
                new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked),
                new ThrowsOnTheRecheckHash(new FileHasher(), target, packed, new InvalidOperationException("boom")),
                authority.IndexCache, authority.Tracked);

            await Assert.ThrowsAnyAsync<Exception>(() => orchestrator.RunAsync(new BackupRequest
            {
                Account = account, Container = name, LocalRoot = _root, Name = "staged-test",
            }));

            // 修复前这里是那一箱的字节，而且此后永不归还——单例活多久它就挂多久。
            Assert.Equal(0, staging.StagedBytes);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>「这一箱已经压完了」的门闩，把两个替身串起来。</summary>
    private sealed class PackedGate
    {
        private volatile bool _packed;
        public bool Packed { get => _packed; set => _packed = value; }
    }

    /// <summary>压完一箱就把指定成员的 mtime 往后拨，让压缩后重校验认定"它在压缩期间变过"。</summary>
    private sealed class TouchesAMemberAfterPacking(IFileCompressor inner, string target, PackedGate gate) : IFileCompressor
    {
        public async Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken ct = default)
        {
            var result = await inner.CompressAsync(request, ct);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(target).AddMinutes(5));
            gate.Packed = true;
            return result;
        }

        public Task<CompressionResult> CompressStreamAsync(
            StreamCompressionRequest request, Func<Stream, CancellationToken, Task<long>> writeSource,
            CancellationToken ct = default) => inner.CompressStreamAsync(request, writeSource, ct);

        public Task ExtractAsync(string firstVolumePath, string outputDir, string? password, CancellationToken ct = default)
            => inner.ExtractAsync(firstVolumePath, outputDir, password, ct);

        public Task<IReadOnlyList<ArchiveEntry>> ListEntriesAsync(
            string firstVolumePath, string? password, CancellationToken ct = default)
            => inner.ListEntriesAsync(firstVolumePath, password, ct);

        public Task<long> ExtractToStreamAsync(
            string firstVolumePath, string? entryName, string? password, Stream destination,
            CancellationToken ct = default) => inner.ExtractToStreamAsync(firstVolumePath, entryName, password, destination, ct);
    }

    /// <summary>这一箱压完之后，对指定文件的全文 hash 抛出——也就是压缩后重校验那一次。</summary>
    private sealed class ThrowsOnTheRecheckHash(IFileHasher inner, string target, PackedGate gate, Exception toThrow) : IFileHasher
    {
        public Task<string> FullHashAsync(string path, CancellationToken ct = default, IProgress<long>? onRead = null)
            => gate.Packed && string.Equals(path, target, StringComparison.Ordinal)
                ? throw toThrow
                : inner.FullHashAsync(path, ct, onRead);

        public Task<string> HeadHashAsync(string path, int headBytes, CancellationToken ct = default)
            => inner.HeadHashAsync(path, headBytes, ct);

        public Task<string> TailHashAsync(string path, int tailBytes, CancellationToken ct = default)
            => inner.TailHashAsync(path, tailBytes, ct);
    }
}
