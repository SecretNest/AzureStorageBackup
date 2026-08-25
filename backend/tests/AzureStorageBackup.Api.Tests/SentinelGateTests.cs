using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The one definition of "the source is there", shared by the backup gate and the check demotion so the two
/// cannot drift apart. Everything else in this feature is plumbing around these answers.
/// </summary>
public sealed class SentinelGateTests : IDisposable
{
    private readonly string _root;

    public SentinelGateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "asb-sentinel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void With_No_Sentinel_The_Local_Root_Is_The_Sentinel()
    {
        // A root that is not there cannot be backed up in any case, so it answers the same question a sentinel
        // does and there is no reason to make every backup configure one to get the check. It also means the
        // gate does something useful for configs that predate the feature.
        Assert.True(SentinelGate.Present(null, _root));
        Assert.False(SentinelGate.Present(null, Path.Combine(_root, "gone")));
    }

    [Fact]
    public void A_Blank_Sentinel_Is_The_Same_As_None()
    {
        // Blank is what an emptied text box sends, and it must mean "no sentinel", not "a sentinel named ''".
        Assert.True(SentinelGate.Present("", _root));
        Assert.True(SentinelGate.Present("   ", _root));
        Assert.False(SentinelGate.Present("  ", Path.Combine(_root, "gone")));
    }

    [Fact]
    public void A_Configured_Sentinel_Outranks_The_Local_Root()
    {
        // The whole point: the root is present (it is a mount point) and the sentinel is not. Falling back to
        // the root here would let exactly the round through that this feature exists to stop.
        Assert.False(SentinelGate.Present(Path.Combine(_root, "not-mounted"), _root));
    }

    [Fact]
    public void An_Existing_File_Is_Present()
    {
        var file = Path.Combine(_root, "marker");
        File.WriteAllText(file, "x");

        Assert.True(SentinelGate.Present(file, _root));
    }

    [Fact]
    public void An_Existing_Directory_Is_Present()
    {
        var dir = Path.Combine(_root, "sub");
        Directory.CreateDirectory(dir);

        Assert.True(SentinelGate.Present(dir, _root));
    }

    [Fact]
    public void An_Empty_Directory_Is_Still_Present()
    {
        // Existence only, by decision: emptiness is not the question the sentinel answers, and a genuinely
        // empty directory must not be mistaken for an unmounted one.
        var dir = Path.Combine(_root, "empty");
        Directory.CreateDirectory(dir);

        Assert.True(SentinelGate.Present(dir, _root));
    }

    [Fact]
    public void Nothing_To_Check_Blocks_Nothing()
    {
        // An imported backup can sit with no local root at all until the operator supplies one (see
        // ImportBackupEndpointTests). There is nothing to probe, so this gate must stand aside and leave that
        // case to the guards that already handle it, rather than turning it into a silent skip.
        Assert.True(SentinelGate.Present(null, null));
        Assert.True(SentinelGate.Present(null, ""));
    }

    [Fact]
    public void Missing_Reports_The_Path_That_Was_Not_There()
    {
        // Whichever of the two did the blocking is the one the operator has to go and look at, so it is the
        // one that has to come back — a message naming the other sends them to the wrong place.
        var absentSentinel = Path.Combine(_root, "not-mounted");
        Assert.Equal(absentSentinel, SentinelGate.Missing(absentSentinel, _root));

        var absentRoot = Path.Combine(_root, "gone");
        Assert.Equal(absentRoot, SentinelGate.Missing(null, absentRoot));

        Assert.Null(SentinelGate.Missing(null, _root));
    }
}
