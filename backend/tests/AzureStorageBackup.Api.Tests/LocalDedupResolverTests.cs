using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class LocalDedupResolverTests
{
    private static readonly BlobAddressScheme Plain = new(null, null);

    private static VersionIndex IndexWith(params IndexEntry[] entries) =>
        new() { Version = 1, Entries = [.. entries] };

    private static IndexEntry Blob(string fullHash, long len, string head, string tail, string @ref, bool raw = false, int volumes = 1) =>
        new()
        {
            Path = @ref, Kind = "file", Length = len, Permissions = "644",
            HeadHash = head, TailHash = tail, FullHash = fullHash,
            Storage = new StorageRef { Kind = "blob", Ref = @ref, Raw = raw, Volumes = volumes },
        };

    [Fact]
    public async Task Dedups_Against_Prior_Version_Content()
    {
        var r = LocalDedupResolver.Build(Plain, [IndexWith(
            Blob("xxh128:h", 100, "xxh128:hd", "xxh128:tl", "data/xxh128:h", raw: true, volumes: 3))]);

        var res = await r.ResolveAsync("xxh128:h", 100, "xxh128:hd", "xxh128:tl");

        Assert.True(res.Exists);
        Assert.Equal("data/xxh128:h", res.Ref);
        Assert.True(res.Existing!.Raw);          // 沿用既有 blob 的 raw
        Assert.Equal(3, res.Existing.Volumes);   // 沿用既有分卷数（免云端 CountVolumes）
        Assert.False(res.Collision);
    }

    [Fact]
    public async Task New_Content_Claims_Base_Address()
    {
        var r = LocalDedupResolver.Build(Plain, []);
        var res = await r.ResolveAsync("xxh128:new", 10, "xxh128:h", "xxh128:t");

        Assert.False(res.Exists);                 // 需上传
        Assert.Equal("data/xxh128:new", res.Ref);
        Assert.False(res.Collision);
    }

    [Fact]
    public async Task Same_Hash_Different_Content_Avoids_To_Suffix()
    {
        // 既有 blob：同 hash、长度 100。新文件同 hash 但长度 200（碰撞）→ 避让到 …~1。
        var r = LocalDedupResolver.Build(Plain, [IndexWith(
            Blob("xxh128:h", 100, "xxh128:hd", "xxh128:tl", "data/xxh128:h"))]);

        var res = await r.ResolveAsync("xxh128:h", 200, "xxh128:hd2", "xxh128:tl2");

        Assert.False(res.Exists);
        Assert.Equal("data/xxh128:h~1", res.Ref); // 避让后的备用名
        Assert.True(res.Collision);
    }

    [Fact]
    public async Task Same_Run_Duplicate_Waits_For_First_Uploader()
    {
        var r = LocalDedupResolver.Build(Plain, []);

        var first = await r.ResolveAsync("xxh128:d", 5, "xxh128:h", "xxh128:t");
        Assert.False(first.Exists); // 首个 → 占位上传

        // 第二个同内容：在首个 Complete 之前不应解析完成（等待上传者）。
        var secondTask = r.ResolveAsync("xxh128:d", 5, "xxh128:h", "xxh128:t");
        Assert.False(secondTask.IsCompleted);

        first.Complete(raw: true, volumes: 2); // 首个上传成功
        var second = await secondTask;

        Assert.True(second.Exists);                  // 同批去重
        Assert.Equal(first.Ref, second.Ref);
        Assert.True(second.Existing!.Raw);           // 拿到与首个一致的 raw
        Assert.Equal(2, second.Existing.Volumes);    // 与首个一致的分卷数
    }

    [Fact]
    public async Task Same_Run_Duplicate_Fails_If_First_Upload_Fails()
    {
        var r = LocalDedupResolver.Build(Plain, []);
        var first = await r.ResolveAsync("xxh128:d", 5, "xxh128:h", "xxh128:t");
        var secondTask = r.ResolveAsync("xxh128:d", 5, "xxh128:h", "xxh128:t");

        first.Fail(new InvalidOperationException("upload boom"));

        // 后到者一并失败，绝不去重到未成功上传的 blob。
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await secondTask);
    }
}
