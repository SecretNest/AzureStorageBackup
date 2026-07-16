using System.Net.Sockets;
using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

[Trait("Category", "Integration")]
public sealed class BlobUploaderTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _dir;

    public BlobUploaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "asb-up-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

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

    private static string RandomName(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private string WriteFile(string name, string content)
    {
        var full = Path.Combine(_dir, name);
        File.WriteAllText(full, content);
        return full;
    }

    private async Task<(BlobUploader Uploader, BlobContainerClient Container, Account Account, string Name)> SetupAsync()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running on 127.0.0.1:10000");
        var factory = new BlobClientFactory();
        var account = AzuriteAccount();
        var name = RandomName("up-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        return (new BlobUploader(factory), container, account, name);
    }

    [SkippableFact]
    public async Task Uploads_File_With_Access_Tier()
    {
        var (uploader, container, account, name) = await SetupAsync();
        try
        {
            var file = WriteFile("data.bin", "payload");

            var uploaded = await uploader.UploadIfMissingAsync(account, name, "data/abc", file, AccessTier.Cool);

            Assert.True(uploaded);
            var blob = container.GetBlobClient("data/abc");
            Assert.True(await blob.ExistsAsync());
            var props = await blob.GetPropertiesAsync();
            Assert.Equal("Cool", props.Value.AccessTier);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Existing_Blob_Is_Skipped_And_Not_Overwritten()
    {
        var (uploader, container, account, name) = await SetupAsync();
        try
        {
            await container.GetBlobClient("data/x").UploadAsync(BinaryData.FromString("original"));

            var again = await uploader.UploadIfMissingAsync(
                account, name, "data/x", WriteFile("new.bin", "different"), AccessTier.Hot);

            Assert.False(again); // 已存在 → 跳过
            var content = (await container.GetBlobClient("data/x").DownloadContentAsync()).Value.Content.ToString();
            Assert.Equal("original", content);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Batch_Uploads_All_Items_Concurrently()
    {
        var (uploader, container, account, name) = await SetupAsync();
        try
        {
            var items = Enumerable.Range(0, 6)
                .Select(i => new UploadItem($"data/b{i}", WriteFile($"f{i}.bin", "c" + i), AccessTier.Hot))
                .ToList();

            await uploader.UploadBatchAsync(account, name, items, maxConcurrency: 3);

            foreach (var item in items)
                Assert.True(await container.GetBlobClient(item.BlobName).ExistsAsync());
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
