using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// "Upload only if it is missing" must be **idempotent**, and it is the server side that has to guarantee that.
/// <para>
/// It used to be "Exists first, then upload", with a non-atomic window between the two steps — and uploads get retried: the network
/// hiccups (very common on a NAS), the server has actually already written the data while the client only sees a timeout, and the
/// retry then goes off and overwrites a blob that already exists.
/// On the Archive tier that fails outright — an archived blob may not be overwritten, it returns 409 BlobArchived, which is not on
/// the retryable list, so the entire backup run falls over and the container keeps only the objects that were uploaded before it.
/// This is something a user actually hit.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class UploadIfMissingRetryTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _dir;

    public UploadIfMissingRetryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "asb-ifmissing-" + Guid.NewGuid().ToString("N"));
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
        AccountKeyProtected = TestSecrets.Protect(AzuriteKey),
        Region = AzureRegion.Global,
    };

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static string RandomName(string p) => p + Guid.NewGuid().ToString("N")[..8];

    private string WriteFile(string name, int size)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    /// <summary>
    /// Upload the same blob name twice in a row: the second time must **quietly** report "did not upload" instead of overwriting.
    /// This is exactly the shape of a retry running into the copy it had just written successfully.
    /// </summary>
    [SkippableFact]
    public async Task Uploading_The_Same_Name_Twice_Is_A_No_Op_The_Second_Time()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var uploader = new BlobUploader(factory);
        var account = AzuriteAccount();
        var name = RandomName("ifmissing-");
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            var file = WriteFile("v1", 4096);

            Assert.True(await uploader.UploadIfMissingAsync(
                account, name, "data/x", file, AccessTier.Hot, null, CancellationToken.None, null, null));

            // Second time around: the blob is already there. Must return false and must not throw — this is precisely where the retry path lands.
            Assert.False(await uploader.UploadIfMissingAsync(
                account, name, "data/x", file, AccessTier.Hot, null, CancellationToken.None, null, null));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// Concurrent if-missing uploads against the same blob name: **exactly one** reports "uploaded", the rest quietly return false.
    /// It used to be that two tasks would each walk through the existence check, both see "not there", and both go write.
    /// </summary>
    [SkippableFact]
    public async Task Concurrent_If_Missing_Uploads_Elect_Exactly_One_Writer()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var uploader = new BlobUploader(factory);
        var account = AzuriteAccount();
        var name = RandomName("ifmissingrace-");
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            var file = WriteFile("v1", 64 * 1024);

            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                uploader.UploadIfMissingAsync(
                    account, name, "data/shared", file, AccessTier.Hot, null, CancellationToken.None, null, null)));

            Assert.Equal(1, results.Count(uploaded => uploaded));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// Overwrite semantics are **not** affected: repair and dead-weight compaction have to be able to replace an existing object, and that path must not be blocked by the conditional request.
    /// </summary>
    [SkippableFact]
    public async Task Overwrite_Still_Replaces_An_Existing_Blob()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var uploader = new BlobUploader(factory);
        var account = AzuriteAccount();
        var name = RandomName("overwrite-");
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            await uploader.UploadIfMissingAsync(
                account, name, "data/y", WriteFile("small", 100), AccessTier.Hot, null,
                CancellationToken.None, null, null);

            await uploader.UploadOverwriteAsync(
                account, name, "data/y", WriteFile("big", 5000), AccessTier.Hot, null, CancellationToken.None);

            Assert.Equal(5000, (await cc.GetBlobClient("data/y").GetPropertiesAsync()).Value.ContentLength);
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }
}
