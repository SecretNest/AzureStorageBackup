using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Now that a single-file blob's full hash is deferred to the compression pass, what gets recorded in the index must still be the
/// hash of **the bytes actually compressed into the archive**. A wrong index hash raises no error at the time: this run still
/// "succeeds", and only when the next run's diff compares against it, or a restore fetches <c>data/{hash}</c>, does the blob it
/// points at turn out not to exist. So this runs real backups over all three compression paths (normal compression / raw
/// passthrough / encrypted) and several change verdicts, checking the index hashes one entry at a time.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DeferredFullHashTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _base;
    private readonly string _root;
    private readonly string _temp;

    public DeferredFullHashTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "asb-defer-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_base, "src");
        _temp = Path.Combine(_base, "temp");
        Directory.CreateDirectory(_root);
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

    private string Write(string rel, int size)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        File.WriteAllBytes(full, bytes);
        return full;
    }

    private BackupOrchestrator Build(BlobClientFactory factory, IBackupInfoStore store)
    {
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var authority = new TestLocalAuthority(store);
        return new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked);
    }

    private BackupRequest Request(Account account, string container, BackupEngineOptions options, string? password = null)
        => new()
        {
            Account = account, Container = container, LocalRoot = _root, Name = "defer",
            Options = options, Password = password,
        };

    /// <summary>Every entry's FullHash in the index must equal the source file's real hash right now.</summary>
    private async Task AssertIndexHashesAreRealAsync(VersionIndex idx)
    {
        var hasher = new FileHasher();
        foreach (var e in idx.Entries.Where(e => e.Kind == "file"))
        {
            var local = Path.Combine(_root, e.Path.Replace('/', Path.DirectorySeparatorChar));
            Assert.NotNull(e.FullHash);
            Assert.Equal(await hasher.FullHashAsync(local), e.FullHash);
            Assert.Equal(new FileInfo(local).Length, e.Length);
        }
    }

    /// <summary>
    /// All three compression paths in one run: a single file compressed normally by 7z, a single file that hits the don't-compress
    /// list and is therefore uploaded as-is (raw), and small files merged into a pack. All three go through the single read pass in
    /// <c>StreamAndStageAsync</c> that computes the hash, but only the first two fall within the scope of the "deferral" — a pack member's hash has to be written into the member table at boxing time, with no second chance to fill it in later.
    /// </summary>
    [SkippableFact]
    public async Task Index_Hashes_Are_Real_Across_Compressed_Raw_And_Packed_Paths()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("defer1-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            Write("big.bin", 40_000);              // Single file + normal compression
            Write("raw/movie.mkv", 40_000);        // Single file + raw passthrough, no compression
            for (var i = 0; i < 4; i++)
                Write($"docs/f{i}.txt", 2_000);    // Packed

            var options = new BackupEngineOptions
            {
                DontCompress = new IgnoreRuleSet(["raw/"]),
                Plan = new PlanOptions { SingleFileThresholdBytes = 10_000, GroupCapBytes = 100_000 },
            };
            await Build(factory, store).RunAsync(Request(account, name, options));

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);

            // First confirm this data really did exercise all three paths, or the assertions below might be checking just one of them.
            var single = idx.Entries.Where(e => e.Storage!.Kind == "blob").ToList();
            Assert.Equal(["big.bin", "raw/movie.mkv"], single.Select(e => e.Path).Order(StringComparer.Ordinal));
            Assert.True(single.Single(e => e.Path == "raw/movie.mkv").Storage!.Raw, "raw passthrough expected");
            Assert.False(single.Single(e => e.Path == "big.bin").Storage!.Raw);
            Assert.Equal(4, idx.Entries.Count(e => e.Storage!.Kind == "pack"));

            await AssertIndexHashesAreRealAsync(idx);

            // Unencrypted, the address is the content address: a wrong hash gives itself away right here, and the blob won't resolve either.
            foreach (var e in single)
            {
                Assert.Equal("data/" + e.FullHash, e.Storage!.Ref);
                Assert.True(await container.GetBlobClient(e.Storage.Ref).ExistsAsync(), e.Storage.Ref);
            }
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// When encrypted, the blob name is <c>HMAC(key, fullHash)</c> (which stops anyone deducing the container's contents from the
    /// hashes of publicly available files), so "is the address right" no longer tells you directly whether the hash is right — the
    /// FullHash in the index is the only source of truth, and both the next run's diff and restore depend on it. This path is exactly the configuration the user actually runs.
    /// </summary>
    [SkippableFact]
    public async Task Index_Hashes_Are_Real_When_The_Backup_Is_Encrypted()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("defer2-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        const string password = "correct horse battery staple";

        try
        {
            Write("big.bin", 40_000);
            Write("raw/movie.mkv", 40_000); // When encrypted, "don't compress" still goes through 7z — plaintext must never be uploaded directly
            for (var i = 0; i < 3; i++)
                Write($"docs/f{i}.txt", 2_000);

            var options = new BackupEngineOptions
            {
                DontCompress = new IgnoreRuleSet(["raw/"]),
                Plan = new PlanOptions { SingleFileThresholdBytes = 10_000, GroupCapBytes = 100_000 },
            };
            await Build(factory, store).RunAsync(Request(account, name, options, password));

            var info = await store.ReadInfoAsync(account, name, password);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, password);

            await AssertIndexHashesAreRealAsync(idx);

            foreach (var e in idx.Entries.Where(e => e.Storage!.Kind == "blob"))
            {
                Assert.False(e.Storage!.Raw, "encrypted backups must never store plaintext raw");
                Assert.NotEqual("data/" + e.FullHash, e.Storage.Ref); // The address has been scrambled by the HMAC
                Assert.True(await container.GetBlobClient(e.Storage.Ref).ExistsAsync(), e.Storage.Ref);
            }
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// Second run: once the deferred hashes are written into the index, the next run's diff has to be able to use them as a
    /// trustworthy baseline. This is where a wrong index hash surfaces first and costs the most — a failed comparison re-uploads
    /// unchanged files in full. One of each of the four verdicts: untouched, mtime only, same length with changed content, changed length.
    /// </summary>
    [SkippableFact]
    public async Task The_Next_Run_Can_Trust_The_Deferred_Hashes()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("defer3-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            var untouched = Write("untouched.bin", 40_000);
            var touched = Write("touched.bin", 40_000);
            var rewritten = Write("rewritten.bin", 40_000);
            var grown = Write("grown.bin", 40_000);

            var options = new BackupEngineOptions
            {
                Plan = new PlanOptions { SingleFileThresholdBytes = 10_000 },
            };
            var first = await Build(factory, store).RunAsync(Request(account, name, options));
            Assert.Equal(4, first.ChangedFiles);

            // The one nothing was done to: it should not even be opened.
            _ = untouched;
            // Only push mtime forward: not one byte of content changed → must be judged MetadataOnly, no re-upload.
            File.SetLastWriteTimeUtc(touched, File.GetLastWriteTimeUtc(touched).AddMinutes(5));
            // Same length, different content: only the full hash can catch it.
            var sameLength = new byte[40_000];
            Random.Shared.NextBytes(sameLength);
            File.WriteAllBytes(rewritten, sameLength);
            // Length changed: the length alone decides it, no need to read the whole file.
            File.WriteAllBytes(grown, new byte[50_000]);

            var second = await Build(factory, store).RunAsync(Request(account, name, options));

            // Only the last two count as changed. If even one hash from the first run is wrong, untouched/touched get re-uploaded along with them.
            Assert.Equal(2, second.ChangedFiles);

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[1].IndexBlob, null);
            Assert.Equal(4, idx.Entries.Count);
            await AssertIndexHashesAreRealAsync(idx);

            foreach (var e in idx.Entries)
                Assert.True(await container.GetBlobClient(e.Storage!.Ref).ExistsAsync(), $"{e.Path} → {e.Storage.Ref}");
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
