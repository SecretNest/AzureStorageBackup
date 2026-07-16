using System.Net;
using System.Net.Http.Json;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class BackupConfigEndpointsTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static BackupConfigRequest SampleRequest(string name = "photos") => new(
        AccountId: 1,
        ContainerName: "photos",
        Name: name,
        Description: "family",
        LocalRoot: "/data/photos",
        Password: "s3cret",
        IndexTier: StorageTier.Hot,
        DataTier: StorageTier.Cool,
        IgnoreRules: "*.tmp",
        DontCompressRules: null,
        DontGroupRules: null,
        IncludeSymlinks: false,
        MaxVersions: 50,
        MaxAgeDays: 180,
        RetentionMode: RetentionMode.EitherTriggers,
        SingleFileThresholdBytes: 5_000_000,
        GroupCapBytes: 100_000_000);

    [Fact]
    public async Task Post_Creates_Config_And_Hides_Password()
    {
        var res = await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest());

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var created = await res.Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.True(created!.Id > 0);
        Assert.Equal("photos", created.Name);
        Assert.True(created.HasPassword); // 有加密密码，但不回传明文

        var body = await (await _client.GetAsync($"/api/backup-configs/{created.Id}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("s3cret", body);
    }

    [Fact]
    public async Task Post_Without_LocalRoot_Returns_400()
    {
        var res = await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest() with { LocalRoot = "" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Put_With_Empty_Password_Keeps_Existing()
    {
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("keep-pw")))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var res = await _client.PutAsJsonAsync($"/api/backup-configs/{created!.Id}",
            SampleRequest("keep-pw") with { Password = null, Name = "renamed" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var updated = await res.Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Equal("renamed", updated!.Name);
        Assert.True(updated.HasPassword); // 密码保留
    }

    [Fact]
    public async Task Delete_Removes_Config()
    {
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("del")))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/backup-configs/{created!.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/backup-configs/{created.Id}")).StatusCode);
    }
}
