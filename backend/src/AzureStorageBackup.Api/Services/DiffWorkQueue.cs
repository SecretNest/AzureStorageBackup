using System.Text;
using System.Threading.Channels;

namespace AzureStorageBackup.Api.Services;

/// <summary>流水线上的一件活：一个单文件 blob，或一箱已封好的 pack 成员。</summary>
internal readonly record struct WorkItem(PlannedFile? Single, IReadOnlyList<PlannedFile>? Pack)
{
    /// <summary>这件活带着几个 <see cref="PlannedFile"/>。内存上界按它算，**不**按件数——
    /// 一件活可以是一个单文件 blob，也可以是一箱上万个小文件，按件数设界等于把两者当成一样重。</summary>
    public int Members => Single is not null ? 1 : Pack?.Count ?? 0;

    /// <summary>不管是哪一种形态，都按成员序列看待（落盘序列化与回读共用这一个视角）。</summary>
    public IReadOnlyList<PlannedFile> AsMembers => Single is { } single ? [single] : Pack ?? [];
}

/// <summary>
/// diff 与压缩上传之间的那条队列。写侧（diff）**永不阻塞**：内存装不下就把多出来的活序列化到
/// 临时文件，读侧照常只从内存拿，后台泵在内存腾出空间时成批把盘上的活捞回来。
/// <para>
/// 为什么不能让写侧阻塞：上传阶段的剩余时间要等 <c>StageTracker.SetTotal</c> 才算得出来
/// （见 <c>StageProgress.Eta</c> 的第一行：<c>_total &lt;= 0</c> 直接返回 null），而那个总数
/// 只有 diff 跑完才是确定的。一旦写侧被队列挡住，diff 就只能跟着上传的节奏往前挪——
/// 于是「diff 收工」＝「只剩一个队列深度的活没做」，剩余时间要到整轮备份的尾巴上才肯出现。
/// 队列开多大都躲不掉这件事，只能让写侧根本不停。
/// </para>
/// <para>
/// 为什么界要设在**成员数**上而不是件数上：真正占内存的是 <see cref="PlannedFile"/>——
/// 路径 + 长度 + 64 字符的 hash，一个约 400 字节（路径字符串与扫描结果共享实例，实际增量
/// 更接近 200）。一箱 100 MB 的 5 KB 小文件有两万个成员，按件数记它和一个单文件 blob 一样重，
/// 差着四个数量级。
/// </para>
/// <para>
/// FIFO 是跨内存与磁盘整体成立的：**盘上只要还有货，新来的活也一律走盘**，否则它会插到
/// 已落盘那些活的前面去。顺序对正确性其实无所谓（pack 号在处理时才分配，见 <c>RunState.NextPackId</c>），
/// 但乱序会让界面上的「当前文件」在目录之间来回跳，没有理由白白牺牲。
/// </para>
/// </summary>
internal sealed class DiffWorkQueue : IDisposable
{
    private readonly Lock _gate = new();
    /// <summary>内存里的那一段。无界——真正的界是 <see cref="_memberLimit"/> 卡在写侧和回读侧，
    /// 用 Channel 只是为了白拿它的等待/完成语义，不是拿它当上界。</summary>
    private readonly Channel<WorkItem> _cache = Channel.CreateUnbounded<WorkItem>();
    /// <summary>叫醒回读泵：落了一件盘、消费掉一件（腾出了空间）、或者写侧收工了。</summary>
    private readonly SemaphoreSlim _wake = new(0);
    private readonly string? _spillPath;
    private readonly int _memberLimit;
    private readonly int _batchItems;
    private readonly Task _pump;

    // 同一个文件两个句柄：写侧只追加，读侧只顺着往前走。两边都在 _gate 里动，位置不会打架。
    private FileStream? _writeStream;
    private BinaryWriter? _writer;
    private FileStream? _readStream;
    private BinaryReader? _reader;

    private int _cachedMembers;
    private long _onDisk;        // 已落盘、还没回读的件数
    private long _spilledTotal;  // 累计落过盘的件数（只增，给界面看）
    private bool _addingDone;
    private int _disposed;

    /// <param name="spillPath">溢出文件的完整路径；传 null＝不落盘，内存无界（测试与不配临时盘时的退路）。</param>
    /// <param name="memberLimit">内存里最多攒多少个成员。</param>
    /// <param name="batchItems">一次从盘上捞几件。成批捞是有意的：一件一件捞，每件都要过一次锁
    /// 和一次 <c>Flush</c>，而回读发生在消费侧的关键路径上。</param>
    public DiffWorkQueue(string? spillPath, int memberLimit, int batchItems)
    {
        _spillPath = spillPath;
        _memberLimit = Math.Max(1, memberLimit);
        _batchItems = Math.Max(1, batchItems);
        _pump = spillPath is null ? Task.CompletedTask : Task.Run(PumpAsync);
    }

    /// <summary>累计有多少件活落过盘。给界面用——它是「diff 跑得比上传快多少」的直接读数。</summary>
    public long SpilledItems => Interlocked.Read(ref _spilledTotal);

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
        var members = item.Members;
        bool spilled;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            // 内存空着就无条件收下，哪怕这一件本身就超界：一箱小文件的成员数可以大于整个上界，
            // 不留这条例外，那种活永远进不了内存，两侧一起停在原地。
            spilled = _spillPath is not null
                && (_onDisk > 0 || (_cachedMembers > 0 && _cachedMembers + members > _memberLimit));
            if (spilled)
            {
                WriteSpill(item);
                _onDisk++;
                Interlocked.Increment(ref _spilledTotal);
            }
            else
            {
                _cachedMembers += members;
                _cache.Writer.TryWrite(item);
            }
        }
        if (spilled)
            _wake.Release();
    }

    /// <summary>写侧收工。盘上剩下的仍然会被回读干净，读侧要等那之后才收到 null。</summary>
    public void CompleteAdding()
    {
        lock (_gate) { _addingDone = true; }
        if (_spillPath is null)
            _cache.Writer.TryComplete();
        else
            _wake.Release(); // 泵来负责「盘也空了」之后才关闸
    }

    /// <summary>取一件活；返回 null＝写侧收工且内存与磁盘都空了。多消费者并发调用。</summary>
    public async ValueTask<WorkItem?> DequeueAsync(CancellationToken ct)
    {
        while (true)
        {
            if (_cache.Reader.TryRead(out var item))
            {
                bool wake;
                lock (_gate)
                {
                    _cachedMembers -= item.Members;
                    wake = _onDisk > 0; // 盘上没货就别叫泵，否则每消费一件都白唤醒一次
                }
                if (wake)
                    _wake.Release();
                return item;
            }
            if (!await _cache.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                return null;
        }
    }

    /// <summary>回读泵：内存一腾出空间就成批把盘上的活捞回来，捞干净且写侧收工了就关闸。</summary>
    private async Task PumpAsync()
    {
        while (true)
        {
            // 中途中止（备份被取消或抛了异常）：盘上还剩什么都不重要了，立刻撤。
            // 不撤的话泵会守着一个没人消费、又腾不出空间的内存段一直等，Dispose 只能干等到超时——
            // 而 Dispose 是在异常路径上被调用的，那里最不该再多花十秒。
            if (Volatile.Read(ref _disposed) != 0)
                return;

            int moved;
            bool done;
            lock (_gate)
            {
                moved = RefillLocked();
                done = _onDisk == 0 && _addingDone;
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

    /// <summary>在 <see cref="_gate"/> 里成批回读。返回这一批捞了几件。</summary>
    private int RefillLocked()
    {
        if (_onDisk == 0 || _reader is null)
            return 0;

        // 一批只刷一次：写侧的缓冲不刷给内核，另一个句柄读不到刚写进去的那几件。
        _writer!.Flush();

        var moved = 0;
        while (moved < _batchItems && _onDisk > 0
               && (_cachedMembers == 0 || _cachedMembers < _memberLimit))
        {
            var item = ReadSpill();
            _onDisk--;
            moved++;
            // 闸已经关了（Dispose 中途中止）：读出来直接丢，只为把 _onDisk 归零好让泵退得出去。
            if (!_cache.Writer.TryWrite(item))
                continue;
            _cachedMembers += item.Members;
        }
        return moved;
    }

    private void WriteSpill(WorkItem item)
    {
        if (_writer is null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_spillPath!)!);
            // FileShare.ReadWrite：同一个文件另开一个只读句柄顺着往前读。
            _writeStream = new FileStream(_spillPath!, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            _writer = new BinaryWriter(_writeStream, Encoding.UTF8, leaveOpen: true);
            _readStream = new FileStream(_spillPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _reader = new BinaryReader(_readStream, Encoding.UTF8, leaveOpen: true);
        }

        // 长度前缀的二进制，不是分行文本：Linux 的路径里可以有换行，也可以有任何非 NUL 字节，
        // 按行切一定会在某个用户的目录上切错，而切错的表现是备份少传文件——不会有人发现。
        var members = item.AsMembers;
        _writer.Write(item.Single is not null);
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

        // 先关闸再叫泵：泵会把盘上剩下的读干净（写不进关掉的闸就丢掉），然后自己退出。
        _cache.Writer.TryComplete();
        lock (_gate) { _addingDone = true; }
        _wake.Release();
        try { _pump.Wait(TimeSpan.FromSeconds(10)); }
        catch { /* 泵怎么收场都不该拦住释放句柄 */ }

        lock (_gate)
        {
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
public sealed class DiffWorkQueueFactory(string spillDirectory, int memberLimit, int refillBatchItems)
{
    public string SpillDirectory => spillDirectory;

    internal DiffWorkQueue Create() =>
        new(Path.Combine(spillDirectory, $"{Guid.NewGuid():N}.spill"), memberLimit, refillBatchItems);
}
