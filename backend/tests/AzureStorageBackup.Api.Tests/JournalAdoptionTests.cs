using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 开卷时那张"采纳还是作废"的判定表（<see cref="BackupRunControl.OpenJournalAsync"/>）。
/// <para>
/// 四个判据里只要有一个写反，全套备份用例照样绿：作废的那一支会静静地把上一轮的成果删掉，
/// 采纳的那一支会静静地把别人的成果当成自己的。ConfigId 那一项尤其要紧——它是唯一一条会去动
/// **不属于本轮**的状态的分支，Task 11 的孤儿清扫落地之后，判错就等于"把一个挂起中的运行
/// 已经做完的活删了"。所以整张表逐项钉死在这里。
/// </para>
/// <para>纯临时目录：不碰云端、不碰 7z、不碰 Azurite。</para>
/// </summary>
public sealed class JournalAdoptionTests : IDisposable
{
    private const int AccountId = 77;
    private const string Container = "photos";
    private const int ConfigId = 5;
    private const int Baseline = 3;
    private const string LocalRoot = "/srv/photos";
    private const string Identity = "plain";

    private readonly string _dir;
    private readonly BackupJournalStore _store;

    public JournalAdoptionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "asb-adopt-" + Guid.NewGuid().ToString("N"));
        _store = new BackupJournalStore(Path.Combine(_dir, "journal"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static JournalRecord Blob(string path) => new()
    {
        Kind = "blob", Ref = "data/" + path, Path = path, FullHash = "f" + path, HeadHash = "h" + path,
        TailHash = "t" + path, Length = 100, Volumes = 1, VolumeSizes = [100],
    };

    /// <summary>在盘上放一卷现成的 journal。四个头字段默认全部对得上，逐个用具名参数改坏。</summary>
    private async Task<string> PlantAsync(
        string runId, int configId = ConfigId, int baseline = Baseline, string localRoot = LocalRoot,
        string identity = Identity, string path = "a.bin")
    {
        await using (var journal = await _store.CreateAsync(AccountId, Container, runId, new JournalHeader
        {
            RunId = runId,
            ConfigId = configId,
            StartedAt = DateTimeOffset.UtcNow,
            BaselineVersion = baseline,
            LocalRoot = localRoot,
            EncryptionIdentity = identity,
        }, default))
        {
            await journal.AppendAsync(Blob(path), default);
        }
        return _store.PathFor(AccountId, Container, runId);
    }

    /// <summary>开一轮新运行并走完开卷。四个判据一律用"对得上"的那份值。</summary>
    private async Task<BackupRunControl> OpenAsync(string runId = "run-new", bool firstRun = false)
    {
        var control = new BackupRunControl(_store, ConfigId, runId);
        await control.OpenJournalAsync(
            AccountId, Container, Baseline, LocalRoot, Identity, DateTimeOffset.UtcNow, default, firstRun);
        return control;
    }

    [Fact]
    public async Task Nothing_on_disk_means_nothing_to_resume_and_nothing_to_sweep()
    {
        await using var control = await OpenAsync();

        Assert.True(control.Resume.IsEmpty);
        Assert.False(control.SweepNeeded);
    }

    /// <summary>
    /// 第一轮：盘上一卷 journal 都没有（既没采纳也没作废），仍然要扫一遍。
    /// <para>
    /// 这一条撑着的是删配置端点那句承诺——删配置会把这个容器的 journal 全丢掉，那批"云上有、
    /// 索引里没有"的块从此无人保护，而重建配置后的第一次清理正是**备份收尾**那次：
    /// 判据的另外两项此刻必然为 false。没有这一条，那批块只能等一个用户未必配过的 Cleanup 计划。
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_first_run_sweeps_even_with_no_journal_in_sight()
    {
        await using var control = await OpenAsync(firstRun: true);

        Assert.True(control.Resume.IsEmpty);
        Assert.True(control.SweepNeeded);
    }

    [Fact]
    public async Task All_four_terms_matching_is_adopted()
    {
        var planted = await PlantAsync("run-old");

        await using var control = await OpenAsync();

        Assert.Equal(1, control.Resume.RecordCount);
        Assert.NotNull(control.Resume.FindBlob("a.bin", "fa.bin", 100, "ha.bin", "ta.bin"));
        // 采纳过 → 容器里躺着"云上有、索引里没有"的块，收尾该扫一遍（Task 11）。
        Assert.True(control.SweepNeeded);
        // 采纳是**只读**的：那一卷原样留在盘上，本轮不抄写、不截断、不删除。
        Assert.True(File.Exists(planted), "an adopted journal must be left on disk");
    }

    [Fact]
    public async Task A_foreign_config_id_is_voided()
    {
        // (AccountId, ContainerName) 在 AppDbContext 里是唯一索引，一个容器至多一个配置——
        // 所以这只可能是"配置删了又在同一个容器上重建"留下的陈迹。
        var planted = await PlantAsync("run-old", configId: ConfigId + 1);

        await using var control = await OpenAsync();

        Assert.True(control.Resume.IsEmpty);
        Assert.False(File.Exists(planted), "a journal belonging to another config must be voided");
        Assert.True(control.SweepNeeded);
    }

    [Fact]
    public async Task A_stale_baseline_version_is_voided()
    {
        // 基线变了 = 别人已经跑完了一整轮，那卷里的引用早就该由索引说了算。
        var planted = await PlantAsync("run-old", baseline: Baseline - 1);

        await using var control = await OpenAsync();

        Assert.True(control.Resume.IsEmpty);
        Assert.False(File.Exists(planted), "a journal from another baseline must be voided");
        Assert.True(control.SweepNeeded);
    }

    [Fact]
    public async Task A_different_local_root_is_voided()
    {
        // 换了根目录，同一条相对路径指的就不是同一个文件了。
        var planted = await PlantAsync("run-old", localRoot: LocalRoot + "-moved");

        await using var control = await OpenAsync();

        Assert.True(control.Resume.IsEmpty);
        Assert.False(File.Exists(planted), "a journal taken under another local root must be voided");
        Assert.True(control.SweepNeeded);
    }

    [Fact]
    public async Task A_different_encryption_identity_is_voided()
    {
        // 换了钥匙，地址空间跟着变，旧卷里的 ref 一个都对不上号。
        var planted = await PlantAsync("run-old", identity: "keyed:abc");

        await using var control = await OpenAsync();

        Assert.True(control.Resume.IsEmpty);
        Assert.False(File.Exists(planted), "a journal written under another key must be voided");
        Assert.True(control.SweepNeeded);
    }

    [Fact]
    public async Task Every_still_valid_volume_is_adopted_not_just_the_first()
    {
        // 反复挂起/恢复会攒下多卷，每一卷都作数。
        await PlantAsync("run-old-1", path: "a.bin");
        await PlantAsync("run-old-2", path: "b.bin");

        await using var control = await OpenAsync();

        Assert.Equal(2, control.Resume.RecordCount);
        Assert.NotNull(control.Resume.FindBlob("a.bin", "fa.bin", 100, "ha.bin", "ta.bin"));
        Assert.NotNull(control.Resume.FindBlob("b.bin", "fb.bin", 100, "hb.bin", "tb.bin"));
    }

    [Fact]
    public async Task Volumes_are_judged_one_by_one_not_as_a_batch()
    {
        var mine = await PlantAsync("run-old-mine", path: "a.bin");
        var foreign = await PlantAsync("run-old-foreign", configId: ConfigId + 1, path: "b.bin");

        await using var control = await OpenAsync();

        Assert.Equal(1, control.Resume.RecordCount);
        Assert.NotNull(control.Resume.FindBlob("a.bin", "fa.bin", 100, "ha.bin", "ta.bin"));
        Assert.Null(control.Resume.FindBlob("b.bin", "fb.bin", 100, "hb.bin", "tb.bin"));
        Assert.True(File.Exists(mine));
        Assert.False(File.Exists(foreign));
    }

    [Fact]
    public async Task An_adopted_volume_survives_a_run_that_never_commits()
    {
        // 挂起或失败收尾（CompleteAsync 没被调过）：采纳来的那一卷必须原样还在，
        // 否则下一轮就再也复用不到它记的那些块了——而它们云上确实有。
        var planted = await PlantAsync("run-old");

        var control = await OpenAsync();
        await control.DisposeAsync();

        Assert.True(File.Exists(planted));
        Assert.True(File.Exists(_store.PathFor(AccountId, Container, "run-new")));
    }

    /// <summary>
    /// 本轮的 runId 与盘上那一卷重名：采纳之后**接着往它后面写**，不能新开一卷把它盖掉。
    /// <para>
    /// 今天的 RunId 是新生成的 GUID 前缀，撞不上；Task 15「启动时自动接着跑」要沿用挂起那一轮的
    /// runId（好让界面上的运行身份不变），一沿用就撞上。截断之后当轮仍然是对的（Resume 已经在内存里），
    /// 坏的是盘上那份担保——所以这条用例钉的是**文件里剩下什么**，不是 Resume 里有什么。
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_volume_carrying_our_own_run_id_is_appended_to_not_truncated()
    {
        var planted = await PlantAsync("run-same");

        await using (var control = await OpenAsync("run-same"))
        {
            Assert.Equal(1, control.Resume.RecordCount);
            await control.RecordBlobAsync("b.bin", "data/b", "fb", "hb", "tb", 7, 1, false, [7], default);
        }

        var content = await BackupJournal.ReadAsync(planted, default);
        Assert.NotNull(content);
        Assert.Equal(ConfigId, content!.Header.ConfigId);
        Assert.Equal(["a.bin", "b.bin"], content.Records.Select(r => r.Path));
    }

    [Fact]
    public async Task Committing_deletes_the_adopted_volume_along_with_its_own()
    {
        // 索引提交成功之后它们才功成身退——这一条让上面那条"还在"不至于是句空话。
        var planted = await PlantAsync("run-old");

        var control = await OpenAsync();
        await control.CompleteAsync();
        await control.DisposeAsync();

        Assert.False(File.Exists(planted));
        Assert.False(File.Exists(_store.PathFor(AccountId, Container, "run-new")));
    }
}
