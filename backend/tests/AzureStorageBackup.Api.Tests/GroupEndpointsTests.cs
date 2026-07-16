using System.Net;
using System.Net.Http.Json;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Tests;

public class GroupEndpointsTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Group_Crud_Through_Api()
    {
        var req = new GroupRequest("g1", [new GroupMemberDto(1, "c1")]);
        var post = await _client.PostAsJsonAsync("/api/groups", req);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var created = await post.Content.ReadFromJsonAsync<GroupResponse>();
        Assert.True(created!.Id > 0);
        Assert.Single(created.Members);

        var get = await _client.GetAsync($"/api/groups/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var update = new GroupRequest("renamed", [new GroupMemberDto(2, "c2"), new GroupMemberDto(2, "c3")]);
        var put = await _client.PutAsJsonAsync($"/api/groups/{created.Id}", update);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var updated = await put.Content.ReadFromJsonAsync<GroupResponse>();
        Assert.Equal("renamed", updated!.Name);
        Assert.Equal(2, updated.Members.Count);

        var del = await _client.DeleteAsync($"/api/groups/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
    }

    [Fact]
    public async Task Create_Empty_Members_Returns_400()
    {
        var req = new GroupRequest("empty", []);
        var post = await _client.PostAsJsonAsync("/api/groups", req);
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }
}
