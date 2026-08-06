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

    /// <summary>
    /// 单文件 blob 的全文 hash 可以延后到压缩那一遍再算（<see cref="PlannedFile.FullHash"/> 为空），
    /// 而 <c>data/{hash}</c> 是内容地址——没有 hash 就没有地址。接错线时必须当场炸掉：
    /// 拼出一个 <c>data/</c> 的空地址传上去，要到还原那天才会被发现指不到 blob。
    /// </summary>
    [Fact]
    public void Addressing_A_File_Whose_Hash_Was_Deferred_Fails_Loudly()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Plan([new PlannedFile("big.bin", 10_000_000, null)]));

        Assert.Contains("big.bin", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 打包成员则**不**拒空：symlink 本来就没有内容 hash（差分对它一律返回 null），
    /// 而 symlink 是可以被打进包的——7z 存的是链接本身。把拒空写宽一格，
    /// 一个指向别处的软链接就能让整轮备份倒掉。
    /// </summary>
    [Fact]
    public void A_Pack_Member_Without_A_Content_Hash_Is_Still_Packed()
    {
        var plan = Plan([F("d/1.txt", 100), new PlannedFile("d/link", 0, null)]);

        Assert.Empty(plan.Blobs);
        Assert.Equal(["d/1.txt", "d/link"], plan.Packs.Single().Members.Select(m => m.Path));
    }

    /// <summary>
    /// 字节上限管不住成员数：文件越小，同样的字节额度装进去的成员越多。
    /// 那一箱的成员表是**整件**拿在手上的（压缩要、重校验要、失败重试也要），
    /// 实测 7z 光成员元数据就是约 1.3 KB/个。
    /// </summary>
    [Fact]
    public void A_Pack_Is_Sealed_When_It_Hits_The_Member_Limit()
    {
        // 10 个 1 字节的文件，字节上限给到天上——只有成员数这条界能把它切开。
        var plan = Plan(
            Enumerable.Range(0, 10).Select(i => F($"d/{i}.txt", 1)),
            new PlanOptions { GroupCapBytes = long.MaxValue, MaxPackMembers = 4 });

        Assert.Equal([4, 4, 2], plan.Packs.Select(p => p.Members.Count));
        // 切开归切开，一个成员都不能丢。
        Assert.Equal(10, plan.Packs.Sum(p => p.Members.Count));
    }

    /// <summary>
    /// 路径字节这条界治的是硬故障：成员路径逐个作为 argv 传给 7z，超了内核直接 E2BIG。
    /// 必须按**字节**设界而不是按成员数——墙的位置随路径长度缩水，实测 1.73 MB 的 argv 额度
    /// 在 52 字符的路径下能放三万多个，500 字符的只剩三千多。
    /// </summary>
    [Fact]
    public void A_Pack_Is_Sealed_When_It_Hits_The_Path_Byte_Limit()
    {
        // 每条路径 "d/xx.txt" = 8 字节 + NUL = 9；额度 30 → 每箱 3 条。
        var plan = Plan(
            Enumerable.Range(0, 7).Select(i => F($"d/{i:D2}.txt", 1)),
            new PlanOptions { GroupCapBytes = long.MaxValue, MaxPackMembers = int.MaxValue, MaxPackPathBytes = 30 });

        Assert.Equal([3, 3, 1], plan.Packs.Select(p => p.Members.Count));
    }

    /// <summary>
    /// 路径按 UTF-8 字节算，不按字符数。中日韩路径一个字符最多三字节，按字符数记会低估两倍多，
    /// 而低估的后果不是包变大，是 <c>E2BIG</c>：压缩当场失败。
    /// </summary>
    [Fact]
    public void Path_Bytes_Are_Counted_As_Utf8_Not_Characters()
    {
        // "照片/01.jpg"：中文 2 字 ×3 + "/01.jpg" 7 = 13 字节 + NUL = 14。按字符数记只有 10。
        Assert.Equal(14, GroupingPlanner.EntryArgBytes("照片/01.jpg"));

        // 额度 28 = 正好两条。按字符数记的话会以为塞得下第三条（33 > 28 才封箱）。
        var plan = Plan(
            Enumerable.Range(0, 5).Select(i => F($"照片/{i:D2}.jpg", 1)),
            new PlanOptions { GroupCapBytes = long.MaxValue, MaxPackPathBytes = 28 });

        Assert.Equal([2, 2, 1], plan.Packs.Select(p => p.Members.Count));
    }

    /// <summary>
    /// 两条新界对常规备份必须是**空操作**。默认下平均 ≥ 5 KB 的文件永远先撞 100 MB 那条，
    /// 所以既有备份的装箱结果一个字节都不变——这是加这两条界的前提，不是附带效果。
    /// </summary>
    [Fact]
    public void The_New_Limits_Do_Not_Change_Grouping_For_Ordinary_Files()
    {
        // 1000 个 200 KB 的文件（共 200 MB）＝ 默认下按 100 MB 切成两箱，各 500 个成员。
        var files = Enumerable.Range(0, 1000).Select(i => F($"d/{i:D4}.bin", 200 * 1024)).ToList();

        var withLimits = Plan(files);                                    // 默认：2 万成员 / 1 MB 路径
        var withoutLimits = Plan(files, new PlanOptions
        {
            MaxPackMembers = int.MaxValue,
            MaxPackPathBytes = long.MaxValue,
        });

        Assert.Equal(
            withoutLimits.Packs.Select(p => p.Members.Select(m => m.Path).ToList()),
            withLimits.Packs.Select(p => p.Members.Select(m => m.Path).ToList()));
        Assert.Equal(2, withLimits.Packs.Count);
    }

    /// <summary>
    /// 单个成员本身就超过某条界时不能死循环、也不能把它丢掉：一件装不下也要单独成箱。
    /// （字节那条界早有这个行为，新加的两条必须一致。）
    /// </summary>
    [Fact]
    public void An_Item_That_Alone_Exceeds_A_Limit_Still_Gets_Its_Own_Pack()
    {
        var longPath = "d/" + new string('x', 200) + ".txt";
        var plan = Plan(
            [F("d/a.txt", 1), F(longPath, 1), F("d/b.txt", 1)],
            new PlanOptions { GroupCapBytes = long.MaxValue, MaxPackPathBytes = 50 });

        // 排序后是 a、b、超长路径：前两条凑一箱，超长那条自己撑爆额度 → 单独成箱。
        Assert.Equal(3, plan.Packs.Sum(p => p.Members.Count));
        Assert.Contains(plan.Packs, p => p.Members.Count == 1 && p.Members[0].Path == longPath);
    }

    /// <summary>
    /// 一箱只能有一种压法，所以同一目录里可压与不可压的文件必须分开装。混装的话规则对被打包的
    /// 文件就等于不存在——那正是这个功能之前的缺陷（整箱一律按配置的 -m… 压）。
    /// </summary>
    [Fact]
    public void Same_Dir_Splits_Into_A_Compressed_Pack_And_A_Store_Only_Pack()
    {
        var options = new PlanOptions { DontCompress = new IgnoreRuleSet(["*.jpg"]) };

        var plan = Plan(
            [F("d/a.jpg", 100), F("d/b.txt", 100), F("d/c.jpg", 100), F("d/e.txt", 100)],
            options);

        Assert.Equal(2, plan.Packs.Count);

        var compressed = Assert.Single(plan.Packs, p => !p.StoreOnly);
        Assert.Equal(["d/b.txt", "d/e.txt"], compressed.Members.Select(m => m.Path));

        var stored = Assert.Single(plan.Packs, p => p.StoreOnly);
        Assert.Equal(["d/a.jpg", "d/c.jpg"], stored.Members.Select(m => m.Path));

        // 两箱同属一个目录，处理池的归属不该因为压法而分家。
        Assert.All(plan.Packs, p => Assert.Equal("d", p.GroupKey));
    }

    /// <summary>「不分组」仍然是最强的意思表示：命中者走单文件 blob，压法由那条路自己按同一套规则推导，
    /// 根本不参与分箱。</summary>
    [Fact]
    public void Dont_Group_Outranks_Dont_Compress()
    {
        var options = new PlanOptions
        {
            DontGroup = new IgnoreRuleSet(["*.iso"]),
            DontCompress = new IgnoreRuleSet(["*.iso", "*.jpg"]),
        };

        var plan = Plan([F("d/x.iso", 100), F("d/y.jpg", 100)], options);

        Assert.Equal("d/x.iso", Assert.Single(plan.Blobs).Path);
        var pack = Assert.Single(plan.Packs);
        Assert.True(pack.StoreOnly);
        Assert.Equal("d/y.jpg", Assert.Single(pack.Members).Path);
    }

    /// <summary>跨路径打包同样要切：它无视的是目录边界，不是压法。</summary>
    [Fact]
    public void Cross_Dir_Grouping_Also_Splits_By_Compressibility()
    {
        var options = new PlanOptions
        {
            CrossDirGroup = new IgnoreRuleSet(["meta/**"]),
            DontCompress = new IgnoreRuleSet(["*.jpg"]),
        };

        var plan = Plan(
            [F("meta/a/1.jpg", 100), F("meta/b/2.txt", 100), F("meta/c/3.jpg", 100)],
            options);

        Assert.Equal(2, plan.Packs.Count);
        Assert.Equal(["meta/b/2.txt"], Assert.Single(plan.Packs, p => !p.StoreOnly).Members.Select(m => m.Path));
        // 跨目录合并照旧发生在同一侧内部：两个 jpg 分属不同目录，仍进同一箱。
        Assert.Equal(
            ["meta/a/1.jpg", "meta/c/3.jpg"],
            Assert.Single(plan.Packs, p => p.StoreOnly).Members.Select(m => m.Path));
    }

    /// <summary>
    /// 严格分箱，**不设最小成员数兜底**：哪怕某一侧只有一个成员也照样独立成箱。
    /// 增量备份里一个目录本轮可能就变了两个文件，「两个各含一个成员的包」是接受的常态——
    /// 这条把那个取舍钉死，免得日后有人顺手加一条"太小就并回去"的例外。
    /// </summary>
    [Fact]
    public void A_Lone_Odd_File_Still_Gets_Its_Own_Pack()
    {
        var options = new PlanOptions { DontCompress = new IgnoreRuleSet(["*.jpg"]) };

        var plan = Plan([F("d/a.jpg", 100), F("d/b.txt", 100)], options);

        Assert.Equal(2, plan.Packs.Count);
        Assert.All(plan.Packs, p => Assert.Single(p.Members));
    }

    /// <summary>
    /// 规则**没命中任何文件**时，装箱结果必须与这条规则存在之前逐字节相同——尤其不能凭空多出
    /// 一个空的第二箱。这是加分箱的前提，不是附带效果（同 The_New_Limits_… 那条的用意）。
    /// </summary>
    [Fact]
    public void Dont_Compress_That_Matches_Nothing_Leaves_Grouping_Unchanged()
    {
        var files = new[] { F("d/a.txt", 100), F("d/b.txt", 100), F("e/c.txt", 100) };

        var withRule = Plan(files, new PlanOptions { DontCompress = new IgnoreRuleSet(["*.nomatch"]) });
        var withoutRule = Plan(files);

        Assert.Equal(
            withoutRule.Packs.Select(p => p.Members.Select(m => m.Path).ToList()),
            withRule.Packs.Select(p => p.Members.Select(m => m.Path).ToList()));
        Assert.Equal(withoutRule.Packs.Select(p => p.PackId), withRule.Packs.Select(p => p.PackId));
        Assert.All(withRule.Packs, p => Assert.False(p.StoreOnly));
    }
}
