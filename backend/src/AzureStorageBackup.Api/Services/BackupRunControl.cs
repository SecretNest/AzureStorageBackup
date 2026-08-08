using System.Collections.Concurrent;

namespace AzureStorageBackup.Api.Services;

/// <summary>怎么个停法。</summary>
public enum StopKind
{
    None,

    /// <summary>主动暂停：做完手上这件，落盘，退出成 Suspended。</summary>
    Suspend,

    /// <summary>取消，但把正在上传的文件（含它的全部分卷）做完再停。</summary>
    FinishCurrentFiles,

    /// <summary>取消，立刻中断在途上传，并删掉它留下的残留卷。</summary>
    StopNow,
}

/// <summary>
/// 一次备份运行的"外部把手"：编排器不认识运行注册表，也不该认识；它只认这一个对象。
/// 装着 journal 与挂起闸门，后续任务会再往里加停止意图。
/// </summary>
public sealed class BackupRunControl(
    BackupJournalStore store, int configId, string runId, PauseGate? gate = null) : IAsyncDisposable
{
    private BackupJournal? _journal;
    private int _accountId;
    private string _container = "";

    /// <summary>被本轮采纳的旧 journal 的 runId。本轮成功提交索引时，它们和自己那卷一起删。</summary>
    private readonly List<string> _adopted = [];

    /// <summary>上一轮（或上几轮）已经确认传上去的东西。没有可采纳的卷时是空表。</summary>
    public JournalResume Resume { get; private set; } = JournalResume.Empty;

    /// <summary>开卷时采纳过或作废过旧卷、或这是这个配置在这个容器上的第一轮 → 容器里多半躺着
    /// 孤儿块，收尾清理该做一次扫描（Task 11）。</summary>
    public bool SweepNeeded { get; private set; }

    /// <summary>瞬时错误的挂起闸门。默认 30s/1m/5m/每 5m 自愈，10 分钟不见好就降级。</summary>
    public PauseGate Gate { get; } = gate ?? new PauseGate();

    public string RunId => runId;

    /// <summary>任何停法都会触发：叫停 diff（继续读盘没有意义）。</summary>
    private readonly CancellationTokenSource _stop = new();

    /// <summary>**只有** Stop now 会触发：打断在途上传。
    /// Suspend 与 Finish current files 绝不能碰它，否则"做完当前这件再停"就是句空话。</summary>
    private readonly CancellationTokenSource _abort = new();

    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);

    private int _stopKind;

    /// <summary>-1 = 还没人下达过 Suspend。用哨兵而不是默认值，是为了让"首次下达说了算"这句话
    /// 能用一次 CAS 表达（见 <see cref="RequestStop"/>）。</summary>
    private int _suspendReason = -1;

    public StopKind Stop => (StopKind)Volatile.Read(ref _stopKind);

    /// <summary>这次挂起是为什么。没人下达过 Suspend 时按 UserRequested 报——挂起本来就只有
    /// 被下达过才成立，这个取值只是让读它的地方不必先判一次"有没有"。</summary>
    public SuspendReason SuspendReason
    {
        get
        {
            var v = Volatile.Read(ref _suspendReason);
            return v < 0 ? Services.SuspendReason.UserRequested : (SuspendReason)v;
        }
    }

    public CancellationToken StopToken => _stop.Token;
    public CancellationToken AbortToken => _abort.Token;

    /// <summary>登记/销账"正在上传的这块内容"。Stop now 收尾时按它删残留卷。</summary>
    public void TrackInFlight(string blobRef) => _inFlight[blobRef] = 1;
    public void ClearInFlight(string blobRef) => _inFlight.TryRemove(blobRef, out _);
    public IReadOnlyCollection<string> InFlight => _inFlight.Keys.ToList();

    /// <summary>
    /// 下达停止意愿。**只升不降**：更强的停法覆盖更弱的，更弱或相同的一律忽略。
    /// <para>
    /// 两个方向不对称，所以不能写成"只认第一次"。Stop now 之后再点 Suspend 确实没有意义——
    /// 已经打断、已经删掉的残留卷不可能因为一次更温和的下达而复活。但反过来是用户在**升级**：
    /// 他点了 Suspend，发现卡在一个几十 GB 的多卷文件后面动不了，于是改点 Stop now。
    /// 首次优先会把这次升级静静丢掉，而 <see cref="BackupRunner.CancelAsync"/> 等到终态之后
    /// 照样返回 true——API 报告成功，实际生效的却是另一种停法。
    /// </para>
    /// <para>
    /// 升级只会**收紧**，因此不可能让已经放弃的活复活：<see cref="StopKind"/> 的成员按强度排序，
    /// 判定就是比大小；CAS 循环保证并发下达时留下的是最强的那一个，而点火由**赢下 CAS 的那个
    /// 线程**负责，所以升级到 Stop now 时 <see cref="AbortToken"/> 一定会被点着——哪怕
    /// <see cref="StopToken"/> 早就为上一次较弱的下达点过了（<c>Cancel()</c> 幂等，重复调用无副作用）。
    /// </para>
    /// </summary>
    /// <param name="reason">
    /// 只对 <see cref="StopKind.Suspend"/> 有意义：这一卷被记在盘上的挂起理由（关机路径传
    /// <see cref="SuspendReason.ShuttingDown"/>）。
    /// <para>
    /// 它**不**参与上面那套升级判定，而是另走一次 CAS：**首次**下达 Suspend 的那个理由说了算。
    /// 一次已经在为"用户按了暂停"收尾的运行，不该因为随后到来的关机被改写成 ShuttingDown——
    /// 那一位正是下次启动用来决定"要不要替他重新开跑"的依据，改错了等于替用户撤销他按下的暂停。
    /// 反过来，理由在停法落定**之前**就写好，所以任何看见 <c>Stop == Suspend</c> 的线程都读得到
    /// 配套的理由，不存在"停法到了、理由还没到"的窗口。
    /// </para>
    /// </param>
    public void RequestStop(StopKind kind, SuspendReason reason = SuspendReason.UserRequested)
    {
        if (kind == StopKind.None)
            return;
        if (kind == StopKind.Suspend)
            Interlocked.CompareExchange(ref _suspendReason, (int)reason, -1);
        while (true)
        {
            var current = Volatile.Read(ref _stopKind);
            if ((int)kind <= current)
                return;     // 更弱或相同：忽略
            if (Interlocked.CompareExchange(ref _stopKind, (int)kind, current) == current)
                break;
        }
        // 正卡在闸门上等重试的工作者要被叫醒，否则它们会一直等到下一次自愈计时器到点。
        //
        // 这一句对 Suspend / Finish current files 同样会触发，于是一件**正卡在闸门上等自愈**的活
        // 会就此被放弃，而不是"做完当前这件"。这是有意的取舍：不叫醒它，用户按下的停止最长要等
        // 5 分钟（自愈计时器的最后一档）才有反应，而那件活本来就正卡在一个还没好的瞬时错误上，
        // 等下去多半也是白等。最终抛出的异常仍由编排器的 SettleStopAsync 按停法纠正，
        // 所以对外的行为（Suspended / Canceled、journal 落盘、残留清理）都是对的——
        // 不成立的只是"做完当前这件"这句话对闸门上那件活的字面含义。
        Gate.Downgrade();
        _stop.Cancel();
        if (kind == StopKind.StopNow)
            _abort.Cancel();
    }

    /// <summary>
    /// 开卷。必须等编排器算出基线版本与寻址身份之后再调——这两样是恢复的前置条件，
    /// 写不进头里，这卷 journal 就没法安全复用。
    /// </summary>
    /// <param name="firstRun">
    /// 本地权威状态还没建立 = 这是这个配置在这个容器上的第一轮（新建，或**删了配置又重建**）。
    /// 它同样要触发一次孤儿扫描，原委见下方 <see cref="SweepNeeded"/> 的赋值处。
    /// </param>
    public async Task OpenJournalAsync(
        int accountId, string container, int baselineVersion, string localRoot, string encryptionIdentity,
        DateTimeOffset startedAt, CancellationToken ct, bool firstRun = false)
    {
        _accountId = accountId;
        _container = container;

        // 对得上号的采纳，对不上的当场删。
        //
        // configId 不同也照删：(AccountId, ContainerName) 在 AppDbContext 里是唯一索引，
        // 一个容器至多一个配置——所以那只可能是"配置删了又在同一个容器上重建"留下的陈迹。
        // 留着它会永远保住那批块不被清理（清理判据认 journal，不认 configId）。
        // 哪天允许多个配置共用一个容器了，这一条必须改回"不是我们的就完全不碰"，
        // 否则会把别人正挂起着的运行的成果变成孤儿。
        var voided = false;
        // 采纳到的那一卷正是本轮自己那卷（runId 重名）→ 接着往它后面写，绝不新开一卷盖上去。
        var reopenMine = false;
        var adopted = new List<JournalContent>();
        var myPath = store.PathFor(accountId, container, runId);
        foreach (var (oldRunId, content) in await store.ListAsync(accountId, container, ct))
        {
            var h = content.Header;
            // 比的是**落到盘上的那个路径**而不是两个 runId 字符串：文件名经过 BackupJournalStore.Safe
            // 扁平化，两个不同的 runId 完全可能落在同一个文件上——那时它们就是同一卷。
            var mine = string.Equals(
                store.PathFor(accountId, container, oldRunId), myPath, StringComparison.Ordinal);
            if (h.ConfigId == configId
                && h.BaselineVersion == baselineVersion
                && string.Equals(h.LocalRoot, localRoot, StringComparison.Ordinal)
                && string.Equals(h.EncryptionIdentity, encryptionIdentity, StringComparison.Ordinal))
            {
                adopted.Add(content);
                // 重名的那一卷**不**进 _adopted：它就是本轮自己那卷，CompleteAsync 已经按 runId 删过它。
                if (mine)
                {
                    reopenMine = true;
                }
                else
                {
                    _adopted.Add(oldRunId);
                    // 采纳的同时把旧卷的挂起标记抹掉：那个标记说的是**已经被顶替掉的那一轮**为什么停下，
                    // 而"这一卷现在归本轮管"正是让它作废的那个事件——操作员按了 Run，或者启动时自动接了一轮。
                    //
                    // 不抹的话它会一直粘着，直到某一轮真的跑成功为止（旧卷只在 CompleteAsync 里删）。
                    // 后果落在自动接着跑的判据上：那条判据要求这个配置底下**每一卷**都写着 ShuttingDown，
                    // 于是一卷陈年的 AutoSuspended / UserRequested 就能一票否决掉后面每一次计划内重启，
                    // 而越是长跑、越是不容易跑完一整轮的配置，越容易卡在这个状态里——恰恰是这个功能要救的那些。
                    //
                    // 抹掉之后这一卷不是就此没有标记了：本轮真挂起时会连它一起重新写上本轮的理由
                    //（见 MarkSuspended）。所以"这个配置停在什么状态"始终由**当前这一轮**说了算。
                    store.ClearSuspendMark(accountId, container, oldRunId);
                }
            }
            else
            {
                // 重名但判据对不上，照删不误：它按本轮的判据已经作废，而下面新开的那一卷本来
                // 就要落在这个路径上（FileMode.Create），删与不删结果一样。
                store.Delete(accountId, container, oldRunId);
                voided = true;
            }
        }
        // 采纳过、或作废过 → 这个容器里多半躺着"云上有、索引里没有"的块。
        // 收尾清理据此决定要不要做一次孤儿扫描（见 Task 11）。
        //
        // 第一轮也扫，而且这一条不是顺手加的：删配置（保留容器）会把这个容器的 journal 全部丢掉，
        // 那批块从此失去保护，而删配置端点承诺的正是"等这个容器上再有配置时，第一次清理会把真孤儿
        // 扫掉"。少了这一条，那句承诺是空的——重建配置后的第一次清理是**备份收尾**那次，而那时
        // journal 目录刚被删空，既没采纳也没作废，上面两项全是 false；只有独立跑的 Cleanup 计划任务
        // 才会扫，而用户完全可能一个清理计划都没配。
        //
        // 代价可控：这是每个配置在每个容器上**只发生一次**的两趟 LIST（data/ 与 packs/），
        // 而新建备份的容器本来就是空的；真正大的那种容器恰恰是"删了配置又重建"的情形，
        // 也正是非扫不可的那一种。
        SweepNeeded = voided || adopted.Count > 0 || firstRun;
        // 采纳是**只读**的：本轮仍新开自己那一卷，旧卷原样留着。这样就不必把复用来的记录再抄一遍，
        // 也不会出现"抄到一半又崩了"的半截状态。旧卷等本轮成功提交索引时一起删。
        Resume = adopted.Count == 0
            ? JournalResume.Empty
            : new JournalResume([.. adopted.SelectMany(c => c.Records)]);

        // runId 与刚采纳的那一卷重名时**接着写**，不能新开：CreateAsync 是 FileMode.Create，
        // 会把刚刚采纳的那一卷当场截断。
        //
        // 今天撞不上，而且**今天没有任何调用方会撞上**：runId 一律取自 BackupRunState.RunId，
        // 那是每轮新生成的 GUID 前缀，没有哪条路径会沿用上一轮的。启动时自动接着跑（AutoResumeService）
        // 也不例外——它走的是上面的**采纳**分支（_adopted），新开自己那一卷。
        // 这段分支是给"哪天真有人让某一轮沿用旧 runId"准备的：譬如为了让界面上的运行身份跨挂起保持不变。
        // 截断之后本轮内存里的 Resume 仍然是全的（上面已经读进来了），所以当轮跑下去不出错；
        // 坏掉的是**盘上**那份担保：再挂起一次，新卷不为那批块作保，下一轮把它们全部重传，
        // 而按 journal 判"这块有没有人认领"的清理/孤儿扫描会直接把它们当垃圾删掉。
        //
        // 接着写时头一行**不**重写（见 BackupJournal.OpenForAppendAsync）：判"是我"用的是落到盘上
        // 那个路径而不是 runId 字符串（上面 mine 的算法），两个不同的 runId 完全可能落在同一个文件
        // 上——盘上那份头记的因此可能是**更早**那个 runId 和它的 StartedAt。今天没人读这两个字段，
        // 但往后谁要读 Header.RunId / Header.StartedAt 指望拿到本轮的值，读到的会是陈迹。
        _journal = reopenMine
            ? await store.AppendAsync(accountId, container, runId, ct)
            : await store.CreateAsync(accountId, container, runId, new JournalHeader
            {
                RunId = runId,
                ConfigId = configId,
                StartedAt = startedAt,
                BaselineVersion = baselineVersion,
                LocalRoot = localRoot,
                EncryptionIdentity = encryptionIdentity,
            }, ct);
    }

    /// <summary>记一个单文件 blob。**只能**在上传确认返回之后调。</summary>
    public async Task RecordBlobAsync(
        string path, string blobRef, string fullHash, string headHash, string tailHash, long length,
        int volumes, bool raw, IReadOnlyList<long> volumeSizes, CancellationToken ct)
    {
        if (_journal is null)
            return;
        await _journal.AppendAsync(new JournalRecord
        {
            Kind = "blob", Ref = blobRef, Path = path, FullHash = fullHash, HeadHash = headHash,
            TailHash = tailHash, Length = length, Volumes = volumes, Raw = raw, VolumeSizes = volumeSizes,
        }, ct);
    }

    /// <summary>记一个 pack。同样**只能**在上传确认返回之后调。</summary>
    public async Task RecordPackAsync(
        string packId, IReadOnlyList<JournalMember> members, IReadOnlyList<long> volumeSizes, bool storeOnly,
        CancellationToken ct)
    {
        if (_journal is null)
            return;
        await _journal.AppendAsync(new JournalRecord
        {
            Kind = "pack", Ref = packId, Members = members, VolumeSizes = volumeSizes,
            Volumes = Math.Max(1, volumeSizes.Count), StoreOnly = storeOnly,
        }, ct);
    }

    /// <summary>
    /// 把"这一卷为什么停下"写在 journal 旁边。内存里那份理由随进程一起没了，而"进程没了"
    /// 恰恰是下次启动要判断的那种情形，所以必须落一份到盘上。
    /// <para>
    /// 还没开卷就挂起的（扫描阶段被叫停）什么都不写：盘上根本没有这一卷 journal，
    /// 标记只会变成一个指向不存在 journal 的孤儿，反倒要让读它的人多一处判空。
    /// </para>
    /// <para>
    /// 写的是**本轮名下每一卷**：自己那卷，加上开卷时采纳来的所有旧卷。理由是标记按卷记、判据按卷读
    /// （<see cref="AutoResumeService.PickResumableAsync"/> 要求每一卷都写着 ShuttingDown），
    /// 而采纳之后这几卷就是同一轮运行的现场，一起停下、一起接着跑，没有哪一卷可以停在别的理由上。
    /// 只写自己那卷的话，采纳来的旧卷会停在"开卷时被抹掉的那个空标记"上，于是**从第二次重启起**
    /// 判据就再也凑不齐——一次计划内重启接上了，第二次就悄没声地不接了。
    /// </para>
    /// </summary>
    public void MarkSuspended(SuspendReason reason)
    {
        if (_journal is null)
            return;
        store.MarkSuspended(_accountId, _container, runId, reason);
        foreach (var old in _adopted)
            store.MarkSuspended(_accountId, _container, old, reason);
    }

    public async Task FlushAsync(bool fsync, CancellationToken ct)
    {
        if (_journal is not null)
            await _journal.FlushAsync(fsync, ct);
    }

    /// <summary>
    /// 运行成功收尾：索引已提交，journal 就没用了。
    /// 必须在信息文件提交**之后**、保留清理**之前**删——顺序反了，
    /// 清理会看到"既不被索引引用、也不被 journal 引用"的空档，把刚传上去的内容删掉。
    /// </summary>
    public async Task CompleteAsync()
    {
        if (_journal is null)
            return;
        await _journal.DisposeAsync();
        _journal = null;
        store.Delete(_accountId, _container, runId);
        // 采纳来的旧卷同样功成身退——它们记的内容此刻已经全在提交好的索引里了。
        foreach (var old in _adopted)
            store.Delete(_accountId, _container, old);
        _adopted.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        Gate.Dispose();
        if (_journal is not null)
            await _journal.DisposeAsync();
        _journal = null;
        _stop.Dispose();
        _abort.Dispose();
    }
}
