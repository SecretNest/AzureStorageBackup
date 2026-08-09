using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The <c>raw</c> flag in the index says "what lies inside this blob: raw bytes, or a 7z archive". The moment it
/// disagrees with the blob's actual content, restore writes the archive itself out as the file content — a restore
/// that looks entirely successful, yet produces broken files.
/// <para>
/// Corruption of this kind really did happen (two files with identical content in the same batch were assigned
/// different storage forms and uploaded separately, see <see cref="EmptyFileRoundTripTests"/>) and has been fixed at
/// the producing end. But backups that were **already written** cannot be fixed retroactively, so this file answers
/// an operational question: given a backup of unknown provenance, can the check feature we already ship tell whether
/// it has this defect? The answer has to be definite — otherwise the only reassurance left to a user is "redo
/// everything from scratch".
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class RawFlagMismatchDetectionTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _src;
    private readonly string _temp;

    public RawFlagMismatchDetectionTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-rawflag-" + Guid.NewGuid().ToString("N"));
        _src = Path.Combine(baseDir, "src");
        _temp = Path.Combine(baseDir, "temp");
        Directory.CreateDirectory(_src);
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_src)!, recursive: true); } catch { /* best effort */ }
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

    /// <summary>
    /// Back up a file that goes to a 7z single-file blob (raw=false), then flip the raw flag on that index entry to
    /// true — what comes out is exactly the shape of those corrupt backups: an archive inside the blob while the
    /// index claims it holds raw bytes. Then run a Content-level check and see whether it recognises the problem.
    /// </summary>
    [SkippableFact]
    public async Task Content_Level_Check_Catches_A_Raw_Flag_That_Lies()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var authority = new TestLocalAuthority(store);
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked);
        var checker = new BackupChecker(
            factory, store, new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "check"));

        var account = AzuriteAccount();
        var name = RandomName("rawflag-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // Incompressible content: the archive bytes differ clearly from the raw bytes, so flipping the flag
            // produces a genuine mismatch.
            var payload = new byte[50_000];
            new Random(4242).NextBytes(payload);
            Directory.CreateDirectory(Path.Combine(_src, "solo"));
            await File.WriteAllBytesAsync(Path.Combine(_src, "solo", "a.bin"), payload);

            await backup.RunAsync(new BackupRequest
            {
                Account = account,
                Container = name,
                LocalRoot = _src,
                Name = "rawflag",
                // DontGroup forces a single-file blob; DontCompress is not set, so it is a 7z archive (raw=false).
                Options = new BackupEngineOptions { DontGroup = new IgnoreRuleSet(["solo/**"]) },
            });

            // The healthy backup has to be green first, or the assertions below prove nothing.
            var healthy = await checker.CheckAsync(
                account, name, null, null, new CheckOptions { Cloud = CloudCheckLevel.Content });
            Assert.True(healthy.Ok, "a healthy backup should pass the Content-level check");

            // Flip the raw flag: the blob still holds an archive while the index starts claiming it is raw bytes.
            var info = await store.ReadInfoAsync(account, name, null);
            var version = info!.Versions[^1];
            var index = await store.ReadIndexAsync(account, name, version.IndexBlob, null);
            var target = index.Entries.Single(e => e.Path == "solo/a.bin");
            Assert.NotNull(target.Storage);
            Assert.False(target.Storage!.Raw, "precondition: this entry should be a 7z archive");

            var tampered = new VersionIndex
            {
                Version = index.Version,
                EmptyDirs = index.EmptyDirs,
                Entries = [.. index.Entries.Select(e => e.Path == "solo/a.bin"
                    ? e with { Storage = e.Storage! with { Raw = true } }
                    : e)],
            };
            await store.WriteIndexAsync(account, name, version.Version, tampered, null);

            var report = await checker.CheckAsync(
                account, name, null, null, new CheckOptions { Cloud = CloudCheckLevel.Content });

            // This is the entire reason this file exists: corruption of this kind must be detectable with the
            // check feature we already ship, and it must point precisely at the offending file, not just say
            // "something is wrong".
            Assert.False(report.Ok, "the raw flag disagrees with the blob's actual content; the Content-level check must report it");
            Assert.Contains("solo/a.bin", report.CorruptedPaths);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// The other direction has to be caught just as well: raw bytes inside the blob while the index says archive.
    /// Only with both directions covered can we tell a user "one Content-level check will tell you".
    /// </summary>
    [SkippableFact]
    public async Task Content_Level_Check_Catches_The_Mismatch_In_The_Other_Direction()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress2"), Path.Combine(_temp, "staged2"), () => 200_000_000);
        var authority = new TestLocalAuthority(store);
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked);
        var checker = new BackupChecker(
            factory, store, new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "check2"));

        var account = AzuriteAccount();
        var name = RandomName("rawflag2-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            var payload = new byte[50_000];
            new Random(777).NextBytes(payload);
            Directory.CreateDirectory(Path.Combine(_src, "raw"));
            await File.WriteAllBytesAsync(Path.Combine(_src, "raw", "b.bin"), payload);

            await backup.RunAsync(new BackupRequest
            {
                Account = account,
                Container = name,
                LocalRoot = _src,
                Name = "rawflag2",
                // DontGroup + DontCompress + no password → raw upload (the blob holds the raw bytes as-is).
                Options = new BackupEngineOptions
                {
                    DontGroup = new IgnoreRuleSet(["raw/**"]),
                    DontCompress = new IgnoreRuleSet(["raw/**"]),
                },
            });

            var info = await store.ReadInfoAsync(account, name, null);
            var version = info!.Versions[^1];
            var index = await store.ReadIndexAsync(account, name, version.IndexBlob, null);
            var target = index.Entries.Single(e => e.Path == "raw/b.bin");
            Assert.True(target.Storage!.Raw, "precondition: this entry should be a raw upload");

            var tampered = new VersionIndex
            {
                Version = index.Version,
                EmptyDirs = index.EmptyDirs,
                Entries = [.. index.Entries.Select(e => e.Path == "raw/b.bin"
                    ? e with { Storage = e.Storage! with { Raw = false } }
                    : e)],
            };
            await store.WriteIndexAsync(account, name, version.Version, tampered, null);

            var report = await checker.CheckAsync(
                account, name, null, null, new CheckOptions { Cloud = CloudCheckLevel.Content });

            Assert.False(report.Ok, "the index claims an archive while the blob holds raw bytes; the Content-level check must report it");
            Assert.Contains("raw/b.bin", report.CorruptedPaths);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
