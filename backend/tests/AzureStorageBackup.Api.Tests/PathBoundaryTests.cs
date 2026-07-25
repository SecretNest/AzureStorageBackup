using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.Configuration;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 符号链接相关用例一律在临时目录里构造**真实**软链——本功能的全部意义就是处理
/// 文件系统的真实行为，mock 掉就等于什么都没测。
/// </summary>
public class PathBoundaryTests : IDisposable
{
    private readonly string _base;

    public PathBoundaryTests()
    {
        var raw = Path.Combine(Path.GetTempPath(), "asb-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(raw);
        // Path.GetTempPath() 本身可能含软链（如 macOS 的 /tmp -> /private/tmp，
        // 或被重定向的 TMPDIR）。测试里凡是拿 _base 手工拼 Path.Combine 去比对
        // ResolveReal/Root 的输出，比的都必须是解析后的真实路径，否则在这类主机上
        // 会假失败——而假失败最危险的后果是有人为了让它「过」去削弱 Critical 复现断言。
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
        // /nasty 不得因为字符串前缀匹配 /nas 而通过
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
        // 根自身是软链时，必须先把根解析成真实路径，否则一切合法路径都会被误拒
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
        // ResolveLinkTarget 单独使用会漏掉这一条：a.jpg 自身不是链接，
        // 但它的父目录 escape 是，逐段展开才能发现越界。
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
        // 「用软链把散落各处的目录聚到一处」是本功能面向的正当用法
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
        // 还原目标常常是尚未创建的目录，不能因为「还不存在」就拒绝
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
        // POSIX 里 `..` 在软链展开**之后**才结算：先词法折叠 `..` 会把
        // `<root>/escape/../secret` 变成 `<root>/secret`，从而放行一个实际落在
        // `<base>/secret` 的路径。内核 realpath 给出的是 `<base>/secret`。
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
        // 软链目标必须被**重新逐段展开**，不能整体替换后就跳过：
        // b -> <base>/outside（越界），a -> <root>/b/c（字面看在界内）。
        // 只有重走 b 才能发现 a 实际落在 <base>/outside/c。
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
        // 深度上限只该数**软链展开次数**；普通深目录不能因为段数多就被拒。
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
        // 根解析不出来时必须在启动期炸掉：静默退化成「无边界」等于边界消失。
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
        // M1：当配置的根本身是软链时，ConfiguredRoot 必须原样保留操作员敲过的
        // 那个字符串（用于错误消息/未来 UI），RealRoot 才是解析后的真实路径——
        // 两者在软链根上必须真的不同，否则这条用例什么都没证明。
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
        // M1 的核心断言：调用方拼错误消息时，应该用 ConfiguredRoot（操作员敲的
        // /nas-link），而不是 RealRoot（内部真正指向的 real-storage）——否则
        // 拒绝消息里出现的路径操作员从没打过、也认不出来。
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
        // 相对目标分支此前完全没有用例覆盖
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
        // 绝对目标那条 Critical 复现（见上面 Rejects_A_Symlink_Whose_Target_Passes_
        // Through_Another_Escaping_Symlink）的相对版本：x -> ../outside（相对，越界），
        // y -> x/z（相对，字面看在界内）。旧的整串替换实现会把 y 判成 <root>/x/z（界内）；
        // 只有重走 x 才能发现 y 实际落在 <base>/outside/z（界外）。
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
        // 还原写入用这个纯词法版本：它防的是索引数据里的 ..，不解析本地软链
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

        // 根为 "/" 时一切绝对路径都在界内，且不能因 TrimEnd 把根削成空串而误判
        Assert.True(PathBoundary.IsWithin("/", "/"));
        Assert.True(PathBoundary.IsWithin("/", "/anything/at/all"));
    }

    [Fact]
    public void IsWithin_Returns_False_Instead_Of_Throwing_On_A_Null_Character()
    {
        // F1：IsWithin 的输入是索引供数据（restore-write 按设计要用它检查
        // Path.Combine(targetRoot, entryPath)），entryPath 来自云端、可能是恶意或
        // 损坏数据。Path.GetFullPath 对含 \0 的路径会抛 ArgumentException——
        // root/candidate 任一位置都必须得到干净的 false，而不是把 500 甩给调用方。
        Assert.False(PathBoundary.IsWithin("/target\0x", "/target/a"));
        Assert.False(PathBoundary.IsWithin("/target", "/target/a\0b"));
    }

    [Fact]
    public void IsWithin_Returns_False_Instead_Of_Throwing_On_An_Empty_String()
    {
        // F1：Path.GetFullPath("") 抛 ArgumentException（"The value cannot be an
        // empty string"）；root/candidate 任一位置为空串都必须判定越界而不是抛异常。
        Assert.False(PathBoundary.IsWithin("", "/target/a"));
        Assert.False(PathBoundary.IsWithin("/target", ""));
    }
}
