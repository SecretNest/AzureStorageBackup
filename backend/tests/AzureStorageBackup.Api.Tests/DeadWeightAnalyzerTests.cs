using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class DeadWeightAnalyzerTests
{
    private static PackState Pack(string id, params (string Hash, long Bytes)[] members) =>
        new(id, members.Select(m => new PackMember(m.Hash, m.Bytes)).ToList());

    private static IReadOnlyList<RepackDecision> Analyze(
        IEnumerable<PackState> packs, string[] referenced, DeadWeightOptions? options = null) =>
        new DeadWeightAnalyzer().Analyze(packs, new HashSet<string>(referenced), options);

    [Fact]
    public void Fully_Referenced_Pack_Is_Not_Repacked()
    {
        var result = Analyze([Pack("p0001", ("a", 100), ("b", 100))], ["a", "b"]);

        Assert.Empty(result);
    }

    [Fact]
    public void Pack_Over_Threshold_Is_Flagged_With_Live_Members()
    {
        // 400 中 200 死 → 50% > 30%。
        var pack = Pack("p0001", ("a", 100), ("b", 100), ("dead1", 100), ("dead2", 100));

        var decision = Assert.Single(Analyze([pack], ["a", "b"]));

        Assert.Equal("p0001", decision.PackId);
        Assert.Equal(200, decision.DeadBytes);
        Assert.Equal(400, decision.OriginalBytes);
        Assert.Equal(0.5, decision.DeadRatio, 3);
        Assert.Equal(["a", "b"], decision.LiveMembers.Select(m => m.FullHash));
    }

    [Fact]
    public void Pack_At_Or_Below_Threshold_Is_Not_Repacked()
    {
        // 恰好 30%（不严格大于）→ 不重组。
        var pack = Pack("p0001", ("a", 70), ("dead", 30));

        Assert.Empty(Analyze([pack], ["a"], new DeadWeightOptions { Threshold = 0.30 }));
    }

    [Fact]
    public void Fully_Dead_Pack_Is_Flagged_With_No_Live_Members()
    {
        var pack = Pack("p0001", ("dead1", 100), ("dead2", 100));

        var decision = Assert.Single(Analyze([pack], []));

        Assert.Empty(decision.LiveMembers);
        Assert.Equal(1.0, decision.DeadRatio, 3);
    }

    [Fact]
    public void Only_Over_Threshold_Packs_Are_Returned()
    {
        var clean = Pack("p0001", ("a", 100), ("b", 100));            // 0% dead
        var dirty = Pack("p0002", ("c", 100), ("dead", 900));         // 90% dead

        var result = Analyze([clean, dirty], ["a", "b", "c"]);

        Assert.Equal(["p0002"], result.Select(d => d.PackId));
    }
}
