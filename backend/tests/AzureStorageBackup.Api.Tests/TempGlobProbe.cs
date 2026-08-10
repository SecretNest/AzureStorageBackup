using AzureStorageBackup.Api.Services;
using Xunit.Abstractions;

namespace AzureStorageBackup.Api.Tests;

public class TempGlobProbe(ITestOutputHelper o)
{
    [Fact]
    public void Probe()
    {
        void Try(string rule, params string[] paths)
        {
            var set = new IgnoreRuleSet(rule.Split('\n'));
            foreach (var p in paths)
                o.WriteLine($"  rule {rule,-18} vs {p,-14} -> {(set.MatchesFileOrAncestorDir(p) ? "MATCH" : "no")}");
        }
        Try("*.wmv", "a.wmv", "a.WMV", "a.Wmv");
        Try("*.WMV", "a.wmv", "a.WMV");
        Try("*.[wW][mM][vV]", "a.wmv", "a.WMV", "a.[wW][mM][vV]");
        Try("*.wmv\n*.WMV", "a.wmv", "a.WMV", "a.Wmv");
    }
}
