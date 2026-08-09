using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Once the diff has passed, the source files get opened again — when 7z compresses pack members, and when a single file is stored as-is and uploaded.
/// A file that was readable at diff time and then got locked (in use / permissions revoked) used to make hasher.FullHashAsync
/// throw a second time inside the "excluded member" handling of the group re-verification, with nobody catching it, crashing the whole run.
/// This file verifies: a read failure that only happens after the diff is handled exactly like one at diff time, as "unreadable" —
/// no blob is produced, the index carries the old entry forward (absent if there is none), one warning is recorded, it counts toward UnreadableFiles, and the run never fails.
/// </summary>
[Trait("Category", "Integration")]
public sealed class UnreadableDuringUploadTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;

    public UnreadableDuringUploadTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-unreadupload-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "src");
        _temp = Path.Combine(baseDir, "temp");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        // Restore permissions, otherwise on some platforms the recursive delete fails because a child file is unreadable.
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                try { File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best effort */ }
        }
        catch { /* best effort */ }
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

    /// <summary>
    /// Simulates "readable at diff time, locked immediately afterwards": wrap a real hasher so that right after the diff finishes hashing the
    /// target path (that is, the moment the diff decides the file is readable and can be classified Added/Modified), that file's
    /// Unix permission bits are zeroed. From then on the orchestrator's own (equally real) hasher/7z that go read it
    /// hit a genuine operating-system permission denial — not simulated with a fake exception, actually unreadable.
    /// Why not a stub that throws a fake exception: this process is not root (chmod 000 really does take effect on this machine),
    /// and using real permissions verifies "are the operating-system calls in production really caught correctly", which is closer to the real failure than a stub.
    /// <para>
    /// The trigger hangs off <c>HeadHashAsync</c>: this test drops the single-file threshold to 1, so the target file is classified as a single-file
    /// blob, and a single-file blob's full hash is already deferred to the compression pass — the diff never calls
    /// <c>FullHashAsync</c> at all, so hooking there would mean the lock never drops and the "unreadable only after the diff" scenario simply vanishes.
    /// The head hash, by contrast, is called exactly once per file whichever path it takes, and it is the diff's last read of that file.
    /// </para>
    /// </summary>
    private sealed class LockAfterDiffHasher(IFileHasher inner, string relPath) : IFileHasher
    {
        private int _locked;

        public async Task<string> HeadHashAsync(string path, int headBytes, CancellationToken ct = default)
        {
            var hash = await inner.HeadHashAsync(path, headBytes, ct);
            if (path.EndsWith(relPath.Replace('/', Path.DirectorySeparatorChar), StringComparison.Ordinal)
                && Interlocked.Exchange(ref _locked, 1) == 0)
                File.SetUnixFileMode(path, UnixFileMode.None); // lock it right after the diff — simulates "then taken/permissions revoked"
            return hash;
        }

        public Task<string> TailHashAsync(string path, int tailBytes, CancellationToken ct = default) =>
            inner.TailHashAsync(path, tailBytes, ct);

        public Task<string> FullHashAsync(string path, CancellationToken ct = default, IProgress<long>? onRead = null) =>
            inner.FullHashAsync(path, ct);
    }

    /// <summary>Right after the diff finishes reading one file, delete **another** one — used to construct "a pending pack member disappeared
    /// before it was boxed up". The trigger hangs off the diff rather than the upload: after pipelining, single files and groups run concurrently,
    /// so "the first single file finished uploading" no longer means grouping has not started, and relying on it for ordering becomes a dice roll.</summary>
    private sealed class DeleteAfterHashHasher(IFileHasher inner, string triggerRelPath, string victimFullPath)
        : IFileHasher
    {
        private int _fired;

        public Task<string> HeadHashAsync(string path, int headBytes, CancellationToken ct = default) =>
            inner.HeadHashAsync(path, headBytes, ct);

        public Task<string> TailHashAsync(string path, int tailBytes, CancellationToken ct = default) =>
            inner.TailHashAsync(path, tailBytes, ct);

        public async Task<string> FullHashAsync(string path, CancellationToken ct = default, IProgress<long>? onRead = null)
        {
            var hash = await inner.FullHashAsync(path, ct);
            if (path.EndsWith(triggerRelPath.Replace('/', Path.DirectorySeparatorChar), StringComparison.Ordinal)
                && Interlocked.Exchange(ref _fired, 1) == 0)
                File.Delete(victimFullPath);
            return hash;
        }
    }

    /// <summary>Lock a whole batch of files (pack members) right after they have been compressed once — simulates "the entire directory suddenly went unreadable":
    /// the group re-verification finds every member's permission bits changed and every re-hash fails, so the "excluded member" handling must
    /// swallow the failure on the very first member and keep processing the rest, rather than throwing an uncaught exception that crashes the whole run.</summary>
    private sealed class LockAllAfterFirstCompressCompressor(
        IFileCompressor inner, string rootPath, IReadOnlyList<string> relPaths) : IFileCompressor
    {
        private int _fired;

        public async Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken ct = default)
        {
            var result = await inner.CompressAsync(request, ct);
            if (Interlocked.Exchange(ref _fired, 1) == 0)
            {
                foreach (var rel in relPaths)
                {
                    var full = Path.Combine(rootPath, rel.Replace('/', Path.DirectorySeparatorChar));
                    File.SetUnixFileMode(full, UnixFileMode.None);
                }
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

    /// <summary>Rewrite one member's content right after a compression pass — simulates "the group re-verification finds the content changed during compression"
    /// (as opposed to unreadable): that member is excluded from this archive and reprocessed with the new content, taking the "content changed"
    /// branch of foreach(changed) rather than the "unreadable" one, which sets up the "all source reads already succeeded" premise for the Finding 1 regression test.</summary>
    private sealed class MutateAfterFirstCompressCompressor(
        IFileCompressor inner, string rootPath, string relPath, string newContent) : IFileCompressor
    {
        private int _fired;

        public async Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken ct = default)
        {
            var result = await inner.CompressAsync(request, ct);
            if (Interlocked.Exchange(ref _fired, 1) == 0)
            {
                var full = Path.Combine(rootPath, relPath.Replace('/', Path.DirectorySeparatorChar));
                File.WriteAllText(full, newContent);
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

    /// <summary>Swap the content before 7z starts reading (so the stored hash ≠ the hash seen at diff time, which is exactly the path that has to write
    /// an index override entry), then lock the file dead the instant the read finishes — after that the single-file path **must not open it even once**.
    /// Before streaming, an override entry had to re-read the source file for its length and head hash, hitting exactly this permission denial and crashing the whole run.</summary>
    private sealed class MutateThenLockCompressor(
        IFileCompressor inner, string fullPath, string newContent) : IFileCompressor
    {
        public async Task<CompressionResult> CompressStreamAsync(
            StreamCompressionRequest request, Func<Stream, CancellationToken, Task<long>> writeSource,
            CancellationToken ct = default)
        {
            File.WriteAllText(fullPath, newContent);
            var result = await inner.CompressStreamAsync(request, writeSource, ct);
            File.SetUnixFileMode(fullPath, UnixFileMode.None);
            return result;
        }

        public Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken ct = default)
            => inner.CompressAsync(request, ct);

        public Task ExtractAsync(string firstVolumePath, string outputDir, string? password, CancellationToken ct = default)
            => inner.ExtractAsync(firstVolumePath, outputDir, password, ct);

        public Task<IReadOnlyList<ArchiveEntry>> ListEntriesAsync(
            string firstVolumePath, string? password, CancellationToken ct = default)
            => inner.ListEntriesAsync(firstVolumePath, password, ct);

        public Task<long> ExtractToStreamAsync(
            string firstVolumePath, string? entryName, string? password, Stream destination,
            CancellationToken ct = default)
            => inner.ExtractToStreamAsync(firstVolumePath, entryName, password, destination, ct);
    }

    /// <summary>Inject a network failure at the upload seam: every data/pack blob upload fails with IOException — which is exactly the shape
    /// a real NAS/network outage has when it escapes <see cref="BlobUploader"/> after the retry budget is exhausted (IsTransient lists
    /// IOException as a retryable network error and rethrows it as-is once retries run out). The failure is injected at the **upload**, while the source file stays
    /// perfectly readable from start to finish, so classifying it as "file unreadable" is necessarily a misdiagnosis.</summary>
    private sealed class NetworkFailingUploader : IBlobUploader
    {
        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null) =>
            throw new IOException("Unable to write data to the transport connection: Network is unreachable.");

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null) =>
            throw new IOException("Unable to write data to the transport connection: Network is unreachable.");
    }

    /// <summary>Captures NotifyAsync calls so we can assert "an unreadable file pushed a notification".</summary>
    private sealed class CapturingNotifier : INotifier
    {
        public List<(NotificationEvents Event, string Title, string Body)> Notifications { get; } = [];
        public Task NotifyAsync(NotificationEvents evt, string title, string body, CancellationToken ct = default)
        {
            lock (Notifications) Notifications.Add((evt, title, body));
            return Task.CompletedTask;
        }
    }

    /// <summary>Captures every progress?.Report call so we can assert "progress really does reach 100% on completion" (Finding 2).</summary>
    private sealed class CapturingProgress : IProgress<BackupProgress>
    {
        public List<BackupProgress> Reports { get; } = [];
        public void Report(BackupProgress value) { lock (Reports) Reports.Add(value); }
    }

    private BackupRequest Request(Account account, string container, long singleFileThresholdBytes) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _root,
        Name = "photos",
        Password = null,
        Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = singleFileThresholdBytes } },
    };

    /// <summary>The core assertion of this fix: a read failure that only happens after the diff (hit when the compress/upload stage reopens the source file)
    /// must not crash the whole run — the run must finish, that file degrades to "unreadable", and the rest upload normally.</summary>
    [SkippableFact]
    public async Task A_File_Locked_After_The_Diff_Does_Not_Abort_The_Run()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var notifier = new CapturingNotifier();

        // Single-file threshold dropped to 1: locked.bin and plain.txt each become their own data/{hash} blob (the single-file path,
        // i.e. HandleBlobAsync/ProcessAsync) instead of going through pack grouping — this test targets the "as-is single file" path specifically.
        var account = AzuriteAccount();
        var name = RandomName("unreadupl-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("locked.bin", "will be locked right after diff reads it");
            WriteText("plain.txt", "ordinary file, uploads fine");

            // The differ gets the hasher that locks the file after reading it; the orchestrator itself gets a real hasher and a real 7z —
            // what they hit is a bona fide operating-system permission denial, not a fake exception thrown by a stub.
            var differ = new BackupDiffer(new LockAfterDiffHasher(new FileHasher(), "locked.bin"));
            var authority = new TestLocalAuthority(store);
            var orchestrator = new BackupOrchestrator(
                new LocalFileScanner(), differ, new GroupingPlanner(),
                new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
                new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked,
                notifier: notifier);

            var result = await orchestrator.RunAsync(Request(account, name, singleFileThresholdBytes: 1));

            Assert.Equal(1, result.Version); // the backup finished and produced a new version — one file did not crash the whole run
            Assert.Equal(1, result.UnreadableFiles); // reuses the existing counter

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);

            // locked.bin is brand new and has never been backed up successfully — there is no old entry to carry forward, and fabricating one would be a lie, so it must be absent entirely.
            Assert.DoesNotContain(idx.Entries, e => e.Path == "locked.bin");

            // plain.txt is completely unaffected: it uploads normally and shows up in the index normally.
            var plain = Assert.Single(idx.Entries, e => e.Path == "plain.txt");
            Assert.Equal("blob", plain.Storage!.Kind);
            Assert.True(await container.GetBlobClient(plain.Storage.Ref).ExistsAsync());

            // Reuses the existing UnrecoverableError notification channel rather than inventing another one.
            var notification = Assert.Single(notifier.Notifications, n => n.Event == NotificationEvents.UnrecoverableError);
            Assert.Contains("locked.bin", notification.Title);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>The worst case the diagnostic report called out: every member of a directory goes unreadable at once. The group re-verification marks
    /// them all as "excluded", and the old "changed" member handling would re-hash the first member, throw again with nobody catching it,
    /// and crash the whole run before the remaining members of that directory were even processed. Verifies this path now holds up and finishes intact.</summary>
    [SkippableFact]
    public async Task A_Whole_Directory_Locked_After_The_Diff_Does_Not_Abort_The_Run()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var notifier = new CapturingNotifier();

        var account = AzuriteAccount();
        var name = RandomName("unreaddir-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // Two small files in the same directory → planned into the same pack under the default grouping threshold.
            WriteText("d/x.txt", "xxxx");
            WriteText("d/y.txt", "yyyy");

            var compressor = new LockAllAfterFirstCompressCompressor(
                new SevenZipCompressor(), _root, ["d/x.txt", "d/y.txt"]);

            var authority = new TestLocalAuthority(store);
            var orchestrator = new BackupOrchestrator(
                new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
                compressor, new BlobUploader(factory), factory, store, staging,
                new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked,
                notifier: notifier);

            var result = await orchestrator.RunAsync(
                Request(account, name, singleFileThresholdBytes: 5_000_000)); // go through pack grouping, not the single-file path

            Assert.Equal(1, result.Version); // the run finished — both members going unreadable together must not crash it
            Assert.Equal(2, result.UnreadableFiles); // both are counted

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);

            // Both are brand new files with no old entry to carry forward, so both are absent entirely.
            Assert.DoesNotContain(idx.Entries, e => e.Path == "d/x.txt");
            Assert.DoesNotContain(idx.Entries, e => e.Path == "d/y.txt");

            // One warning each, both reusing the existing notification channel.
            Assert.Contains(notifier.Notifications, n => n.Event == NotificationEvents.UnrecoverableError && n.Title.Contains("d/x.txt"));
            Assert.Contains(notifier.Notifications, n => n.Event == NotificationEvents.UnrecoverableError && n.Title.Contains("d/y.txt"));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Finding 2: when every member of an entire pack goes unreadable after the diff (stable.Count == 0),
    /// HandleBlobAsync's catch used to call onItem(), but the sibling catch in ProcessDirectoryAsync's foreach(changed)
    /// did not — and with stable.Count == 0 the one other onItem() call site, "if (stable.Count > 0)",
    /// is skipped too, so this pack, which occupies a slot in total, gets onItem() called zero times for the entire run.
    /// uploaded is then forever 1 short of total, and the progress report on completion never reaches 100% — even though the backup actually finished.
    /// This test watches the progress reports directly: the last report after the run finishes must be Stage=Completed with Percent=100.</summary>
    [SkippableFact]
    public async Task A_Whole_Pack_Unreadable_After_The_Diff_Still_Reports_Full_Progress()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var notifier = new CapturingNotifier();
        var progress = new CapturingProgress();

        var account = AzuriteAccount();
        var name = RandomName("unreadprog-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // Two small files in the same directory → planned into the same pack, occupying one slot in total.
            WriteText("d/x.txt", "xxxx");
            WriteText("d/y.txt", "yyyy");

            var compressor = new LockAllAfterFirstCompressCompressor(
                new SevenZipCompressor(), _root, ["d/x.txt", "d/y.txt"]);

            var authority = new TestLocalAuthority(store);
            var orchestrator = new BackupOrchestrator(
                new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
                compressor, new BlobUploader(factory), factory, store, staging,
                new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked,
                notifier: notifier);

            var result = await orchestrator.RunAsync(
                Request(account, name, singleFileThresholdBytes: 5_000_000), progress); // go through pack grouping

            Assert.Equal(1, result.Version);
            Assert.Equal(2, result.UnreadableFiles); // both members unreadable — the whole pack fails

            var completed = Assert.Single(progress.Reports, p => p.Stage == BackupStage.Completed);
            Assert.Equal(completed.TotalItems, completed.UploadedItems); // uploaded caught up with total
            Assert.Equal(100, completed.Percent); // completion must show 100%, not be forever one short

            // Confirm the other direction too, that there is no overcorrection into double counting: no report during the whole run may exceed total.
            Assert.All(progress.Reports, p => Assert.True(p.UploadedItems <= p.TotalItems));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Finding 1 regression test: before the fix, ProcessDirectoryAsync's foreach(changed) wrapped the entire
    /// HandleBlobAsync(...) call in one try — and that method, long after its own processing has successfully uploaded the content to the cloud,
    /// still does downstream work unrelated to reading the source (reproduced here by making verbose logging raise a genuine disk IOException:
    /// point VerboseFileLog's log root at a path where one segment is a file rather than a directory, so that Directory.CreateDirectory under such
    /// a path necessarily throws IOException — not simulated with a stub that throws a fake exception, a real filesystem call failure).
    /// Before the fix that downstream failure got caught by the overly wide try, the file was misdiagnosed as "unreadable", the already
    /// successfully uploaded blob vanished from the index, and the backup itself wrapped up as a "success" — which is the worst case of all: data loss with nobody alerted.
    /// After the fix, foreach(changed)'s try wraps only the hasher/BuildOverrideAsync section that really reads the source;
    /// HandleBlobAsync no longer pulls that downstream work into its own catch either. So this downstream failure must
    /// propagate faithfully out of RunAsync — fail loudly, instead of quietly treating an already successfully uploaded file as unreadable.</summary>
    [SkippableFact]
    public async Task A_Downstream_Failure_After_Successful_Upload_Is_Not_Misreported_As_Unreadable()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var notifier = new CapturingNotifier();

        // One segment of the verbose log root path is actually an ordinary file — Directory.CreateDirectory under such a path
        // necessarily reports ENOTDIR (IOException), which is a bona fide filesystem failure, not a fake exception from a stub.
        var logBlockerFile = Path.Combine(_temp, "log-root-blocker");
        await File.WriteAllTextAsync(logBlockerFile, "not a directory");
        var verboseLog = new VerboseFileLog(Path.Combine(logBlockerFile, "logs"));

        var account = AzuriteAccount();
        var name = RandomName("unreaddownstream-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // Directory d holds only this one small file (22 bytes < the 30-byte threshold) → planning gives it its own pack with exactly
            // 1 member (GroupingPlanner groups by directory, and a directory forms a pack even with only a single groupable file
            // inside). Deliberately only one file: if there were other "stable" members, they would first hit the same broken
            // log directory through LogFileAsync in the "if (stable.Count > 0)" branch of ProcessDirectoryAsync and fail
            // ahead of us, and the test would never reach the foreach(changed) path.
            // x.txt gets rewritten after the first compression into new content longer than the threshold (simulating "changed during processing",
            // not unreadable). The group re-verification sees the change and excludes it from the archive (at which point stable.Count == 0 and no
            // LogFileAsync call is made at all); foreach(changed) sees the new length ≥ the threshold → takes the "over threshold → single file" branch
            // and recursively calls HandleBlobAsync down the single-file upload path — which is exactly the call path Finding 1 hit
            // (the caller's try used to wrap this entire call, including the LogFileAsync after the successful upload inside it).
            WriteText("d/x.txt", "original content of x"); // 22 bytes, < 30, goes into a pack at planning time

            var compressor = new MutateAfterFirstCompressCompressor(
                new SevenZipCompressor(), _root, "d/x.txt",
                "mutated content of x, now much longer than the 30-byte threshold"); // > 30 bytes

            var authority = new TestLocalAuthority(store);
            var orchestrator = new BackupOrchestrator(
                new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
                compressor, new BlobUploader(factory), factory, store, staging,
                new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked,
                notifier: notifier, verboseLog: verboseLog);

            var request = Request(account, name, singleFileThresholdBytes: 30) with
            {
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 30 },
                    VerboseLogging = true,
                },
            };

            // The verbose log write only fails after the content has been successfully compressed and uploaded to the cloud — that failure must be thrown faithfully
            // and must never be quietly swallowed with x.txt misdiagnosed as "unreadable" (that would leave the data in the cloud but gone from the index,
            // with the backup still wrapping up as a "success" — silent data loss, which is worse than a crash). DirectoryNotFoundException
            // is a subclass of IOException; ThrowsAnyAsync keeps us compatible with whichever concrete type .NET maps ENOTDIR to.
            await Assert.ThrowsAnyAsync<IOException>(() => orchestrator.RunAsync(request));

            // The pre-fix mishandling would first write an "unreadable" warning and then swallow the exception; after the fix the exception really propagates out,
            // and no file gets misdiagnosed as "unreadable" and notified about.
            Assert.DoesNotContain(notifier.Notifications, n => n.Title.Contains("d/x.txt"));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Final-review Important 1: HandleBlobAsync's catch wraps not just the source file read but compression, staging
    /// and upload as well. BlobUploader.IsTransient lists IOException as a retryable network error and rethrows it as-is once the retry
    /// budget is exhausted — a shape identical to "the file is unreadable". Accepting it on exception type alone means a network outage gets recorded file by file as
    /// "unreadable, carry the old entry forward" while the run happily commits a new version and reports success: an hour of NAS network outage, and what the operator receives is
    /// "Backup succeeded, 0 changed files" — a silent failure far worse than a crash.
    /// This test injects the failure at the upload seam (the source file is readable throughout) and asserts that this IOException propagates faithfully,
    /// that no file is misdiagnosed as unreadable, and that no "successful" version is produced.</summary>
    [SkippableFact]
    public async Task An_Upload_Network_Failure_Is_Not_Misreported_As_An_Unreadable_File()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var notifier = new CapturingNotifier();

        var account = AzuriteAccount();
        var name = RandomName("unreadnet-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // Threshold dropped to 1 → the single-file path (HandleBlobAsync/ProcessAsync), which is where that overly wide catch lives.
            WriteText("reachable.bin", "this file is perfectly readable the whole time");

            var authority = new TestLocalAuthority(store);
            var orchestrator = new BackupOrchestrator(
                new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
                new SevenZipCompressor(), new NetworkFailingUploader(), factory, store, staging,
                new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked,
                notifier: notifier);

            // An upload failure must fail the whole run, not be quietly skipped as "this file is unreadable".
            await Assert.ThrowsAnyAsync<IOException>(() =>
                orchestrator.RunAsync(Request(account, name, singleFileThresholdBytes: 1)));

            // The source file is readable throughout, so any "unreadable" warning is a misdiagnosis.
            Assert.DoesNotContain(notifier.Notifications, n => n.Title.Contains("unreadable"));
            // A failure must go down the failure channel and must not sneak into the success notifications.
            Assert.Contains(notifier.Notifications, n => n.Event == NotificationEvents.BackupFailure);
            Assert.DoesNotContain(notifier.Notifications, n => n.Event == NotificationEvents.BackupSuccess);

            // Nor may it leave a "successful" version behind: before the fix it would commit v1 from the old entries and treat it as a normal backup.
            var info = await store.ReadInfoAsync(account, name, null);
            Assert.True(info is null || info.Versions.Count == 0);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Final-review Important 2: the <c>before</c> metadata snapshot taken ahead of the group re-verification sits outside every try, and for a
    /// member that has already disappeared it throws FileNotFoundException. Between the diff classifying a file and the box it lives in being compressed,
    /// a large backup can leave a very long gap — a single deleted build artifact is enough to make the whole run fall over in exactly
    /// the same shape this branch fixes. This test deletes one pending pack member at the moment the diff finishes reading the last file in that directory
    /// (which is precisely when the box gets sealed), and asserts the backup still finishes, that member degrades to "unreadable", and its siblings in the group are untouched.</summary>
    [SkippableFact]
    public async Task A_Pack_Member_Deleted_Before_The_Metadata_Snapshot_Does_Not_Abort_The_Run()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var notifier = new CapturingNotifier();

        var account = AzuriteAccount();
        var name = RandomName("unreadsnap-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // big.bin is over the threshold → a single-file blob, uploaded first; the two small files under d/ → one pack, processed afterwards.
            // That ordering is exactly why the gap exists, so the test has to have both kinds of file.
            WriteText("big.bin", new string('b', 400));
            WriteText("d/x.txt", "xxxx");
            WriteText("d/y.txt", "yyyy");

            // The scan advances in ordinal path order → d/y.txt is the last member of this directory to be diffed,
            // and the box is sealed the moment it is classified. Deleting d/x.txt at that moment guarantees the snapshot hits a file that is already gone.
            var victim = Path.Combine(_root, "d", "x.txt");
            var differ = new BackupDiffer(new DeleteAfterHashHasher(new FileHasher(), "d/y.txt", victim));

            var authority = new TestLocalAuthority(store);
            var orchestrator = new BackupOrchestrator(
                new LocalFileScanner(), differ, new GroupingPlanner(),
                new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
                new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked,
                notifier: notifier);

            var result = await orchestrator.RunAsync(Request(account, name, singleFileThresholdBytes: 100));

            Assert.Equal(1, result.Version);         // finished — did not fall over on the snapshot line
            Assert.Equal(1, result.UnreadableFiles); // the vanished member counts toward the existing counter

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);

            // Brand new file, never successfully backed up → no old entry to carry forward, absent entirely.
            Assert.DoesNotContain(idx.Entries, e => e.Path == "d/x.txt");

            // Siblings in the same group are untouched: packed and uploaded as usual, and the pack blob really is in the cloud.
            var sibling = Assert.Single(idx.Entries, e => e.Path == "d/y.txt");
            Assert.Equal("pack", sibling.Storage!.Kind);
            Assert.True(await container.GetBlobClient($"packs/{sibling.Storage.Ref}.7z").ExistsAsync());

            // The single file uploaded earlier is likewise unaffected.
            var big = Assert.Single(idx.Entries, e => e.Path == "big.bin");
            Assert.Equal("blob", big.Storage!.Kind);
            Assert.True(await container.GetBlobClient(big.Storage.Ref).ExistsAsync());

            // The vanished member goes down the existing warning channel, so the operator knows it was not stored this run.
            Assert.Contains(notifier.Notifications,
                n => n.Event == NotificationEvents.UnrecoverableError && n.Title.Contains("d/x.txt"));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>The combination "content changes during processing and then gets locked" used to crash the whole run on the single-file path:
    /// writing the index override entry required re-reading the source file for its length and head hash, and by then the file was unreadable.
    /// After streaming, that re-read disappears entirely — the length and the head/tail hashes all come from the single compression read. This test nails exactly that:
    /// the file is locked dead the instant after it is read, the backup still finishes, and the entry records the content that was **actually stored**.</summary>
    [SkippableFact]
    public async Task A_File_Locked_Right_After_Being_Read_Still_Records_What_Was_Stored()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var notifier = new CapturingNotifier();

        var account = AzuriteAccount();
        var name = RandomName("unreadoverride-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("churn.bin", "content as seen by the diff");

            var compressor = new MutateThenLockCompressor(
                new SevenZipCompressor(), Path.Combine(_root, "churn.bin"),
                "content rewritten while the backup was compressing it");

            var authority = new TestLocalAuthority(store);
            var orchestrator = new BackupOrchestrator(
                new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
                compressor, new BlobUploader(factory), factory, store, staging,
                new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked,
                notifier: notifier);

            // Threshold dropped to 1 → the single-file path (HandleBlobAsync), the one that used to have no protection.
            var result = await orchestrator.RunAsync(Request(account, name, singleFileThresholdBytes: 1));

            Assert.Equal(1, result.Version);
            Assert.Equal(0, result.UnreadableFiles); // the content was read in full and stored, so nothing is "unreadable"
            Assert.DoesNotContain(
                notifier.Notifications, n => n.Event == NotificationEvents.UnrecoverableError);

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);
            var entry = Assert.Single(idx.Entries, e => e.Path == "churn.bin");

            var rewritten = "content rewritten while the backup was compressing it";
            Assert.Equal(rewritten.Length, entry.Length);   // records what got compressed in, not what the diff saw
            Assert.True(await VolumeBlobIO.ExistsAsync(container, entry.Storage!.Ref, CancellationToken.None));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
