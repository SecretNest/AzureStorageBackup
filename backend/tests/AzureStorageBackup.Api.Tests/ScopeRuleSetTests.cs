using System.Text.Json;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 规则集的判定与写入。用例来自 shared/scope-rule-cases.json——前端 scopeRules.ts 的测试
/// 读的是同一份文件，两份实现行为分叉时两边同时红。
/// </summary>
public sealed class ScopeRuleSetTests
{
    private sealed record QueryCase(
        string Name, string[] Rules,
        string[] InScope, string[] OutOfScope,
        string[] Partial, string[] NotPartial,
        string[] MayContain, string[] MayNotContain);

    private sealed record WriteOp(string Path, bool Included);

    private sealed record WriteCase(string Name, string[] Start, WriteOp[] Ops, string[] Expect);

    private sealed record Fixture(QueryCase[] Queries, WriteCase[] Writes);

    private static Fixture LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "scope-rule-cases.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Fixture>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    public static TheoryData<string> QueryNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in LoadFixture().Queries)
            data.Add(c.Name);
        return data;
    }

    public static TheoryData<string> WriteNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in LoadFixture().Writes)
            data.Add(c.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(QueryNames))]
    public void Query_Cases_From_Shared_Fixture(string name)
    {
        var c = LoadFixture().Queries.Single(x => x.Name == name);
        var set = ScopeRuleSet.Parse(string.Join('\n', c.Rules));

        foreach (var p in c.InScope)
            Assert.True(set.IsInScope(p), $"expected in scope: '{p}'");
        foreach (var p in c.OutOfScope)
            Assert.False(set.IsInScope(p), $"expected out of scope: '{p}'");
        foreach (var p in c.Partial)
            Assert.True(set.IsPartial(p), $"expected partial: '{p}'");
        foreach (var p in c.NotPartial)
            Assert.False(set.IsPartial(p), $"expected not partial: '{p}'");
        foreach (var p in c.MayContain)
            Assert.True(set.MayContainIncluded(p), $"expected may contain included: '{p}'");
        foreach (var p in c.MayNotContain)
            Assert.False(set.MayContainIncluded(p), $"expected may not contain included: '{p}'");
    }

    [Theory]
    [MemberData(nameof(WriteNames))]
    public void Write_Cases_From_Shared_Fixture(string name)
    {
        var c = LoadFixture().Writes.Single(x => x.Name == name);
        var set = ScopeRuleSet.Parse(string.Join('\n', c.Start));

        foreach (var op in c.Ops)
            set = set.With(op.Path, op.Included);

        Assert.Equal(string.Join('\n', c.Expect), set.ToString());
    }

    [Fact]
    public void Parse_Of_Null_Is_The_All_Set()
    {
        Assert.True(ScopeRuleSet.Parse(null).IsAll);
        Assert.True(ScopeRuleSet.Parse("").IsAll);
        Assert.True(ScopeRuleSet.Parse("   \n  ").IsAll);
    }

    [Fact]
    public void Parse_Then_ToString_Drops_Redundant_Rules()
    {
        // 手工编辑出来的冗余规则（与最近祖先判定相同）在解析时就被清掉，
        // 否则 IsPartial 会把一个实际上全同的子树报成灰选。
        var set = ScopeRuleSet.Parse("-\n- music\n+ photos");

        Assert.Equal("-\n+ photos", set.ToString());
        Assert.False(set.IsPartial("music"));
    }

    [Fact]
    public void With_Does_Not_Mutate_The_Original_Set()
    {
        var original = ScopeRuleSet.Parse("");
        var changed = original.With("photos", false);

        Assert.True(original.IsAll);
        Assert.False(changed.IsInScope("photos"));
    }
}
