namespace AzureStorageBackup.Api.Services;

/// <summary>已移入 staged-temp 的一组卷文件，等待上传。</summary>
public sealed record StagedItem(IReadOnlyList<string> Files, long Bytes);

/// <summary>
/// 临时区状态机（M4 设计 §7）。
/// 压缩全局非并发（单一压缩锁）；压缩产出先写 compress-temp，完成后整套移入 staged-temp。
/// staged-temp 有字节上限：未达上限才启动下一个压缩（允许单个结果临时超限）；
/// 超限则阻塞新压缩，直到上传调用 <see cref="ReleaseFile"/> / <see cref="Release"/> 腾出空间。
/// <para>
/// 释放粒度是**单卷**而不是整族：一个大文件切出上千卷，整族传完才删的话，峰值占用等于整个归档
/// （100 GB 的文件就要 100 GB 临时空间——这条已经把备份撞失败过一次），而且水位整段贴在上限上，
/// 压缩被背压一直堵着。传完一卷删一卷之后，峰值只剩"还没传完的那几卷"。
/// </para>
/// </summary>
public sealed class StagingArea(string compressTempDir, string stagedTempDir, Func<long> stagedLimit) : IDisposable
{
    private readonly SemaphoreSlim _compressLock = new(1, 1);
    private readonly SemaphoreSlim _releaseSignal = new(0);
    // 每个已暂存文件占的字节，连同它记在谁账上。按卷释放要能精确扣账，而且必须**幂等**——
    // 同一卷会被上传路径逐卷释放一次、收尾时再随整族兜底一次，重复扣会把水位记成负的，
    // 压缩就再也不会被背压挡住了。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (StagingLease? Lease, long Bytes)> _staged =
        new(StringComparer.Ordinal);
    private long _stagedBytes;

    /// <summary>当前在跑的运行数（= 配额的分母）。存量的非活动备份不占席位。</summary>
    private int _leases;

    /// <summary>正等着腾空间的调用方数量。释放时要把它们**全部**唤醒，见 <see cref="SignalRelease"/>。</summary>
    private int _waiting;

    public long StagedBytes => Interlocked.Read(ref _stagedBytes);

    /// <summary>
    /// 进程启动时清掉上一个进程留下的压缩/暂存残留。
    /// <para>
    /// 必须在**进程启动**时清，不能在每次备份开始时清：多个备份可以同时在跑，
    /// 按运行清会把别人正在写的文件删掉。进程刚起来时没有任何运行存活，
    /// 这里看到的一切都是上次非正常退出（容器被 kill、断电）的垃圾。
    /// </para>
    /// <para>
    /// 恢复时不复用这些暂存文件——重压一遍比校验一堆来路不明的半成品便宜也安全得多。
    /// </para>
    /// </summary>
    public static void ClearStale(string compressTempDir, string stagedTempDir)
    {
        foreach (var dir in new[] { compressTempDir, stagedTempDir })
        {
            try
            {
                if (!Directory.Exists(dir))
                    continue;
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    try { Directory.Delete(sub, recursive: true); } catch { /* 删不掉就算了，下次再说 */ }
                foreach (var file in Directory.EnumerateFiles(dir))
                    try { File.Delete(file); } catch { /* 同上 */ }
            }
            catch { /* 同上 */ }
        }
    }

    /// <summary>
    /// 一次运行在暂存区里的席位。暂存盘的额度按**当前持有席位的运行数**均分，所以席位必须随
    /// 运行开始而取、随运行结束而还——存量的非活动备份不该占份额，否则配了十个备份、只跑一个，
    /// 那一个也只能用十分之一的盘。
    /// </summary>
    public sealed class StagingLease : IDisposable
    {
        private readonly StagingArea _area;
        private long _bytes;
        private int _disposed;

        internal StagingLease(StagingArea area) => _area = area;

        /// <summary>这次运行当前占着的暂存字节。</summary>
        public long Bytes => Interlocked.Read(ref _bytes);

        internal void Add(long bytes) => Interlocked.Add(ref _bytes, bytes);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            Interlocked.Decrement(ref _area._leases);
            // 席位一走，剩下的运行额度立刻变大——必须叫醒它们，否则它们会一直等在旧配额上，
            // 直到下一次有卷上传完才偶然被唤醒。
            _area.SignalRelease();
        }
    }

    /// <summary>取一个席位。返回值必须在运行结束时释放（<c>using</c>）。</summary>
    public StagingLease AcquireLease()
    {
        var lease = new StagingLease(this);
        Interlocked.Increment(ref _leases);
        return lease;
    }

    /// <summary>本次调用可用的额度。没有席位的调用方不参与均分，只受全局上限约束。</summary>
    private long QuotaFor(StagingLease? lease)
    {
        var limit = stagedLimit();
        if (lease is null)
            return limit;
        return limit / Math.Max(1, Volatile.Read(ref _leases));
    }

    /// <summary>
    /// 现在能不能开压。两道闸都要过：自己的额度（公平），以及全局上限（暂存盘是物理磁盘，
    /// 写满就是备份直接失败）。判据都是「**当前**占用低于额度就放行」，沿用既有语义——
    /// 于是从零起步的一件活总能开压，哪怕它的产物注定超出额度，否则比额度大的文件永远压不出来。
    /// </summary>
    private bool HasRoom(StagingLease? lease) =>
        Interlocked.Read(ref _stagedBytes) < stagedLimit()
        && (lease is null || lease.Bytes < QuotaFor(lease));

    /// <summary>
    /// 唤醒**所有**等待者，而不是一个。各人等的是各自的额度，只放一个的话，醒来的未必是那个
    /// 能继续的——信号还会被它消费掉，真正该醒的那个就此错过，一直干等到下一次释放。
    /// 多放的信号会让后续 WaitAsync 立即返回一次，等待循环重新判条件即可，无害。
    /// </summary>
    private void SignalRelease()
    {
        var waiters = Volatile.Read(ref _waiting);
        if (waiters > 0)
            _releaseSignal.Release(waiters);
    }

    /// <summary>
    /// 预留一块额度给**调用方自己管理**的临时空间——修复与死重压实要把成员拼进 compose 目录、
    /// 有时还要下载并解压整个旧 pack，那些字节同样落在这块物理盘上。
    /// <para>
    /// 与 <see cref="StageAsync"/> 的区别：那边是"产出等待上传"，量是压完才知道的精确值，
    /// 而且能逐卷释放；这边是"输入等待消费"，量只能事先估，且整段持有到操作结束。所以这里
    /// 只记账与背压，不搬文件——调用方自己保证写入不超过预留值，用完 Dispose 归还。
    /// </para>
    /// <para>
    /// 不抢压缩锁：预留期间调用方多半在下载或拷贝，把压缩锁按在那儿等网络是这次改造刚修掉的病。
    /// </para>
    /// </summary>
    public async Task<IDisposable> ReserveAsync(long bytes, StagingLease? lease = null, CancellationToken ct = default)
    {
        await WaitForRoomAsync(lease, ct);
        Interlocked.Add(ref _stagedBytes, bytes);
        lease?.Add(bytes);
        return new Reservation(this, lease, bytes);
    }

    private sealed class Reservation(StagingArea area, StagingLease? lease, long bytes) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            Interlocked.Add(ref area._stagedBytes, -bytes);
            lease?.Add(-bytes);
            area.SignalRelease();
        }
    }

    /// <summary>等到有空间为止。**不持压缩锁**——这正是要点所在，见类注释。</summary>
    private async Task WaitForRoomAsync(StagingLease? lease, CancellationToken ct)
    {
        while (!HasRoom(lease))
        {
            Interlocked.Increment(ref _waiting);
            try
            {
                if (HasRoom(lease))   // 登记为等待者之后再看一眼，避免错过刚刚发生的释放
                    return;
                await _releaseSignal.WaitAsync(ct);
            }
            finally
            {
                Interlocked.Decrement(ref _waiting);
            }
        }
    }

    /// <param name="tracker">可选的进度记账。<b>只能</b>传本次备份自己的那个：本类是单例、
    /// 跨备份共享，把全局状态直接算给某一个备份会让并发的两轮互相污染。谁调用谁记账，
    /// 各自只看得见自己的活。</param>
    /// <param name="lease">
    /// 本次运行的席位（见 <see cref="AcquireLease"/>）。传 null 表示不参与额度均分，只受全局上限约束。
    /// </param>
    public async Task<StagedItem> StageAsync(
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> produce,
        StagingLease? lease = null,
        CancellationToken ct = default,
        StageTracker? tracker = null)
    {
        // 进了这一段就先算"排队中"：压缩锁是全局的，这里多半要干等一会儿，而干等对用户
        // 与"还没被领走"没有区别。拿到锁之后才翻成"在准备"。
        tracker?.BeginStaging();
        try
        {
            // 等空间**不持压缩锁**。从前是先抢锁再等空间，理由是"锁已在手上、别人一样压不了"——
            // 单个备份跑时确实如此。但多个备份并行时那就成了病根：一个运行被暂存挡住，就抱着
            // 全局压缩锁干等，别的运行连压缩都开始不了。给谁加配额都救不了，只是换个理由卡死。
            while (true)
            {
                await WaitForRoomAsync(lease, ct);

                await _compressLock.WaitAsync(ct);
                // 等到空位和拿到锁之间有个窗口，空位可能已经被别人用掉。锁到手了必须再看一眼，
                // 不然就突破了上限——放锁回去重等，让真正有空间的那个先走。
                if (HasRoom(lease))
                    break;
                _compressLock.Release();
            }

            try
            {
                try
                {
                    // BeginPacking 挪进 try：它会在 _gate 下调用 publish(...)，非心跳路径故意让
                    // publish 抛出的异常继续往外传（见 StageProgress.cs 里 BeginPacking 的说明）。
                    // 留在 try 外面的话，一旦这里抛出，_inPacking 加了却没有配对的 EndPacking，
                    // preparing 会在余下的运行里卡在虚高的数字上；挪进来就有下面这个 finally 兜底。
                    tracker?.BeginPacking();

                    Directory.CreateDirectory(compressTempDir);
                    Directory.CreateDirectory(stagedTempDir);

                    var produced = await produce(compressTempDir, ct);
                    var item = MoveToStaged(produced, lease);
                    Interlocked.Add(ref _stagedBytes, item.Bytes);
                    lease?.Add(item.Bytes);
                    return item;
                }
                finally
                {
                    tracker?.EndPacking();
                }
            }
            finally
            {
                _compressLock.Release();
            }
        }
        finally
        {
            tracker?.EndStaging();
        }
    }

    /// <summary>
    /// **一卷**传完后调用：删掉这一卷、扣掉它占的字节、唤醒等待的压缩。
    /// 幂等——已经释放过（或压根不属于本暂存区）的路径直接忽略。
    /// </summary>
    public void ReleaseFile(string file)
    {
        if (!_staged.TryRemove(file, out var entry))
            return;
        try { File.Delete(file); } catch { /* best effort */ }
        Interlocked.Add(ref _stagedBytes, -entry.Bytes);
        // 席位的账也要扣：不扣的话这次运行的占用只增不减，它自己的配额很快就永远满着。
        entry.Lease?.Add(-entry.Bytes);
        SignalRelease();
    }

    /// <summary>
    /// 把一份归档的所有权系在一个作用域上：块结束就还，无论是正常走完、<c>continue</c>、还是抛出。
    /// <para>
    /// 存在的理由是**抛出**那一条：这份账记在单例上，是进程内内存计数，漏一次就永远挂在那里
    /// （只有重启才清），而它同时是产出的背压闸门——虚高到上限，所有运行的压缩/打包都会被卡在
    /// <see cref="WaitForRoomAsync"/> 上，整条流水线退化成"一件传完才放行下一件"。
    /// 界面上还看不出来：那一栏显示的是本次运行席位的占用，不是全局账。
    /// </para>
    /// <para>
    /// 与"用完立刻还"并存而不是取代它：调用方仍该在用完的那一刻 <see cref="Release(StagedItem)"/>
    /// 腾出额度（早还一秒，别人就早一秒开得了工），这里只兜异常路径。两次释放是安全的——
    /// <see cref="ReleaseFile"/> 按路径 <c>TryRemove</c>，第二次直接短路。
    /// </para>
    /// <param name="item">可以为 null（整组成员都被 7z 丢掉时连空归档都没有），那时什么都不做。</param>
    /// </summary>
    public IDisposable Hold(StagedItem? item) => new Holder(this, item);

    private sealed class Holder(StagingArea area, StagedItem? item) : IDisposable
    {
        public void Dispose()
        {
            if (item is not null)
                area.Release(item);
        }
    }

    /// <summary>整族收尾：把还没逐卷释放掉的都释放掉（去重命中时一卷都没传，全在这里还），
    /// 再删空的 GUID 子目录。逐卷释放过的部分在 <see cref="ReleaseFile"/> 里已经幂等短路。</summary>
    public void Release(StagedItem item)
    {
        foreach (var file in item.Files)
            ReleaseFile(file);
        // 删空的 GUID 子目录。
        foreach (var dir in item.Files.Select(Path.GetDirectoryName).Distinct())
        {
            try { if (dir is not null && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
            catch { /* best effort */ }
        }
        // 整族收尾也发一次信号：全部卷都已逐卷释放时上面一次都没发，等在背压里的压缩会漏掉唤醒。
        SignalRelease();
    }

    private StagedItem MoveToStaged(IReadOnlyList<string> producedFiles, StagingLease? lease)
    {
        if (producedFiles.Count == 0)
            return new StagedItem([], 0); // 无产出：不建子目录，避免留下空 GUID 目录

        // 每次暂存独立 GUID 子目录：不同备份即使产出同名文件也不互相覆盖（跨 container 并发安全）。
        var subDir = Path.Combine(stagedTempDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(subDir);
        var staged = new List<string>(producedFiles.Count);
        long bytes = 0;
        try
        {
            foreach (var src in producedFiles)
            {
                var dest = Path.Combine(subDir, Path.GetFileName(src));
                File.Move(src, dest, overwrite: false);
                var size = new FileInfo(dest).Length;
                bytes += size;
                // 逐卷释放要按这份账扣，不能事后再 stat（那时文件已经删了）。
                // 一并记下这一卷记在谁账上，否则释放时无从知道该给哪个席位退额度。
                _staged[dest] = (lease, size);
                staged.Add(dest);
            }
        }
        catch
        {
            // 中途失败：清理已移动文件 + 子目录，不泄漏。异常沿 StageAsync 抛出，调用方不会把 bytes 记入 _stagedBytes，
            // 所以这里只从账本上摘掉、**不**去扣 _stagedBytes——那笔钱根本没记上过。
            foreach (var f in staged)
                _staged.TryRemove(f, out _);
            try { Directory.Delete(subDir, recursive: true); } catch { /* best effort */ }
            throw;
        }
        return new StagedItem(staged, bytes);
    }

    public void Dispose()
    {
        _compressLock.Dispose();
        _releaseSignal.Dispose();
    }
}
