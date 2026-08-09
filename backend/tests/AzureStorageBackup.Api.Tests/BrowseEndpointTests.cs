using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;

namespace AzureStorageBackup.Api.Tests;

public class BrowseEndpointTests : IDisposable
{
    private readonly string _root;

    public BrowseEndpointTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "asb-browse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "photos"));
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "readme.txt"), "hello");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private sealed record BrowseEntryDto(
        string Name, string FullPath, bool IsDirectory, long? Length,
        DateTimeOffset ModifiedAt, bool OutsideRoot);

    private sealed record BrowseDto(
        string Path, string? Parent, bool Truncated, int Skipped,
        int Total, int Offset, List<BrowseEntryDto> Entries);

    private sealed class RootedFactory(string root) : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Backup:Root", root);
        }
    }

    [Fact]
    public async Task Lists_Directories_And_Files_With_Full_Paths()
    {
        using var factory = new RootedFactory(_root);
        var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<BrowseDto>(
            $"/api/system/browse?path={Uri.EscapeDataString(_root)}");

        Assert.NotNull(body);
        Assert.Contains(body!.Entries, e => e.Name == "photos" && e.IsDirectory);
        Assert.Contains(body.Entries, e => e.Name == "readme.txt" && !e.IsDirectory);
        // Full paths; configuring a root does not truncate them
        Assert.Contains(body.Entries, e => e.FullPath == Path.Combine(_root, "photos"));
    }

    [Fact]
    public async Task Defaults_To_The_Configured_Root()
    {
        using var factory = new RootedFactory(_root);
        var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<BrowseDto>("/api/system/browse");

        Assert.NotNull(body);
        Assert.Contains(body!.Entries, e => e.Name == "photos");
        // B6: every entry of the default (no `path`) browse is inside the root that produced it —
        // OutsideRoot must be false throughout. Without this, `!boundary.IsInside(item)` could be
        // mutated to something always-true on this code path and no test would catch it: the only
        // other coverage of OutsideRoot=false is An_Entry_Inside_A_Configured_Root_Is_Not_Marked_Outside,
        // which always passes an explicit `path`, never exercising the default-start branch.
        Assert.All(body.Entries, e => Assert.False(e.OutsideRoot));
    }

    [Fact]
    public async Task Rejects_A_Path_Outside_The_Root()
    {
        using var factory = new RootedFactory(_root);
        var client = factory.CreateClient();

        var res = await client.GetAsync("/api/system/browse?path=%2Fdefinitely%2Foutside");

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("path_outside_root", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Parent_Stops_At_The_Root()
    {
        using var factory = new RootedFactory(_root);
        var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<BrowseDto>(
            $"/api/system/browse?path={Uri.EscapeDataString(_root)}");

        Assert.Null(body!.Parent);
    }

    // F3: Parent_Stops_At_The_Root would pass just as well with Parent hard-coded to null —
    // no test ever browsed an actual subdirectory and asserted that Parent points back at its real parent. This fills
    // that in, proving the "go up one level" path works at all, not just the "stop at the root" end of it.
    [Fact]
    public async Task Parent_Points_Back_To_The_Actual_Parent_Directory()
    {
        using var factory = new RootedFactory(_root);
        var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<BrowseDto>(
            $"/api/system/browse?path={Uri.EscapeDataString(Path.Combine(_root, "photos"))}");

        Assert.Equal(_root, body!.Parent);
    }

    // F4: Parent used to fold `..` lexically with Path.GetFullPath, which gets the wrong answer once symlinks are involved —
    // with <root>/link -> <root>/a/b, lexical folding gives <root>, while the real parent is <root>/a.
    // PathBoundary.ResolveReal's docs cover exactly this pitfall (it is the same algorithm IsInside uses);
    // Parent must be computed the same way as the rest of the component, or the user gets silently teleported into the wrong directory.
    [Fact]
    public async Task Parent_Of_A_Symlinked_Directory_Follows_The_Real_Path_Not_The_Lexical_One()
    {
        var a = Path.Combine(_root, "a");
        var b = Path.Combine(a, "b");
        Directory.CreateDirectory(b);
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, b);

        using var factory = new RootedFactory(_root);
        var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<BrowseDto>(
            $"/api/system/browse?path={Uri.EscapeDataString(link)}");

        Assert.Equal(a, body!.Parent);
    }

    [Fact]
    public async Task Marks_A_Symlink_Escaping_The_Root_As_Outside()
    {
        var outside = Path.Combine(Path.GetTempPath(), "asb-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(_root, "escape"), outside);
        try
        {
            using var factory = new RootedFactory(_root);
            var client = factory.CreateClient();

            var body = await client.GetFromJsonAsync<BrowseDto>(
                $"/api/system/browse?path={Uri.EscapeDataString(_root)}");

            // Return it rather than filtering it out — otherwise the user is left puzzled: "that thing is clearly in the directory"
            var escape = Assert.Single(body!.Entries, e => e.Name == "escape");
            Assert.True(escape.OutsideRoot);
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Without_A_Root_Nothing_Is_Marked_Outside()
    {
        using var factory = new TestWebAppFactory();
        var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<BrowseDto>(
            $"/api/system/browse?path={Uri.EscapeDataString(_root)}");

        Assert.All(body!.Entries, e => Assert.False(e.OutsideRoot));
    }

    // F2: mutating `!boundary.IsInside(item)` to `boundary.Enabled` at SystemEndpoints.cs
    // leaves all six original tests green — nothing pins "root configured + entry
    // actually inside it -> OutsideRoot is false". Marks_A_Symlink_Escaping only checks
    // the escaping entry is true; Without_A_Root only covers the disabled-boundary branch.
    // This closes the gap: a normal in-root entry, with a root configured, must be false.
    [Fact]
    public async Task An_Entry_Inside_A_Configured_Root_Is_Not_Marked_Outside()
    {
        using var factory = new RootedFactory(_root);
        var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<BrowseDto>(
            $"/api/system/browse?path={Uri.EscapeDataString(_root)}");

        var photos = Assert.Single(body!.Entries, e => e.Name == "photos");
        Assert.False(photos.OutsideRoot);
    }

    // F5: Backup:Root is allowed to be a relative path (PathBoundary.ResolveReal resolves it against the process CWD),
    // but the old default start handed ConfiguredRoot straight to IsInside as start — and that only accepts absolute
    // input, rejecting any relative root, so browsing your own root without a path 409s and the picker is dead on arrival.
    [Fact]
    public async Task Defaults_To_The_Configured_Root_Even_When_The_Root_Is_Relative()
    {
        var relativeRoot = Path.GetRelativePath(Directory.GetCurrentDirectory(), _root);

        using var factory = new RootedFactory(relativeRoot);
        var client = factory.CreateClient();

        var res = await client.GetAsync("/api/system/browse");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<BrowseDto>();
        Assert.Contains(body!.Entries, e => e.Name == "photos");
    }

    // F1 (final review): a relative Backup__Root is normalised to an absolute path once, in the
    // PathBoundary constructor, so everything the browse response hands out is rooted. Every one
    // of these three strings is fed straight back into an API that runs it through IsInside —
    // which rejects any non-rooted path outright — so a relative form here means an unusable
    // picker (409 on the first click). Pinning "rooted" is what makes the follow-a-link test below
    // meaningful rather than accidental.
    [Fact]
    public async Task A_Relative_Root_Is_Reported_As_An_Absolute_Path()
    {
        var relativeRoot = Path.GetRelativePath(Directory.GetCurrentDirectory(), _root);

        using var factory = new RootedFactory(relativeRoot);
        var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<BrowseDto>("/api/system/browse");

        Assert.NotNull(body);
        Assert.True(Path.IsPathRooted(body!.Path));
        var photos = Assert.Single(body.Entries, e => e.Name == "photos");
        Assert.True(Path.IsPathRooted(photos.FullPath));
        // Normalisation **only prepends the CWD, it does not fold `..`** (folding is lexical, and when the CWD itself goes
        // through a symlink it lands in a different directory — exactly the pitfall in PathBoundary.ResolveReal's docs), so
        // the string keeps visible traces of the concatenation. The assertion looks at where it points, not what it looks like.
        Assert.Equal(
            Path.GetFullPath(Path.Combine(_root, "photos")), Path.GetFullPath(photos.FullPath));
    }

    // F1 (final review): the composition test. Each piece was locally correct before — the
    // response shape was asserted, the boundary was asserted — but nothing ever *followed* a
    // link, so a listing whose FullPath/Parent could not be handed back to the very endpoint
    // that produced them went unnoticed. Walk the picker's real click path: list the root, click
    // into a subfolder using the fullPath as given, then click ".. (up)" using parent as given.
    [Fact]
    public async Task Every_Listed_Path_Can_Be_Browsed_Again_When_The_Root_Is_Relative()
    {
        var relativeRoot = Path.GetRelativePath(Directory.GetCurrentDirectory(), _root);

        using var factory = new RootedFactory(relativeRoot);
        var client = factory.CreateClient();

        var start = await client.GetFromJsonAsync<BrowseDto>("/api/system/browse");
        var photos = Assert.Single(start!.Entries, e => e.Name == "photos");

        // 1) Click into a subdirectory: send the fullPath the listing gave back as ?path=, unchanged down to the byte.
        var downRes = await client.GetAsync(
            $"/api/system/browse?path={Uri.EscapeDataString(photos.FullPath)}");
        Assert.Equal(HttpStatusCode.OK, downRes.StatusCode);
        var down = await downRes.Content.ReadFromJsonAsync<BrowseDto>();

        // 2) Click ".. (up)": send back the parent it returned, unchanged down to the byte, and we should land on the root listing.
        Assert.NotNull(down!.Parent);
        var upRes = await client.GetAsync(
            $"/api/system/browse?path={Uri.EscapeDataString(down.Parent!)}");
        Assert.Equal(HttpStatusCode.OK, upRes.StatusCode);
        var up = await upRes.Content.ReadFromJsonAsync<BrowseDto>();
        Assert.Contains(up!.Entries, e => e.Name == "photos");
    }

    // F6a: the default start with no root configured and no path passed had no test coverage at all — every existing case
    // either passes an explicit path or configures a root.
    [Fact]
    public async Task Without_A_Root_The_Default_Browse_Succeeds_From_The_Filesystem_Root()
    {
        using var factory = new TestWebAppFactory();
        var client = factory.CreateClient();

        var res = await client.GetAsync("/api/system/browse");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // F6b/c: the truncation logic (entries.Count >= MaxBrowseEntries, evaluated before the Add) had only ever been verified
    // by reading the code. Exactly 2000 entries must not truncate; 2001 must truncate and still return only 2000 — the latter
    // catches the regression of writing `>=` as `>`: with 2001 entries the test would still be false at count==2000,
    // squeezing in a 2001st entry with Truncated staying false.
    [Fact]
    public async Task Exactly_The_Entry_Cap_Is_Not_Truncated()
    {
        var dir = Path.Combine(Path.GetTempPath(), "asb-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            for (var i = 0; i < 2000; i++)
                File.WriteAllBytes(Path.Combine(dir, $"f{i:D5}"), []);

            using var factory = new RootedFactory(dir);
            var client = factory.CreateClient();
            var body = await client.GetFromJsonAsync<BrowseDto>(
                $"/api/system/browse?path={Uri.EscapeDataString(dir)}");

            Assert.False(body!.Truncated);
            Assert.Equal(2000, body.Entries.Count);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task One_Entry_Over_The_Cap_Is_Truncated_And_Still_Caps_At_2000()
    {
        var dir = Path.Combine(Path.GetTempPath(), "asb-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            for (var i = 0; i < 2001; i++)
                File.WriteAllBytes(Path.Combine(dir, $"f{i:D5}"), []);

            using var factory = new RootedFactory(dir);
            var client = factory.CreateClient();
            var body = await client.GetFromJsonAsync<BrowseDto>(
                $"/api/system/browse?path={Uri.EscapeDataString(dir)}");

            Assert.True(body!.Truncated);
            Assert.Equal(2000, body.Entries.Count);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // F1: Directory.Exists returning true does not mean the directory is readable — the first MoveNext of
    // Directory.EnumerateFileSystemEntries happens in the foreach header, outside the old try that only wrapped
    // per-entry processing, so UnauthorizedAccessException escapes the handler and becomes a bare 500 (empty body in
    // Production, stack trace in Development). The picker hits this path within its "first few clicks" — with no root
    // configured it starts at /, and opening /root or any volume-mount subdirectory owned by another uid triggers it.
    // mode 000 is only a real barrier for a non-root process: CI (ubuntu-latest's runner user) and the local sandbox both
    // run as unprivileged users, but guard with Environment.IsPrivilegedProcess anyway — as root, chmod 000 blocks
    // nothing, the assertion is meaningless in that environment, and a Skip beats producing a green light earned
    // for entirely the wrong reason.
    [SkippableFact]
    public async Task An_Unreadable_Directory_Returns_A_Clean_Error_Not_A_Bare_500()
    {
        Skip.If(Environment.IsPrivilegedProcess, "running as a privileged user; chmod 000 is not a barrier");

        var locked = Path.Combine(_root, "locked");
        Directory.CreateDirectory(locked);
#pragma warning disable CA1416 // tests run on Linux only
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            using var factory = new RootedFactory(_root);
            var client = factory.CreateClient();

            var res = await client.GetAsync(
                $"/api/system/browse?path={Uri.EscapeDataString(locked)}");

            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            var body = await res.Content.ReadAsStringAsync();
            Assert.Contains("could not be read", body);
        }
        finally
        {
            // Restore the permissions so the recursive delete in Dispose() (which has to open locked itself to list it) can succeed
            File.SetUnixFileMode(locked,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
#pragma warning restore CA1416
    }

    // F2 (final review): the per-entry catch branch was previously judged "impossible to construct deterministically" —
    // mode 000 fails at **enumeration** (that is the 403 case above), and dangling symlinks and invalid UTF-8 names do not
    // trigger it either. What was missed is mode `r--`: a readable but **non-executable** directory lets readdir return the
    // names while stat on the children is refused, and in such a directory FileInfo.Attributes **throws**
    // UnauthorizedAccessException instead of returning the -1 sentinel. So enumeration succeeds and every entry lands in
    // the per-entry catch: 200 + empty list + Skipped == the number of children.
    // Same reasoning as the 403 case above: as root the mode bits are not a barrier, both children would be listed
    // normally, and the assertion would go green for entirely the wrong reason — in that environment, prefer a Skip.
    [SkippableFact]
    public async Task Entries_Whose_Attributes_Cannot_Be_Read_Are_Skipped_And_Counted()
    {
        Skip.If(Environment.IsPrivilegedProcess, "running as a privileged user; chmod 400 is not a barrier");

        var listable = Path.Combine(_root, "listable");
        Directory.CreateDirectory(Path.Combine(listable, "child-dir"));
        File.WriteAllText(Path.Combine(listable, "child-file.txt"), "x");
#pragma warning disable CA1416 // tests run on Linux only
        // r--: readdir works (the names come back), stat on the children does not (no x bit).
        File.SetUnixFileMode(listable, UnixFileMode.UserRead);
        try
        {
            using var factory = new RootedFactory(_root);
            var client = factory.CreateClient();

            var res = await client.GetAsync(
                $"/api/system/browse?path={Uri.EscapeDataString(listable)}");

            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<BrowseDto>();
            Assert.Empty(body!.Entries);
            // The empty list alone is not enough: only Skipped tells "two entries were skipped" apart from "the directory really is empty".
            Assert.Equal(2, body.Skipped);
            Assert.False(body.Truncated);
        }
        finally
        {
            File.SetUnixFileMode(listable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
#pragma warning restore CA1416
    }

    [Fact]
    public async Task Pages_Through_A_Directory_With_A_Stable_Order()
    {
        for (var i = 0; i < 12; i++)
            File.WriteAllText(Path.Combine(_root, $"f{i:D2}.txt"), "x");

        using var factory = new RootedFactory(_root);
        var client = factory.CreateClient();

        var first = await client.GetFromJsonAsync<BrowseDto>(
            $"/api/system/browse?path={Uri.EscapeDataString(_root)}&offset=0&limit=5");
        var second = await client.GetFromJsonAsync<BrowseDto>(
            $"/api/system/browse?path={Uri.EscapeDataString(_root)}&offset=5&limit=5");

        // 2 directories (photos/docs, from the constructor) + 13 files (readme.txt + f00..f11)
        Assert.Equal(15, first!.Total);
        Assert.Equal(5, first.Entries.Count);
        Assert.Equal(0, first.Offset);
        Assert.Equal(5, second!.Offset);

        // Directories first, then sorted by name; the two pages neither overlap nor drop an entry.
        Assert.Equal(["docs", "photos"], first.Entries.Take(2).Select(e => e.Name));
        Assert.Empty(first.Entries.Select(e => e.Name).Intersect(second.Entries.Select(e => e.Name)));
    }

    [Fact]
    public async Task Paged_Requests_Are_Not_Marked_Truncated()
    {
        using var factory = new RootedFactory(_root);
        var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<BrowseDto>(
            $"/api/system/browse?path={Uri.EscapeDataString(_root)}&offset=0&limit=1");

        // Truncated means "there is more but you cannot get at it". A paged request can get at it, and Total already tells the whole story.
        Assert.False(body!.Truncated);
        Assert.Equal(3, body.Total);
    }

    [Fact]
    public async Task Offset_Past_The_End_Returns_An_Empty_Page_Not_An_Error()
    {
        using var factory = new RootedFactory(_root);
        var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<BrowseDto>(
            $"/api/system/browse?path={Uri.EscapeDataString(_root)}&offset=999&limit=10");

        Assert.Empty(body!.Entries);
        Assert.Equal(3, body.Total);
    }
}
