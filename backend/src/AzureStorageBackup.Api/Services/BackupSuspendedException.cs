namespace AzureStorageBackup.Api.Services;

/// <summary>一次运行为什么会停在挂起态。</summary>
public enum SuspendReason
{
    /// <summary>用户主动点了 Suspend。</summary>
    UserRequested,

    /// <summary>瞬时错误持续超过耐心阈值，闸门降级。</summary>
    AutoSuspended,

    /// <summary>
    /// 进程被要求正常退出（<c>docker stop</c>、升级重启），运行顺手挂起。
    /// <para>
    /// 与被砍掉的 <c>Crashed</c> 不是一回事：关机时代码**还在跑**，能给自己写下理由；
    /// 崩溃时没有任何代码在跑，那种运行只能靠盘上还留着 journal 事后认出来。
    /// </para>
    /// <para>
    /// **只有这一个理由**可以据以不问自取地自动接着跑：它唯一地对应"计划内重启/升级"这一种情形，
    /// 而自动恢复要覆盖的正是且仅是这一种。反过来不成立——盘上没有标记**不**说明"进程被 kill"：
    /// 崩溃、掉电、关机等落盘超时把这一卷丢在半路、操作员自己按了 Cancel（取消照样落盘，但不写标记）、
    /// 甚至写标记这一步本身失败，看上去都一样。这些一律等操作员按 Resume。
    /// </para>
    /// </summary>
    ShuttingDown,

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
