using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

public class SettingsTransferTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly SettingsTransfer _sut;

    public SettingsTransferTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
        // The same DbContext for every collaborator: that is how the scoped services share one transaction in production too.
        _sut = new SettingsTransfer(_db, TestSecrets.Encryption, TestSecrets.Reader,
            new GlobalSettingsService(_db), new NotificationConfigService(_db));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<Account> SeedAccountAsync(string name = "prod", string endpoint = "https://prod.blob.core.windows.net",
        string key = "the-secret-key==", string? proxyPassword = null)
    {
        var a = new Account
        {
            Name = name,
            Description = "primary",
            BlobEndpoint = endpoint,
            Region = AzureRegion.Global,
            AccountKeyProtected = TestSecrets.Protect(key),
            UseProxy = proxyPassword is not null,
            ProxyHost = proxyPassword is not null ? "proxy.local" : null,
            ProxyPort = proxyPassword is not null ? 3128 : null,
            ProxyUsername = proxyPassword is not null ? "pu" : null,
            ProxyPasswordProtected = proxyPassword is null ? null : TestSecrets.Protect(proxyPassword),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.Accounts.Add(a);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return a;
    }

    [Fact]
    public async Task Export_Without_Secrets_Leaves_Key_And_Proxy_Password_Null()
    {
        await SeedAccountAsync(proxyPassword: "pp");

        var doc = await _sut.ExportAsync(includeSecrets: false, CancellationToken.None);

        Assert.Equal(SettingsDocument.FormatName, doc.Format);
        Assert.Equal(SettingsDocument.CurrentVersion, doc.Version);
        Assert.False(doc.IncludesSecrets);
        var entry = Assert.Single(doc.Accounts!);
        Assert.Equal("prod", entry.Name);
        Assert.Equal("https://prod.blob.core.windows.net", entry.BlobEndpoint);
        Assert.Null(entry.AccountKey);
        Assert.Null(entry.ProxyPassword);
        Assert.NotNull(doc.BackupDefaults);
        Assert.NotNull(doc.Performance);
        Assert.NotNull(doc.Notifications);
    }

    [Fact]
    public async Task Export_With_Secrets_Carries_Plaintext()
    {
        await SeedAccountAsync(proxyPassword: "pp");

        var doc = await _sut.ExportAsync(includeSecrets: true, CancellationToken.None);

        Assert.True(doc.IncludesSecrets);
        var entry = Assert.Single(doc.Accounts!);
        Assert.Equal("the-secret-key==", entry.AccountKey);
        Assert.Equal("pp", entry.ProxyPassword);
    }

    [Fact]
    public async Task Export_Reflects_The_Stored_Settings_Halves_And_Notifications()
    {
        await new GlobalSettingsService(_db).UpsertDefaultsAsync(
            BackupDefaultsSettings.From(new GlobalSettings()) with { DefaultMaxVersions = 7, DefaultIgnoreRules = "*.tmp" });
        await new NotificationConfigService(_db).UpsertAsync(new NotificationConfig { Enabled = true, Url = "https://n.example" });
        _db.ChangeTracker.Clear();

        var doc = await _sut.ExportAsync(includeSecrets: false, CancellationToken.None);

        Assert.Equal(7, doc.BackupDefaults!.DefaultMaxVersions);
        Assert.Equal("*.tmp", doc.BackupDefaults.DefaultIgnoreRules);
        Assert.True(doc.Notifications!.Enabled);
        Assert.Equal("https://n.example", doc.Notifications.Url);
    }
}
