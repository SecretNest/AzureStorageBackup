using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class VolumeBlobIOTests
{
    /// <summary>记录上传顺序的假 uploader。</summary>
    private sealed class RecordingUploader : IBlobUploader
    {
        public List<string> Order { get; } = [];

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default)
        {
            Order.Add(blobName);
            return Task.FromResult(true);
        }

        public Task UploadBatchAsync(
            Account account, string container, IReadOnlyList<UploadItem> items,
            int maxConcurrency, RetryOptions? retry = null, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static Account Acc() => new() { Name = "a", BlobEndpoint = "http://x", AccountKey = "k" };

    [Fact]
    public async Task Multi_Volume_Uploads_First_Volume_Last_As_Commit_Marker()
    {
        var up = new RecordingUploader();

        await VolumeBlobIO.UploadAsync(
            up, Acc(), "c", "data/h", ["/tmp/a.001", "/tmp/a.002", "/tmp/a.003"], AccessTier.Hot);

        // 倒序上传：.003 先、.001 最后（首卷作为「整族齐全」提交标记）。
        Assert.Equal(["data/h.003", "data/h.002", "data/h.001"], up.Order);
    }

    [Fact]
    public async Task Single_Volume_Uploads_Base_Name()
    {
        var up = new RecordingUploader();

        await VolumeBlobIO.UploadAsync(up, Acc(), "c", "data/h", ["/tmp/a.7z"], AccessTier.Hot);

        Assert.Equal(["data/h"], up.Order);
    }
}
