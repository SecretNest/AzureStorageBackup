using System.Net.Sockets;
using Azure.Storage.Blobs;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// A file that cannot be read must not pass in silence — the operator has to see a record in the operation log, and has to see the verbatim reason the system gave
/// ("in use by another process", "permission denied" and "device read error" each call for different handling; flattening them into one "cannot read" tells the operator nothing).
/// The operation log is pull-only, and in a single-user unattended deployment nobody goes looking at it; so it must also be pushed out by reusing the UnrecoverableError
/// notification event — that is the actual fix this file covers, and the log level follows that event's mapping to Error (no longer Warning).
/// </summary>
[Trait("Category", "Integration")]
public sealed class UnreadableWarningTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;

    public UnreadableWarningTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-unreadwarn-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Reads of the given path always throw the given exception; every other file is hashed as usual (same approach as UnreadableIndexEntryTests).</summary>
    private sealed class ThrowingHasher(string lockedPath, Exception toThrow) : IFileHasher
    {
        public Task<string> HeadHashAsync(string path, int headBytes, CancellationToken ct = default) =>
            path.EndsWith(lockedPath, StringComparison.Ordinal)
                ? throw toThrow
                : Task.FromResult("head-" + Path.GetFileName(path));

        public Task<string> TailHashAsync(string path, int tailBytes, CancellationToken ct = default) =>
            path.EndsWith(lockedPath, StringComparison.Ordinal)
                ? throw toThrow
                : Task.FromResult("tail-" + Path.GetFileName(path));

        public Task<string> FullHashAsync(string path, CancellationToken ct = default, IProgress<long>? onRead = null) =>
            path.EndsWith(lockedPath, StringComparison.Ordinal)
                ? throw toThrow
                : Task.FromResult("full-" + Path.GetFileName(path));
    }

    /// <summary>Captures AppendAsync calls (level/source/message) so we can assert on the warning's content.</summary>
    private sealed class CapturingLog : IOperationLog
    {
        public List<(OperationLogLevel Level, string Source, string Message)> Entries { get; } = [];
        public Task AppendAsync(OperationLogLevel level, string source, string message, CancellationToken ct = default, bool? durable = null)
        {
            lock (Entries) Entries.Add((level, source, message));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<LogEntry>> QueryAsync(OperationLogLevel? l, string? s, DateTimeOffset? f, DateTimeOffset? t, int n, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LogEntry>>([]);
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteForContainerAsync(int accountId, string container, CancellationToken ct = default) => Task.CompletedTask;
        public Task PurgeBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default) => Task.CompletedTask;
        public Task TrimAsync(int? maxAgeDays, DateTimeOffset now, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Captures NotifyAsync calls (event/title/body) so we can assert "an unreadable file pushed a notification".</summary>
    private sealed class CapturingNotifier : INotifier
    {
        public List<(NotificationEvents Event, string Title, string Body)> Notifications { get; } = [];
        public Task NotifyAsync(NotificationEvents evt, string title, string body, CancellationToken ct = default)
        {
            lock (Notifications) Notifications.Add((evt, title, body));
            return Task.CompletedTask;
        }
    }

    /// <summary>Builds a runnable orchestrator; with no differ it uses the real hasher, and passing a custom differ lets you simulate a file being unreadable.</summary>
    private (BackupOrchestrator Orchestrator, IBackupInfoStore Store, BlobClientFactory Factory) Build(
        BackupDiffer? differ = null, IOperationLog? opLog = null, INotifier? notifier = null)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var tag = Guid.NewGuid().ToString("N");
        var staging = new StagingArea(
            Path.Combine(_temp, "compress-" + tag), Path.Combine(_temp, "staged-" + tag), () => 200_000_000);
        var authority = new TestLocalAuthority(store);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), differ ?? new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked,
            notifier: notifier, opLog: opLog);
        return (orchestrator, store, factory);
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

    [SkippableFact]
    public async Task Each_Unreadable_File_Produces_One_Log_Entry_Carrying_The_System_Reason()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var log = new CapturingLog();
        const string reason = "The process cannot access the file 'locked.mdf' because it is being used by another process.";
        var differ = new BackupDiffer(new ThrowingHasher("locked.mdf", new IOException(reason)));
        var (orchestrator, _, factory) = Build(differ, log);
        var account = AzuriteAccount();
        var name = RandomName("unreadwarn-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("locked.mdf", "database content");
            WriteText("plain.txt", "ordinary file");

            await orchestrator.RunAsync(Request(account, name));

            var expectedSource = $"backup:{account.Id}/{name}";
            // Now that the UnrecoverableError event is reused, the log level follows that event's mapping and becomes Error (no longer Warning) — a deliberate
            // consequence: unreadable files are now reported at the same level as "kept changing during processing".
            var entry = Assert.Single(log.Entries, e => e.Level == OperationLogLevel.Error);
            Assert.Equal(expectedSource, entry.Source);
            Assert.Contains("locked.mdf", entry.Message);
            Assert.Contains(reason, entry.Message); // the verbatim reason must survive as-is and must not be flattened into one "cannot read"
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>The core assertion of this fix: an unreadable file no longer just lands in the pull-only operation log, it must also be pushed out through the notification webhook
    /// (reusing the existing UnrecoverableError event, no new toggle needed) — otherwise in an unattended deployment nobody ever finds out.</summary>
    [SkippableFact]
    public async Task Each_Unreadable_File_Raises_An_UnrecoverableError_Notification()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var notifier = new CapturingNotifier();
        const string reason = "The process cannot access the file 'locked.mdf' because it is being used by another process.";
        var differ = new BackupDiffer(new ThrowingHasher("locked.mdf", new IOException(reason)));
        var (orchestrator, _, factory) = Build(differ, notifier: notifier);
        var account = AzuriteAccount();
        var name = RandomName("unreadnotify-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("locked.mdf", "database content");
            WriteText("plain.txt", "ordinary file");

            await orchestrator.RunAsync(Request(account, name));

            var notification = Assert.Single(notifier.Notifications, n => n.Event == NotificationEvents.UnrecoverableError);
            Assert.Contains("locked.mdf", notification.Title);
            Assert.Contains(reason, notification.Body); // the verbatim reason must be pushed along with it and must not be flattened
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Final-review Minor: UnreadableFiles used to be a write-only field with no consumer whatsoever. Each unreadable file did push
    /// a warning of its own, but those warnings drown among the other messages, while "backup succeeded" is the one the operator definitely reads — and it said not
    /// a word about any file being skipped, so a "successful" backup could completely mask files that were never stored this run. When it is non-zero it must go into the summary;
    /// when it is zero it must not add noise, otherwise every normal backup drags along a meaningless "0 unreadable".</summary>
    [SkippableFact]
    public async Task The_Success_Notification_Reports_Skipped_Files_Only_When_There_Are_Any()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var account = AzuriteAccount();

        // Case one: a file is unreadable → the success notification's summary must carry the count.
        var withUnreadable = new CapturingNotifier();
        var differ = new BackupDiffer(new ThrowingHasher("locked.mdf",
            new IOException("The process cannot access the file because it is being used by another process.")));
        var (orchestrator, _, factory) = Build(differ, notifier: withUnreadable);
        var name = RandomName("unreadsummary-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            WriteText("locked.mdf", "database content");
            WriteText("plain.txt", "ordinary file");

            await orchestrator.RunAsync(Request(account, name));

            var success = Assert.Single(withUnreadable.Notifications, n => n.Event == NotificationEvents.BackupSuccess);
            // The wording changed when the summary was reworked (the message now also carries added/changed/deleted counts and byte totals; see BackupSummary for the layout),
            // but what this test nails has never been that sentence itself — it is that "the number of skipped files must appear in the success summary".
            Assert.Contains("1 unreadable", success.Body);
        }
        finally { await container.DeleteIfExistsAsync(); }

        // Case two: everything readable → the word unreadable must not appear in the summary. This reverse assertion guards against overcorrection:
        // tacking "0 unreadable file(s) skipped" onto every normal backup would turn this signal into background noise in no time.
        var allReadable = new CapturingNotifier();
        var (clean, _, factory2) = Build(notifier: allReadable);
        var name2 = RandomName("readsummary-");
        var container2 = factory2.CreateServiceClient(account).GetBlobContainerClient(name2);
        await container2.CreateIfNotExistsAsync();
        try
        {
            await clean.RunAsync(Request(account, name2));

            var success = Assert.Single(allReadable.Notifications, n => n.Event == NotificationEvents.BackupSuccess);
            Assert.DoesNotContain("unreadable", success.Body, StringComparison.OrdinalIgnoreCase);
        }
        finally { await container2.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task The_Run_Result_Counts_Unreadable_Files()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var differ = new BackupDiffer(new ThrowingHasher("locked.mdf",
            new IOException("The process cannot access the file because it is being used by another process.")));
        var (orchestrator, _, factory) = Build(differ);
        var account = AzuriteAccount();
        var name = RandomName("unreadcnt-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("locked.mdf", "database content");
            WriteText("plain.txt", "ordinary file");

            var result = await orchestrator.RunAsync(Request(account, name));

            Assert.Equal(1, result.UnreadableFiles);
            Assert.Equal(1, result.Version); // the backup itself still completes successfully and produces a new version
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Decision 8: a file that stays in use long-term warns on every single run. That is deliberate — it really is not being backed up.
    /// If the second run went silent, the operator would think the problem had fixed itself.</summary>
    [SkippableFact]
    public async Task Two_Consecutive_Runs_Each_Report_About_The_Same_File()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var log = new CapturingLog();
        var differ = new BackupDiffer(new ThrowingHasher("locked.mdf",
            new IOException("locked by another process")));
        var (orchestrator, _, factory) = Build(differ, log);
        var account = AzuriteAccount();
        var name = RandomName("unreadrpt-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("locked.mdf", "database content");

            var r1 = await orchestrator.RunAsync(Request(account, name)); // first run: produces one warning
            var r2 = await orchestrator.RunAsync(Request(account, name)); // second run: the file is still locked, so it must produce another one rather than go silent

            Assert.Equal(1, r1.Version);
            Assert.Equal(2, r2.Version); // the second run completes successfully too; a permanently unreadable file does not fail it
            Assert.Equal(1, r1.UnreadableFiles);
            Assert.Equal(1, r2.UnreadableFiles);

            var warnings = log.Entries.Where(e => e.Level == OperationLogLevel.Error && e.Message.Contains("locked.mdf")).ToList();
            Assert.Equal(2, warnings.Count); // one per run; a long-term lock must not report once and then fall silent
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
