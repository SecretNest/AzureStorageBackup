using System.Net.Sockets;
using Azure.Storage.Blobs;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 不变量：绝不把一个含有已知不可读成员的包留下来上传。分组成员压缩后重校验时若读不开
/// （占用/权限被收回等瞬时故障），效果等同于「内容变了」——排除出当前归档，其余成员照常成包。
/// </summary>
[Trait("Category", "Integration")]
public sealed class UnreadablePackMemberTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;

    public UnreadablePackMemberTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-unreadpack-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "src");
        _temp = Path.Combine(baseDir, "temp");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
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

    private BackupRequest Request(Account account, string container) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _root,
        Name = "photos",
        Password = null,
        Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 } },
    };

    /// <summary>压缩后仅触碰目标文件的 mtime（内容不动）：模拟"元数据抖动、此刻恰好读不开"——
    /// 触发重校验去重新读一次内容，而不是像 <c>MutatingCompressor</c> 那样真的改内容。</summary>
    private sealed class TouchAfterCompressCompressor(IFileCompressor inner, string relPath) : IFileCompressor
    {
        private int _fired;
        public async Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken ct = default)
        {
            var result = await inner.CompressAsync(request, ct);
            if (request.Entries.Contains(relPath) && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                var full = Path.Combine(request.SourceDirectory, relPath.Replace('/', Path.DirectorySeparatorChar));
                File.SetLastWriteTimeUtc(full, File.GetLastWriteTimeUtc(full).AddSeconds(3));
            }
            return result;
        }
        public Task ExtractAsync(string firstVolumePath, string outputDir, string? password, CancellationToken ct = default)
            => inner.ExtractAsync(firstVolumePath, outputDir, password, ct);
    }

    /// <summary>指定路径的 <c>FullHashAsync</c> 只在第一次调用时抛出（模拟压缩后重校验瞬间读不开），
    /// 此后（成员被排除、重新入队参与下一组时）恢复正常——验证读失败被当成"需排除"而非让整轮备份崩溃。</summary>
    private sealed class FlakyOnceHasher(IFileHasher inner, string relPath, Exception toThrow) : IFileHasher
    {
        private int _thrown;

        public Task<string> HeadHashAsync(string path, int headBytes, CancellationToken ct = default) =>
            inner.HeadHashAsync(path, headBytes, ct);

        public Task<string> TailHashAsync(string path, int tailBytes, CancellationToken ct = default) =>
            inner.TailHashAsync(path, tailBytes, ct);

        public Task<string> FullHashAsync(string path, CancellationToken ct = default)
        {
            if (path.EndsWith(relPath.Replace('/', Path.DirectorySeparatorChar), StringComparison.Ordinal)
                && Interlocked.Exchange(ref _thrown, 1) == 0)
                throw toThrow;
            return inner.FullHashAsync(path, ct);
        }
    }

    private static async Task AssertReferencedBlobsExist(BlobContainerClient container, VersionIndex index)
    {
        foreach (var e in index.Entries)
        {
            var baseRef = e.Storage!.Kind == "pack" ? $"packs/{e.Storage.Ref}.7z" : e.Storage.Ref;
            Assert.True(await VolumeBlobIO.ExistsAsync(container, baseRef, CancellationToken.None),
                $"missing blob {baseRef} for {e.Path}");
        }
    }

    /// <summary>下载指定 pack blob 并用 7z 解出实际归档条目名（不是从索引推断，而是查归档本身）。</summary>
    private async Task<List<string>> PackEntriesAsync(BlobContainerClient container, string packId)
    {
        var work = Path.Combine(_temp, "verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var first = await VolumeBlobIO.DownloadAsync(container, $"packs/{packId}.7z", work, CancellationToken.None);
        var ex = Path.Combine(work, "x");
        await new SevenZipCompressor().ExtractAsync(first, ex, null, CancellationToken.None);
        return Directory.EnumerateFiles(ex, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(ex, f).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    /// <summary>不变量：绝不上传一个内含已知不可读成员的包。</summary>
    [SkippableFact]
    public async Task A_Member_That_Becomes_Unreadable_Is_Excluded_And_The_Pack_Is_Recompressed()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var compactor = new DeadWeightCompactor(
            new BlobUploader(factory), new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "compact"));

        // d/y.txt 压缩后重校验时读不开一次；orchestrator 自身的 hasher（分组重校验用）被替换成会抛的版本。
        var flaky = new FlakyOnceHasher(new FileHasher(), "d/y.txt", new IOException("The process cannot access the file 'y.txt' because it is being used by another process."));
        var touching = new TouchAfterCompressCompressor(new SevenZipCompressor(), "d/y.txt");

        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            touching, new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), compactor), flaky,
            verifier: new ProcessingVerifier(new FileHasher()));

        var account = AzuriteAccount();
        var name = RandomName("unreadpk-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("d/x.txt", "xxxx"); // 同目录两小文件 → 增量分组，首个 pack 含两者
            WriteText("d/y.txt", "yyyy");

            await orchestrator.RunAsync(Request(account, name)); // 未抛异常 == 读失败被吞并转化为"排除"，而非让整轮崩溃

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);
            var x = idx.Entries.Single(e => e.Path == "d/x.txt");
            var y = idx.Entries.Single(e => e.Path == "d/y.txt");

            Assert.Equal("pack", x.Storage!.Kind);
            Assert.Equal("pack", y.Storage!.Kind);
            Assert.NotEqual(x.Storage.Ref, y.Storage.Ref); // 不同的 pack —— y 没能留在 x 所在的第一个包里

            // 核心断言：直接查第一个包（x 所在）的实际归档内容，证明其中确实不含 y —— 不是从索引推断。
            var firstPackEntries = await PackEntriesAsync(container, x.Storage.Ref);
            Assert.Contains("d/x.txt", firstPackEntries);
            Assert.DoesNotContain("d/y.txt", firstPackEntries);

            // y 最终仍然被正常打包上传、可还原（在它自己落脚的那个包里）。
            var secondPackEntries = await PackEntriesAsync(container, y.Storage.Ref);
            Assert.Contains("d/y.txt", secondPackEntries);

            var expectedY = await new FileHasher().FullHashAsync(Path.Combine(_root, "d/y.txt"));
            Assert.Equal(expectedY, y.FullHash); // 内容其实没变——只是重校验那一刻读不开

            await AssertReferencedBlobsExist(container, idx);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }
}
