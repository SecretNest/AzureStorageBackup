using System.Net;
using System.Net.Http.Json;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Tests;

public class TaskEndpointsTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static TaskRequest BackupTaskRequest() => new(
        TargetKind: TaskTargetKind.Backup,
        AccountId: 1,
        ContainerName: "c1",
        GroupId: null,
        TaskType: ScheduledTaskType.Backup,
        CronExpression: "0 2 * * *",
        Enabled: true);

    [Fact]
    public async Task Task_Crud_Through_Api()
    {
        var post = await _client.PostAsJsonAsync("/api/tasks", BackupTaskRequest());
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var created = await post.Content.ReadFromJsonAsync<TaskResponse>();
        Assert.True(created!.Id > 0);
        Assert.Equal("0 2 * * *", created.CronExpression);

        var get = await _client.GetAsync($"/api/tasks/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var del = await _client.DeleteAsync($"/api/tasks/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
    }

    [Fact]
    public async Task Create_Invalid_Group_Target_Returns_400()
    {
        var req = new TaskRequest(
            TaskTargetKind.Group, null, null, null,
            ScheduledTaskType.Check, "0 3 * * 0", true); // GroupId missing
        var post = await _client.PostAsJsonAsync("/api/tasks", req);
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }
}
