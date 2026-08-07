namespace AzureStorageBackup.Api.Services;

/// <summary>一次运行为什么会停在挂起态。</summary>
public enum SuspendReason
{
    /// <summary>用户主动点了 Suspend。</summary>
    UserRequested,

    /// <summary>瞬时错误持续超过耐心阈值，闸门降级。</summary>
    AutoSuspended,

    // 设计稿里还有第三个值 Crashed（进程被 kill / 断电）。这里**故意不要**：
    // 崩溃时没有任何代码在跑，没人能给自己写下一个 reason。那种运行是靠盘上还留着 journal
    // 认出来的，由 GET /{id}/interrupted 直接读目录得到（Task 12），不必在内存里伪造一条
    // 没有 Control、没有忙碌锁的 Suspended 记录——伪造出来的那条记录，每个碰它的分支都要
    // 额外记得"这一条是假的"。
}

/// <summary>
/// "这轮没做完，但现场保住了"。与失败的区别很实在：失败是终点，挂起是可以接着跑的中点，
/// 所以它不能走 <c>RunStatus.Failed</c> 那条路——否则用户看到的是一个红字终局，
/// 而 journal 里其实躺着一整轮已经传上去的内容。
/// </summary>
public sealed class BackupSuspendedException(SuspendReason reason, string message) : Exception(message)
{
    public SuspendReason Reason { get; } = reason;
}
