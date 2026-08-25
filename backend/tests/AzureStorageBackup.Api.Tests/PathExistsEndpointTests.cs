using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The existence probe behind the sentinel field. The setting is a path that is *supposed* to be absent half
/// the time, so a form that cannot say which half it is right now leaves the operator to guess whether they
/// typed it correctly — and a typo and an unmounted disk look identical until the next backup silently skips.
/// </summary>
public sealed class PathExistsEndpointTests : IDisposable
{
    private readonly string _root;

    public PathExistsEndpointTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "asb-exists-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "photos"));
        File.WriteAllText(Path.Combine(_root, "readme.txt"), "hello");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private sealed record ExistsDto(bool Exists, string? Kind);

    private sealed class RootedFactory(string root) : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Backup:Root", root);
        }
    }

    private async Task<ExistsDto?> ProbeAsync(HttpClient client, string path) =>
        await client.GetFromJsonAsync<ExistsDto>(
            $"/api/system/path-exists?path={Uri.EscapeDataString(path)}");

    [Fact]
    public async Task Reports_An_Existing_File_As_A_File()
    {
        // The kind is worth reporting, not just the yes/no: a sentinel pointed at a directory that happens to
        // exist while the mount is absent is the mistake this feature is about, and seeing "directory" where
        // "file" was meant is the cheapest moment to catch it.
        using var factory = new RootedFactory(_root);

        var body = await ProbeAsync(factory.CreateClient(), Path.Combine(_root, "readme.txt"));

        Assert.True(body!.Exists);
        Assert.Equal("file", body.Kind);
    }

    [Fact]
    public async Task Reports_An_Existing_Directory_As_A_Directory()
    {
        using var factory = new RootedFactory(_root);

        var body = await ProbeAsync(factory.CreateClient(), Path.Combine(_root, "photos"));

        Assert.True(body!.Exists);
        Assert.Equal("directory", body.Kind);
    }

    [Fact]
    public async Task An_Absent_Path_Is_A_Plain_Answer_Not_An_Error()
    {
        // 200 with exists:false, not 404. Absence is the expected answer here — it is what an unmounted
        // source looks like — and a form field cannot tell an error status apart from "the backend is down".
        using var factory = new RootedFactory(_root);

        var res = await factory.CreateClient()
            .GetAsync($"/api/system/path-exists?path={Uri.EscapeDataString(Path.Combine(_root, "nope"))}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<ExistsDto>();
        Assert.False(body!.Exists);
        Assert.Null(body.Kind);
    }

    [Fact]
    public async Task A_Path_Outside_The_Root_Is_Refused()
    {
        // Same admission filter as every other path this application will go and stat. Without it this
        // endpoint is an existence oracle for the whole file system.
        using var factory = new RootedFactory(_root);

        var res = await factory.CreateClient()
            .GetAsync("/api/system/path-exists?path=/definitely/outside/the/root");

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("path_outside_root", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_Blank_Path_Is_Refused()
    {
        // "No sentinel" is a state the form has to be able to hold, and it is not a question worth asking the
        // file system; answering "exists: false" for a blank would paint the empty field as a problem.
        using var factory = new RootedFactory(_root);

        var res = await factory.CreateClient().GetAsync("/api/system/path-exists?path=");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
