using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

[Trait("Category", "Integration")]
public class ContainerEndpointsTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly HttpClient _client = factory.CreateClient();

    private static bool AzuriteReachable()
    {
        try
        {
            using var c = new TcpClient();
            c.Connect("127.0.0.1", 10000);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static AccountRequest AzuriteAccountRequest() => new(
        Name: "azurite",
        Description: null,
        BlobEndpoint: "http://127.0.0.1:10000/devstoreaccount1",
        Region: AzureRegion.Global,
        AccountKey: AzuriteKey,
        UseProxy: false,
        ProxyMode: ProxyMode.Independent,
        ProxyHost: null,
        ProxyPort: null,
        ProxyUsername: null,
        ProxyPassword: null);

    [SkippableFact]
    public async Task Container_Crud_Through_Api()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running on 127.0.0.1:10000");

        var acctRes = await _client.PostAsJsonAsync("/api/accounts", AzuriteAccountRequest());
        var acct = await acctRes.Content.ReadFromJsonAsync<AccountResponse>();

        var name = "api-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            var create = await _client.PostAsJsonAsync(
                $"/api/accounts/{acct!.Id}/containers", new { name });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);

            var list = await _client.GetFromJsonAsync<List<ContainerInfo>>(
                $"/api/accounts/{acct.Id}/containers");
            Assert.Contains(list!, c => c.Name == name);

            var del = await _client.DeleteAsync($"/api/accounts/{acct.Id}/containers/{name}");
            Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

            var after = await _client.GetFromJsonAsync<List<ContainerInfo>>(
                $"/api/accounts/{acct.Id}/containers");
            Assert.DoesNotContain(after!, c => c.Name == name);
        }
        finally
        {
            await _client.DeleteAsync($"/api/accounts/{acct!.Id}/containers/{name}");
        }
    }

    [SkippableFact]
    public async Task Containers_For_Missing_Account_Returns_404()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");

        var res = await _client.GetAsync("/api/accounts/999999/containers");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
