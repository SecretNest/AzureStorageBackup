using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The version index is the one blob in a backup whose size nobody can bound in advance: it grows with the file
/// count (161 bytes per entry, measured) and the file count is not knowable before the scan. At 150k files it is
/// 24 MB; ten times that puts it past what one request can carry up a home uplink, and a Put Blob that times out
/// restarts from zero, so every retry meets the same wall.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IndexVolumeTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "asb-idxvol-" + Guid.NewGuid().ToString("N"));

    public IndexVolumeTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* best effort */ }
    }

    private static Account AzuriteAccount() => new()
    {
        Id = 83,
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

    private static string RandomName(string p) => p + Guid.NewGuid().ToString("N")[..8];

    /// <summary>Entries with incompressible-ish distinct content, so the encoded index really does exceed the split threshold.</summary>
    private static VersionIndex BigIndex(int entries)
    {
        var list = new List<IndexEntry>(entries);
        for (var i = 0; i < entries; i++)
        {
            list.Add(new IndexEntry
            {
                Path = $"Photos/{i / 1000:D4}/IMG_{i:D7}_{Guid.NewGuid():N}.jpg",
                Kind = "file",
                Length = 3_500_000 + i,
                Mtime = DateTimeOffset.UnixEpoch.AddSeconds(i),
                Permissions = "644",
                HeadHash = $"xxh128:{Guid.NewGuid():N}",
                TailHash = $"xxh128:{Guid.NewGuid():N}",
                FullHash = $"xxh128:{Guid.NewGuid():N}",
                Storage = new StorageRef { Kind = "blob", Ref = $"data/{Guid.NewGuid():N}", Volumes = 1 },
            });
        }
        return new VersionIndex { Version = 1, Entries = list };
    }

    /// <summary>
    /// An index past the threshold goes up as volumes and comes back byte-identical. The volume count is returned so
    /// the caller can record it, because nothing about the blob names themselves says how many there are.
    /// </summary>
    [SkippableFact]
    public async Task A_large_index_round_trips_through_volumes()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");

        var account = AzuriteAccount();
        var name = RandomName("idxvol");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec()) { IndexVolumeBytes = 256 * 1024 };
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            await cc.CreateIfNotExistsAsync();

            // The threshold is lowered rather than the index inflated: what matters is crossing it, and the entries
            // carry random hashes precisely so the encoded form cannot compress back under it.
            var index = BigIndex(20_000);

            var (blobName, volumes) = await store.WriteIndexAsync(account, name, 1, index, password: null);
            Assert.True(volumes > 1, $"expected a split index, got {volumes} volume(s)");

            // The volumes are named exactly like a data blob's, and the base name itself must not exist.
            var expected = VolumeBlobIO.VolumeNames(blobName, volumes);
            foreach (var n in expected)
                Assert.True((await cc.GetBlobClient(n).ExistsAsync()).Value, $"{n} is missing");
            Assert.False((await cc.GetBlobClient(blobName).ExistsAsync()).Value,
                "a split index must not also occupy the unsuffixed name");

            var back = await store.ReadIndexAsync(account, name, blobName, password: null, volumes);
            Assert.Equal(index.Version, back.Version);
            Assert.Equal(index.Entries.Count, back.Entries.Count);
            Assert.Equal(index.Entries[0].Path, back.Entries[0].Path);
            Assert.Equal(index.Entries[^1].FullHash, back.Entries[^1].FullHash);
        }
        finally
        {
            await cc.DeleteIfExistsAsync();
        }
    }

    /// <summary>
    /// The index write is the one stretch of a big backup that used to run blind: at a few million entries the index
    /// is hundreds of MB, and up a home uplink and back down again for verification is minutes of "Writing index"
    /// with nothing moving on screen. Every volume goes up once and comes back once, so the stage has 2 × volumes
    /// transfers to count, and it counts each one as it completes — with its bytes, which is what the speed and the
    /// remaining time are read off.
    /// </summary>
    [SkippableFact]
    public async Task A_split_index_write_reports_every_volume_up_and_back()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");

        var account = AzuriteAccount();
        var name = RandomName("idxprog");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec()) { IndexVolumeBytes = 256 * 1024 };
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            await cc.CreateIfNotExistsAsync();
            var snapshots = new List<StageProgress>();
            // A clock that jumps a second per reading: local Azurite finishes the whole write inside the tracker's
            // 200 ms throttle window, and without this the only snapshot is the forced final one.
            var now = 0L;
            var tracker = new StageTracker("WritingIndex", total: 0, d => { lock (snapshots) snapshots.Add(d); })
            {
                Clock = () => Interlocked.Add(ref now, 1_000),
            };

            var (_, volumes) = await store.WriteIndexAsync(
                account, name, 1, BigIndex(20_000), password: null, tier: null, ct: default, progress: tracker);
            tracker.Complete();

            Assert.True(volumes > 1, $"expected a split index, got {volumes} volume(s)");
            var last = snapshots[^1];
            Assert.Equal(2 * volumes, last.Total);
            Assert.Equal(last.Total, last.Processed);
            Assert.Equal(100, last.Percent);
            Assert.True(last.Bytes > 0, "the transferred bytes are what the speed readout is computed from");
            Assert.Empty(last.ActiveItems);
            // The volumes are counted one at a time, not all at once at the end: an intermediate snapshot with some
            // but not all of them done is the whole point.
            Assert.Contains(snapshots, d => d.Processed > 0 && d.Processed < d.Total);
        }
        finally
        {
            await cc.DeleteIfExistsAsync();
        }
    }

    /// <summary>
    /// The single-blob layout is three transfers, not one: the temp blob goes up, comes back down for verification,
    /// and the verified bytes go up again under the real name. Reported as such — a stage that says "1 of 1" while
    /// the second and third transfer are still running is the same blind stretch in miniature.
    /// </summary>
    [SkippableFact]
    public async Task A_single_blob_index_write_reports_its_three_transfers()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");

        var account = AzuriteAccount();
        var name = RandomName("idxprog1");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            await cc.CreateIfNotExistsAsync();
            var snapshots = new List<StageProgress>();
            var tracker = new StageTracker("WritingIndex", total: 0, d => { lock (snapshots) snapshots.Add(d); });

            var (_, volumes) = await store.WriteIndexAsync(
                account, name, 1, BigIndex(50), password: null, tier: null, ct: default, progress: tracker);
            tracker.Complete();

            Assert.Equal(1, volumes);
            var last = snapshots[^1];
            Assert.Equal(3, last.Total);
            Assert.Equal(3, last.Processed);
            Assert.True(last.Bytes > 0);
        }
        finally
        {
            await cc.DeleteIfExistsAsync();
        }
    }

    /// <summary>A small index keeps the single-blob layout, which is what every existing backup already has on disk.</summary>
    [SkippableFact]
    public async Task A_small_index_stays_one_blob()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");

        var account = AzuriteAccount();
        var name = RandomName("idxsmall");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            await cc.CreateIfNotExistsAsync();
            var (blobName, volumes) = await store.WriteIndexAsync(
                account, name, 1, BigIndex(50), password: null);

            Assert.Equal(1, volumes);
            Assert.True((await cc.GetBlobClient(blobName).ExistsAsync()).Value);
            Assert.False((await cc.GetBlobClient(blobName + ".001").ExistsAsync()).Value);

            // And it reads back through the default parameter, which is the path every pre-format-5 caller takes.
            Assert.Equal(50, (await store.ReadIndexAsync(account, name, blobName, password: null)).Entries.Count);
        }
        finally
        {
            await cc.DeleteIfExistsAsync();
        }
    }

    /// <summary>
    /// The compatibility guarantee, stated as a test: an info file written before info format 5 has no volume count,
    /// and reads back as 1 — the single-blob layout it was actually written with. Getting this wrong would make
    /// every existing backup unreadable, which is the whole reason the count is recorded rather than probed for.
    /// </summary>
    [Fact]
    public void An_info_file_without_the_volume_count_reads_as_a_single_blob()
    {
        var info = new BackupInfoFile
        {
            Backup = new BackupMeta { Name = "old", CreatedAt = DateTimeOffset.UnixEpoch },
            Versions =
            {
                new BackupVersion
                {
                    Version = 1,
                    CreatedAt = DateTimeOffset.UnixEpoch,
                    IndexBlob = "indexes/v1.json",
                    Stats = new VersionStats(1, 1, 1, 1),
                },
            },
        };

        var round = IndexSerializer.DeserializeInfoFile(IndexSerializer.SerializeInfoFile(info));

        Assert.Equal(1, round.Versions[0].IndexVolumes);
        // VolumeNames collapses to the bare name at 1, so the read path is byte-for-byte the one it always was.
        Assert.Equal(["indexes/v1.json"], VolumeBlobIO.VolumeNames(round.Versions[0].IndexBlob, round.Versions[0].IndexVolumes));
    }
}
