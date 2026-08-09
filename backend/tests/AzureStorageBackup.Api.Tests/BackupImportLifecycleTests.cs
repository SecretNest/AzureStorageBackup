using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The last link in the lifecycle: **importing from existing storage into a brand-new environment**.
/// The cloud backup is created by an "old machine" (its own separate local database + orchestrator); the host serving HTTP has never seen this container —
/// no BackupConfig, no CachedVersionIndex, no LocalBackupState. After importing through <c>POST /api/backup-configs/import</c>,
/// assert that every version can be listed, that the local authoritative state was backfilled, and that any version restores byte for byte.
/// Encrypted and unencrypted are both covered: their info files use different blob names (IndexBlobName vs EncryptedIndexBlobName).
/// </summary>
[Trait("Category", "Integration")]
public sealed class BackupImportLifecycleTests : IClassFixture<TestWebAppFactory>, IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    private const string AzuriteEndpoint = "http://127.0.0.1:10000/devstoreaccount1";

    private static readonly DateTime MtimeBase = new(2021, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly TestWebAppFactory _factory;
    private readonly HttpClient _client;
    private readonly string _base;
    private readonly string _src;
    private readonly string _temp;
    private int _mtimeSeq;

    public BackupImportLifecycleTests(TestWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _base = Path.Combine(Path.GetTempPath(), "asb-imp-" + Guid.NewGuid().ToString("N"));
        _src = Path.Combine(_base, "src");
        _temp = Path.Combine(_base, "temp");
        Directory.CreateDirectory(_src);
    }

    public void Dispose()
    {
        _client.Dispose();
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
    }

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static bool SevenZip() => SevenZipArchiveCodec.TryResolveExecutable() is not null;
    private static string RandomName(string p) => p + Guid.NewGuid().ToString("N")[..8];

    // ───────────────────────── Source tree and snapshots ─────────────────────────

    private void Write(string rel, byte[] content)
    {
        var full = Path.Combine(_src, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        File.SetLastWriteTimeUtc(full, MtimeBase.AddMinutes(++_mtimeSeq));
    }

    private void WriteText(string rel, string text) => Write(rel, Encoding.UTF8.GetBytes(text));

    private static byte[] Rand(int size, int seed)
    {
        var buf = new byte[size];
        new Random(seed).NextBytes(buf);
        return buf;
    }

    private Dictionary<string, byte[]> Snapshot()
    {
        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(_src, "*", SearchOption.AllDirectories))
            map[Rel(_src, f)] = File.ReadAllBytes(f);
        return map;
    }

    private static string Rel(string root, string full) =>
        Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');

    private static void AssertTreeEquals(Dictionary<string, byte[]> expected, string target, string label)
    {
        var actual = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Rel(target, f), StringComparer.Ordinal);

        Assert.Equal(expected.Keys.Order(), actual.Keys.Order()); // directory structure matches
        foreach (var (rel, bytes) in expected)
        {
            var got = File.ReadAllBytes(actual[rel]);
            Assert.True(bytes.AsSpan().SequenceEqual(got),
                $"{label}: restored content differs for {rel} ({bytes.Length} vs {got.Length} bytes)");
        }
    }

    // ───────────────────────── The "old machine": build a multi-version backup in the cloud ─────────────────────────

    /// <summary>
    /// Runs the real orchestrator against a **separate** local database to write the cloud backup, then throws that
    /// database away — so the host serving HTTP knows nothing at all about this container, and the import faces a
    /// genuinely empty environment.
    /// </summary>
    private async Task SeedCloudBackupAsync(string container, string? password, Action beforeSecondRun)
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        var blobFactory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(blobFactory, new SevenZipArchiveCodec());
        var hasher = new FileHasher();
        var tracked = new TrackedInfoStore(store, new LocalBackupStateStore(db));
        var indexCache = new LocalIndexCache(db, store);
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(hasher), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(blobFactory), blobFactory, store, staging,
            new RetentionCleaner(blobFactory, store, new RetentionEvaluator(), indexCache: indexCache, trackedInfo: tracked),
            hasher, indexCache: indexCache, trackedInfo: tracked);

        var account = new Account
        {
            Id = 1,
            Name = "azurite",
            BlobEndpoint = AzuriteEndpoint,
            AccountKeyProtected = TestSecrets.Protect(AzuriteKey),
            Region = AzureRegion.Global,
        };

        BackupRequest Request() => new()
        {
            Account = account,
            Container = container,
            LocalRoot = _src,
            Name = "imported-fixture",
            Description = "created on another machine",
            Password = password,
            Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 20_000 } },
        };

        var v1 = await orchestrator.RunAsync(Request());
        Assert.Equal(1, v1.Version);
        beforeSecondRun();
        var v2 = await orchestrator.RunAsync(Request());
        Assert.Equal(2, v2.Version);
    }

    // ───────────────────────── Import → list versions → restore ─────────────────────────

    [SkippableTheory]
    [InlineData("import pass phrase")] // encrypted: the password arrives in the clear in the request body, must be stored as ciphertext, and is decrypted at the throat on restore
    [InlineData(null)]
    public async Task Import_Into_Empty_Environment_Lists_All_Versions_And_Restores_Them_Byte_For_Byte(string? password)
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var container = RandomName(password is null ? "implife-" : "implifeenc-");
        var blobFactory = new BlobClientFactory(TestSecrets.Reader);
        var azurite = new Account
        {
            BlobEndpoint = AzuriteEndpoint,
            AccountKeyProtected = TestSecrets.Protect(AzuriteKey),
            Region = AzureRegion.Global,
        };
        var cc = blobFactory.CreateServiceClient(azurite).GetBlobContainerClient(container);

        try
        {
            // The "old machine" builds a two-version cloud backup.
            Write("docs/one.txt", Rand(3000, 31));
            Write("docs/two.txt", Rand(3000, 32));
            Write("media/blob.bin", Rand(40_000, 33)); // ≥20K → single-file data blob
            WriteText("top.txt", "first revision");
            var snap1 = Snapshot();

            Dictionary<string, byte[]>? snap2 = null;
            await SeedCloudBackupAsync(container, password, () =>
            {
                Write("docs/two.txt", Rand(3000, 132));   // modified
                WriteText("docs/three.txt", "brand new"); // added
                snap2 = Snapshot();
            });
            Assert.NotNull(snap2);

            // The cloud really did write different info-file blob names for the encrypted and unencrypted cases.
            Assert.True(await cc.GetBlobClient(password is null
                ? BackupDiscovery.IndexBlobName
                : BackupDiscovery.EncryptedIndexBlobName).ExistsAsync());

            // ─── Empty environment: the host knows only the account and nothing about this container ───
            var account = await (await _client.PostAsJsonAsync("/api/accounts", new AccountRequest(
                "azurite", null, AzuriteEndpoint, AzureRegion.Global, AzuriteKey,
                false, ProxyMode.Independent, null, null, null, null)))
                .Content.ReadFromJsonAsync<AccountResponse>();
            Assert.NotNull(account);
            await AssertLocalEnvironmentEmptyAsync(account!.Id, container);

            // ─── Import ───
            var response = await _client.PostAsJsonAsync("/api/backup-configs/import",
                new ImportRequest(account.Id, container, password));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            var result = await response.Content.ReadFromJsonAsync<ImportResponse>();
            Assert.NotNull(result);
            var imported = result!.Config;
            Assert.Equal("imported-fixture", imported.Name);          // the config is recovered from the info file
            Assert.Equal("created on another machine", imported.Description);
            Assert.Equal(container, imported.ContainerName);
            Assert.Equal(_src, imported.LocalRoot);                     // sourceRootHint
            Assert.Equal(password is not null, imported.HasPassword);
            Assert.Empty(result.UnreadableVersions);                    // the file lists of both versions can be read

            // The local authoritative state and every version index have been backfilled (from here on, backup/restore
            // no longer downloads the cloud index in normal operation).
            await AssertLocalStateSeededAsync(account.Id, container, expectedVersions: 2);

            // The cloud verification starts on its own, so the user need not go and click check again; it should come back healthy.
            Assert.True(result.CheckStarted);
            var check = await PollUntilDoneAsync<CheckRunResponse>(
                $"/api/backup-configs/{imported.Id}/check", c => c.Status is not "Running");
            Assert.NotNull(check);
            // Assert with the Error included: this line failed once, intermittently, during a fully loaded complete run,
            // and all the screen said was "Completed != something", with no sign of what the check itself was
            // complaining about. Next time it breaks, the reason will be right there in the message.
            Assert.True(check!.Status is "Completed", $"check ended as {check.Status}: {check.Error}");
            Assert.True(check.Report?.Ok,
                $"the automatic check after import reported unhealthy: {string.Join(", ", check.Report?.CorruptedPaths ?? [])}");

            // ─── List every version (newest first, matching the order after Latest in the restore/check dropdowns) ───
            var versions = await _client.GetFromJsonAsync<List<VersionRow>>(
                $"/api/backup-configs/{imported.Id}/versions");
            Assert.NotNull(versions);
            Assert.Equal([2, 1], versions!.Select(v => v.version));
            Assert.Equal(snap2!.Count, versions[0].files);
            Assert.Equal(snap1.Count, versions[1].files);

            // ─── Restore from the imported backup (both versions must be byte-for-byte correct) ───
            await RestoreAndAssertAsync(imported.Id, version: 1, snap1, "v1");
            await RestoreAndAssertAsync(imported.Id, version: null, snap2!, "latest"); // default = latest
        }
        finally
        {
            await cc.DeleteIfExistsAsync();
        }
    }

    /// <summary>Before the import: this container leaves no trace whatsoever on the host locally — that is exactly the definition of an "empty environment".</summary>
    private async Task AssertLocalEnvironmentEmptyAsync(int accountId, string container)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.BackupConfigs.Where(c => c.ContainerName == container).ToListAsync());
        Assert.Empty(await db.CachedVersionIndexes
            .Where(c => c.AccountId == accountId && c.Container == container).ToListAsync());
        Assert.Empty(await db.LocalBackupStates
            .Where(s => s.AccountId == accountId && s.Container == container).ToListAsync());
    }

    /// <summary>After the import: the local authoritative state is backfilled from the cloud info file, and every version's index has landed in the local cache.</summary>
    private async Task AssertLocalStateSeededAsync(int accountId, string container, int expectedVersions)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var state = await db.LocalBackupStates
            .SingleOrDefaultAsync(s => s.AccountId == accountId && s.Container == container);
        Assert.NotNull(state);
        Assert.NotEmpty(state!.InfoBytes);
        Assert.NotEmpty(state.ETag); // an ETag is what makes the later conditional writes possible

        var cached = await db.CachedVersionIndexes
            .Where(c => c.AccountId == accountId && c.Container == container)
            .Select(c => c.Version).ToListAsync();
        Assert.Equal(Enumerable.Range(1, expectedVersions), cached.Order());
    }

    /// <summary>Restores over HTTP into a brand-new empty directory and compares it against the snapshot byte for byte.</summary>
    private async Task RestoreAndAssertAsync(int configId, int? version, Dictionary<string, byte[]> expected, string label)
    {
        var target = Path.Combine(_base, "restore", label);

        var start = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{configId}/restore", new RestoreRequestBody(target, version));
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);

        var run = await PollUntilDoneAsync<RestoreRunRow>(
            $"/api/backup-configs/{configId}/restore", s => s.status != "Running");
        Assert.NotNull(run);
        Assert.Equal("Completed", run!.status);
        Assert.Null(run.error);
        if (version is { } v)
            Assert.Equal(v, run.version);
        Assert.Equal(expected.Count, run.restoredFiles);

        AssertTreeEquals(expected, target, label);
    }

    private async Task<T?> PollUntilDoneAsync<T>(string url, Func<T, bool> done) where T : class
    {
        for (var i = 0; i < 600; i++) // generous: concurrent integration tests slow the background jobs down on a machine with few cores
        {
            var s = await (await _client.GetAsync(url)).Content.ReadFromJsonAsync<T>();
            if (s is not null && done(s))
                return s;
            await Task.Delay(200);
        }
        return null;
    }

    // Mirrors the backend's camelCase JSON
    private sealed record VersionRow(int version, long files, long bytes, long changedFiles);

    private sealed record RestoreRunRow(string status, int? version, int? restoredFiles, int? skippedFiles, string? error);
}
