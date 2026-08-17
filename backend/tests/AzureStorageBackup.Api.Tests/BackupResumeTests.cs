using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json.Nodes;
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
        // Each file's content has to differ from the others, otherwise the three files would dedup into one blob and the upload count would prove nothing.
        for (var i = 0; i < bytes.Length; i += 4096) bytes[i] = (byte)rel.Length;
        File.WriteAllBytes(full, bytes);
    }

    /// <summary>Writes incompressible content (fixed seed, reproducible). The packing path needs "every member different";
    /// all-zero small files get collapsed into one by local dedup, and then one pack cannot be told from another by upload count.</summary>
    private void WriteIncompressible(string rel, int size, int seed)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        File.WriteAllBytes(full, bytes);
    }

    /// <summary>Counts how many content uploads were actually issued, and supports "stop after the Nth" along the way.
    /// <para>
    /// Both UploadIfMissing overloads have to be taken over and both have to pass through the counter: the one with progress **has a default implementation** on the interface,
    /// and the main backup path (VolumeUploadScope is always present) goes through exactly that one. Take over only the overload without progress and
    /// this stand-in intercepts not a single backup upload, <see cref="Uploads"/> stays permanently 0 — and the assertions become empty words.
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

    /// <summary>
    /// Counts, per source path, every call that opens that file to hash it. The resume path's "reads nothing" claim is
    /// only worth anything if it is <b>measured</b>: a timing assertion would pass on a fast disk whatever the code did,
    /// and a run that quietly re-read every file would look exactly like a run that did not.
    /// <para>
    /// All four members are counted, not just <c>HeadHashAsync</c>. The probe today happens to start with the head hash,
    /// but that is an implementation detail of one method; what the assertions mean is "the source file was not opened
    /// on the upload path at all", and that has to stay true however the probe is rearranged later.
    /// </para>
    /// </summary>
    private sealed class CountingHasher(IFileHasher inner) : IFileHasher
    {
        private readonly ConcurrentDictionary<string, int> _reads = new(StringComparer.Ordinal);

        /// <param name="localPath">The absolute path, exactly as the orchestrator forms it from root + relative path.</param>
        public int Reads(string localPath) => _reads.GetValueOrDefault(localPath);

        private void Note(string path) => _reads.AddOrUpdate(path, 1, (_, n) => n + 1);

        public Task<string> HeadHashAsync(string path, int headBytes, CancellationToken ct = default)
        {
            Note(path);
            return inner.HeadHashAsync(path, headBytes, ct);
        }

        public Task<string> TailHashAsync(string path, int tailBytes, CancellationToken ct = default)
        {
            Note(path);
            return inner.TailHashAsync(path, tailBytes, ct);
        }

        public Task<string> FullHashAsync(
            string path, CancellationToken ct = default, IProgress<long>? onRead = null)
        {
            Note(path);
            return inner.FullHashAsync(path, ct, onRead);
        }

        public Task<ContentIdentity> ContentIdentityAsync(
            string path, int segmentBytes, CancellationToken ct = default)
        {
            Note(path);
            return inner.ContentIdentityAsync(path, segmentBytes, ct);
        }
    }

    /// <param name="hasher">Substituted for the orchestrator's hasher only — the differ keeps its own. What the counting
    /// tests below are about is the <b>second</b> read, the one the resume path pays to answer "was this already
    /// uploaded"; the diff's own reads are a separate question with a separate answer, and mixing them into one counter
    /// would make every number ambiguous.</param>
    private (BackupOrchestrator Orchestrator, BackupInfoStore Store, BlobClientFactory Factory) Build(
        IBlobUploader uploader, IFileHasher? hasher = null)
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
            hasher ?? new FileHasher(), authority.IndexCache, authority.Tracked);
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
            // An upload budget of 1 = only one volume in flight at any moment, so the **moment** at which "stop after the 1st upload" is issued is accurate.
            // But it does not guarantee that only one item was done when it stopped: the orchestrator starts Math.Max(2, UploadConcurrency + 1)
            // workers, and the second item may perfectly well already be underway (see the note in the test below for details).
            UploadConcurrency = 1,
            VolumeBytes = volumeBytes,
            Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
        },
    };

    /// <summary>The volumes currently living under a given ref: name + ETag. A re-upload changes both.</summary>
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

            // First run: suspend once one item has been uploaded.
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
            // How many items were actually done at suspension time is not for this test to decide: the orchestrator starts UploadConcurrency + 1
            // workers (at least 2), and at the moment the stop intent lands the second item may perfectly well already be underway. So no hardcoded
            // number — however many were done, the second run should upload exactly that many fewer, and that is what this test is really pinning down.
            var done = (await _journals.ListAsync(account.Id, name, default))[0].Content.Records;
            Assert.NotEmpty(done);
            Assert.True(done.Count < 3, $"the first run was supposed to be interrupted, it did all {done.Count}");

            // Second run: same config, same key and same root → adopt the old volume and only fill in the rest.
            var resuming = new CountingUploader(new BlobUploader(factory0));
            var (o2, store2, _) = Build(resuming);
            await using (var c2 = new BackupRunControl(_journals, 9, "run-b"))
            {
                var result = await o2.RunAsync(Request(account, name), null, default, c2);
                Assert.Equal(1, result.Version);
            }
            Assert.Equal(3 - done.Count, resuming.Uploads);   // not one byte of the reused ones was re-uploaded

            // All three index entries are present, and the reused ones point at exactly the blob the previous run uploaded.
            var info = await store2.ReadInfoAsync(account, name, null, default);
            var index = await store2.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null, default);
            Assert.Equal(3, index.Entries.Count(e => e.Storage is not null));
            foreach (var r in done)
                Assert.Equal(r.Ref, index.Entries.Single(e => e.Path == r.Path).Storage!.Ref);

            // Every journal has retired — its own volume together with the adopted one.
            Assert.Empty(await _journals.ListAsync(account.Id, name, default));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// The pack path — the part of resume most prone to going wrong, which the case above does not touch with a single word.
    /// <para>
    /// A pack that hits still has to go through <c>RecordPackAsync</c> (it just does not upload): <c>info.Packs</c> has to contain this pack,
    /// and each member's index entry has to point back at the <c>entryName</c> inside it. Skip that step and this whole pack vanishes from the index.
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
            // One pack per directory (packing never crosses directories), two small files per pack; the contents all differ so local dedup does not collapse them.
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

            Assert.Equal(3 - done.Count, resuming.Uploads);   // not one of the reused packs was recompressed or re-uploaded

            var info = await store2.ReadInfoAsync(account, name, null, default);
            var index = await store2.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null, default);
            Assert.Equal(6, index.Entries.Count(e => e.Storage is not null));
            foreach (var r in done)
            {
                // RecordPackAsync really did run: info.Packs contains this pack, and the member table is the same one as before.
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
    /// Same content, different path: the previous run finished uploading a.bin and then suspended, and this run has an extra b.bin that is byte for byte identical to it.
    /// b.bin must never delete a.bin's volumes and upload them all over again.
    /// <para>
    /// Resume recognizes things by **path**, and b.bin appears nowhere in the journal. Yet recompressing it yields the **same**
    /// address (content addressing: identical content, identical address), and the step before a multi-volume upload, <c>ClearLeftoverVolumesAsync</c>,
    /// unconditionally deletes every volume under that address before uploading (7z's <c>-si</c> is not byte-for-byte deterministic, and old and new volumes
    /// mixed together do not reassemble into an archive). Get interrupted by Stop now, or have the process crash, inside that delete-then-upload window, and the cloud is left with half a set of volumes;
    /// the next run adopts the same journal volume, reuses a.bin as usual and commits it to the index as usual, pointing at content that is missing volumes —
    /// an error that only becomes visible at restore or check time.
    /// </para>
    /// <para>
    /// So the adopted blocks have to be fed into the local dedup table as well (the confirmed parameter of <c>LocalDedupResolver.Build</c>),
    /// putting b.bin on the cross-version dedup path: no compression, no upload, and those volumes never get a chance to be touched at all.
    /// What this test pins down is exactly that "never touched" — not one volume's name or ETag may change.
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
            // Incompressible + 2 MB per volume → reliably compresses into multiple volumes. A single volume clears no leftovers, and then this test would not hold.
            WriteIncompressible("a.bin", 6_000_000, seed: 7);

            // The first run has only a.bin: Suspend does not interrupt an in-flight upload (only Stop now touches AbortToken),
            // so all of its volumes finish uploading and get recorded in the journal, and then the whole run ends as Suspended.
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

            // The second run adds a b.bin that is byte for byte identical to a.bin.
            File.Copy(Path.Combine(_root, "a.bin"), Path.Combine(_root, "b.bin"));

            var resuming = new CountingUploader(new BlobUploader(factory0));
            var (o2, store2, _) = Build(resuming);
            await using (var c2 = new BackupRunControl(_journals, 9, "run-b"))
                Assert.Equal(1, (await o2.RunAsync(
                    Request(account, name, volumeBytes: 2_000_000), null, default, c2)).Version);

            // Those volumes are untouched: had they been deleted and re-uploaded, both the names (7z volume numbers) and the ETags would have changed. This is the test's actual subject.
            Assert.Equal(before, await VolumesOfAsync(container, record.Ref));
            // a.bin is reused from the journal and b.bin from the dedup table → not one byte was uploaded again.
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

    /// <summary>
    /// Passed by the prescreen, stopped by the content criteria: the file had its tail changed between the two runs, with the length and the leading bytes untouched.
    /// <para>
    /// The prescreen only asks (path + length + head hash), all three of which agree, so it lets the file through. What stops this file is
    /// the two items in <c>FindBlob</c> that the prescreen **never asked about** (the full-content hash and the tail). Let <c>FindBlob</c> trust only
    /// the three things the prescreen asked — "we got this far, what else could it be" — and the file gets treated as "already uploaded last run"
    /// and skipped, with the index recording the **old content's** hash and pointing at the **old content's** blob: the newly written stretch would from then on
    /// no longer be in the backup at all, while the UI looks perfectly fine. This is the only checkpoint on the resume path that can catch it.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_file_whose_tail_changed_is_uploaded_again_even_though_the_prescreen_passes()
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

            var done = (await _journals.ListAsync(account.Id, name, default))[0].Content.Records;
            Assert.NotEmpty(done);
            Assert.True(done.Count < 3, $"the first run was supposed to be interrupted, it did all {done.Count}");

            // Pick a file that has **already been uploaded** and change only its last byte: the length is unchanged, the head is unchanged,
            // so the prescreen lets it through as before, and only the tail hash (and the full-content hash) can tell that it changed.
            var changed = done[0].Path!;
            var full = Path.Combine(_root, changed.Replace('/', Path.DirectorySeparatorChar));
            using (var fs = new FileStream(full, FileMode.Open, FileAccess.Write))
            {
                fs.Seek(-1, SeekOrigin.End);
                fs.WriteByte(0xFF);
            }
            var hasher = new FileHasher();
            var rewritten = await hasher.FullHashAsync(full);
            Assert.Equal(new FileInfo(full).Length, done[0].Length);   // the length really did not change
            Assert.NotEqual(done[0].FullHash, rewritten);

            var resuming = new CountingUploader(new BlobUploader(factory0));
            var (o2, store2, _) = Build(resuming);
            await using (var c2 = new BackupRunControl(_journals, 9, "run-b"))
                Assert.Equal(1, (await o2.RunAsync(Request(account, name), null, default, c2)).Version);

            // The items that were never done + the one whose tail was changed. The only reused ones are the rest, which are untouched.
            Assert.Equal(3 - done.Count + 1, resuming.Uploads);

            var info = await store2.ReadInfoAsync(account, name, null, default);
            var index = await store2.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null, default);
            var entry = index.Entries.Single(e => e.Path == changed);
            Assert.Equal(rewritten, entry.FullHash);            // records the **new** content
            Assert.NotEqual(done[0].Ref, entry.Storage!.Ref);   // and no longer points at the old content's blob
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

            // Password changed → the addressing identity changed → every ref in the old volume misses, so the whole volume must be voided and all three files re-uploaded.
            var again = new CountingUploader(new BlobUploader(factory0));
            var (o2, _, _) = Build(again);
            await using (var c2 = new BackupRunControl(_journals, 9, "run-b"))
                await o2.RunAsync(Request(account, name, password: "pw"), null, default, c2);

            Assert.Equal(3, again.Uploads);
            Assert.Empty(await _journals.ListAsync(account.Id, name, default));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    private const string FirstRunId = "run-a";

    /// <summary>
    /// The scene every metadata-resume test below starts from: three single-file blobs, a run that suspends once the
    /// first upload has confirmed, and the journal it leaves behind. Returns the records that run really did upload —
    /// how many is deliberately not pinned down (the orchestrator runs more than one worker, so the second item may
    /// well have been underway when the stop landed), only that it is at least one and not all three.
    /// </summary>
    private async Task<IReadOnlyList<JournalRecord>> InterruptedFirstRunAsync(Account account, string container)
    {
        WriteBytes("a.bin", 6_000_000);
        WriteBytes("b.bin", 6_000_001);
        WriteBytes("c.bin", 6_000_002);

        BackupRunControl? first = null;
        var stopping = new CountingUploader(
            new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), stopAt: 1,
            stop: () => { first!.RequestStop(StopKind.Suspend); return StopKind.Suspend; });
        await using (var c = new BackupRunControl(_journals, 9, FirstRunId))
        {
            first = c;
            var (o1, _, _) = Build(stopping);
            await Assert.ThrowsAsync<BackupSuspendedException>(
                () => o1.RunAsync(Request(account, container), null, default, c));
        }

        var done = (await _journals.ListAsync(account.Id, container, default))[0].Content.Records;
        Assert.NotEmpty(done);
        Assert.True(done.Count < 3, $"the first run was supposed to be interrupted, it did all {done.Count}");
        // Everything below turns on the recorded mtime, so make sure it was recorded at all: without this, a run that
        // wrote nothing into the field would send every one of these tests down the fallback path and they would all
        // still be green, proving nothing.
        Assert.All(done, r => Assert.NotNull(r.MtimeUtcTicks));
        return done;
    }

    private string LocalPath(string rel) => Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The paths of the three source files that the interrupted run did <b>not</b> get to.</summary>
    private static IEnumerable<string> NotDone(IReadOnlyList<JournalRecord> done) =>
        new[] { "a.bin", "b.bin", "c.bin" }.Except(done.Select(r => r.Path!), StringComparer.Ordinal);

    /// <summary>
    /// A file the previous run already uploaded, untouched since, is reused <b>without being opened</b>.
    /// <para>
    /// The point of the whole change: answering "did the previous run already upload this?" used to require a content
    /// identity, and a content identity requires reading the file end to end — so a resume read every file it was about
    /// to skip. Measured on a real resume, 194 GB was read to establish that 704 MB needed sending.
    /// </para>
    /// <para>
    /// The zero is only meaningful next to a number that is not zero, which is why the files the first run never
    /// reached are asserted on in the same breath, off the same counter: an instrumentation that had come unplugged
    /// would report zero for both, and half this test would still pass.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task An_untouched_file_is_reused_without_being_read()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("resume");
        var factory0 = new BlobClientFactory(TestSecrets.Reader);
        var container = factory0.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            var done = await InterruptedFirstRunAsync(account, name);

            var counting = new CountingHasher(new FileHasher());
            var resuming = new CountingUploader(new BlobUploader(factory0));
            var (o2, store2, _) = Build(resuming, counting);
            await using (var c2 = new BackupRunControl(_journals, 9, "run-b"))
                Assert.Equal(1, (await o2.RunAsync(Request(account, name), null, default, c2)).Version);

            foreach (var r in done)
                Assert.Equal(0, counting.Reads(LocalPath(r.Path!)));
            foreach (var rest in NotDone(done))
                Assert.True(counting.Reads(LocalPath(rest)) > 0, $"{rest} had to be read, it was never uploaded");

            // And the reuse is a real one: nothing re-uploaded, and the index points at the very blob the previous run left behind.
            Assert.Equal(3 - done.Count, resuming.Uploads);
            var info = await store2.ReadInfoAsync(account, name, null, default);
            var index = await store2.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null, default);
            Assert.Equal(3, index.Entries.Count(e => e.Storage is not null));
            foreach (var r in done)
            {
                var entry = index.Entries.Single(e => e.Path == r.Path);
                Assert.Equal(r.Ref, entry.Storage!.Ref);
                Assert.Equal(r.FullHash, entry.FullHash);
            }
            Assert.Empty(await _journals.ListAsync(account.Id, name, default));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// The mtime moved, so the cheap question cannot be answered and the file goes down the content test — which then
    /// accepts it, because the content really is the same.
    /// <para>
    /// Both halves matter. That it <b>was read</b> is the fast path declining to answer; that it was <b>still reused</b>
    /// is the fallback being exactly the route that runs today. A mismatch must fall through, not settle the item as a
    /// miss — settling it would re-upload content that is already in the cloud, and worse, it would mean the fast path
    /// had taken over a decision it is not entitled to make.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_file_whose_mtime_moved_falls_back_to_the_content_test()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("resume");
        var factory0 = new BlobClientFactory(TestSecrets.Reader);
        var container = factory0.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            var done = await InterruptedFirstRunAsync(account, name);
            var touched = done[0];
            var full = LocalPath(touched.Path!);

            // Touched, not modified: the content is byte for byte what it was, only the timestamp moved.
            File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddMinutes(-5));
            Assert.NotEqual(touched.MtimeUtcTicks, new FileInfo(full).LastWriteTimeUtc.Ticks);
            Assert.Equal(touched.Length, new FileInfo(full).Length);   // the length is untouched, so only the mtime can do the stopping

            var counting = new CountingHasher(new FileHasher());
            var resuming = new CountingUploader(new BlobUploader(factory0));
            var (o2, store2, _) = Build(resuming, counting);
            await using (var c2 = new BackupRunControl(_journals, 9, "run-b"))
                Assert.Equal(1, (await o2.RunAsync(Request(account, name), null, default, c2)).Version);

            Assert.True(counting.Reads(full) > 0, "a moved mtime must not be answered from the journal record");
            Assert.Equal(3 - done.Count, resuming.Uploads);   // the content test then reused it, exactly as it did before this change
            var info = await store2.ReadInfoAsync(account, name, null, default);
            var index = await store2.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null, default);
            Assert.Equal(touched.Ref, index.Entries.Single(e => e.Path == touched.Path).Storage!.Ref);
            Assert.Empty(await _journals.ListAsync(account.Id, name, default));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// The length changed while the mtime was put back to what the journal recorded: the file is read, and the content
    /// test correctly sends it up again. Two tests are needed because either half of the metadata test on its own would
    /// let a modified file through — this is the one that fails if the length comparison is ever dropped as redundant.
    /// </summary>
    [SkippableFact]
    public async Task A_file_whose_length_changed_falls_back_even_when_the_mtime_matches()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("resume");
        var factory0 = new BlobClientFactory(TestSecrets.Reader);
        var container = factory0.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            var done = await InterruptedFirstRunAsync(account, name);
            var grown = done[0];
            var full = LocalPath(grown.Path!);

            using (var fs = new FileStream(full, FileMode.Append, FileAccess.Write))
                fs.WriteByte(0xFF);
            File.SetLastWriteTimeUtc(full, new DateTime(grown.MtimeUtcTicks!.Value, DateTimeKind.Utc));

            // The scene guard, and the whole reason this test is separate from the one above: the mtime really does
            // match to the tick, so nothing but the length comparison can stop this file.
            Assert.Equal(grown.MtimeUtcTicks, new FileInfo(full).LastWriteTimeUtc.Ticks);
            Assert.Equal(grown.Length + 1, new FileInfo(full).Length);

            var counting = new CountingHasher(new FileHasher());
            var resuming = new CountingUploader(new BlobUploader(factory0));
            var (o2, store2, _) = Build(resuming, counting);
            await using (var c2 = new BackupRunControl(_journals, 9, "run-b"))
                Assert.Equal(1, (await o2.RunAsync(Request(account, name), null, default, c2)).Version);

            Assert.True(counting.Reads(full) > 0, "a changed length must not be answered from the journal record");
            // The ones the first run never reached, plus this one: its content changed, so the content test rejects the record too.
            Assert.Equal(3 - done.Count + 1, resuming.Uploads);
            var info = await store2.ReadInfoAsync(account, name, null, default);
            var index = await store2.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null, default);
            var entry = index.Entries.Single(e => e.Path == grown.Path);
            Assert.Equal(grown.Length + 1, entry.Length);          // the index records the file as it is now
            Assert.NotEqual(grown.Ref, entry.Storage!.Ref);        // and no longer points at the old content's blob
            Assert.Empty(await _journals.ListAsync(account.Id, name, default));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// Rewrite a journal volume the way a version without the mtime field wrote it: the property removed outright, not
    /// set to null. That is what is actually on disk in a journal left behind by an older build.
    /// </summary>
    private void StripMtimeFromJournal(int accountId, string container, string runId)
    {
        var path = _journals.PathFor(accountId, container, runId);
        var lines = File.ReadAllLines(path);
        for (var i = 1; i < lines.Length; i++)   // line 0 is the header, which has no such field
        {
            if (lines[i].Length == 0)
                continue;
            var node = JsonNode.Parse(lines[i])!.AsObject();
            node.Remove(nameof(JournalRecord.MtimeUtcTicks));
            lines[i] = node.ToJsonString();
        }
        File.WriteAllLines(path, lines);
    }

    /// <summary>
    /// A record from before the mtime field existed must fall back, <b>not</b> match on path alone.
    /// <para>
    /// This is the compatibility case, and it is the one that would put wrong content into the index if it were got
    /// wrong: a record that cannot answer the cheap question must not be allowed to answer it by default. Every journal
    /// on a NAS that has not been upgraded yet looks exactly like this, and the file at that path may well have been
    /// modified since the interruption — which is precisely what the content test exists to catch.
    /// </para>
    /// <para>
    /// The files here are untouched, so the fallback accepts them and nothing is re-uploaded. That is the point: what
    /// must be visible is not a different outcome but a different <b>route</b> — the file was opened and its content
    /// checked, rather than waved through on the strength of its path.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_record_without_an_mtime_falls_back_instead_of_matching_on_path_alone()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("resume");
        var factory0 = new BlobClientFactory(TestSecrets.Reader);
        var container = factory0.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            var done = await InterruptedFirstRunAsync(account, name);
            StripMtimeFromJournal(account.Id, name, FirstRunId);

            // The scene guard: the volume still parses, still holds the same records, and none of them can answer the cheap question.
            var stripped = (await _journals.ListAsync(account.Id, name, default))[0].Content.Records;
            Assert.Equal(done.Count, stripped.Count);
            Assert.All(stripped, r => Assert.Null(r.MtimeUtcTicks));

            var counting = new CountingHasher(new FileHasher());
            var resuming = new CountingUploader(new BlobUploader(factory0));
            var (o2, store2, _) = Build(resuming, counting);
            await using (var c2 = new BackupRunControl(_journals, 9, "run-b"))
                Assert.Equal(1, (await o2.RunAsync(Request(account, name), null, default, c2)).Version);

            foreach (var r in stripped)
                Assert.True(
                    counting.Reads(LocalPath(r.Path!)) > 0,
                    $"{r.Path} was accepted without being read, on a record that carries no mtime at all");

            // The fallback is unchanged: the content matches, so these are still reused rather than uploaded again.
            Assert.Equal(3 - done.Count, resuming.Uploads);
            var info = await store2.ReadInfoAsync(account, name, null, default);
            var index = await store2.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null, default);
            foreach (var r in stripped)
                Assert.Equal(r.Ref, index.Entries.Single(e => e.Path == r.Path).Storage!.Ref);
            Assert.Empty(await _journals.ListAsync(account.Id, name, default));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
