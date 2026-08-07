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

    public StopKind Stop => (StopKind)Volatile.Read(ref _stopKind);
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
    public void RequestStop(StopKind kind)
    {
        if (kind == StopKind.None)
            return;
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
    public async Task OpenJournalAsync(
        int accountId, string container, int baselineVersion, string localRoot, string encryptionIdentity,
        DateTimeOffset startedAt, CancellationToken ct)
    {
        _accountId = accountId;
        _container = container;
        _journal = await store.CreateAsync(accountId, container, runId, new JournalHeader
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
