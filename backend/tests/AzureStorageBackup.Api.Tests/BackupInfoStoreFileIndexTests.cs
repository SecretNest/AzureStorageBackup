using System.Net.Sockets;
using Azure.Storage.Blobs;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using static AzureStorageBackup.Api.Tests.IndexAssert;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The file-shaped half of <see cref="IBackupInfoStore"/>: an index is serialized to a file, encoded from that file
/// and uploaded straight off disk, and comes back down as a file again — so an index of any size never has to sit
/// in memory as a byte array. The bytes on the wire are the same as the in-memory members produce, which is what
/// these tests pin: whatever the file path writes, the old <c>ReadIndexAsync</c> still reads, and vice versa.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BackupInfoStoreFileIndexTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "asb-fileidx-" + Guid.NewGuid().ToString("N"));

    public BackupInfoStoreFileIndexTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* best effort */ }
    }

    private static Account AzuriteAccount() => new()
    {
        Id = 84,
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

    /// <summary>The store under test, plus a container created for it; <paramref name="volumeBytes"/> lowers the split threshold.</summary>
    private (BackupInfoStore Store, BlobContainerClient Container, Account Account, string Name) Build(int? volumeBytes = null)
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = volumeBytes is { } b
            ? new BackupInfoStore(factory, new SevenZipArchiveCodec(), Path.Combine(_temp, "store")) { IndexVolumeBytes = b }
            : new BackupInfoStore(factory, new SevenZipArchiveCodec(), Path.Combine(_temp, "store"));
        var account = AzuriteAccount();
        var name = RandomName("fileidx-");
        return (store, factory.CreateServiceClient(account).GetBlobContainerClient(name), account, name);
    }

    /// <summary>Writes <paramref name="index"/> out in <see cref="IndexStreamWriter"/> format and returns the path.</summary>
    private string Serialize(VersionIndex index, string fileName = "index.bin")
    {
        var path = Path.Combine(_temp, fileName);
        using var fs = File.Create(path);
        using var w = new IndexStreamWriter(fs);
        w.WriteHeader(index.Version, index.Entries.Count);
        foreach (var e in index.Entries) w.WriteEntry(e);
        w.WriteEmptyDirs(index.EmptyDirs);
        w.WriteUnrecoverable(index.UnrecoverablePaths);
        return path;
    }

    /// <summary>Entries with random hashes, so the encoded index really does exceed a lowered split threshold.</summary>
    private static VersionIndex BigIndex(int entries)
    {
        var list = new List<IndexEntry>(entries);
        for (var i = 0; i < entries; i++)
        {
            list.Add(new IndexEntry
            {
                Path = $"Photos/{i / 1000:D4}/IMG_{i:D7}.jpg",
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
        return new VersionIndex { Version = 9, Entries = list };
    }

    /// <summary>
    /// The file path and the byte-array path produce the same blob: an index uploaded from a file is read back by
    /// the old in-memory <c>ReadIndexAsync</c> entry for entry. Both write the same format-4 bytes through the same
    /// codec, and this is what says so — until Task 21 retires the old members, both are live and both must agree.
    /// </summary>
    [SkippableFact]
    public async Task WriteIndexFile_single_blob_matches_WriteIndexAsync()
    {
        var (store, cc, account, name) = Build();
        try
        {
            await cc.CreateIfNotExistsAsync();
            var index = IndexSamples.Sample();
            var path = Serialize(index);

            var (blobName, volumes) = await store.WriteIndexFileAsync(account, name, index.Version, path, password: null);

            Assert.Equal($"indexes/v{index.Version}.json", blobName);
            Assert.Equal(1, volumes);

            var back = await store.ReadIndexAsync(account, name, blobName, password: null);
            Assert.Equal(index.Version, back.Version);
            Assert.Equal(index.Entries.Count, back.Entries.Count);
            for (var i = 0; i < index.Entries.Count; i++)
                AssertSameEntry(index.Entries[i], back.Entries[i]);
            Assert.Equal(index.EmptyDirs, back.EmptyDirs);
            Assert.Equal(index.UnrecoverablePaths, back.UnrecoverablePaths);
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// Past the threshold the file path splits into volumes exactly as the byte-array path does — and the round
    /// trip back to a file returns every entry. Reading with <see cref="IndexStreamReader"/> rather than
    /// materializing a <c>VersionIndex</c> is the whole point: 2 000 entries here stand in for the millions that
    /// made holding the index in memory untenable.
    /// </summary>
    [SkippableFact]
    public async Task WriteIndexFile_splits_into_volumes_past_the_threshold()
    {
        var (store, cc, account, name) = Build(volumeBytes: 4096);
        try
        {
            await cc.CreateIfNotExistsAsync();
            var index = BigIndex(2_000);
            var path = Serialize(index, "big.bin");

            var (blobName, volumes) = await store.WriteIndexFileAsync(account, name, index.Version, path, password: null);
            Assert.True(volumes > 1, $"expected a split index, got {volumes} volume(s)");

            // Volume naming is the shared one, and the unsuffixed name must stay empty.
            foreach (var n in VolumeBlobIO.VolumeNames(blobName, volumes))
                Assert.True((await cc.GetBlobClient(n).ExistsAsync()).Value, $"{n} is missing");
            Assert.False((await cc.GetBlobClient(blobName).ExistsAsync()).Value,
                "a split index must not also occupy the unsuffixed name");

            var dest = Path.Combine(_temp, "back.bin");
            await store.ReadIndexToFileAsync(account, name, blobName, password: null, volumes, dest);

            using var fs = File.OpenRead(dest);
            using var reader = new IndexStreamReader(fs);
            Assert.Equal(index.Version, reader.Version);
            Assert.Equal(2_000, reader.EntryCount);
            var entries = reader.Entries().ToList();
            Assert.Equal(2_000, entries.Count);
            AssertSameEntry(index.Entries[0], entries[0]);
            AssertSameEntry(index.Entries[^1], entries[^1]);
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// With a password the archive is 7z AES-256 and the blob name carries <c>.enc</c>; the file round trip has to
    /// carry the password through both halves, and the blob must not contain the plaintext paths.
    /// </summary>
    [SkippableFact]
    public async Task ReadIndexToFile_decodes_an_encrypted_index()
    {
        var (store, cc, account, name) = Build();
        try
        {
            await cc.CreateIfNotExistsAsync();
            var index = IndexSamples.Sample();
            var path = Serialize(index, "enc.bin");

            var (blobName, volumes) = await store.WriteIndexFileAsync(account, name, index.Version, path, "s3cret");

            Assert.Equal($"indexes/v{index.Version}.json.enc", blobName);
            Assert.Equal(1, volumes);

            var dest = Path.Combine(_temp, "enc-back.bin");
            await store.ReadIndexToFileAsync(account, name, blobName, "s3cret", volumes, dest);

            Assert.Equal(await File.ReadAllBytesAsync(path), await File.ReadAllBytesAsync(dest));

            using var fs = File.OpenRead(dest);
            using var reader = new IndexStreamReader(fs);
            Assert.Equal(index.Version, reader.Version);
            Assert.Equal(index.Entries.Count, reader.EntryCount);
            var entries = reader.Entries().ToList();
            for (var i = 0; i < index.Entries.Count; i++)
                AssertSameEntry(index.Entries[i], entries[i]);
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }
}
