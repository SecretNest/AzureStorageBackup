namespace AzureStorageBackup.Api.Services;

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
    }
}
