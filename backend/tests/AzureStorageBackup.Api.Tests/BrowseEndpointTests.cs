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
        string Path, string? Parent, bool Truncated, int Skipped, List<BrowseEntryDto> Entries);

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
        // 完整路径，不因为设了根就截断
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

    // F3: Parent_Stops_At_The_Root 也会在 Parent 被写死成 null 的情况下通过——
    // 没有任何测试真的浏览一个子目录，断言 Parent 指回它的真实上级。这里补上，
    // 证明「往上一级」这条路径本身是走通的，不只是「到根为止」这一端点。
    [Fact]
    public async Task Parent_Points_Back_To_The_Actual_Parent_Directory()
    {
        using var factory = new RootedFactory(_root);
        var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<BrowseDto>(
            $"/api/system/browse?path={Uri.EscapeDataString(Path.Combine(_root, "photos"))}");

        Assert.Equal(_root, body!.Parent);
    }

    // F4: Parent 之前用 Path.GetFullPath 词法折叠 `..`，跟着符号链接走时会算错——
    // <root>/link -> <root>/a/b 时词法折叠给出 <root>，真实上级其实是 <root>/a。
    // PathBoundary.ResolveReal 的文档专门讲了这个坑（跟 IsInside 用的是同一套算法），
    // Parent 的计算必须跟组件的其余部分一致，否则用户会被静默传送到错误的目录。
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

            // 返回而不是过滤掉——否则用户会困惑「目录里明明有这个东西」
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

    // F5: Backup:Root 允许配成相对路径（PathBoundary.ResolveReal 按进程 CWD 解析），
    // 但旧的默认起点直接把 ConfiguredRoot 原样当 start 传给 IsInside——后者只认绝对
    // 输入，相对根一律拒绝，于是不传 path 浏览自己的根都会 409，picker 直接瘫痪。
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
        // 归一化**只拼 CWD、不折叠 `..`**（折叠是词法的，CWD 自身经软链时会算到别的目录去——
        // 正是 PathBoundary.ResolveReal 文档里那个坑），所以字符串里会留着拼接痕迹。
        // 断言看的是它指向哪儿，不是它长什么样。
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

        // 1) 点进子目录：把列表给出的 fullPath 一字不改地当 ?path= 送回去。
        var downRes = await client.GetAsync(
            $"/api/system/browse?path={Uri.EscapeDataString(photos.FullPath)}");
        Assert.Equal(HttpStatusCode.OK, downRes.StatusCode);
        var down = await downRes.Content.ReadFromJsonAsync<BrowseDto>();

        // 2) 点「.. (up)」：把它返回的 parent 一字不改地送回去，应当回到根的列表。
        Assert.NotNull(down!.Parent);
        var upRes = await client.GetAsync(
            $"/api/system/browse?path={Uri.EscapeDataString(down.Parent!)}");
        Assert.Equal(HttpStatusCode.OK, upRes.StatusCode);
        var up = await upRes.Content.ReadFromJsonAsync<BrowseDto>();
        Assert.Contains(up!.Entries, e => e.Name == "photos");
    }

    // F6a: 未配根、也不传 path 的默认起点此前完全没有测试覆盖——所有既有用例要么显式
    // 传 path，要么配了根。
    [Fact]
    public async Task Without_A_Root_The_Default_Browse_Succeeds_From_The_Filesystem_Root()
    {
        using var factory = new TestWebAppFactory();
        var client = factory.CreateClient();

        var res = await client.GetAsync("/api/system/browse");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // F6b/c: 截断逻辑（entries.Count >= MaxBrowseEntries 在 Add 之前判断）此前只靠代码
    // 阅读验证过。刚好 2000 项不该截断；2001 项必须截断且仍只返回 2000 项——后者能
    // 抓住把 `>=` 误写成 `>` 的回归：那样 2001 项时判断在 count==2000 时仍是 false，
    // 会多塞进第 2001 项且 Truncated 保持 false。
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

    // F1: Directory.Exists 为 true 不代表目录可读——Directory.EnumerateFileSystemEntries
    // 的第一次 MoveNext 在 foreach 头部，落在原来那层只包住单项处理的 try 之外，会让
    // UnauthorizedAccessException 直接冲出 handler，变成裸 500（Production 空 body，
    // Development 带堆栈）。这条路径是 picker 的「前几次点击」就会撞见的——不设根时从
    // / 开始，点开 /root 或任何别的 uid 拥有的卷挂载子目录都会触发。
    // mode 000 只在非 root 进程下才是真正的屏障：CI（ubuntu-latest 的 runner 用户）和
    // 本机沙箱都以非特权用户运行，但用 Environment.IsPrivilegedProcess 防一手，root 下
    // chmod 000 不挡任何东西，断言在那种环境里没有意义，宁可 Skip 也不要产出一个
    // 为错误原因通过的绿灯。
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
            // 恢复权限，好让 Dispose() 里的递归删除（需要能打开 locked 本身列目录）能成功
            File.SetUnixFileMode(locked,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
#pragma warning restore CA1416
    }

    // F2 (final review): 单项 catch 分支此前被判定为「无法确定性构造」——mode 000 在**枚举**
    // 就失败（走的是上面那条 403），悬空软链和非法 UTF-8 名字也都不触发。漏掉的是 mode `r--`：
    // 可读但**不可执行**的目录 readdir 拿得到名字，对子项 stat 却被拒，而 FileInfo.Attributes
    // 在这种目录下是**抛** UnauthorizedAccessException，不是返回 -1 哨兵值。于是枚举成功、
    // 每一项都落进单项 catch：200 + 空列表 + Skipped == 子项数。
    // 与上面的 403 用例同样的理由：root 下 mode 位不是屏障，两个子项都会被正常列出，
    // 断言会为完全错误的原因变绿——那种环境下宁可 Skip。
    [SkippableFact]
    public async Task Entries_Whose_Attributes_Cannot_Be_Read_Are_Skipped_And_Counted()
    {
        Skip.If(Environment.IsPrivilegedProcess, "running as a privileged user; chmod 400 is not a barrier");

        var listable = Path.Combine(_root, "listable");
        Directory.CreateDirectory(Path.Combine(listable, "child-dir"));
        File.WriteAllText(Path.Combine(listable, "child-file.txt"), "x");
#pragma warning disable CA1416 // tests run on Linux only
        // r-- ：可 readdir（拿得到名字），不可 stat 子项（缺 x 位）。
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
            // 空列表本身还不够：Skipped 才把「跳过了两项」与「真的是空目录」区分开。
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
}
