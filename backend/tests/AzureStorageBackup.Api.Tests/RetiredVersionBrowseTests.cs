using System.Net;
using System.Net.Http.Json;
using Azure;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The metadata-browse endpoints (/tree, /file-versions, /unrecoverable, /unreadable, /hash-file,
/// /repair-plan, /restore-estimate) read a specific version's index without registering as readers —
/// so a retention round can retire that exact version mid-request: the info file still listed it when
/// the request loaded, and the index blob is already gone when the request reaches for it. That is not
/// an internal error, it is a state one refresh old — the endpoint must answer the way it answers a
/// version that never existed, not surface a bare 500 from the uncaught 404.
/// </summary>
public sealed class RetiredVersionBrowseTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    /// <summary>Serves an info file listing version 3 — the version whose index the cache below has lost.</summary>
    private sealed class CannedInfoStore : IBackupInfoStore
    {
        public static BackupInfoFile Info() => new()
        {
            Backup = new BackupMeta { Name = "canned", CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) },
            Versions =
            {
                new BackupVersion { Version = 3, CreatedAt = DateTimeOffset.UtcNow.AddDays(-1), IndexBlob = "indexes/v3.bin", Stats = new VersionStats(1, 10, 1, 10) },
            },
        };

        public Task<BackupInfoFile?> ReadInfoAsync(Account a, string c, string? p, CancellationToken ct = default)
            => Task.FromResult<BackupInfoFile?>(Info());
        public Task<(BackupInfoFile Info, string ETag)?> ReadInfoWithETagAsync(Account a, string c, string? p, CancellationToken ct = default)
            => Task.FromResult<(BackupInfoFile, string)?>((Info(), "etag-1"));
        public Task WriteInfoAsync(Account a, string c, BackupInfoFile i, string? p, AccessTier? t = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> WriteInfoConditionalAsync(Account a, string c, BackupInfoFile i, string? p, AccessTier? t, string? e, CancellationToken ct = default) => Task.FromResult("etag-2");
        public Task<VersionIndex> ReadIndexAsync(Account a, string c, string i, string? p, int v = 1, CancellationToken ct = default)
            => throw new RequestFailedException(404, "The specified blob does not exist.", "BlobNotFound", null);
        public Task<(string Name, int Volumes)> WriteIndexAsync(Account a, string c, int v, VersionIndex i, string? p, AccessTier? t = null, CancellationToken ct = default, StageTracker? progress = null) => Task.FromResult(("indexes/v.bin", 1));
    }

    /// <summary>What the cold path throws when retention deleted the index blob between the info load and this read.</summary>
    private sealed class GoneIndexCache : ILocalIndexCache
    {
        public Task<VersionIndex> ReadAsync(Account account, string container, int version, long identityTicks,
            string indexBlob, string? password, int indexVolumes = 1, CancellationToken ct = default)
            => throw new RequestFailedException(404, "The specified blob does not exist.", "BlobNotFound", null);
        public Task PutAsync(int accountId, string container, int version, long identityTicks, VersionIndex index, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveAsync(int accountId, string container, int version, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveForContainerAsync(int accountId, string container, CancellationToken ct = default) => Task.CompletedTask;
    }

    private async Task<(HttpClient Client, int ConfigId)> HostAsync(string tag)
    {
        var host = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.Remove(s.Single(d => d.ServiceType == typeof(IBackupInfoStore)));
            s.AddScoped<IBackupInfoStore, CannedInfoStore>();
            s.Remove(s.Single(d => d.ServiceType == typeof(ILocalIndexCache)));
            s.AddScoped<ILocalIndexCache, GoneIndexCache>();
        }));
        var client = host.CreateClient();
        var account = await (await client.PostAsJsonAsync("/api/accounts", new AccountRequest(
                "acct-" + tag + "-" + Guid.NewGuid().ToString("N")[..6], null,
                "https://t" + Guid.NewGuid().ToString("N")[..12] + ".blob.core.windows.net", AzureRegion.Global,
                "dGVzdGtleQ==", false, ProxyMode.Independent, null, null, null, null)))
            .Content.ReadFromJsonAsync<AccountResponse>();
        var root = Path.Combine(Path.GetTempPath(), "asb-rvb-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var config = await (await client.PostAsJsonAsync("/api/backup-configs", new BackupConfigRequest(
                account!.Id, "rvb-" + Guid.NewGuid().ToString("N")[..8], tag, null, root,
                null, StorageTier.Hot, StorageTier.Hot, null, null, null, false,
                100, 180, RetentionMode.EitherTriggers, 5_000_000, 100_000_000)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();
        return (client, config!.Id);
    }

    [Fact]
    public async Task The_Tree_Of_A_Just_Retired_Version_Answers_Empty_Not_500()
    {
        var (client, configId) = await HostAsync("tree");
        var res = await client.GetAsync($"/api/backup-configs/{configId}/tree?version=3");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Empty((await res.Content.ReadFromJsonAsync<List<object>>())!);
    }

    [Fact]
    public async Task File_Versions_Skip_A_Version_Whose_Index_Is_Gone()
    {
        var (client, configId) = await HostAsync("fv");
        var res = await client.GetAsync($"/api/backup-configs/{configId}/file-versions?path=a.txt");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Empty((await res.Content.ReadFromJsonAsync<List<object>>())!);
    }

    [Fact]
    public async Task Unrecoverable_Of_A_Just_Retired_Version_Answers_Empty_Not_500()
    {
        var (client, configId) = await HostAsync("unrec");
        var res = await client.GetAsync($"/api/backup-configs/{configId}/unrecoverable?version=3");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Empty((await res.Content.ReadFromJsonAsync<List<string>>())!);
    }

    [Fact]
    public async Task Hash_File_Against_A_Just_Retired_Version_Is_A_NotFound_Not_500()
    {
        var (client, configId) = await HostAsync("hash");
        var res = await client.GetAsync($"/api/backup-configs/{configId}/hash-file?version=3&path=a.txt");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
