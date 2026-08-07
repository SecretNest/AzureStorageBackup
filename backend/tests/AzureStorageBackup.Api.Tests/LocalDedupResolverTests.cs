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

    /// <summary>
    /// Task 10 采纳 journal 时的折入项：<c>confirmed</c> 里的块要和索引里的块享受同一套去重待遇。
    /// 这条覆盖三项里的第一项——<c>byContent</c>：跨版本去重直接命中。
    /// </summary>
    [Fact]
    public async Task Confirmed_Blob_Dedups_Like_An_Indexed_One()
    {
        var confirmed = new[]
        {
            new ConfirmedBlob("xxh128:h", 100, "xxh128:hd", "xxh128:tl",
                new ResolvedBlob("data/xxh128:h", Raw: true, Volumes: 2, VolumeSizes: [60, 40])),
        };
        var r = LocalDedupResolver.Build(Plain, [], confirmed);

        var res = await r.ResolveAsync("xxh128:h", 100, "xxh128:hd", "xxh128:tl");

        Assert.True(res.Exists);
        Assert.Equal("data/xxh128:h", res.Ref);
        Assert.True(res.Existing!.Raw);
        Assert.Equal(2, res.Existing.Volumes);
        Assert.False(res.Collision);
    }

    /// <summary>
    /// 折入项第二条——<c>refs</c>：confirmed 块占的地址一样要挡碰撞。只喂 byContent 不喂 refs 的话，
    /// 同 hash 不同内容的新文件会直接把这个地址当空的抢占，而不是避让到 …~1——那就是把新内容写进
    /// 了 confirmed 块正占着的地址上。
    /// </summary>
    [Fact]
    public async Task Confirmed_Blob_Ref_Is_Guarded_Against_Collision()
    {
        var confirmed = new[]
        {
            new ConfirmedBlob("xxh128:h", 100, "xxh128:hd", "xxh128:tl",
                new ResolvedBlob("data/xxh128:h", Raw: true, Volumes: 1, VolumeSizes: [100])),
        };
        var r = LocalDedupResolver.Build(Plain, [], confirmed);

        // 同 hash（地址算法只看 fullHash）、内容不同（长度/头/尾都变了）→ 碰撞，必须避让。
        var res = await r.ResolveAsync("xxh128:h", 200, "xxh128:hd2", "xxh128:tl2");

        Assert.False(res.Exists);
        Assert.Equal("data/xxh128:h~1", res.Ref);
        Assert.True(res.Collision);
    }

    /// <summary>
    /// 折入项第三条——<c>heads</c>：confirmed 块要能被预筛问到，否则同内容不同路径的文件
    /// 会在预筛这一关就被判"没有候选"，白白重压一遍（见 JournalResume.ConfirmedBlobs 的说明）。
    /// </summary>
    [Fact]
    public void Confirmed_Blob_Participates_In_Prescreen()
    {
        var confirmed = new[]
        {
            new ConfirmedBlob("xxh128:h", 100, "xxh128:hd", "xxh128:tl",
                new ResolvedBlob("data/xxh128:h", Raw: true, Volumes: 1, VolumeSizes: [100])),
        };
        var r = LocalDedupResolver.Build(Plain, [], confirmed);

        Assert.True(r.MayDeduplicate(100, "xxh128:hd"));
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

        first.Complete(raw: true, volumes: 2, volumeSizes: [111, 222]); // 首个上传成功
        var second = await secondTask;

        Assert.True(second.Exists);                  // 同批去重
        Assert.Equal(first.Ref, second.Ref);
        Assert.True(second.Existing!.Raw);           // 拿到与首个一致的 raw
        Assert.Equal(2, second.Existing.Volumes);    // 与首个一致的分卷数
        Assert.Equal([111L, 222L], second.Existing.VolumeSizes); // 与首个一致的分卷尺寸
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

    /// <summary>Task 7 回归：闸门把上传失败的整件活原样重试，重试用的是**同一个**内容身份。
    /// 首次占位失败后必须把 ref 让出来——不让的话，重试者会撞上那个已经判死的占位，直接等一个
    /// 永远失败的 Completion、原样重放同一个异常，闸门的"退避后再试一次"就变成了摆设：
    /// 不管等多久、放行多少次，第二次真正的上传尝试永远不会发生。</summary>
    [Fact]
    public async Task Retry_After_Failure_Gets_A_Fresh_Claim_Not_The_Stale_One()
    {
        var r = LocalDedupResolver.Build(Plain, []);

        var first = await r.ResolveAsync("xxh128:d", 5, "xxh128:h", "xxh128:t");
        Assert.False(first.Exists);
        first.Fail(new InvalidOperationException("upload boom"));

        // 整件重试：同一个内容身份再来一次，必须拿到一个全新的、未失败过的占位，
        // 而不是原样重放上一次的异常。
        var retry = await r.ResolveAsync("xxh128:d", 5, "xxh128:h", "xxh128:t");
        Assert.False(retry.Exists);
        Assert.Equal(first.Ref, retry.Ref);

        // 这一次占位真的能被后续上传完成——证明它不是共享着上一个已判死的 TaskCompletionSource。
        retry.Complete(raw: false, volumes: 1, volumeSizes: [5]);
    }

    /// <summary>
    /// 「让出 ref」这一笔顺手把预约表的索引器从**全**变成了**偏**：抢占失败的后到者随后要读一次
    /// <c>_run[refName]</c>，而持有者可能恰好在这两步之间失败并把这条记录撤走。
    /// <para>
    /// 撞上的后果不是"重试一次"：<see cref="KeyNotFoundException"/> 不在
    /// <see cref="TransientErrors"/> 的瞬时判据里，闸门根本接不住它，整轮备份直接判死——
    /// 正是闸门存在的意义所反对的那个结局。而它偏偏只在失败风暴里出现（多个工作者、同一份内容、
    /// 失败与查表挤在一起），也就是闸门最该起作用的那一刻。
    /// </para>
    /// <para>
    /// 窗口只有"抢占失败"到"查表"这几条指令宽，一次发令枪齐跑打不中（试过：持有者早在后到者
    /// 摸到表之前就撤完了）。这里改成**持续搅动**：一群工作者在同一个地址上反复"抢到就立刻判死"，
    /// 于是任一时刻都有人正在撤占位、也有人正卡在抢占失败之后——两者本来就在同一段代码里挤着，
    /// 不必去凑。撞不上判失败的是断言而不是超时：只要出现一次 <see cref="KeyNotFoundException"/>
    /// 就红。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Losing_The_Claim_Race_Survives_The_Holder_Failing_At_That_Instant()
    {
        var r = LocalDedupResolver.Build(Plain, []);
        var stop = DateTime.UtcNow.AddMilliseconds(1500);
        var workers = Math.Max(4, Environment.ProcessorCount);

        var tasks = new Task[workers];
        for (var i = 0; i < workers; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                while (DateTime.UtcNow < stop)
                {
                    try
                    {
                        var res = await r.ResolveAsync("xxh128:d", 5, "xxh128:h", "xxh128:t");
                        // 抢到就立刻判死：这一下既撤占位、又放掉所有等在它身上的后到者，
                        // 地址随即空出来给下一个人抢——搅动就是这么来的。
                        if (!res.Exists)
                            res.Fail(new InvalidOperationException("upload boom"));
                    }
                    catch (InvalidOperationException)
                    {
                        // 后到者跟着持有者一起失败：既有行为，不变。
                    }
                    // KeyNotFoundException 一个都不许有，所以别的异常原样往上抛。
                }
            });
        }

        await Task.WhenAll(tasks);
    }
}
