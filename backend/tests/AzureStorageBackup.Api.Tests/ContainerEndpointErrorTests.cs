using System.Net;
using System.Net.Http.Json;
using Azure;
using AzureStorageBackup.Api.Endpoints;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>用假 IContainerService 抛出指定异常，验证端点的错误映射，不需要 Azurite。</summary>
file sealed class ThrowingContainerService(Exception toThrow) : IContainerService
{
    public Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(Account a, CancellationToken ct = default) =>
        throw toThrow;
    public Task CreateContainerAsync(Account a, string name, CancellationToken ct = default) =>
        throw toThrow;
    public Task DeleteContainerAsync(Account a, string name, CancellationToken ct = default) =>
        throw toThrow;
}

public class ContainerEndpointErrorTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private sealed record ErrorBody(string error);

    private HttpClient ClientThrowing(Exception ex) =>
        factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<IContainerService>(new ThrowingContainerService(ex));
        })).CreateClient();

    // 复用 ContainerEndpointsTests 里已验证过的请求形状。建账户不连云，
    // 所以这里不需要 Azurite——这正是本组测试与那组的区别。
    private static async Task<int> CreateAccountAsync(HttpClient client)
    {
        var res = await client.PostAsJsonAsync("/api/accounts", new AccountRequest(
            Name: "err-" + Guid.NewGuid().ToString("N")[..8],
            Description: null,
            BlobEndpoint: "http://127.0.0.1:10000/devstoreaccount1",
            Region: AzureRegion.Global,
            AccountKey: "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==",
            UseProxy: false,
            ProxyMode: ProxyMode.Independent,
            ProxyHost: null,
            ProxyPort: null,
            ProxyUsername: null,
            ProxyPassword: null));
        res.EnsureSuccessStatusCode();
        var acct = await res.Content.ReadFromJsonAsync<AccountResponse>();
        return acct!.Id;
    }

    [Fact]
    public async Task Invalid_Name_Returns_400_Without_Calling_Azure()
    {
        // 服务一被调用就抛，所以能拿到 400 就证明校验发生在连云之前。
        var client = ClientThrowing(new InvalidOperationException("must not be called"));
        var id = await CreateAccountAsync(client);

        var res = await client.PostAsJsonAsync($"/api/accounts/{id}/containers", new { name = "Bad_Name" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Contains("lowercase letters, digits, and hyphens", body!.error);
    }

    [Fact]
    public async Task Azure_4xx_Is_Passed_Through_With_A_Readable_Message()
    {
        var client = ClientThrowing(new RequestFailedException(403, "This request is not authorized.", "AuthorizationFailure", null));
        var id = await CreateAccountAsync(client);

        var res = await client.PostAsJsonAsync($"/api/accounts/{id}/containers", new { name = "valid-name" });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Contains("AuthorizationFailure", body!.error);
    }

    [Fact]
    public async Task Unreachable_Storage_Account_Becomes_502()
    {
        // Status 0 是 SDK 表示「请求根本没发出去/没拿到响应」的方式。
        var client = ClientThrowing(new RequestFailedException(0, "No such host is known."));
        var id = await CreateAccountAsync(client);

        var res = await client.PostAsJsonAsync($"/api/accounts/{id}/containers", new { name = "valid-name" });

        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Contains("could not be reached", body!.error);
    }

    [Fact]
    public async Task List_Also_Maps_Azure_Failures()
    {
        var client = ClientThrowing(new RequestFailedException(0, "No such host is known."));
        var id = await CreateAccountAsync(client);

        var res = await client.GetAsync($"/api/accounts/{id}/containers");

        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
    }
}
