using System.Net.Sockets;
using Azure.Storage.Blobs;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Invariant: never keep and upload a pack that contains a known-unreadable member. If a grouped member turns out unreadable during the
/// post-compression re-verification (a transient failure such as being in use or having permissions revoked), the effect is the same as "the content changed" — exclude it from the current archive, and the remaining members form the pack as usual.
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

    /// <summary>After compression, touch only the target file's mtime (content untouched): simulates "metadata jitter, and it happens to be unreadable right now" —
    /// this makes the re-verification go read the content again, rather than actually changing the content the way <c>MutatingCompressor</c> does.</summary>
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

        public Task<IReadOnlyList<ArchiveEntry>> ListEntriesAsync(
            string firstVolumePath, string? password, CancellationToken ct = default)
            => inner.ListEntriesAsync(firstVolumePath, password, ct);

        public Task<long> ExtractToStreamAsync(
            string firstVolumePath, string? entryName, string? password, Stream destination,
            CancellationToken ct = default)
            => inner.ExtractToStreamAsync(firstVolumePath, entryName, password, destination, ct);

        public Task<CompressionResult> CompressStreamAsync(
            StreamCompressionRequest request, Func<Stream, CancellationToken, Task<long>> writeSource,
            CancellationToken ct = default)
            => inner.CompressStreamAsync(request, writeSource, ct);
    }

    /// <summary><c>FullHashAsync</c> for the given path throws only on the first call (simulating being unreadable at the instant of the post-compression re-verification),
    /// and behaves normally afterwards (once the member is excluded and requeued for the next group) — verifies a read failure is treated as "must be excluded" rather than crashing the whole run.</summary>
    private sealed class FlakyOnceHasher(IFileHasher inner, string relPath, Exception toThrow) : IFileHasher
    {
        private int _thrown;

        public Task<string> HeadHashAsync(string path, int headBytes, CancellationToken ct = default) =>
            inner.HeadHashAsync(path, headBytes, ct);

        public Task<string> TailHashAsync(string path, int tailBytes, CancellationToken ct = default) =>
            inner.TailHashAsync(path, tailBytes, ct);

        public Task<string> FullHashAsync(string path, CancellationToken ct = default, IProgress<long>? onRead = null)
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

    /// <summary>Download the given pack blob and use 7z to extract the actual archive entry names (not inferred from the index — read out of the archive itself).</summary>
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

    /// <summary>Invariant: never upload a pack that contains a known-unreadable member.</summary>
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
            new BlobUploader(factory), new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "compact"),
            staging);

        // d/y.txt is unreadable once during the post-compression re-verification; the orchestrator's own hasher (the one used for group re-verification) is swapped for a throwing version.
        var flaky = new FlakyOnceHasher(new FileHasher(), "d/y.txt", new IOException("The process cannot access the file 'y.txt' because it is being used by another process."));
        var touching = new TouchAfterCompressCompressor(new SevenZipCompressor(), "d/y.txt");

        var authority = new TestLocalAuthority(store);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            touching, new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), compactor, indexCache: authority.IndexCache, trackedInfo: authority.Tracked), flaky, authority.IndexCache, authority.Tracked);

        var account = AzuriteAccount();
        var name = RandomName("unreadpk-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("d/x.txt", "xxxx"); // two small files in one directory → incremental grouping, so the first pack holds both
            WriteText("d/y.txt", "yyyy");

            await orchestrator.RunAsync(Request(account, name)); // no exception thrown == the read failure was absorbed and turned into an "exclude", instead of crashing the whole run

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);
            var x = idx.Entries.Single(e => e.Path == "d/x.txt");
            var y = idx.Entries.Single(e => e.Path == "d/y.txt");

            Assert.Equal("pack", x.Storage!.Kind);
            Assert.Equal("pack", y.Storage!.Kind);
            Assert.NotEqual(x.Storage.Ref, y.Storage.Ref); // different packs — y did not get to stay in the first pack, the one x is in

            // Core assertion: read the actual archive contents of the first pack (x's) to prove it really does not contain y — not inferred from the index.
            var firstPackEntries = await PackEntriesAsync(container, x.Storage.Ref);
            Assert.Contains("d/x.txt", firstPackEntries);
            Assert.DoesNotContain("d/y.txt", firstPackEntries);

            // y still ends up packed, uploaded and restorable in the end (in whichever pack it landed in).
            var secondPackEntries = await PackEntriesAsync(container, y.Storage.Ref);
            Assert.Contains("d/y.txt", secondPackEntries);

            var expectedY = await new FileHasher().FullHashAsync(Path.Combine(_root, "d/y.txt"));
            Assert.Equal(expectedY, y.FullHash); // the content never actually changed — it was just unreadable at the moment of re-verification

            await AssertReferencedBlobsExist(container, idx);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    /// <summary>Lock the member only for **the instant 7z is compressing**, restoring the permissions before the compression call returns.
    /// That way 7z cannot read it and silently drops it (exit code 1, and the archive is still valid), while the post-compression re-verification looking at metadata sees
    /// mtime/length/permission bits all exactly as they were before compression — the comparison says "this member did not change".
    /// This is precisely the hole a metadata comparison **cannot see**: nothing short of inspecting the archive's actual contents will find it.</summary>
    private sealed class LockDuringCompressCompressor(IFileCompressor inner, string rootPath, string relPath) : IFileCompressor
    {
        private int _fired;

        public async Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken ct = default)
        {
            var full = Path.Combine(rootPath, relPath.Replace('/', Path.DirectorySeparatorChar));
            var lockIt = request.Entries.Contains(relPath) && Interlocked.Exchange(ref _fired, 1) == 0;
            if (lockIt)
                File.SetUnixFileMode(full, UnixFileMode.None);
            try
            {
                return await inner.CompressAsync(request, ct);
            }
            finally
            {
                if (lockIt)
                    File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        public Task ExtractAsync(string firstVolumePath, string outputDir, string? password, CancellationToken ct = default)
            => inner.ExtractAsync(firstVolumePath, outputDir, password, ct);

        public Task<IReadOnlyList<ArchiveEntry>> ListEntriesAsync(
            string firstVolumePath, string? password, CancellationToken ct = default)
            => inner.ListEntriesAsync(firstVolumePath, password, ct);

        public Task<long> ExtractToStreamAsync(
            string firstVolumePath, string? entryName, string? password, Stream destination,
            CancellationToken ct = default)
            => inner.ExtractToStreamAsync(firstVolumePath, entryName, password, destination, ct);

        public Task<CompressionResult> CompressStreamAsync(
            StreamCompressionRequest request, Func<Stream, CancellationToken, Task<long>> writeSource,
            CancellationToken ct = default)
            => inner.CompressStreamAsync(request, writeSource, ct);
    }

    /// <summary>Core invariant: **a member the index claims is in a pack must really be in that pack**.
    /// For a member it cannot read, 7z only emits a warning (exit code 1), silently drops it and still produces a valid archive, while we used to count only exit code >= 2
    /// as a failure — so a pack missing a member got uploaded as a normal artifact while the index recorded it as being inside. The member is locked for the instant
    /// of compression and restored immediately after, so the post-compression metadata re-verification sees nothing wrong at all; only inspecting the archive contents finds it.</summary>
    [SkippableFact]
    public async Task The_Index_Never_Claims_A_Member_The_Archiver_Dropped()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");
        Skip.If(OperatingSystem.IsWindows(), "Relies on Unix permission bits.");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);

        var account = AzuriteAccount();
        var name = RandomName("dropped-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("d/x.txt", "xxxx");
            WriteText("d/y.txt", "yyyy"); // this is the one that is unreadable for the instant of compression
            WriteText("d/z.txt", "zzzz");

            var compressor = new LockDuringCompressCompressor(new SevenZipCompressor(), _root, "d/y.txt");
            var authority = new TestLocalAuthority(store);
            var orchestrator = new BackupOrchestrator(
                new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
                compressor, new BlobUploader(factory), factory, store, staging,
                new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked);

            await orchestrator.RunAsync(Request(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);

            // Check the index's claims against the archives one by one: every entry that claims to be packed must really be extractable from that pack.
            // Before the fix, d/y.txt would be recorded in the first pack while that pack's archive did not contain it at all.
            foreach (var e in idx.Entries.Where(e => e.Storage!.Kind == "pack"))
            {
                var actual = await PackEntriesAsync(container, e.Storage!.Ref);
                Assert.Contains(e.Path, actual);
            }

            // All three files must be there: the lock only lasts until compression returns, after which it is fully readable, so the retry has to store it.
            Assert.Contains(idx.Entries, e => e.Path == "d/x.txt");
            Assert.Contains(idx.Entries, e => e.Path == "d/y.txt");
            Assert.Contains(idx.Entries, e => e.Path == "d/z.txt");

            await AssertReferencedBlobsExist(container, idx);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Lock the target file the instant the diff finishes reading it. The file has to be readable at diff time — otherwise it gets classified
    /// as "unreadable" and never enters the pack plan at all, leaving the test spinning its wheels (a trap we actually fell into: a version that locked it from
    /// the start also passed before the fix). Locking after the diff is what lets it enter the pack as a normal member and then get dropped by 7z.</summary>
    private sealed class LockAfterDiffHasher(IFileHasher inner, string relPath) : IFileHasher
    {
        private int _locked;

        public Task<string> HeadHashAsync(string path, int headBytes, CancellationToken ct = default) =>
            inner.HeadHashAsync(path, headBytes, ct);

        public Task<string> TailHashAsync(string path, int tailBytes, CancellationToken ct = default) =>
            inner.TailHashAsync(path, tailBytes, ct);

        public async Task<string> FullHashAsync(string path, CancellationToken ct = default, IProgress<long>? onRead = null)
        {
            var hash = await inner.FullHashAsync(path, ct);
            if (path.EndsWith(relPath.Replace('/', Path.DirectorySeparatorChar), StringComparison.Ordinal)
                && Interlocked.Exchange(ref _locked, 1) == 0)
                File.SetUnixFileMode(path, UnixFileMode.None);
            return hash;
        }
    }

    /// <summary>The member is locked only after the diff — it really does enter the pack plan and then stays unreadable: 7z drops it and
    /// the post-compression re-verification cannot read it either. This path used to upload the member-less pack straight up, with the index claiming it anyway.</summary>
    [SkippableFact]
    public async Task A_Member_Locked_After_Diff_Is_Not_Claimed_By_The_Index()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");
        Skip.If(OperatingSystem.IsWindows(), "Relies on Unix permission bits.");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);

        var account = AzuriteAccount();
        var name = RandomName("droppednv-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        var victim = Path.Combine(_root, "d", "y.txt");

        try
        {
            WriteText("d/x.txt", "xxxx");
            WriteText("d/y.txt", "yyyy");

            var authority = new TestLocalAuthority(store);
            var orchestrator = new BackupOrchestrator(
                new LocalFileScanner(), new BackupDiffer(new LockAfterDiffHasher(new FileHasher(), "d/y.txt")),
                new GroupingPlanner(), new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
                new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked);

            var result = await orchestrator.RunAsync(Request(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);

            // Whatever the index says is in the pack must really be in the pack. Before the fix, d/y.txt would be recorded in this pack while the archive did not have it.
            foreach (var e in idx.Entries.Where(e => e.Storage!.Kind == "pack"))
                Assert.Contains(e.Path, await PackEntriesAsync(container, e.Storage!.Ref));

            // y is a brand new file that could not be stored this run → absent entirely (no old entry to carry forward), and counted as unreadable.
            Assert.DoesNotContain(idx.Entries, e => e.Path == "d/y.txt");
            Assert.Equal(1, result.UnreadableFiles);

            // x goes into the pack as usual.
            var x = Assert.Single(idx.Entries, e => e.Path == "d/x.txt");
            Assert.Equal("pack", x.Storage!.Kind);

            await AssertReferencedBlobsExist(container, idx);
        }
        finally
        {
            try { File.SetUnixFileMode(victim, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best effort */ }
            await container.DeleteIfExistsAsync();
        }
    }
}
