using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 反序列化后的版本索引的**进程内**缓存（单例，跨请求共享）。
/// <para>
/// 为什么需要：<see cref="LocalIndexCache"/> 在 SQLite 里存的是**序列化字节**，所以每次读都要把
/// 整份索引重建成对象。实测 50 万条目的索引：一次反序列化 + 一次 <see cref="VersionTreeService.Children"/>
/// 全表扫描 = 939 ms / 分配 350 MB —— 而还原对话框每展开一个目录就要走一遍这个过程。
/// </para>
/// <para>
/// 代价是常驻内存：一份 50 万条目的索引在堆上约 190 MB。所以容量可配，
/// <c>Backup__IndexCacheSize=0</c> 时完全禁用（小内存机器），此时行为与加这层缓存之前完全一致。
/// </para>
/// <para>
/// **契约：取出的实例是共享的，调用方不得修改它**（<see cref="VersionIndex.Entries"/> 等都是可变
/// 集合）。目前唯一会改动索引对象的是 <see cref="BackupRepairer"/>，而它读的是云端 store 而非本层；
/// 为了不把这个约定寄托在"以后没人改错"上，写入路径（<see cref="LocalIndexCache.PutAsync"/>）一律
/// **失效**对应条目而不是把调用方的对象放进来 —— 代价只是下次少一次命中。
/// </para>
/// </summary>
public sealed class VersionIndexMemoryCache(int capacity)
{
    private readonly record struct Key(int AccountId, string Container, int Version, long IdentityTicks);

    // 容量按项计（通常 1–2），用一个按最近使用排序的列表即可，不值得上专门的 LRU 结构。
    private readonly List<(Key Key, VersionIndex Index)> _entries = [];
    private readonly Lock _gate = new();

    /// <summary>缓存项数上限；0 = 禁用。</summary>
    public int Capacity { get; } = Math.Max(0, capacity);

    public bool Enabled => Capacity > 0;

    public bool TryGet(int accountId, string container, int version, long identityTicks, out VersionIndex index)
    {
        index = null!;
        if (!Enabled)
            return false;

        var key = new Key(accountId, container, version, identityTicks);
        lock (_gate)
        {
            var i = _entries.FindIndex(e => e.Key == key);
            if (i < 0)
                return false;

            var hit = _entries[i];
            _entries.RemoveAt(i);
            _entries.Add(hit); // 移到末尾＝最近使用
            index = hit.Index;
            return true;
        }
    }

    public void Set(int accountId, string container, int version, long identityTicks, VersionIndex index)
    {
        if (!Enabled)
            return;

        var key = new Key(accountId, container, version, identityTicks);
        lock (_gate)
        {
            var i = _entries.FindIndex(e => e.Key == key);
            if (i >= 0)
                _entries.RemoveAt(i);
            _entries.Add((key, index));
            while (_entries.Count > Capacity)
                _entries.RemoveAt(0); // 最久未使用
        }
    }

    /// <summary>某版本的索引已变化（备份写入新版本、修复改写、版本退役）→ 丢弃，绝不留陈旧副本。
    /// identityTicks 不参与匹配：容器重建后旧身份的条目同样必须走人。</summary>
    public void Invalidate(int accountId, string container, int version)
    {
        if (!Enabled)
            return;
        lock (_gate)
            _entries.RemoveAll(e => e.Key.AccountId == accountId
                && e.Key.Container == container && e.Key.Version == version);
    }

    /// <summary>某 (账户, container) 的全部版本失效（删除备份配置 / 容器重建）。</summary>
    public void InvalidateContainer(int accountId, string container)
    {
        if (!Enabled)
            return;
        lock (_gate)
            _entries.RemoveAll(e => e.Key.AccountId == accountId && e.Key.Container == container);
    }
}
