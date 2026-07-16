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
}
