using System.Net;
using System.Net.Http.Json;
using Azure;
using AzureStorageBackup.Api.Endpoints;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>A fake IContainerService that throws the given exception, to verify the endpoint's error mapping without needing Azurite.</summary>
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

    // Reuses the request shape already verified in ContainerEndpointsTests. Creating an account does not go to the cloud,
    // so no Azurite is needed here — which is exactly what sets this group of tests apart from that one.
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
        // The service throws the moment it is called, so getting a 400 proves the validation happened before going to the cloud.
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
    public async Task Azure_409_Is_Also_Passed_Through_Generically()
    {
        // Together with the 403 case above this proves pass-through is a general rule over a status code range,
        // not a special case for one particular status code — 409 (container already exists) is another one that really happens.
        var client = ClientThrowing(new RequestFailedException(409, "The specified container already exists.", "ContainerAlreadyExists", null));
        var id = await CreateAccountAsync(client);

        var res = await client.PostAsJsonAsync($"/api/accounts/{id}/containers", new { name = "valid-name" });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Contains("ContainerAlreadyExists", body!.error);
    }

    [Fact]
    public async Task Azure_401_Does_Not_Pass_Through_As_401()
    {
        // A 401 from an Azure storage account means "this request to the storage account was not authenticated" (in reality
        // usually a proxy playing tricks), not "the operator's login session expired". Passed straight through as a 401 the
        // frontend would read it as an expired session and kick the operator back to the login page — this pins that it must become a 502.
        var client = ClientThrowing(new RequestFailedException(401, "Server failed to authenticate the request.", "InvalidAuthenticationInfo", null));
        var id = await CreateAccountAsync(client);

        var res = await client.PostAsJsonAsync($"/api/accounts/{id}/containers", new { name = "valid-name" });

        Assert.NotEqual(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Contains("could not be reached", body!.error);
    }

    [Fact]
    public async Task Unreachable_Storage_Account_Becomes_502()
    {
        // Status 0 is how the SDK says "the request never went out / never got a response".
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
