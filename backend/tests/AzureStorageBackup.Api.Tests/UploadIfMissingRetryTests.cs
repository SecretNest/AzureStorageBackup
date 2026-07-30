using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// "仅当不存在时上传"必须是**幂等**的，而且这件事得由服务端保证。
/// <para>
/// 从前是"先 Exists 再上传"，两步之间有个不原子的窗口，而上传是会重试的：网络抖一下（NAS 上
/// 很常见），服务端其实已经写进去了、客户端只收到超时，重试于是去覆盖一个已经存在的 blob。
/// 数据层是 Archive 时这一下直接失败——归档 blob 不允许被覆盖，返回 409 BlobArchived，
/// 而它不在可重试之列，整轮备份就此倒掉，容器里只留下之前传成功的那些对象。
/// 这是用户实际踩到的。
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
    /// 同一个 blob 名连传两次：第二次必须**安静地**返回"没传"，而不是去覆盖。
    /// 这就是重试碰上自己刚写成功那一份时的形状。
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

            // 第二次：blob 已经在了。必须返回 false 且不抛——重试路径正是走到这里。
            Assert.False(await uploader.UploadIfMissingAsync(
                account, name, "data/x", file, AccessTier.Hot, null, CancellationToken.None, null, null));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 并发对同一个 blob 名走 if-missing：**恰好一个**报告"传了"，其余安静返回 false。
    /// 从前两个任务会先后走过存在性检查、都看到"不存在"，于是都去写。
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
    /// 覆盖语义**不**受影响：修复与死重压实要能替换已有对象，那条路不该被条件请求挡住。
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
