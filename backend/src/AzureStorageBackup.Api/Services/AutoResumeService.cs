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
    /// <summary>
    /// 开工前先等这么久：让 Web 端口先起来、调度器先跑第一拍，再去抢产出锁。
    /// <para>
    /// 可写（而不是 <c>const</c>/<c>readonly</c>）纯粹为了测试：等 15 秒的用例没人愿意跑，而这个 if
    /// （"设置关掉时真的不开工"）恰恰是这个功能最该被钉住的一句话——它坏掉的样子是**静默**的：
    /// 操作员取消勾选，界面照样显示保存成功，直到某天重启后一轮他不想要的备份自己跑起来。
    /// 先例是 <see cref="BackupRunner.SuspendWaitCap"/>。
    /// </para>
    /// <para>
    /// 之所以敢开成可写的静态字段，是因为读它的人只有一个、而且在测试里根本不存在：
    /// 本服务只在 <c>Scheduler:Enabled=true</c> 时才注册（见 Program.cs），而
    /// <see cref="TestWebAppFactory"/> 一律把它设成 false。所以整个测试进程里跑着的
    /// <see cref="AutoResumeService"/> 只有测试自己 new 出来的那一个，改这个字段影响不到别人。
    /// 举个例子说清这个前提有多重要：哪天有人让测试主机也起这个服务，两个并行的用例一个把它改成
    /// 50 毫秒、一个正等着它别开工，后者就会被前者的值捅穿——那时这个字段必须换成注入的选项。
    /// </para>
    /// </summary>
    internal static TimeSpan Delay = TimeSpan.FromSeconds(15);

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
    /// <param name="logger">
    /// 给被**否掉**的配置各记一句。可选，纯函数的单测不用传。
    /// <para>
    /// 这一句不是可有可无的排场：这个部署形态是 NAS 上的成品机，操作员既没有 shell 也没有任何看标记
    /// 文件的工具。少了它，"重启之后我的备份怎么没接上"这个问题在他那边是**完全没有线索**的——
    /// 界面上开关是开的，日志里一个字都没有，而真正的原因（某一卷停在别的理由上）只长在盘上。
    /// </para>
    /// </param>
    public static async Task<IReadOnlyList<int>> PickResumableAsync(
        BackupJournalStore journals,
        IReadOnlyList<(int ConfigId, int AccountId, string Container)> configs,
        CancellationToken ct,
        ILogger? logger = null)
    {
        var picked = new List<int>();
        foreach (var (configId, accountId, container) in configs)
        {
            // 用 PeekAsync 而不是 ListAsync：这里只要每一卷的 runId，而 ListAsync 会把每一卷的
            // **每一条记录**都反序列化一遍。停在半路的那一卷恰恰可能有几十万条（本仓库实测过 20 万
            // 条目的扫描），而这段代码跑在启动路径上——为了拿一串文件名去解析几百 MB JSON，
            // 代价与它买到的东西完全不成比例。PeekAsync 只读头一行、剩下的数行数。
            var volumes = await journals.PeekAsync(accountId, container, ct);
            if (volumes.Count == 0)
                continue;

            // 第一卷不合格的就足以否掉整个配置，也正好是日志里该点名的那一卷。
            var blocker = volumes.FirstOrDefault(x =>
                journals.ReadSuspendMark(accountId, container, x.RunId) != SuspendReason.ShuttingDown);
            if (blocker is null)
            {
                picked.Add(configId);
                continue;
            }

            var mark = journals.ReadSuspendMark(accountId, container, blocker.RunId);
            logger?.LogInformation(
                "Not resuming backup config {ConfigId} on startup: its journal {RunId} is marked "
                + "'{Mark}', and only runs left behind by a planned shutdown are resumed automatically. "
                + "Press Run to continue this backup.",
                configId, blocker.RunId, mark?.ToString() ?? "none");
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
                stoppingToken,
                logger);

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

                // StartAsync 有两条**当场就返回终态**的短路（配置查不到、忙碌锁在别人手里），
                // 那时压根没有哪一轮运行开起来，state.RunId 指的是一次不存在的运行。
                // 那两种都不该报成"已经接着跑了"，否则日志会拿一个查无此运行的 RunId 骗人。
                if (state.Status != RunStatus.Running)
                {
                    logger.LogWarning(
                        "Could not auto-resume interrupted backup {ConfigId}: {Error}",
                        configId, state.Error ?? state.Status.ToString());
                    continue;
                }

                logger.LogInformation(
                    "Auto-resuming interrupted backup {ConfigId} (run {RunId})", configId, state.RunId);

                // 等它到终态。关机时这一等不会拖住宿主：GracefulSuspendService 注册在本服务之后、
                // 因而停在本服务之前，它会把这一轮挂起落盘，这里的等待随之结束。
                //
                // 这一等**有意不设上限**：串行是必须的（见上），设了上限就等于在超时之后放并发进来。
                // 但代价要说清：卡在 PauseGate 上等瞬时错误自愈的运行按设计仍然是 Running（席位还占着，
                // 报成终态会让调度器再起一轮把它顶掉），它的 Completion 因此可以很久很久都不落定——
                // 闸门最长会耐着性子等 10 分钟才降级，而排在它后面的每一个可接着跑的配置就一直排着。
                // 这不是死锁（闸门总会降级或成功，两条路都通向终态），是一段可能很长的队。
                await state.Completion.Task.WaitAsync(stoppingToken);

                // 失败的自动恢复要比成功的显眼一档：它是"系统自己决定开的一轮"，没人守在旁边看结果，
                // 记成 Information 就等于埋进正常流水里。
                if (state.Status == RunStatus.Failed)
                    logger.LogWarning(
                        "Auto-resumed backup {ConfigId} failed: {Error}", configId, state.Error);
                else
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
