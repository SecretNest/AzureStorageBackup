using AzureStorageBackup.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 启动后把上次被**计划内退出**打断的备份接着跑（受 <c>GlobalSettings.AutoResumeInterruptedRuns</c> 控制）。
/// <para>
/// 前提是盘上还留着 journal —— 跑完的运行会删掉自己那卷，所以留着就等于"没跑完"。
/// 但"没跑完"远不足以构成"该替他重开"，判据见 <see cref="PickResumableAsync"/>。
/// </para>
/// </summary>
public sealed class AutoResumeService(
    IServiceScopeFactory scopes, BackupJournalStore journals, BackupRunner runner,
    ILogger<AutoResumeService> logger) : BackgroundService
{
    /// <summary>让 Web 端口先起来、调度器先跑第一拍，再去抢产出锁。</summary>
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 从盘上挑出该自动接着跑的 configId。纯函数（只读盘），单测直接调它。
    /// <para>
    /// 判据只有一条：这个配置**至少留了一卷** journal，而且**每一卷**旁边的标记都写着
    /// <see cref="SuspendReason.ShuttingDown"/>。别的一概不动，逐一说清为什么：
    /// </para>
    /// <list type="bullet">
    /// <item><b>ShuttingDown</b> —— 一次计划内的进程退出把它停在这儿的，落盘走的是
    /// <c>SettleStopAsync</c>（journal 先 fsync 再写标记）。这是唯一一种"这个进程自己造成的中断、
    /// 而且现场是好的"，所以也是唯一一种可以不问自取接着跑的。</item>
    /// <item><b>UserRequested</b> —— 操作员亲手按的暂停。替他重开等于把他按那一下的意图擦掉。</item>
    /// <item><b>AutoSuspended</b> —— 闸门耐心耗尽降级停的。那个瞬时错误多半还在（网线还没插上、
    /// 对端还在 503），马上接着跑只会立刻再撞一次墙、再挂起一次，白烧一轮。</item>
    /// <item><b>没有标记</b> —— 说不清。崩了、被 kill、关机等落盘超时被丢在半路、操作员按了 Cancel
    /// （两种取消都照样落盘，都有意不写标记）、或者就是那次写标记本身失败了，长在盘上一模一样。
    /// 其中至少有一种（Cancel）是用户明确表达过"别跑了"的，所以这一大类整个不碰。</item>
    /// </list>
    /// <para>
    /// 要求**每一卷**都是 ShuttingDown，而不是挑最新那卷说了算：标记是按卷记的，一个配置底下
    /// 完全可能出现取值打架的几卷（按暂停 → 又点 Run → 新一轮采纳了旧卷 → 一次关机把新一轮停成
    /// ShuttingDown）。而接着跑那一轮开卷时会把所有还作数的卷一起认下来，所以只要有一卷是不该动的，
    /// 动了就等于连它一起动了。要求全票通过，就不必再发明一套"哪卷更新说了算"的仲裁。
    /// </para>
    /// <para>同一个备份留了几卷都只算一次：接着跑是**一轮新的运行**，它会自己去认所有卷。</para>
    /// </summary>
    public static async Task<IReadOnlyList<int>> PickResumableAsync(
        BackupJournalStore journals,
        IReadOnlyList<(int ConfigId, int AccountId, string Container)> configs,
        CancellationToken ct)
    {
        var picked = new List<int>();
        foreach (var (configId, accountId, container) in configs)
        {
            var listed = await journals.ListAsync(accountId, container, ct);
            if (listed.Count == 0)
                continue;

            var allShuttingDown = listed.All(x =>
                journals.ReadSuspendMark(accountId, container, x.RunId) == SuspendReason.ShuttingDown);
            if (allShuttingDown)
                picked.Add(configId);
        }
        return picked;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(Delay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        try
        {
            using var scope = scopes.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<IGlobalSettingsService>();
            if (!(await settings.GetAsync(stoppingToken)).AutoResumeInterruptedRuns)
                return;

            // 每个配置都是候选：BackupConfig 上没有"启用/停用"这回事，一个配置存在就是要备份的。
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var configs = await db.BackupConfigs.AsNoTracking()
                .Select(c => new { c.Id, c.AccountId, c.ContainerName })
                .ToListAsync(stoppingToken);

            var resumable = await PickResumableAsync(
                journals,
                [.. configs.Select(c => (c.Id, c.AccountId, c.ContainerName))],
                stoppingToken);

            // 逐个起，**并且等前一个跑完再起下一个**：产出锁是全局的，一起冲上去只会互相排队，
            // 还看不出是谁在等谁（并发备份反而更慢，这一条本仓库实测过）。
            //
            // 光写个 foreach 是不够的：StartAsync 把活丢进 Task.Run 就返回了，连着调几次的效果
            // 就是几轮同时在跑。真正让它串起来的是下面那个 await Completion。
            foreach (var configId in resumable)
            {
                if (stoppingToken.IsCancellationRequested)
                    return;
                var state = await runner.StartAsync(configId);
                logger.LogInformation(
                    "Auto-resuming interrupted backup {ConfigId} (run {RunId})", configId, state.RunId);

                // 等它到终态。关机时这一等不会拖住宿主：GracefulSuspendService 注册在本服务之后、
                // 因而停在本服务之前，它会把这一轮挂起落盘，这里的等待随之结束。
                await state.Completion.Task.WaitAsync(stoppingToken);
                logger.LogInformation(
                    "Auto-resumed backup {ConfigId} ended as {Status}", configId, state.Status);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // 自动恢复失败不能让进程起不来：用户还可以自己去点一下。
            logger.LogError(ex, "Auto-resume of interrupted backups failed");
        }
    }
}
