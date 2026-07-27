using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 还原的逐文件消息此前写进一个**单值**字段，后一条覆盖前一条——跳过/失败几十个文件，
/// 跑完只剩最后一条，其余仅体现为 FailedFiles 那个数字，而"哪几个、为什么"才是要看的。
/// </summary>
public sealed class RecentEventsTests
{
    [Fact]
    public void Keeps_Every_Message_Instead_Of_Only_The_Last()
    {
        var events = new RecentEvents();
        events.Add("Failed to restore 'a.txt': permission denied");
        events.Add("Skipped unsafe directory entry: ../evil");
        events.Add("Failed to restore 'b.txt': disk full");

        Assert.Equal(3, events.Snapshot().Count);
        Assert.Contains("a.txt", events.Snapshot()[0]);
        Assert.Contains("b.txt", events.Snapshot()[2]);
    }

    /// <summary>有上限：这类消息可能与文件数同量级，无上限地留着就是拿内存换一份没人翻得完的日志。
    /// 满了丢最旧的——最近发生的更可能与当下的问题相关。</summary>
    [Fact]
    public void Drops_The_Oldest_Once_Full()
    {
        var events = new RecentEvents(capacity: 3);
        foreach (var i in Enumerable.Range(1, 5))
            events.Add($"event {i}");

        Assert.Equal(["event 3", "event 4", "event 5"], events.Snapshot());
    }

    /// <summary>快照必须是副本：写入方在还原线程上，读取方在 HTTP 序列化线程上。</summary>
    [Fact]
    public void Snapshot_Is_Isolated_From_Later_Writes()
    {
        var events = new RecentEvents();
        events.Add("first");
        var snapshot = events.Snapshot();
        events.Add("second");

        Assert.Single(snapshot);
    }
}
