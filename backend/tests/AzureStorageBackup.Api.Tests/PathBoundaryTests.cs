using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.Configuration;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Every symlink-related case builds **real** symlinks in a temp directory — the entire point of this feature is handling
/// the filesystem's real behavior, and mocking it out would be testing nothing at all.
/// </summary>
public class PathBoundaryTests : IDisposable
{
    private readonly string _base;

    public PathBoundaryTests()
    {
        var raw = Path.Combine(Path.GetTempPath(), "asb-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(raw);
        // Path.GetTempPath() may itself contain symlinks (macOS's /tmp -> /private/tmp, say,
        // or a redirected TMPDIR). Anywhere a test hand-builds a Path.Combine off _base to compare against
        // the output of ResolveReal/Root, what it compares must be the resolved real path, otherwise it would
        // fail spuriously on hosts like those — and the most dangerous consequence of a spurious failure is someone weakening a Critical reproduction assertion to make it "pass".
        _base = PathBoundary.ResolveReal(raw)!;
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string Dir(string name)
    {
        var p = Path.Combine(_base, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private static PathBoundary Boundary(string? root)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(root is null
                ? []
                : new Dictionary<string, string?> { ["Backup:Root"] = root })
            .Build();
        return new PathBoundary(config);
    }

    [Fact]
    public void Disabled_When_Root_Is_Absent()
    {
        var sut = Boundary(null);
        Assert.False(sut.Enabled);
        Assert.True(sut.IsInside("/anywhere/at/all"));
    }

    [Fact]
    public void Disabled_When_Root_Is_Empty()
    {
        var sut = Boundary("");
        Assert.False(sut.Enabled);
        Assert.True(sut.IsInside("/anywhere/at/all"));
    }

    [Fact]
    public void Accepts_The_Root_Itself_And_Its_Descendants()
    {
        var root = Dir("nas");
        var sut = Boundary(root);

        Assert.True(sut.IsInside(root));
        Assert.True(sut.IsInside(Path.Combine(root, "photos")));
        Assert.True(sut.IsInside(Path.Combine(root, "photos", "2024", "a.jpg")));
    }

    [Fact]
    public void Rejects_A_Sibling_Sharing_The_Root_Name_Prefix()
    {
        // /nasty must not pass merely because it matches /nas as a string prefix
        var root = Dir("nas");
        Dir("nasty");
        var sut = Boundary(root);

        Assert.False(sut.IsInside(Path.Combine(_base, "nasty")));
        Assert.False(sut.IsInside(Path.Combine(_base, "nasty", "x")));
    }

    [Fact]
    public void Rejects_Dot_Dot_Escape()
    {
        var root = Dir("nas");
        Dir("outside");
        var sut = Boundary(root);

        Assert.False(sut.IsInside(Path.Combine(root, "..", "outside")));
    }

    [Fact]
    public void Accepts_Paths_Under_A_Root_That_Is_Itself_A_Symlink()
    {
        // When the root itself is a symlink it must be resolved to a real path first, otherwise every legitimate path gets wrongly rejected
        var real = Dir("real-storage");
        var link = Path.Combine(_base, "nas-link");
        Directory.CreateSymbolicLink(link, real);
        var sut = Boundary(link);

        Assert.True(sut.IsInside(Path.Combine(link, "photos")));
        Assert.True(sut.IsInside(Path.Combine(real, "photos")));
    }

    [Fact]
    public void Rejects_When_The_Final_Segment_Is_A_Symlink_Pointing_Outside()
    {
        var root = Dir("nas");
        var outside = Dir("outside");
        var link = Path.Combine(root, "escape");
        Directory.CreateSymbolicLink(link, outside);
        var sut = Boundary(root);

        Assert.False(sut.IsInside(link));
    }

    [Fact]
    public void Rejects_When_A_MIDDLE_Segment_Is_A_Symlink_Pointing_Outside()
    {
        // ResolveLinkTarget used on its own would miss this one: a.jpg is not a link itself,
        // but its parent directory escape is, and only segment-by-segment expansion finds the escape.
        var root = Dir("nas");
        var outside = Dir("outside");
        Directory.CreateDirectory(Path.Combine(outside, "photos"));
        var link = Path.Combine(root, "escape");
        Directory.CreateSymbolicLink(link, outside);
        var sut = Boundary(root);

        Assert.False(sut.IsInside(Path.Combine(link, "photos", "a.jpg")));
    }

    [Fact]
    public void Accepts_A_Symlink_That_Stays_Inside_The_Root()
    {
        // "use symlinks to gather scattered directories into one place" is a legitimate use this feature is meant to serve
        var root = Dir("nas");
        var real = Path.Combine(root, "real");
        Directory.CreateDirectory(real);
        var link = Path.Combine(root, "alias");
        Directory.CreateSymbolicLink(link, real);
        var sut = Boundary(root);

        Assert.True(sut.IsInside(Path.Combine(link, "a.jpg")));
    }

    [Fact]
    public void Rejects_Rather_Than_Hanging_On_A_Symlink_Cycle()
    {
        var root = Dir("nas");
        var a = Path.Combine(root, "a");
        var b = Path.Combine(root, "b");
        Directory.CreateSymbolicLink(a, b);
        Directory.CreateSymbolicLink(b, a);
        var sut = Boundary(root);

        Assert.False(sut.IsInside(Path.Combine(a, "x")));
    }

    [Fact]
    public void Accepts_A_Path_That_Does_Not_Exist_Yet_Inside_The_Root()
    {
        // A restore target is often a directory that has not been created yet, and must not be rejected just because it "does not exist yet".
        var root = Dir("nas");
        var sut = Boundary(root);

        Assert.True(sut.IsInside(Path.Combine(root, "not", "created", "yet")));
    }

    [Fact]
    public void Rejects_A_Nonexistent_Path_Behind_An_Escaping_Symlink()
    {
        var root = Dir("nas");
        var outside = Dir("outside");
        var link = Path.Combine(root, "escape");
        Directory.CreateSymbolicLink(link, outside);
        var sut = Boundary(root);

        Assert.False(sut.IsInside(Path.Combine(link, "not-created-yet")));
    }

    [Fact]
    public void Rejects_Dot_Dot_Applied_After_An_Escaping_Symlink()
    {
        // On POSIX, `..` is settled **after** symlink expansion: folding `..` lexically first would turn
        // `<root>/escape/../secret` into `<root>/secret` and thereby let through a path that actually lands at
        // `<base>/secret`. The kernel's realpath gives `<base>/secret`.
        var root = Dir("nas");
        var outside = Dir("outside");
        Directory.CreateDirectory(Path.Combine(_base, "secret"));
        Directory.CreateSymbolicLink(Path.Combine(root, "escape"), outside);
        var sut = Boundary(root);

        var query = Path.Combine(root, "escape", "..", "secret");
        Assert.Equal(Path.Combine(_base, "secret"), PathBoundary.ResolveReal(query));
        Assert.False(sut.IsInside(query));
    }

    [Fact]
    public void Rejects_A_Symlink_Whose_Target_Passes_Through_Another_Escaping_Symlink()
    {
        // A symlink target must be **re-expanded segment by segment**, not substituted wholesale and then skipped:
        // b -> <base>/outside (out of bounds), a -> <root>/b/c (looks inside, taken literally).
        // Only re-walking b reveals that a actually lands at <base>/outside/c.
        var root = Dir("nas");
        var outside = Dir("outside");
        Directory.CreateDirectory(Path.Combine(outside, "c"));
        Directory.CreateSymbolicLink(Path.Combine(root, "b"), outside);
        Directory.CreateSymbolicLink(Path.Combine(root, "a"), Path.Combine(root, "b", "c"));
        var sut = Boundary(root);

        var query = Path.Combine(root, "a");
        Assert.Equal(Path.Combine(outside, "c"), PathBoundary.ResolveReal(query));
        Assert.False(sut.IsInside(query));
    }

    [Fact]
    public void Accepts_A_Deep_Path_With_No_Symlinks_At_All()
    {
        // The depth cap should only count **symlink expansions**; an ordinary deep directory must not be rejected for having many segments.
        var root = Dir("nas");
        var deep = root;
        for (var i = 0; i < 60; i++)
            deep = Path.Combine(deep, "d" + i);
        var sut = Boundary(root);

        Assert.True(sut.IsInside(deep));
    }

    [Fact]
    public void Throws_When_The_Configured_Root_Cannot_Be_Resolved()
    {
        // When the root cannot be resolved it must blow up at startup: silently degrading to "no boundary" means the boundary is gone.
        var a = Path.Combine(_base, "cyclic-a");
        var b = Path.Combine(_base, "cyclic-b");
        Directory.CreateSymbolicLink(a, b);
        Directory.CreateSymbolicLink(b, a);

        var ex = Assert.Throws<InvalidOperationException>(() => Boundary(a));
        Assert.Contains(a, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Enabled_And_RealRoot_Report_The_Resolved_Root()
    {
        var real = Dir("real-storage");
        var link = Path.Combine(_base, "nas-link");
        Directory.CreateSymbolicLink(link, real);
        var sut = Boundary(link);

        Assert.True(sut.Enabled);
        Assert.Equal(real, sut.RealRoot);
    }

    [Fact]
    public void ConfiguredRoot_Keeps_What_The_Operator_Typed_Not_The_Resolved_Target()
    {
        // M1: when the configured root is itself a symlink, ConfiguredRoot must keep the exact string
        // the operator typed (for error messages / future UI), while RealRoot is the resolved real path —
        // on a symlinked root the two must genuinely differ, otherwise this case proves nothing.
        var real = Dir("real-storage");
        var link = Path.Combine(_base, "nas-link");
        Directory.CreateSymbolicLink(link, real);
        var sut = Boundary(link);

        Assert.Equal(link, sut.ConfiguredRoot);
        Assert.Equal(real, sut.RealRoot);
        Assert.NotEqual(sut.ConfiguredRoot, sut.RealRoot);
    }

    [Fact]
    public void A_Rejection_Can_Be_Reported_Against_The_Configured_Root_Not_The_Resolved_One()
    {
        // M1's core assertion: when a caller builds an error message it should use ConfiguredRoot (the
        // /nas-link the operator typed), not RealRoot (the real-storage it internally points to) — otherwise
        // the path appearing in the rejection message is one the operator never typed and would not recognize.
        var real = Dir("real-storage");
        var link = Path.Combine(_base, "nas-link");
        Directory.CreateSymbolicLink(link, real);
        var outside = Dir("outside");
        var sut = Boundary(link);

        var rejected = Path.Combine(outside, "secret");
        Assert.False(sut.IsInside(rejected));

        var message = $"Path '{rejected}' is outside the configured root '{sut.ConfiguredRoot}'.";
        Assert.Contains(link, message, StringComparison.Ordinal);
        Assert.DoesNotContain(real, message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_A_Relative_Symlink_Target_Pointing_Outside()
    {
        // The relative-target branch previously had no case covering it at all
        var root = Dir("nas");
        Dir("outside");
        Directory.CreateSymbolicLink(Path.Combine(root, "escape"), Path.Combine("..", "outside"));
        var sut = Boundary(root);

        Assert.Equal(Path.Combine(_base, "outside"), PathBoundary.ResolveReal(Path.Combine(root, "escape")));
        Assert.False(sut.IsInside(Path.Combine(root, "escape", "a.jpg")));
    }

    [Fact]
    public void Accepts_A_Relative_Symlink_Target_That_Stays_Inside()
    {
        var root = Dir("nas");
        Directory.CreateDirectory(Path.Combine(root, "real"));
        Directory.CreateSymbolicLink(Path.Combine(root, "alias"), "real");
        var sut = Boundary(root);

        Assert.True(sut.IsInside(Path.Combine(root, "alias", "a.jpg")));
    }

    [Fact]
    public void Rejects_A_Relative_Symlink_Whose_Target_Passes_Through_Another_Escaping_Relative_Symlink()
    {
        // The relative version of the Critical reproduction for absolute targets (see Rejects_A_Symlink_Whose_Target_Passes_
        // Through_Another_Escaping_Symlink above): x -> ../outside (relative, out of bounds),
        // y -> x/z (relative, looks inside taken literally). The old whole-string substitution implementation judged y as <root>/x/z (inside);
        // only re-walking x reveals that y actually lands at <base>/outside/z (outside).
        var root = Dir("nas");
        var outside = Dir("outside");
        Directory.CreateDirectory(Path.Combine(outside, "z"));
        Directory.CreateSymbolicLink(Path.Combine(root, "x"), Path.Combine("..", "outside"));
        Directory.CreateSymbolicLink(Path.Combine(root, "y"), Path.Combine("x", "z"));
        var sut = Boundary(root);

        var query = Path.Combine(root, "y");
        Assert.Equal(Path.Combine(outside, "z"), PathBoundary.ResolveReal(query));
        Assert.False(sut.IsInside(query));
    }

    [Fact]
    public void Rejects_A_Path_Containing_A_Null_Character_Instead_Of_Throwing()
    {
        var root = Dir("nas");
        var sut = Boundary(root);

        Assert.False(sut.IsInside(Path.Combine(root, "a\0b")));
    }

    [Fact]
    public void IsWithin_Compares_On_Segment_Boundaries_Without_Resolving_Links()
    {
        // Restore writes use this purely lexical version: what it guards against is .. in index data, and it does not resolve local symlinks
        Assert.True(PathBoundary.IsWithin("/target", "/target"));
        Assert.True(PathBoundary.IsWithin("/target", "/target/a/b.txt"));
        Assert.False(PathBoundary.IsWithin("/target", "/targetx/b.txt"));
        Assert.False(PathBoundary.IsWithin("/target", "/target/../etc/passwd"));
    }

    [Fact]
    public void IsWithin_Tolerates_Trailing_Separators_And_A_Filesystem_Root()
    {
        Assert.True(PathBoundary.IsWithin("/target/", "/target"));
        Assert.True(PathBoundary.IsWithin("/target", "/target/"));
        Assert.True(PathBoundary.IsWithin("/target/", "/target/a/"));
        Assert.False(PathBoundary.IsWithin("/target/", "/targetx/"));

        // With "/" as the root every absolute path is inside, and TrimEnd must not shave the root down to an empty string and cause a misjudgment
        Assert.True(PathBoundary.IsWithin("/", "/"));
        Assert.True(PathBoundary.IsWithin("/", "/anything/at/all"));
    }

    [Fact]
    public void IsWithin_Returns_False_Instead_Of_Throwing_On_A_Null_Character()
    {
        // F1: IsWithin's input is data supplied by the index (restore-write is designed to use it to check
        // Path.Combine(targetRoot, entryPath)), and entryPath comes from the cloud and may be malicious or
        // corrupted data. Path.GetFullPath throws ArgumentException on a path containing \0 —
        // either of root/candidate must yield a clean false, not dump a 500 on the caller.
        Assert.False(PathBoundary.IsWithin("/target\0x", "/target/a"));
        Assert.False(PathBoundary.IsWithin("/target", "/target/a\0b"));
    }

    [Fact]
    public void IsWithin_Returns_False_Instead_Of_Throwing_On_An_Empty_String()
    {
        // F1: Path.GetFullPath("") throws ArgumentException ("The value cannot be an
        // empty string"); an empty string in either of root/candidate must be judged out of bounds rather than throwing.
        Assert.False(PathBoundary.IsWithin("", "/target/a"));
        Assert.False(PathBoundary.IsWithin("/target", ""));
    }

    [Fact]
    public void ToDisplayPath_Replaces_The_Real_Root_Prefix_With_The_Configured_Root()
    {
        var real = Dir("real-storage");
        var link = Path.Combine(_base, "nas-link");
        Directory.CreateSymbolicLink(link, real);
        var sut = Boundary(link);

        Assert.Equal(link, sut.ToDisplayPath(real));
        Assert.Equal(Path.Combine(link, "photos"), sut.ToDisplayPath(Path.Combine(real, "photos")));
    }

    [Fact]
    public void ToDisplayPath_Returns_Input_Unchanged_When_The_Boundary_Is_Disabled()
    {
        var sut = Boundary(null);
        Assert.Equal("/anywhere/at/all", sut.ToDisplayPath("/anywhere/at/all"));
    }

    // B4: a caller must have confirmed with IsInside before calling this method — here we pass a real path that genuinely lands
    // outside RealRoot, simulating a caller violating the contract. The old implementation returned that string unchanged (in the case where it
    // carries the RealRoot prefix, that quietly hands the host's real path to the caller, and letting it travel into a response is a leak);
    // now it must blow up here rather than quietly letting it through.
    [Fact]
    public void ToDisplayPath_Throws_When_The_Real_Path_Is_Not_Under_The_Real_Root()
    {
        var root = Dir("nas");
        var outside = Dir("outside");
        var sut = Boundary(root);

        Assert.Throws<InvalidOperationException>(() => sut.ToDisplayPath(outside));
    }
}
