using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 暂存区在**多个备份同时跑**时的配额与并发。两件事一起才有意义：
/// <list type="number">
/// <item>配额按当前在跑的运行数均分（存量的非活动备份不占席位）；</item>
/// <item>等空间时**不能**占着那把全局压缩锁——否则给谁加配额都没用：被自己配额挡住的那个
/// 运行照样卡在锁上，别人一样压不了，只是换了个理由卡死。</item>
/// </list>
/// 全局上限继续生效，因为暂存盘是**物理**磁盘：配额管的是公平，全局上限管的是不写满盘。
/// </summary>
public sealed class StagingQuotaTests : IDisposable
{
    private readonly string _root;
    private readonly string _compressTemp;
    private readonly string _stagedTemp;

    public StagingQuotaTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "asb-quota-" + Guid.NewGuid().ToString("N"));
        _compressTemp = Path.Combine(_root, "compress");
        _stagedTemp = Path.Combine(_root, "staged");
        Directory.CreateDirectory(_compressTemp);
        Directory.CreateDirectory(_stagedTemp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private StagingArea Area(long limit) => new(_compressTemp, _stagedTemp, () => limit);

    private static Func<string, CancellationToken, Task<IReadOnlyList<string>>> Produce(string name, int size)
        => async (dir, ct) =>
        {
            var path = Path.Combine(dir, name);
            await File.WriteAllBytesAsync(path, new byte[size], ct);
            return [path];
        };

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 这是整个改造的验收点。改造前背压是**持着压缩锁**等的，于是 A 一旦被暂存挡住，B 连压缩都
    /// 开始不了——加配额只会让 A 更早开始占着锁干等，B 的处境分毫未变。
    /// </summary>
    [Fact]
    public async Task A_Run_Blocked_By_Its_Own_Quota_Releases_The_Compression_Lock()
    {
        using var area = Area(limit: 1000);   // 两个席位 → 各 500
        using var a = area.AcquireLease();
        using var b = area.AcquireLease();

        var itemA = await area.StageAsync(Produce("a1", 500), a);
        Assert.Equal(500, area.StagedBytes);

        // A 已经占满自己那一半，下一件必须被挡住。
        var blockedA = area.StageAsync(Produce("a2", 100), a);
        await Task.Delay(200);
        Assert.False(blockedA.IsCompleted, "A 占满了自己的配额，这一件本该被挡住");

        // 而 B 必须照样压得动——A 在等空间，但它不该占着那把全局压缩锁。
        var itemB = await area.StageAsync(Produce("b1", 400), b).WaitAsync(Patience);
        Assert.Equal(400, itemB.Bytes);

        // A 的占用一还回来，被挡住的那件立刻继续。
        area.Release(itemA);
        var resumed = await blockedA.WaitAsync(Patience);
        Assert.Equal(100, resumed.Bytes);
    }

    /// <summary>配额是公平，全局上限是磁盘安全——后者必须继续拦得住，否则并行时会把盘写满。</summary>
    [Fact]
    public async Task The_Global_Limit_Still_Caps_The_Sum_Across_Runs()
    {
        using var area = Area(limit: 1000);
        using var a = area.AcquireLease();
        using var b = area.AcquireLease();

        // 两边各占满自己的一半，总量正好到顶。
        var itemA = await area.StageAsync(Produce("a1", 500), a);
        var itemB = await area.StageAsync(Produce("b1", 500), b);
        Assert.Equal(1000, area.StagedBytes);

        // 此时谁都不该再进得去。
        var moreA = area.StageAsync(Produce("a2", 10), a);
        var moreB = area.StageAsync(Produce("b2", 10), b);
        await Task.Delay(200);
        Assert.False(moreA.IsCompleted, "全局已到上限，A 不该再压");
        Assert.False(moreB.IsCompleted, "全局已到上限，B 不该再压");

        area.Release(itemA);
        area.Release(itemB);
        await Task.WhenAll(moreA, moreB).WaitAsync(Patience);
    }

    /// <summary>
    /// 只有一个备份在跑时，它就该拿到**全部**额度。存量的非活动备份不占席位——
    /// 否则配了十个备份、只跑一个，那一个也只能用十分之一的暂存盘。
    /// </summary>
    [Fact]
    public async Task A_Single_Active_Run_Gets_The_Whole_Limit()
    {
        using var area = Area(limit: 1000);
        using var only = area.AcquireLease();

        var first = await area.StageAsync(Produce("s1", 600), only).WaitAsync(Patience);
        Assert.Equal(600, first.Bytes);
        // 600 已经超过"两个席位时的一半"，独占时却必须放行。
        var second = await area.StageAsync(Produce("s2", 300), only).WaitAsync(Patience);
        Assert.Equal(300, second.Bytes);
    }

    /// <summary>席位是随运行来去的：一个备份跑完，剩下的那个应当立刻拿到更大的额度。</summary>
    [Fact]
    public async Task Finishing_A_Run_Hands_Its_Share_To_Whoever_Is_Left()
    {
        using var area = Area(limit: 1000);
        var a = area.AcquireLease();
        using var b = area.AcquireLease();

        // 两个席位时 B 的额度是 500。占**满**它才会被挡下一件——判据是"当前占用低于额度就放行"
        // （与 An_Item_Larger_Than_The_Quota_Still_Gets_Through 同一条语义），占 400 是拦不住的。
        await area.StageAsync(Produce("b1", 500), b);
        var blocked = area.StageAsync(Produce("b2", 200), b);
        await Task.Delay(200);
        Assert.False(blocked.IsCompleted, "两个席位时 B 只有一半额度");

        // A 收工交还席位 → B 独占全部额度，那一件就该放行。
        a.Dispose();
        var resumed = await blocked.WaitAsync(Patience);
        Assert.Equal(200, resumed.Bytes);
    }

    /// <summary>
    /// 一件活比整个配额还大时必须放行，否则它永远压不出来。沿用既有语义：
    /// 只要**当前**占用在额度以下就开压，允许这一件的产物临时超出。
    /// </summary>
    [Fact]
    public async Task An_Item_Larger_Than_The_Quota_Still_Gets_Through()
    {
        using var area = Area(limit: 1000);
        using var a = area.AcquireLease();
        using var b = area.AcquireLease();

        // 席位额度 500，产物 900——从零起步，必须放行。
        var item = await area.StageAsync(Produce("big", 900), a).WaitAsync(Patience);
        Assert.Equal(900, item.Bytes);
    }

    /// <summary>不带席位的调用方（不关心公平的路径、既有测试）只受全局上限约束，行为与从前一致。</summary>
    [Fact]
    public async Task Callers_Without_A_Lease_Are_Bounded_By_The_Global_Limit_Only()
    {
        using var area = Area(limit: 1000);
        using var a = area.AcquireLease();

        var item = await area.StageAsync(Produce("anon", 800)).WaitAsync(Patience);
        Assert.Equal(800, item.Bytes);
    }
}
