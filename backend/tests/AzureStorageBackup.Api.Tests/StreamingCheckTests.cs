using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Acceptance for the content-level check after it moved to streaming comparison (phase 1). Three load-bearing
/// points: an encrypted + multi-volume archive passes member-by-member streaming verification; a member missing from
/// the archive must be reported as **corruption** rather than passing (`x -so` produces empty output yet exits 0 for
/// a member that does not exist); and a byte count that disagrees with the length recorded in the index also fails.
/// </summary>
[Trait("Category", "Integration")]
public sealed class StreamingCheckTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _base;
    private readonly string _src;
    private readonly string _temp;

    public StreamingCheckTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "asb-sck-" + Guid.NewGuid().ToString("N"));
        _src = Path.Combine(_base, "src");
        _temp = Path.Combine(_base, "temp");
        Directory.CreateDirectory(_src);
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

    private (BackupOrchestrator Backup, BackupChecker Checker, BlobClientFactory Factory, BackupInfoStore Store) Build()
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(Path.Combine(_temp, "c"), Path.Combine(_temp, "s"), () => 200_000_000);
        var authority = new TestLocalAuthority(store);
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked);
        var checker = new BackupChecker(
            factory, store, new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "check"));
        return (backup, checker, factory, store);
    }

    private async Task WriteSourceAsync(string relPath, int size)
    {
        var full = Path.Combine(_src, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        await File.WriteAllBytesAsync(full, bytes);
    }

    [SkippableFact]
    public async Task Deep_Check_Passes_On_Encrypted_Split_Backup()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, checker, factory, _) = Build();
        var account = AzuriteAccount();
        var name = RandomName("sck-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // One large file goes to a single-file blob (which gets split), a few small ones go into a pack; throw
            // in an empty file too.
            await WriteSourceAsync("big.bin", 400_000);
            await WriteSourceAsync("dir/small-a.bin", 3_000);
            await WriteSourceAsync("dir/small-b.bin", 7_000);
            await WriteSourceAsync("dir/zero.bin", 0);

            await backup.RunAsync(new BackupRequest
            {
                Account = account, Container = name, LocalRoot = _src, Name = "enc", Password = "pw",
                Options = new BackupEngineOptions
                {
                    VolumeBytes = 64 * 1024,
                    Plan = new PlanOptions { SingleFileThresholdBytes = 100_000 },
                },
            });

            var report = await checker.CheckAsync(
                account, name, "pw", null, new CheckOptions { Cloud = CloudCheckLevel.Content }, _src);

            Assert.Empty(report.CorruptedPaths);
            Assert.True(report.Ok);
            Assert.Equal(4, report.Findings.Count);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Deep_Check_Reports_A_Member_Missing_From_The_Pack()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, checker, factory, store) = Build();
        var account = AzuriteAccount();
        var name = RandomName("sckm-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            await WriteSourceAsync("dir/keep.bin", 4_000);
            await WriteSourceAsync("dir/lost.bin", 6_000);
            await backup.RunAsync(new BackupRequest
            {
                Account = account, Container = name, LocalRoot = _src, Name = "pack",
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1_000_000 } },
            });

            var info = await store.ReadInfoAsync(account, name, null)
                ?? throw new InvalidOperationException("no info file");
            var packId = Assert.Single(info.Packs.Keys);

            // Swap the pack for an archive that is **missing dir/lost.bin**, and update the volume sizes in the
            // info file to match — otherwise the "existence + size" level catches it first, the content level never
            // gets a turn, and the test would not be exercising this phase's change at all.
            var forged = Path.Combine(_temp, "forged.7z");
            Directory.CreateDirectory(_temp);
            await new SevenZipCompressor().CompressAsync(
                new CompressionRequest(_src, ["dir/keep.bin"], forged));
            var blobName = info.Packs[packId].Blob;
            await using (var s = File.OpenRead(forged))
                await container.GetBlobClient(blobName).UploadAsync(s, overwrite: true);

            info.Packs[packId] = info.Packs[packId] with
            {
                Volumes = 1,
                VolumeSizes = [new FileInfo(forged).Length],
            };
            await store.WriteInfoAsync(account, name, info, null);

            var report = await checker.CheckAsync(
                account, name, null, null, new CheckOptions { Cloud = CloudCheckLevel.Content });

            Assert.Contains("dir/lost.bin", report.CorruptedPaths);
            Assert.DoesNotContain("dir/keep.bin", report.CorruptedPaths);
            Assert.False(report.Ok);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Deep_Check_Reports_Length_Mismatch_Even_Without_A_Recorded_Hash()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, checker, factory, store) = Build();
        var account = AzuriteAccount();
        var name = RandomName("sckl-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            await WriteSourceAsync("solo.bin", 20_000);
            await backup.RunAsync(new BackupRequest
            {
                Account = account, Container = name, LocalRoot = _src, Name = "solo",
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
            });

            // A legacy index may have no FullHash (the field is nullable). Length is then the only line of defence
            // — strip the hash, put the length off by one byte, and the check must still report corruption;
            // otherwise "empty output + exit code 0" sails straight through.
            var info = await store.ReadInfoAsync(account, name, null)!
                ?? throw new InvalidOperationException("no info file");
            var version = info.Versions[^1];
            var index = await store.ReadIndexAsync(account, name, version.IndexBlob, null);
            var entry = Assert.Single(index.Entries);
            index.Entries[0] = entry with { FullHash = null, Length = entry.Length + 1 };
            await store.WriteIndexAsync(account, name, version.Version, index, null);

            var report = await checker.CheckAsync(
                account, name, null, null, new CheckOptions { Cloud = CloudCheckLevel.Content });

            Assert.Contains("solo.bin", report.CorruptedPaths);
            Assert.False(report.Ok);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Deep_Check_Leaves_No_Extracted_Files_Behind()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, checker, factory, _) = Build();
        var account = AzuriteAccount();
        var name = RandomName("sckt-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            await WriteSourceAsync("dir/a.bin", 5_000);
            await WriteSourceAsync("dir/b.bin", 5_000);
            await WriteSourceAsync("big.bin", 200_000);
            await backup.RunAsync(new BackupRequest
            {
                Account = account, Container = name, LocalRoot = _src, Name = "temp",
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 100_000 } },
            });

            var report = await checker.CheckAsync(
                account, name, null, null, new CheckOptions { Cloud = CloudCheckLevel.Content });
            Assert.True(report.Ok);

            // The verification workspace is wiped entirely: now that it is streaming, not even "the extracted
            // member" step should ever have appeared on disk.
            var checkTemp = Path.Combine(_temp, "check");
            Assert.True(!Directory.Exists(checkTemp)
                || !Directory.EnumerateFileSystemEntries(checkTemp, "*", SearchOption.AllDirectories).Any());
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
