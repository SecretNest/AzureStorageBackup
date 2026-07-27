using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class GroupingPlannerTests
{
    private static PlannedFile F(string path, long length, string? hash = null) =>
        new(path, length, hash ?? "sha256:" + path);

    private static BackupPlan Plan(IEnumerable<PlannedFile> files, PlanOptions? options = null) =>
        new GroupingPlanner().Plan(files.ToList(), options);

    [Fact]
    public void Large_File_Goes_To_Single_Blob()
    {
        var plan = Plan([F("big.bin", 6_000_000)], new PlanOptions { SingleFileThresholdBytes = 5_000_000 });

        var blob = Assert.Single(plan.Blobs);
        Assert.Equal("big.bin", blob.Path);
        Assert.Equal("data/sha256:big.bin", blob.Ref);
        Assert.Empty(plan.Packs);
    }

    [Fact]
    public void Dont_Group_Match_Goes_To_Single_Blob_Even_If_Small()
    {
        var options = new PlanOptions { DontGroup = new IgnoreRuleSet(["*.iso"]) };

        var plan = Plan([F("small.iso", 100), F("small.txt", 100)], options);

        Assert.Contains(plan.Blobs, b => b.Path == "small.iso");
        Assert.Contains(plan.Packs.SelectMany(p => p.Members), m => m.Path == "small.txt");
    }

    [Fact]
    public void Small_Files_In_Same_Dir_Are_Packed_Together()
    {
        var plan = Plan([F("dir/a.txt", 100), F("dir/b.txt", 200)]);

        var pack = Assert.Single(plan.Packs);
        Assert.Equal(2, pack.Members.Count);
        Assert.Equal(300, pack.OriginalBytes);
        Assert.Contains(pack.Members, m => m.Path == "dir/a.txt" && m.EntryName == "dir/a.txt");
        Assert.Empty(plan.Blobs);
    }

    [Fact]
    public void Files_In_Different_Dirs_Are_Packed_Separately()
    {
        var plan = Plan([F("x/a.txt", 100), F("y/b.txt", 100)]);

        Assert.Equal(2, plan.Packs.Count);
        Assert.All(plan.Packs, p => Assert.Single(p.Members));
        Assert.Equal(["p0001", "p0002"], plan.Packs.Select(p => p.PackId));
    }

    [Fact]
    public void Group_Cap_Splits_Into_Multiple_Packs()
    {
        // 同目录 3 个 40 字节文件，单组上限 100 → 拆成 2 个 pack（40+40 | 40）。
        var plan = Plan(
            [F("d/a", 40), F("d/b", 40), F("d/c", 40)],
            new PlanOptions { GroupCapBytes = 100 });

        Assert.Equal(2, plan.Packs.Count);
        Assert.All(plan.Packs, p => Assert.True(p.OriginalBytes <= 100));
        Assert.Equal(3, plan.Packs.Sum(p => p.Members.Count));
    }

    [Fact]
    public void Pack_Members_Reference_Pack_Storage()
    {
        var plan = Plan([F("dir/a.txt", 100, "sha256:aaa")]);

        var member = plan.Packs[0].Members[0];
        Assert.Equal("sha256:aaa", member.FullHash);
        Assert.Equal("dir/a.txt", member.EntryName);
        Assert.Equal("p0001", plan.Packs[0].PackId);
    }

    // ---- 跨路径打包（散列分片目录）----
    //
    // 用户实测发现的问题：Emby 元数据是 .../library/09/<guid>/poster.jpg 这种结构——目录极多、
    // 每个目录一两个文件。按目录切分时包数逼近文件数，46,624 个文件产生上万个包，
    // 每个包一次 7z 进程加一次计费的上传请求，分组打包（合并小文件、减少 blob 数）完全落空。

    /// <summary>命中跨路径规则的文件无视目录边界装箱：四个分处不同目录的小文件应当只成一个包。</summary>
    [Fact]
    public void Cross_Dir_Rule_Packs_Across_Directory_Boundaries()
    {
        var options = new PlanOptions
        {
            CrossDirGroup = new IgnoreRuleSet(["meta/**"]),
            GroupCapBytes = 10_000,
        };

        var plan = Plan(
        [
            F("meta/09/aaa/poster.jpg", 100),
            F("meta/09/bbb/poster.jpg", 100),
            F("meta/1a/ccc/poster.jpg", 100),
            F("meta/1a/ddd/poster.jpg", 100),
        ], options);

        var pack = Assert.Single(plan.Packs);
        Assert.Equal(4, pack.Members.Count);
        Assert.Empty(plan.Blobs);
    }

    /// <summary>默认（规则为空）必须与历史行为逐字节一致：仍按目录切分，一个目录一个包。</summary>
    [Fact]
    public void Without_The_Rule_Each_Directory_Still_Gets_Its_Own_Pack()
    {
        var plan = Plan(
        [
            F("meta/09/aaa/poster.jpg", 100),
            F("meta/09/bbb/poster.jpg", 100),
        ], new PlanOptions { GroupCapBytes = 10_000 });

        Assert.Equal(2, plan.Packs.Count); // 不同目录 → 两个包，正是要解决的那个形态
    }

    /// <summary>优先级：不分组 > 跨路径打包。「不分组」说的是"根本不该和别人合并"，
    /// 不该被跨路径规则翻案。</summary>
    [Fact]
    public void Dont_Group_Outranks_Cross_Dir_Grouping()
    {
        var options = new PlanOptions
        {
            CrossDirGroup = new IgnoreRuleSet(["meta/**"]),
            DontGroup = new IgnoreRuleSet(["*.iso"]),
        };

        var plan = Plan([F("meta/a/x.iso", 100), F("meta/b/y.jpg", 100)], options);

        Assert.Equal("meta/a/x.iso", Assert.Single(plan.Blobs).Path); // 不分组胜出
        Assert.Equal("meta/b/y.jpg", Assert.Single(Assert.Single(plan.Packs).Members).Path);
    }

    /// <summary>跨路径的包各自带独立的 GroupKey：编排器按它建池，池间并发。
    /// 若都用同一个键，成千上万个跨目录文件会被塞进同一个串行池里。</summary>
    [Fact]
    public void Cross_Dir_Packs_Get_Distinct_Group_Keys_So_They_Stay_Parallel()
    {
        var options = new PlanOptions
        {
            CrossDirGroup = new IgnoreRuleSet(["meta/**"]),
            GroupCapBytes = 150, // 每包最多一个文件
        };

        var plan = Plan([F("meta/a/1.jpg", 100), F("meta/b/2.jpg", 100), F("meta/c/3.jpg", 100)], options);

        Assert.Equal(3, plan.Packs.Count);
        Assert.Equal(3, plan.Packs.Select(p => p.GroupKey).Distinct().Count());
    }

    /// <summary>按目录打包时 GroupKey 就是目录，编排器据此把同目录的包归入一个池（历史行为）。</summary>
    [Fact]
    public void By_Directory_Packs_Use_The_Directory_As_Group_Key()
    {
        var plan = Plan(
        [
            F("d/1.txt", 100),
            F("d/2.txt", 100),
        ], new PlanOptions { GroupCapBytes = 150 }); // 拆成两个包，但同属一个目录池

        Assert.Equal(2, plan.Packs.Count);
        Assert.Equal(["d"], plan.Packs.Select(p => p.GroupKey).Distinct());
    }
}
