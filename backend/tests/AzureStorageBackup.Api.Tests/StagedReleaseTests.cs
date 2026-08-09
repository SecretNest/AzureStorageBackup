using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The staging area's account must be handed back **on every path**, including the one that throws.
/// <para>
/// <see cref="StagingArea"/> is a DI singleton shared across backups, and its byte count is an in-process memory counter — leak
/// it once and it hangs there forever, cleared only by a restart. It is at the same time the backpressure gate on
/// compression/packing (<c>HasRoom</c>): inflate the account to the ceiling and every run's output gets stuck on
/// <c>WaitForRoomAsync</c>, degrading the whole pipeline into "one item uploads before the next is let through".
/// And you cannot see it in the UI — that column shows this run's **seat** usage, not the global account.
/// </para>
/// <para>
/// The stretch in <c>ProcessPackAsync</c> between getting <c>staged</c> and being done with it (the per-member recheck after
/// compression) used to have no <c>finally</c> covering it: the <c>catch</c> there only collects <c>IOException</c> / <c>UnauthorizedAccessException</c>,
/// so any other exception (cancellation being the classic) escaping leaves this pack's bytes and temp files behind. The
/// single-file path always had one (<c>HandleBlobAsync</c>'s finally); only pack was missing it.
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
            // A pack of small files. The target member is "modified" after compression (its mtime changes), so the recheck
            // recomputes its hash — and that recomputation throws InvalidOperationException: outside what that catch collects,
            // it escapes all the way out of ProcessPackAsync, which is exactly the path cancellation (OperationCanceledException) takes.
            var dir = Path.Combine(_root, "pack");
            Directory.CreateDirectory(dir);
            for (var i = 0; i < 6; i++)
                await File.WriteAllTextAsync(Path.Combine(dir, $"m{i}.txt"), new string('x', 300 + i));
            var target = Path.Combine(dir, "m3.txt");

            var authority = new TestLocalAuthority(store);
            // The latch keys off "this pack has finished compressing" rather than a call count: how many times the diff stage
            // computes a full hash for the same file is none of this test's business, and binding to it makes the test drift with diff's implementation.
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

            // Before the fix this held that pack's bytes, and they were never handed back — hanging there for as long as the singleton lives.
            Assert.Equal(0, staging.StagedBytes);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>The "this pack has finished compressing" latch, wiring the two test doubles together.</summary>
    private sealed class PackedGate
    {
        private volatile bool _packed;
        public bool Packed { get => _packed; set => _packed = value; }
    }

    /// <summary>Once a pack is compressed, push the named member's mtime forward so the post-compression recheck decides "it changed while we were compressing".</summary>
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

    /// <summary>After this pack is compressed, throw on the full hash of the named file — that is, on the post-compression recheck.</summary>
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
