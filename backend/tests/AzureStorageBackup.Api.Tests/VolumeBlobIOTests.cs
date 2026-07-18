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
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            Order.Add(blobName);
            return Task.FromResult(true);
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            Order.Add(blobName);
            return Task.CompletedTask;
        }
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

    [Theory]
    // 自身卷：基名、卷后缀（含 >3 位数）
    [InlineData("data/abc", "data/abc", true)]
    [InlineData("data/abc", "data/abc.001", true)]
    [InlineData("data/abc", "data/abc.1000", true)]
    [InlineData("packs/1.7z", "packs/1.7z.002", true)]
    // 碰撞避让兄弟：同前缀但内容不同，必须排除（ReplaceAsync 删残留卷时不得误删）
    [InlineData("data/abc", "data/abc~1", false)]
    [InlineData("data/abc", "data/abc~1.001", false)]
    [InlineData("data/abc~1", "data/abc~10", false)]
    [InlineData("data/abc~1", "data/abc~1.001", true)]
    // 其它同前缀噪声
    [InlineData("data/abc", "data/abcd", false)]
    [InlineData("data/abc", "data/abc.00x", false)]
    [InlineData("data/abc", "data/abc.", false)]
    public void IsVolumeOf_Matches_Only_Own_Volumes_Not_Collision_Siblings(string baseRef, string name, bool expected)
        => Assert.Equal(expected, VolumeBlobIO.IsVolumeOf(baseRef, name));
}
