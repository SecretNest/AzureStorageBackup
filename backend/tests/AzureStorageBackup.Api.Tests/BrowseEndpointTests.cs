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
        string Path, string? Parent, bool Truncated, List<BrowseEntryDto> Entries);

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
}
