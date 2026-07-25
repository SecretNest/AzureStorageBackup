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
        _base = Path.Combine(Path.GetTempPath(), "asb-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_base);
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
    public void IsWithin_Compares_On_Segment_Boundaries_Without_Resolving_Links()
    {
        // 还原写入用这个纯词法版本：它防的是索引数据里的 ..，不解析本地软链
        Assert.True(PathBoundary.IsWithin("/target", "/target"));
        Assert.True(PathBoundary.IsWithin("/target", "/target/a/b.txt"));
        Assert.False(PathBoundary.IsWithin("/target", "/targetx/b.txt"));
        Assert.False(PathBoundary.IsWithin("/target", "/target/../etc/passwd"));
    }
}
