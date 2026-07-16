using System.Net.Sockets;
using System.Text;
using Azure.Storage.Blobs;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

[Trait("Category", "Integration")]
public sealed class BackupInfoStoreTests
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private static Account AzuriteAccount() => new()
    {
        Name = "azurite",
        BlobEndpoint = "http://127.0.0.1:10000/devstoreaccount1",
        AccountKey = AzuriteKey,
        Region = AzureRegion.Global,
    };

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static bool SevenZipAvailable() => SevenZipArchiveCodec.TryResolveExecutable() is not null;

    private static string RandomName(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private static (BackupInfoStore Store, BlobClientFactory Factory) NewStore()
    {
        var factory = new BlobClientFactory();
        return (new BackupInfoStore(factory, new SevenZipArchiveCodec()), factory);
    }

    private static BackupInfoFile SampleInfo(string name = "photos", bool encrypted = false) => new()
    {
        Backup = new BackupMeta
        {
            Name = name,
            Encrypted = encrypted,
            CreatedAt = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero),
        },
        Versions =
        [
            new BackupVersion
            {
                Version = 1,
                CreatedAt = new DateTimeOffset(2026, 7, 16, 12, 5, 0, TimeSpan.Zero),
                IndexBlob = "indexes/v1.json",
                Stats = new VersionStats(10, 1000, 10, 1000),
            },
        ],
    };

    private async Task<(BackupInfoStore Store, BlobContainerClient Container, Account Account, string Name)> SetupContainerAsync()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZipAvailable(), "7z executable not found");

        var (store, factory) = NewStore();
        var account = AzuriteAccount();
        var name = RandomName("info-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        return (store, container, account, name);
    }

    [SkippableFact]
    public async Task ReadInfo_Returns_Null_When_Absent()
    {
        var (store, container, account, name) = await SetupContainerAsync();
        try
        {
            Assert.Null(await store.ReadInfoAsync(account, name, password: null));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task WriteInfo_Applies_Access_Tier()
    {
        var (store, container, account, name) = await SetupContainerAsync();
        try
        {
            await store.WriteInfoAsync(account, name, SampleInfo(), password: null,
                tier: Azure.Storage.Blobs.Models.AccessTier.Cool);

            var props = await container.GetBlobClient(BackupDiscovery.IndexBlobName).GetPropertiesAsync();
            Assert.Equal("Cool", props.Value.AccessTier);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Info_Plain_RoundTrips()
    {
        var (store, container, account, name) = await SetupContainerAsync();
        try
        {
            await store.WriteInfoAsync(account, name, SampleInfo(), password: null);

            var back = await store.ReadInfoAsync(account, name, password: null);

            Assert.NotNull(back);
            Assert.Equal("photos", back!.Backup.Name);
            Assert.Equal(1, Assert.Single(back.Versions).Version);
            Assert.True(await container.GetBlobClient(BackupDiscovery.IndexBlobName).ExistsAsync());
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Info_Encrypted_RoundTrips_And_Uses_Enc_Blob()
    {
        var (store, container, account, name) = await SetupContainerAsync();
        try
        {
            await store.WriteInfoAsync(account, name, SampleInfo(encrypted: true), password: "s3cret");

            var back = await store.ReadInfoAsync(account, name, password: "s3cret");

            Assert.Equal("photos", back!.Backup.Name);
            Assert.True(await container.GetBlobClient(BackupDiscovery.EncryptedIndexBlobName).ExistsAsync());
            Assert.False(await container.GetBlobClient(BackupDiscovery.IndexBlobName).ExistsAsync());
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Info_Encrypted_Blob_Does_Not_Leak_Plaintext()
    {
        var (store, container, account, name) = await SetupContainerAsync();
        try
        {
            await store.WriteInfoAsync(account, name, SampleInfo("SECRET-NAME", encrypted: true), password: "pw");

            var raw = (await container.GetBlobClient(BackupDiscovery.EncryptedIndexBlobName).DownloadContentAsync())
                .Value.Content.ToArray();
            Assert.DoesNotContain("SECRET-NAME", Encoding.Latin1.GetString(raw));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task WriteInfo_Is_Atomic_Overwrite_Without_Leftover_Temp()
    {
        var (store, container, account, name) = await SetupContainerAsync();
        try
        {
            await store.WriteInfoAsync(account, name, SampleInfo("v1name"), password: null);
            await store.WriteInfoAsync(account, name, SampleInfo("v2name"), password: null);

            var back = await store.ReadInfoAsync(account, name, password: null);
            Assert.Equal("v2name", back!.Backup.Name);

            var blobs = new List<string>();
            await foreach (var b in container.GetBlobsAsync())
                blobs.Add(b.Name);
            Assert.DoesNotContain(blobs, n => n.Contains("writing"));
            Assert.Contains(BackupDiscovery.IndexBlobName, blobs);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task VersionIndex_RoundTrips_And_Reports_Blob_Name()
    {
        var (store, container, account, name) = await SetupContainerAsync();
        try
        {
            var index = new VersionIndex
            {
                Version = 3,
                Entries =
                [
                    new IndexEntry
                    {
                        Path = "a.txt", Kind = "file", Length = 5, Permissions = "0644",
                        HeadHash = "sha256:x", FullHash = "sha256:y",
                        Storage = new StorageRef { Kind = "blob", Ref = "data/sha256:y" },
                    },
                ],
                EmptyDirs = ["empty"],
            };

            var blobName = await store.WriteIndexAsync(account, name, 3, index, password: null);

            Assert.Equal("indexes/v3.json", blobName);
            var back = await store.ReadIndexAsync(account, name, blobName, password: null);
            Assert.Equal(3, back.Version);
            Assert.Equal("a.txt", Assert.Single(back.Entries).Path);
            Assert.Equal(["empty"], back.EmptyDirs);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
