using System.Net.Sockets;
using Azure.Storage.Blobs;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// A directory whose contents cannot be listed used to crash the whole run in the scan stage — but "wrap it in a try and skip it" is an **even worse** answer:
/// its entire subtree goes unscanned and the diff therefore classifies it all as deleted, so one permission failure wipes a whole subtree out of the index
/// and nobody notices until a restore comes up short. What this file nails is exactly that invariant: unreadable ≠ deleted, and the whole subtree must carry its old entries forward.
/// </summary>
[Trait("Category", "Integration")]
public sealed class UnreadableDirectoryTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;

    public UnreadableDirectoryTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-unreaddir-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "src");
        _temp = Path.Combine(baseDir, "temp");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        // Restore permissions first, otherwise the recursive delete gets stuck on the unreadable directory.
        try
        {
            foreach (var d in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
                try { File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
                catch { /* best effort */ }
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

    /// <summary>Captures NotifyAsync calls so we can assert on notification granularity.</summary>
    private sealed class CapturingNotifier : INotifier
    {
        public List<(NotificationEvents Event, string Title, string Body)> Notifications { get; } = [];
        public Task NotifyAsync(NotificationEvents evt, string title, string body, CancellationToken ct = default)
        {
            lock (Notifications) Notifications.Add((evt, title, body));
            return Task.CompletedTask;
        }
    }

    private BackupOrchestrator Orchestrator(
        BlobClientFactory factory, IBackupInfoStore store, INotifier? notifier = null)
    {
        var staging = new StagingArea(
            Path.Combine(_temp, "compress-" + Guid.NewGuid().ToString("N")),
            Path.Combine(_temp, "staged-" + Guid.NewGuid().ToString("N")), () => 200_000_000);
        var authority = new TestLocalAuthority(store);
        return new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked,
            notifier: notifier);
    }

    private BackupRequest Request(Account account, string container) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _root,
        Name = "photos",
        Password = null,
        Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
    };

    /// <summary>Core invariant: after a successful v1 backup the directory becomes unlistable, and v2 must **carry forward** every entry of the subtree
    /// and stamp UnreadableAt on them, rather than classify them as deleted. Classifying them as deleted means: those files vanish from the new version's index,
    /// and retention then reclaims their data blobs as unreferenced — one permission failure causing permanent data loss.</summary>
    [SkippableFact]
    public async Task An_Unreadable_Directory_Carries_Its_Subtree_Forward_Instead_Of_Deleting_It()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");
        Skip.If(OperatingSystem.IsWindows(), "Relies on Unix permission bits.");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var notifier = new CapturingNotifier();

        var account = AzuriteAccount();
        var name = RandomName("unreaddir-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        var lockedDir = Path.Combine(_root, "vault");

        try
        {
            WriteText("outside.txt", "not affected");
            WriteText("vault/a.txt", "secret a");
            WriteText("vault/b.txt", "secret b");
            WriteText("vault/deep/c.txt", "secret c"); // one level of nesting, to verify the coverage is the whole subtree and not just direct children

            // v1: everything readable, a normal backup.
            var v1 = await Orchestrator(factory, store).RunAsync(Request(account, name));
            Assert.Equal(1, v1.Version);

            var info1 = await store.ReadInfoAsync(account, name, null);
            var idx1 = await store.ReadIndexAsync(account, name, info1!.Versions[0].IndexBlob, null);
            var storageBefore = idx1.Entries.ToDictionary(e => e.Path, e => e.Storage!.Ref, StringComparer.Ordinal);
            Assert.Equal(4, idx1.Entries.Count);

            // v2: the whole directory has become unreadable.
            File.SetUnixFileMode(lockedDir, UnixFileMode.None);
            var v2 = await Orchestrator(factory, store, notifier).RunAsync(Request(account, name));

            Assert.Equal(2, v2.Version); // did not crash in the scan stage
            Assert.Equal(3, v2.UnreadableFiles); // all three entries in the subtree count as unreadable

            var info2 = await store.ReadInfoAsync(account, name, null);
            var idx2 = await store.ReadIndexAsync(account, name, info2!.Versions[1].IndexBlob, null);

            // The whole subtree must still be there, carrying the original storage references (nothing re-uploaded, nothing classified as deleted).
            foreach (var path in new[] { "vault/a.txt", "vault/b.txt", "vault/deep/c.txt" })
            {
                var entry = Assert.Single(idx2.Entries, e => e.Path == path);
                Assert.NotNull(entry.UnreadableAt);
                Assert.Equal(storageBefore[path], entry.Storage!.Ref);
                Assert.True(await container.GetBlobClient(entry.Storage.Ref).ExistsAsync(),
                    $"data blob for {path} must survive"); // if classified as deleted, retention would reclaim it
            }

            // Files outside the directory are completely unaffected.
            Assert.Single(idx2.Entries, e => e.Path == "outside.txt");

            // Notifications are aggregated into one per directory rather than one per file in the subtree — a directory with five thousand files
            // would become five thousand webhooks, which both drowns the operator and stalls the backup on pushing them.
            var dirNotices = notifier.Notifications
                .Where(n => n.Event == NotificationEvents.UnrecoverableError && n.Title.Contains("vault")).ToList();
            Assert.Single(dirNotices);
            Assert.Contains("Directory unreadable", dirNotices[0].Title);
            Assert.Contains("3 entries", dirNotices[0].Body);
            Assert.DoesNotContain(notifier.Notifications, n => n.Title.Contains("vault/a.txt"));
        }
        finally
        {
            try { File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch { /* best effort */ }
            await container.DeleteIfExistsAsync();
        }
    }

    /// <summary>The question UnreadableAt answers is "since when has this content stopped being updated". It used to be refreshed to
    /// UtcNow every run, which erased the answer each time and left only "we could not read it just now" — the operator could no longer ask "how long has this file been missing from backups".
    /// Three runs back to back: the timestamp in the third run's index must still be the one from the moment of the second run.</summary>
    [SkippableFact]
    public async Task The_Unreadable_Timestamp_Records_When_It_First_Went_Unread()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");
        Skip.If(OperatingSystem.IsWindows(), "Relies on Unix permission bits.");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());

        var account = AzuriteAccount();
        var name = RandomName("unreadstamp-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        var lockedDir = Path.Combine(_root, "vault");

        try
        {
            WriteText("vault/a.txt", "content");
            await Orchestrator(factory, store).RunAsync(Request(account, name)); // v1: readable

            File.SetUnixFileMode(lockedDir, UnixFileMode.None);
            await Orchestrator(factory, store).RunAsync(Request(account, name)); // v2: unreadable for the first time

            var info2 = await store.ReadInfoAsync(account, name, null);
            var idx2 = await store.ReadIndexAsync(account, name, info2!.Versions[1].IndexBlob, null);
            var firstSeen = idx2.Entries.Single(e => e.Path == "vault/a.txt").UnreadableAt;
            Assert.NotNull(firstSeen);

            await Task.Delay(1100); // the timestamp has one-second resolution, so this guarantees that "if it were refreshed" it would be a distinguishably new value
            await Orchestrator(factory, store).RunAsync(Request(account, name)); // v3: still unreadable

            var info3 = await store.ReadInfoAsync(account, name, null);
            var idx3 = await store.ReadIndexAsync(account, name, info3!.Versions[2].IndexBlob, null);
            var stillFirstSeen = idx3.Entries.Single(e => e.Path == "vault/a.txt").UnreadableAt;

            Assert.Equal(firstSeen, stillFirstSeen); // it records "since when", not "just now"
        }
        finally
        {
            try { File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch { /* best effort */ }
            await container.DeleteIfExistsAsync();
        }
    }

    /// <summary>If an unreadable directory contains empty directories recorded by the previous version, those have to be carried across too: using this run's scan result directly
    /// would make those empty directories vanish from the new version, leaving a hole in the restored directory structure.</summary>
    [SkippableFact]
    public async Task Empty_Directories_Under_An_Unreadable_Directory_Are_Carried_Forward()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");
        Skip.If(OperatingSystem.IsWindows(), "Relies on Unix permission bits.");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());

        var account = AzuriteAccount();
        var name = RandomName("unreademptydir-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        var lockedDir = Path.Combine(_root, "vault");

        try
        {
            WriteText("vault/a.txt", "content");
            Directory.CreateDirectory(Path.Combine(_root, "vault", "placeholder"));

            await Orchestrator(factory, store).RunAsync(Request(account, name));

            var info1 = await store.ReadInfoAsync(account, name, null);
            var idx1 = await store.ReadIndexAsync(account, name, info1!.Versions[0].IndexBlob, null);
            Assert.Contains("vault/placeholder", idx1.EmptyDirs);

            File.SetUnixFileMode(lockedDir, UnixFileMode.None);
            await Orchestrator(factory, store).RunAsync(Request(account, name));

            var info2 = await store.ReadInfoAsync(account, name, null);
            var idx2 = await store.ReadIndexAsync(account, name, info2!.Versions[1].IndexBlob, null);

            Assert.Contains("vault/placeholder", idx2.EmptyDirs); // the directory structure must not lose a piece just because it could not be read
            Assert.DoesNotContain("vault", idx2.EmptyDirs);       // and the unreadable directory itself must never be treated as an empty directory
        }
        finally
        {
            try { File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch { /* best effort */ }
            await container.DeleteIfExistsAsync();
        }
    }
}
