namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 一次运行中最近若干条事件（跳过了什么、哪个文件失败了、在等什么）。
/// <para>
/// 为什么需要：还原此前把这类消息写进一个**单值**字段（`RestoreRunState.Phase`），
/// 后一条直接覆盖前一条。于是一次还原跳过/失败了几十个文件，跑完界面上只剩最后一条，
/// 其余全部无从追溯——只体现为 FailedFiles 那个数字，而"哪几个文件、为什么"才是操作员要的。
/// </para>
/// <para>容量有上限：这类消息可能与文件数同量级，无上限地留着就是拿内存换一份没人会翻到底的日志。
/// 满了丢最旧的——最近发生的更可能与当下的问题相关。</para>
/// </summary>
public sealed class RecentEvents(int capacity = 200)
{
    private readonly Queue<string> _items = new();
    private readonly Lock _gate = new();

    public void Add(string message)
    {
        lock (_gate)
        {
            _items.Enqueue(message);
            while (_items.Count > capacity)
                _items.Dequeue();
        }
    }

    /// <summary>取快照。返回副本而非内部集合——调用方（HTTP 序列化）与写入方在不同线程上。</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
            return [.. _items];
    }
}
