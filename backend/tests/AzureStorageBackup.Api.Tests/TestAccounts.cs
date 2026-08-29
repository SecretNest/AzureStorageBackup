using System.Net;
using System.Net.Http.Json;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Get-or-create for accounts created through the HTTP API. Since the operator's ruling that one endpoint may
/// be registered exactly once ("直接禁止同一个endpoint被添加超过一次" — the busy tracker keys on the local record id,
/// so an endpoint alias would let two operations hit the same cloud container at once), a second POST for the
/// same endpoint answers 409. Tests that point several accounts at the one real Azurite endpoint therefore
/// adopt the record that already exists — which is exactly the product's intended model: one endpoint, one
/// record, shared by whoever needs it.
/// </summary>
internal static class TestAccounts
{
    public static async Task<int> EnsureAsync(HttpClient client, AccountRequest req)
        => await EnsureFromAsync(client, await client.PostAsJsonAsync("/api/accounts", req), req.BlobEndpoint);

    /// <summary>Settle a create that has already been sent: a success yields the new id, a 409 adopts the
    /// record that owns the endpoint (a duplicate registration IS that record, by the one-endpoint rule).</summary>
    public static async Task<int> EnsureFromAsync(HttpClient client, HttpResponseMessage res, string endpoint)
    {
        if (res.StatusCode != HttpStatusCode.Conflict)
        {
            res.EnsureSuccessStatusCode();
            return (await res.Content.ReadFromJsonAsync<AccountResponse>())!.Id;
        }
        static string Normalize(string e) => e.TrimEnd('/').ToLowerInvariant();
        var all = await client.GetFromJsonAsync<List<AccountResponse>>("/api/accounts");
        return all!.Single(a => Normalize(a.BlobEndpoint) == Normalize(endpoint)).Id;
    }
}
