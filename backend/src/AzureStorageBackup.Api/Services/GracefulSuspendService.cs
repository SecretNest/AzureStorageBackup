namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 进程正常退出时（<c>docker stop</c>、升级重启）把在跑的备份挂起落盘。
/// <para>
/// 用 <see cref="IHostedService.StopAsync"/> 而不是 <c>ApplicationStopping</c> 的回调：
/// 前者是 await 得到的，宿主会等它返回才继续拆服务；后者是同步事件，等不住异步落盘。
/// </para>
/// <para>
/// 注册顺序有意义：宿主按注册的**逆序**停服务，所以这个要注册在 <c>SchedulerService</c> **之后**，
/// 才能先于调度器停下来——不然调度器可能在挂起进行到一半时又起一轮，而那一轮再没有人来挂起它。
/// </para>
/// </summary>
public sealed class GracefulSuspendService(BackupRunner runner, ILogger<GracefulSuspendService> logger)
    : IHostedService
{
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken ct)
    {
        try
        {
            // 这个数是"真的停成 Suspended、盘上留下了标记"的条数，不是"发出去几条停止请求"——
            // 等超时的、以及被同时到达的 Stop now 抢先按成 Canceled 的都不算。日志说的话必须与
            // 盘上的东西对得上，否则事后按这条日志去找标记只会找空。
            var stopped = await runner.SuspendAllAsync(SuspendReason.ShuttingDown, ct);
            if (stopped > 0)
                logger.LogInformation("Suspended {Count} running backup(s) for shutdown", stopped);
        }
        catch (Exception ex)
        {
            // 关机路径上抛出去只会变成一条谁也看不见的宿主错误，还可能盖掉别的服务的收尾。
            logger.LogError(ex, "Failed to suspend running backups during shutdown");
        }
    }
}
