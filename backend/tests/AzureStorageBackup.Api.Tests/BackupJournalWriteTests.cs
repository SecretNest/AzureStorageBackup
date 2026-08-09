using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

[Trait("Category", "Integration")]
public sealed class BackupJournalWriteTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;
    private readonly BackupJournalStore _journals;

    public BackupJournalWriteTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-jwrite-" + Guid.NewGuid().ToString("N"));
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
        Id = 41,
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

    private void WriteBytes(string rel, int size)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[size]);
    }

    private (BackupOrchestrator Orchestrator, BlobClientFactory Factory) Build(IBlobUploader? uploader = null)
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
            new SevenZipCompressor(), uploader ?? new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), compactor,
                indexCache: authority.IndexCache, trackedInfo: authority.Tracked),
            new FileHasher(), authority.IndexCache, authority.Tracked);
        return (orchestrator, factory);
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

    /// <summary>From the Nth upload on, always throw a permanent error — used to wedge a run halfway through.</summary>
    private sealed class FailAfter(IBlobUploader inner, int allowed) : IBlobUploader
    {
        private int _count;

        private void Gate()
        {
            if (Interlocked.Increment(ref _count) > allowed)
                throw new InvalidOperationException("upload refused by test");
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
        {
            Gate();
            return inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry, CancellationToken ct, IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
        {
            Gate();
            return inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata, progress);
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
        {
            Gate();
            return inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }
    }

    /// <summary>Stall every upload until the test lets it go — used to peek at whether the journal file is already on disk during
    /// the real time window where a run is "halfway through", which separates "never created" from "created and then deleted".</summary>
    private sealed class GatedUploader(IBlobUploader inner, TaskCompletionSource ready, TaskCompletionSource proceed)
        : IBlobUploader
    {
        private async Task GateAsync()
        {
            ready.TrySetResult();
            await proceed.Task;
        }

        public async Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
        {
            await GateAsync();
            return await inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }

        public async Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry, CancellationToken ct, IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
        {
            await GateAsync();
            return await inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata, progress);
        }

        public async Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
        {
            await GateAsync();
            await inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }
    }

    /// <summary>Let through only uploads whose address equals <paramref name="keepRef"/>, refusing everything else — used within one
    /// run to force an unrelated file to fail (so the run fails as a whole and the tail does not delete the journal) while the
    /// target content (one real upload, one if-missing hit, both at the same address) makes it safely all the way through.</summary>
    private sealed class FailExceptRef(IBlobUploader inner, string keepRef) : IBlobUploader
    {
        private static void Gate(string blobName, string keepRef)
        {
            if (!string.Equals(blobName, keepRef, StringComparison.Ordinal))
                throw new InvalidOperationException("upload refused by test");
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
        {
            Gate(blobName, keepRef);
            return inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry, CancellationToken ct, IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
        {
            Gate(blobName, keepRef);
            return inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata, progress);
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
        {
            Gate(blobName, keepRef);
            return inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }
    }

    /// <summary>Refuse only single-file blobs (the "data/" prefix) and let packs through (the "packs/" prefix) — forcing an unrelated
    /// big file to fail (so the run fails as a whole and the tail does not delete the journal) while the pack uploads safely and gets recorded in the journal.</summary>
    private sealed class FailDataBlobs(IBlobUploader inner) : IBlobUploader
    {
        private static void Gate(string blobName)
        {
            if (blobName.StartsWith("data/", StringComparison.Ordinal))
                throw new InvalidOperationException("upload refused by test");
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
        {
            Gate(blobName);
            return inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry, CancellationToken ct, IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
        {
            Gate(blobName);
            return inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata, progress);
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
        {
            Gate(blobName);
            return inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }
    }

    [SkippableFact]
    public async Task Successful_run_deletes_its_journal()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("jw");
        var factoryOnly = new BlobClientFactory(TestSecrets.Reader);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (orchestrator, factory) = Build(new GatedUploader(new BlobUploader(factoryOnly), ready, proceed));
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteText("a.txt", "hello");
            await using var control = new BackupRunControl(_journals, configId: 3, runId: "run-ok");
            var journalPath = _journals.PathFor(account.Id, name, "run-ok");
            var runTask = orchestrator.RunAsync(Request(account, name), null, default, control);

            // Stalled just before the first upload: opening the volume happens after scanning and grouping but before uploading,
            // so at this moment the volume must already be on disk. Without this one look, the "journal is gone" assertion below
            // reads the same for "never created" as for "created and then deleted", the test cannot tell the two apart, and so it cannot catch a "deleted too early" regression.
            await ready.Task;
            Assert.True(File.Exists(journalPath), "journal file should exist while the run is in progress");
            proceed.SetResult();

            await runTask;

            Assert.False(File.Exists(journalPath));
            Assert.Empty(await _journals.ListAsync(account.Id, name, default));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task IfMissing_hit_is_journalled()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("jw");

        WriteBytes("orig.bin", 6_000_000);
        WriteBytes("dup.bin", 6_000_000);      // byte-identical to orig.bin (both all zeros) → the same address
        WriteBytes("trigger.bin", 6_000_001);  // different length → different content → its own address, permanently refused by the wrapper

        // Plaintext addressing is just "data/" + fullHash (BlobAddressScheme.DataAddress) and Password is null,
        // so we can copy the same hash logic here and work out up front the target address orig/dup share.
        var expectedRef = "data/" + await new FileHasher().FullHashAsync(Path.Combine(_root, "orig.bin"), default);

        var factoryOnly = new BlobClientFactory(TestSecrets.Reader);
        var (orchestrator, factory) = Build(new FailExceptRef(new BlobUploader(factoryOnly), expectedRef));
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            await using var control = new BackupRunControl(_journals, configId: 3, runId: "run-ifmiss");
            await Assert.ThrowsAnyAsync<Exception>(
                () => orchestrator.RunAsync(Request(account, name), null, default, control));

            var listed = await _journals.ListAsync(account.Id, name, default);
            var journal = Assert.Single(listed);
            // orig.bin and dup.bin have identical content and identical addresses: whoever wins the conditional write first, the
            // other necessarily gets an if-missing hit (UploadIfMissingAsync returns false) — the brief explicitly requires that to
            // be journalled too, so no matter who won the race, both records must be there.
            var records = journal.Content.Records.Where(r => r.Kind == "blob" && r.Ref == expectedRef).ToList();
            Assert.Equal(2, records.Count);
            Assert.Equal(
                new[] { "dup.bin", "orig.bin" },
                records.Select(r => r.Path).OrderBy(p => p, StringComparer.Ordinal));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Pack_record_captures_members_and_volume_sizes()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("jw");
        var factoryOnly = new BlobClientFactory(TestSecrets.Reader);
        var (orchestrator, factory) = Build(new FailDataBlobs(new BlobUploader(factoryOnly)));
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            // Three small files in one directory → merged into a single pack; big.bin takes the single-file path and is permanently
            // refused by FailDataBlobs, forcing the whole run to fail so the tail does not delete the journal and we can inspect it.
            WriteText("d/a.txt", new string('a', 2000));
            WriteText("d/b.txt", new string('b', 2000));
            WriteText("d/c.txt", new string('c', 2000));
            WriteBytes("big.bin", 6_000_000);

            await using var control = new BackupRunControl(_journals, configId: 3, runId: "run-pack");
            await Assert.ThrowsAnyAsync<Exception>(
                () => orchestrator.RunAsync(Request(account, name), null, default, control));

            var listed = await _journals.ListAsync(account.Id, name, default);
            var journal = Assert.Single(listed);
            var record = Assert.Single(journal.Content.Records, r => r.Kind == "pack");

            Assert.False(string.IsNullOrEmpty(record.Ref));
            Assert.False(record.StoreOnly);
            Assert.Equal(3, record.Members.Count);
            var byPath = record.Members.ToDictionary(m => m.Path, StringComparer.Ordinal);
            foreach (var p in new[] { "d/a.txt", "d/b.txt", "d/c.txt" })
            {
                Assert.True(byPath.ContainsKey(p), $"missing member {p}");
                var m = byPath[p];
                Assert.False(string.IsNullOrEmpty(m.EntryName));
                Assert.False(string.IsNullOrEmpty(m.FullHash));
                Assert.Equal(2000, m.Length);
            }
            Assert.NotEmpty(record.VolumeSizes);
            Assert.All(record.VolumeSizes, s => Assert.True(s > 0));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Journal_keeps_what_was_confirmed_before_the_failure()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("jw");
        var factoryOnly = new BlobClientFactory(TestSecrets.Reader);
        // Two big files → each takes the single-file blob path; the first is allowed through, everything from the second on is refused.
        var (orchestrator, factory) = Build(new FailAfter(new BlobUploader(factoryOnly), allowed: 1));
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteBytes("big1.bin", 6_000_000);
            WriteBytes("big2.bin", 6_000_001);
            await using (var control = new BackupRunControl(_journals, configId: 3, runId: "run-boom"))
            {
                await Assert.ThrowsAnyAsync<Exception>(
                    () => orchestrator.RunAsync(Request(account, name), null, default, control));
            }

            var listed = await _journals.ListAsync(account.Id, name, default);
            var journal = Assert.Single(listed);
            Assert.Equal("run-boom", journal.RunId);
            Assert.Equal(3, journal.Content.Header.ConfigId);
            Assert.Equal(0, journal.Content.Header.BaselineVersion);
            Assert.Equal(_root, journal.Content.Header.LocalRoot);
            // Only the one that really finished uploading is recorded; the refused one must never show up in there.
            var record = Assert.Single(journal.Content.Records);
            Assert.Equal("blob", record.Kind);
            Assert.False(string.IsNullOrEmpty(record.FullHash));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
