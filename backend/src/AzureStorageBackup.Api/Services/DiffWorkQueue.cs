using System.Text;
using System.Threading.Channels;

namespace AzureStorageBackup.Api.Services;

/// <summary>流水线上的一件活：一个单文件 blob，或一箱已封好的 pack 成员。</summary>
internal readonly record struct WorkItem(PlannedFile? Single, IReadOnlyList<PlannedFile>? Pack)
{
    /// <summary>这件活带着几个 <see cref="PlannedFile"/>。</summary>
    public int Members => Single is not null ? 1 : Pack?.Count ?? 0;

    /// <summary>不管是哪一种形态，都按成员序列看待（落盘序列化与回读共用这一个视角）。</summary>
    public IReadOnlyList<PlannedFile> AsMembers => Single is { } single ? [single] : Pack ?? [];

    /// <summary>
    /// 这件活压在托管堆上大约多少字节。队列的字节额度按它累计。
    /// <para>
    /// 刻意高估：路径字符串在**还没落过盘**的活里与扫描结果共享实例，那部分严格说不是增量；
    /// 但从盘上回读出来的是新串，那时确实是这条队列自己在持有。两种活混在同一个额度里，
    /// 按"自己持有"记才不会低估——低估的后果是额度形同虚设。
    /// </para>
    /// </summary>
    public long EstimatedBytes
    {
        get
        {
            long total = 0;
            foreach (var m in AsMembers)
                total += 48 + StringBytes(m.Path) + StringBytes(m.FullHash);
            return total;
        }
    }

    /// <summary>字符串按 CLR 的实际布局算：对象头 24 + 每字符 2 字节（UTF-16），8 字节对齐。</summary>
    private static long StringBytes(string? s) =>
        s is null ? 0 : (24 + (2L * s.Length) + 7) & ~7L;
}

/// <summary>
/// <see cref="DiffWorkQueue"/> 的各段额度。全部可配置（见 Program.cs 的 <c>Backup:DiffQueue*</c>）。
/// </summary>
/// <param name="MaxCachedItems">r 段：内存里最多攒多少**件**活。流水线说话的单位就是件，
/// 界面上的 processed/queued/total 数的也是它，这是主旋钮。</param>
/// <param name="MaxCachedBytes">r 段的字节兜底。光有件数管不住内存：一件活可以是一个单文件 blob，
/// 也可以是一箱两万个小文件（100 MB 的箱子装 5 KB 的文件就是这个数），差着四个数量级。</param>
/// <param name="WriteBatchItems">w 段：攒够多少件就成批刷进临时文件。</param>
/// <param name="WriteBatchBytes">w 段的字节兜底。**这一条不能省**：w 也在内存里，
/// 只按件数限制它，小文件场景下 200 件满员的箱子就是好几个 GB——给 r 段设的额度会从这里被绕过去。</param>
/// <param name="RefillBatchItems">一次从临时文件捞几件。成批捞是为了摊平 IO：
/// 一件一件捞，每件都要过一次锁和一次 Flush，而回读发生在消费侧的关键路径上。</param>
/// <param name="FileBufferBytes">临时文件的 <see cref="FileStream"/> 缓冲。写侧的批量化其实主要
/// 发生在这里——每件活的 Write 只是写进这个缓冲，满了才真正下系统调用。</param>
public sealed record DiffQueueLimits(
    int MaxCachedItems = 2_000,
    long MaxCachedBytes = 64L * 1024 * 1024,
    int WriteBatchItems = 200,
    long WriteBatchBytes = 8L * 1024 * 1024,
    int RefillBatchItems = 1_000,
    int FileBufferBytes = 256 * 1024);

/// <summary>
/// diff 与压缩上传之间的那条队列。写侧（diff）**永不阻塞**。
/// <para>
/// 整条队列是三段，头在左、尾在右：
/// </para>
/// <code>
///   rrrrr | fffffffffff | www
///     r  = 在内存里，等着被消费者领走
///     f  = 在临时文件里
///     w  = 在内存里，等着成批写进临时文件
/// </code>
/// <para>
/// 「额度」是**事先定死的数**（<see cref="DiffQueueLimits"/>），不是"等内存耗尽"。这条队列不去
/// 观察进程的内存水位，也不该去观察：它只管住自己那一份，超了就把多的放到盘上。
/// r 段与 w 段**都**算在内存预算里——w 也在内存，不算它的话预算是假的。
/// </para>
/// <para>
/// 为什么不能让写侧阻塞：上传阶段的剩余时间要等 <c>StageTracker.SetTotal</c> 才算得出来
/// （见 <c>StageProgress.Eta</c> 的第一行：<c>_total &lt;= 0</c> 直接返回 null），而那个总数
/// 只有 diff 跑完才是确定的。一旦写侧被队列挡住，diff 就只能跟着上传的节奏往前挪——
/// 于是「diff 收工」＝「只剩一个队列深度的活没做」，剩余时间要到整轮备份的尾巴上才肯出现。
/// 队列开多大都躲不掉这件事，只能让写侧根本不停。
/// </para>
/// <para>
/// <b>至少备好一件</b>：r 段空着时无条件收下下一件，哪怕那一件自己就超过整个额度
/// （1 字节的文件装满 100 MB 的箱子就是上亿个成员）。这条例外必须在**写侧和回读侧都有**——
/// 只在写侧留，那件超大的活落盘之后回读时照样被额度挡住，泵和消费者一起停在原地。
/// 代价是实际内存峰值 = max(额度, 最大的那一件)：额度是软下限，不是硬上限。
/// 真要给"一件能有多大"设界，得去 <c>GroupingPlanner</c> 封箱那一层加每箱成员数上限，
/// 这条队列决定不了。
/// </para>
/// <para>
/// FIFO 跨三段整体成立：只要 f 或 w 非空，新来的活就一律进 w，否则它会插到前面那些活之前。
/// 顺序对正确性其实无所谓（pack 号在处理时才分配，见 <c>RunState.NextPackId</c>），
/// 但乱序会让界面上的「当前文件」在目录之间来回跳，没有理由白白牺牲。
/// </para>
/// <para>
/// <b>f 空时 w 直接进 r，不碰盘。</b>消费侧一旦追上来，w 里的活就没必要写下去再读回来——
/// diff 收尾时那半批尤其明显，不走这条捷径就是纯粹的白跑一趟盘。
/// </para>
/// </summary>
internal sealed class DiffWorkQueue : IDisposable
{
    private readonly Lock _gate = new();
    /// <summary>r 段。Channel 本身无界——真正的界是 <see cref="DiffQueueLimits.MaxCachedItems"/> 与
    /// <see cref="DiffQueueLimits.MaxCachedBytes"/> 卡在写侧和回读侧，用 Channel 只是为了白拿它的
    /// 等待/完成语义，不是拿它当上界。</summary>
    private readonly Channel<WorkItem> _cache = Channel.CreateUnbounded<WorkItem>();
    /// <summary>w 段。用 Queue 不用 List：回读是从头取，List 从头删是 O(n)。</summary>
    private readonly Queue<WorkItem> _pendingWrite = new();
    /// <summary>叫醒回读泵：w 进了新活、r 消费掉一件（腾出空间）、写侧收工、或者要释放了。</summary>
    private readonly SemaphoreSlim _wake = new(0);
    private readonly string? _spillPath;
    private readonly DiffQueueLimits _limits;
    private readonly Task _pump;

    // 同一个文件两个句柄：写侧只追加，读侧只顺着往前走。两边都在 _gate 里动，位置不会打架。
    private FileStream? _writeStream;
    private BinaryWriter? _writer;
    private FileStream? _readStream;
    private BinaryReader? _reader;

    private int _cachedItems;
    private long _cachedBytes;
    private long _pendingWriteBytes;
    private long _onDisk;        // f 段：已落盘、还没回读的件数
    private long _spilledTotal;  // 累计真正写进过文件的件数（只增，给界面看）
    private bool _addingDone;
    private int _disposed;

    /// <param name="spillPath">溢出文件的完整路径；传 null＝不落盘，内存无界（测试与不配临时盘时的退路）。</param>
    /// <param name="limits">各段额度。</param>
    public DiffWorkQueue(string? spillPath, DiffQueueLimits limits)
    {
        _spillPath = spillPath;
        _limits = limits with
        {
            MaxCachedItems = Math.Max(1, limits.MaxCachedItems),
            MaxCachedBytes = Math.Max(1, limits.MaxCachedBytes),
            WriteBatchItems = Math.Max(1, limits.WriteBatchItems),
            WriteBatchBytes = Math.Max(1, limits.WriteBatchBytes),
            RefillBatchItems = Math.Max(1, limits.RefillBatchItems),
            FileBufferBytes = Math.Max(4096, limits.FileBufferBytes),
        };
        _pump = spillPath is null ? Task.CompletedTask : Task.Run(PumpAsync);
    }

    /// <summary>累计有多少件活真正写进过临时文件。给界面用——它是「diff 跑得比上传快多少」的直接读数。
    /// 只在 w 段刷盘时增长：还躺在 w 里、后来又直接进了 r 的那些活从没碰过盘，不该算进来。</summary>
    public long SpilledItems => Interlocked.Read(ref _spilledTotal);

    /// <summary>此刻 r 段有几件、估算占多少字节，以及 w 段还压着几件。诊断与测试用。</summary>
    public (int Items, long Bytes, int PendingWrite) Cached
    {
        get { lock (_gate) return (_cachedItems, _cachedBytes, _pendingWrite.Count); }
    }

    /// <summary>
    /// 进程启动时清掉上一次非正常退出留下的溢出文件。
    /// <para>
    /// 只能在**进程启动**时清，不能在每次备份开始时清：多个备份可以同时在跑，按运行清会把
    /// 别人正在用的文件删掉。每次运行用自己的随机文件名，正常收尾时各删各的（见 <see cref="Dispose"/>）；
    /// 进程被 kill 掉那一路留下的，才由这里兜底。
    /// </para>
    /// </summary>
    public static void ClearStale(string spillDir)
    {
        try
        {
            Directory.CreateDirectory(spillDir);
            foreach (var file in Directory.EnumerateFiles(spillDir, "*.spill"))
            {
                try { File.Delete(file); }
                catch { /* 删不掉就算了：占着点盘不影响正确性，拦住启动才是真出事 */ }
            }
        }
        catch { /* 同上 */ }
    }

    /// <summary>塞一件活进来。单写者（diff 是单线程推进的），**永不阻塞**。</summary>
    public void Enqueue(WorkItem item)
    {
        var bytes = item.EstimatedBytes;
        var wake = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);

            // 前面两段都空、r 还装得下 → 直接进 r，一次拷贝、一次系统调用都不多花。
            // f 或 w 非空时**必须**走 w，否则这一件会插到它们前面去。
            if (_spillPath is null || (_onDisk == 0 && _pendingWrite.Count == 0 && HasRoomLocked(bytes)))
            {
                AdmitLocked(item, bytes);
            }
            else
            {
                _pendingWrite.Enqueue(item);
                _pendingWriteBytes += bytes;
                // w 满了就整批刷进文件。件数与字节谁先到都算——只按件数限制的话，
                // 小文件场景下 200 个满员的箱子就是好几个 GB，r 段的额度等于被从后门绕过去。
                if (_pendingWrite.Count >= _limits.WriteBatchItems || _pendingWriteBytes >= _limits.WriteBatchBytes)
                    FlushPendingWritesLocked();
                wake = true;
            }
        }
        if (wake)
            _wake.Release();
    }

    /// <summary>写侧收工。f 与 w 里剩下的仍然会被送完，读侧要等那之后才收到 null。</summary>
    public void CompleteAdding()
    {
        lock (_gate) { _addingDone = true; }
        if (_spillPath is null)
            _cache.Writer.TryComplete();
        else
            _wake.Release(); // 泵来负责「f 和 w 都空了」之后才关闸
    }

    /// <summary>取一件活；返回 null＝写侧收工且三段都空了。多消费者并发调用。</summary>
    public async ValueTask<WorkItem?> DequeueAsync(CancellationToken ct)
    {
        while (true)
        {
            if (_cache.Reader.TryRead(out var item))
            {
                bool wake;
                lock (_gate)
                {
                    _cachedItems--;
                    _cachedBytes -= item.EstimatedBytes;
                    // 后面没货就别叫泵，否则每消费一件都白唤醒一次。
                    wake = _onDisk > 0 || _pendingWrite.Count > 0;
                }
                if (wake)
                    _wake.Release();
                return item;
            }
            if (!await _cache.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                return null;
        }
    }

    /// <summary>回读泵：r 段一腾出空间就把 f（其次 w）里的活补进来，三段都空且写侧收工就关闸。</summary>
    private async Task PumpAsync()
    {
        while (true)
        {
            // 中途中止（备份被取消或抛了异常）：后面剩什么都不重要了，立刻撤。
            // 不撤的话泵会守着一个没人消费、又腾不出空间的 r 段一直等，而 Dispose 正在等它退出。
            if (Volatile.Read(ref _disposed) != 0)
                return;

            int moved;
            bool done;
            lock (_gate)
            {
                moved = RefillLocked();
                done = _onDisk == 0 && _pendingWrite.Count == 0 && _addingDone;
            }
            if (done)
            {
                _cache.Writer.TryComplete();
                return;
            }
            if (moved == 0)
                await _wake.WaitAsync().ConfigureAwait(false);
        }
    }

    /// <summary>在 <see cref="_gate"/> 里成批往 r 段补货。返回这一轮补了几件。</summary>
    private int RefillLocked()
    {
        var moved = 0;

        // 先 f 后 w：f 在 w 前面，反过来就乱序了。
        if (_onDisk > 0)
        {
            // 一批只刷一次：写侧的缓冲不刷给内核，另一个句柄读不到刚写进去的那几件。
            _writer!.Flush();
            while (moved < _limits.RefillBatchItems && _onDisk > 0)
            {
                if (!HasRoomForNextLocked())
                    return moved;
                var item = ReadSpill();
                _onDisk--;
                moved++;
                // 闸已经关了（Dispose 中途中止）：读出来直接丢，只为把 _onDisk 归零好让泵退得出去。
                if (_cache.Writer.TryWrite(item))
                {
                    _cachedItems++;
                    _cachedBytes += item.EstimatedBytes;
                }
            }
        }

        // f 空了 → w 里的活直接进 r，不必写下去再读回来。
        if (_onDisk == 0)
        {
            while (moved < _limits.RefillBatchItems && _pendingWrite.Count > 0)
            {
                if (!HasRoomForNextLocked())
                    return moved;
                var item = _pendingWrite.Dequeue();
                var bytes = item.EstimatedBytes;
                _pendingWriteBytes -= bytes;
                moved++;
                AdmitLocked(item, bytes);
            }
        }

        return moved;
    }

    /// <summary>r 段还装不装得下下一件。r 空着时恒为 true——那是「至少备好一件」那条例外。</summary>
    private bool HasRoomForNextLocked() =>
        _cachedItems == 0 || (_cachedItems < _limits.MaxCachedItems && _cachedBytes < _limits.MaxCachedBytes);

    /// <summary>r 段装不装得下这一件（写侧用，看的是加上它之后会不会超）。</summary>
    private bool HasRoomLocked(long bytes) =>
        _cachedItems == 0
        || (_cachedItems + 1 <= _limits.MaxCachedItems && _cachedBytes + bytes <= _limits.MaxCachedBytes);

    private void AdmitLocked(WorkItem item, long bytes)
    {
        if (!_cache.Writer.TryWrite(item))
            return; // 闸已关（Dispose 中途中止）
        _cachedItems++;
        _cachedBytes += bytes;
    }

    private void FlushPendingWritesLocked()
    {
        EnsureSpillOpenLocked();
        while (_pendingWrite.Count > 0)
        {
            WriteSpill(_pendingWrite.Dequeue());
            _onDisk++;
            Interlocked.Increment(ref _spilledTotal);
        }
        _pendingWriteBytes = 0;
    }

    private void EnsureSpillOpenLocked()
    {
        if (_writer is not null)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(_spillPath!)!);
        // FileShare.ReadWrite：同一个文件另开一个只读句柄顺着往前读。
        // 缓冲开大：写侧的批量化主要就发生在这里，每件活的 Write 只是写进它。
        _writeStream = new FileStream(
            _spillPath!, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, _limits.FileBufferBytes);
        _writer = new BinaryWriter(_writeStream, Encoding.UTF8, leaveOpen: true);
        _readStream = new FileStream(
            _spillPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, _limits.FileBufferBytes);
        _reader = new BinaryReader(_readStream, Encoding.UTF8, leaveOpen: true);
    }

    private void WriteSpill(WorkItem item)
    {
        // 长度前缀的二进制，不是分行文本：Linux 的路径里可以有换行，也可以有任何非 NUL 字节，
        // 按行切一定会在某个用户的目录上切错，而切错的表现是备份少传文件——不会有人发现。
        // 每件是一条完整记录，读的时候要么整件出来要么不动，不存在"读了半个 pack"。
        var members = item.AsMembers;
        _writer!.Write(item.Single is not null);
        _writer.Write(members.Count);
        foreach (var m in members)
        {
            _writer.Write(m.Path);
            _writer.Write(m.Length);
            _writer.Write(m.FullHash is not null);
            if (m.FullHash is not null)
                _writer.Write(m.FullHash);
        }
    }

    private WorkItem ReadSpill()
    {
        var single = _reader!.ReadBoolean();
        var count = _reader.ReadInt32();
        var members = new List<PlannedFile>(count);
        for (var i = 0; i < count; i++)
        {
            var path = _reader.ReadString();
            var length = _reader.ReadInt64();
            var hash = _reader.ReadBoolean() ? _reader.ReadString() : null;
            members.Add(new PlannedFile(path, length, hash));
        }
        return single ? new WorkItem(members[0], null) : new WorkItem(null, members);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // 先关闸再叫泵：泵看到 _disposed 就直接退出，不再试图把剩下的送完。
        _cache.Writer.TryComplete();
        lock (_gate) { _addingDone = true; }
        _wake.Release();
        try { _pump.Wait(TimeSpan.FromSeconds(10)); }
        catch { /* 泵怎么收场都不该拦住释放句柄 */ }

        lock (_gate)
        {
            _pendingWrite.Clear();
            _pendingWriteBytes = 0;
            _writer?.Dispose();
            _writeStream?.Dispose();
            _reader?.Dispose();
            _readStream?.Dispose();
            _writer = null;
            _writeStream = null;
            _reader = null;
            _readStream = null;
        }

        if (_spillPath is not null)
        {
            try { File.Delete(_spillPath); }
            catch { /* 进程下次启动时 ClearStale 兜底 */ }
        }
        _wake.Dispose();
    }
}

/// <summary>
/// 每次备份运行开一条自己的队列。溢出文件按运行取随机名——并发的备份各写各的，
/// 谁也别想删到别人头上（见 <see cref="DiffWorkQueue.ClearStale"/>）。
/// </summary>
public sealed class DiffWorkQueueFactory(string spillDirectory, DiffQueueLimits limits)
{
    public string SpillDirectory => spillDirectory;

    public DiffQueueLimits Limits => limits;

    internal DiffWorkQueue Create() =>
        new(Path.Combine(spillDirectory, $"{Guid.NewGuid():N}.spill"), limits);
}
