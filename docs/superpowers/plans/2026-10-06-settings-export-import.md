# Settings Export / Import Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Settings → About gets "Export settings" (one JSON file holding Accounts, Backup defaults, Performance, Notifications, with account keys only when the user ticks a box) and "Import settings…" (preview → fill in any missing account keys → apply, accounts matched by storage endpoint so existing ids survive).

**Architecture:** One backend class `SettingsTransfer` does export, preview (`PlanAsync`) and apply (`ImportAsync`), the last two sharing one validation + reconciliation routine. Three endpoints under `/api/settings`. The frontend parses the file, asks the server for the plan, shows it in a Modal with password inputs for new accounts lacking a key, then posts the same document back with the keys filled in. Pure frontend logic (which accounts need a key, summary wording, file name) sits in `lib/settingsTransfer.ts` under vitest.

**Tech Stack:** ASP.NET Core minimal APIs, EF Core + SQLite, xUnit; React + TypeScript, vitest, oxlint.

**Spec:** `docs/superpowers/specs/2026-10-06-settings-export-import-design.md`

## Global Constraints

- File `format` is exactly `azure-storage-backup-settings`; `version` is `1`; a version greater than 1 is rejected.
- The file never carries account ids or `createdAt`.
- Account matching key: `blobEndpoint` with trailing `/` stripped and lower-cased — the same rule `AccountService` already applies when rejecting a duplicate endpoint. One shared function, `BlobEndpointKey.Normalize`.
- Accounts present in the database but absent from the file are never touched or deleted.
- Import is one database transaction under `AccountTopologyGate.Gate`.
- Export with secrets while the keyring is lost → the existing 409 `keyring_lost` payload (`KeyringGuard.Payload`).
- Frontend conventions: cookie auth via `api` in `frontend/src/api/client.ts`; dialogs use `components/Modal.tsx`; form rows use `components/Field.tsx`; no UI unit tests, only `lib/*.test.ts`.
- Docs: at the end the spec is folded into `docs/operations.md` and `docs/web-ui.md` and `docs/superpowers/` is deleted (project rule).
- Commit messages end with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.

## Review Focus

1. An endpoint in the file that differs from a stored one only by case or a trailing slash must be an **update**, never a second account. — pinned in Task 2 (`Plan_Matches_Endpoint_Ignoring_Case_And_Trailing_Slash`).
2. Importing while the keyring is lost: a matched account whose entry carries no key must keep its (unreadable) ciphertext and not throw. — pinned in Task 3 (`Import_Leaves_Unreadable_Ciphertext_Alone_When_Entry_Has_No_Key`).
3. A document with `accounts: []` and no sections is a legal no-op, not an error. — pinned in Task 3 (`Import_Of_Empty_Document_Changes_Nothing`).
4. A request body that is not a JSON object (e.g. `[]`, or plain text) must come back 400, not 500. — pinned in Task 4 (`Import_Rejects_Non_Object_Body_With_400`).
5. Picking the same file twice in a row (after cancelling the dialog) must open the dialog again — a file input does not fire `onChange` for an unchanged selection, so the input's value is cleared after every read. — Task 6, step 3 comment; verified by hand in Task 6 step 7.

---

### Task 1: Document DTOs and export

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Models/SettingsDocument.cs`
- Create: `backend/src/AzureStorageBackup.Api/Services/SettingsTransfer.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/SettingsTransferTests.cs`

**Interfaces:**
- Consumes: `BackupDefaultsSettings`, `PerformanceSettings` (`Models/SettingsHalves.cs`), `NotificationRequest` (`Models/NotificationDtos.cs`), `ISecretReader`, `IGlobalSettingsService`, `INotificationConfigService`.
- Produces: `SettingsDocument`, `SettingsAccountEntry`, `ImportPlan`, `ImportPlanAccount`, `SettingsImportException`; `SettingsTransfer.ExportAsync(bool includeSecrets, CancellationToken ct) : Task<SettingsDocument>`.

- [ ] **Step 1: Write the DTOs**

`backend/src/AzureStorageBackup.Api/Models/SettingsDocument.cs`:

```csharp
namespace AzureStorageBackup.Api.Models;

/// <summary>
/// The settings file: everything under Settings (accounts, backup defaults, performance, notifications) as one
/// JSON document. Carries no ids — accounts are matched on import by endpoint, so backups, groups and schedules
/// that reference an account by id keep working after an import onto a database that already has it.
/// Every section is optional on the way in: a missing one leaves that part of the database untouched.
/// </summary>
public sealed record SettingsDocument
{
    public const string FormatName = "azure-storage-backup-settings";
    public const int CurrentVersion = 1;

    public string? Format { get; init; }
    public int Version { get; init; }
    public DateTimeOffset? ExportedAt { get; init; }
    /// <summary>True when the export was asked to include account keys and proxy passwords (plaintext).</summary>
    public bool IncludesSecrets { get; init; }
    public List<SettingsAccountEntry>? Accounts { get; init; }
    public BackupDefaultsSettings? BackupDefaults { get; init; }
    public PerformanceSettings? Performance { get; init; }
    public NotificationRequest? Notifications { get; init; }
}

/// <summary>One account in the file. The same fields as <see cref="AccountRequest"/>; secrets are plaintext or null.</summary>
public sealed record SettingsAccountEntry(
    string? Name,
    string? Description,
    string? BlobEndpoint,
    AzureRegion Region,
    string? AccountKey,
    bool UseProxy,
    ProxyMode ProxyMode,
    string? ProxyHost,
    int? ProxyPort,
    string? ProxyUsername,
    string? ProxyPassword);

/// <summary>What an import would do (preview) or did (apply). Returned by both import endpoints.</summary>
public sealed record ImportPlan(
    IReadOnlyList<ImportPlanAccount> Accounts,
    bool BackupDefaults,
    bool Performance,
    bool Notifications);

/// <summary><paramref name="Action"/> is "create" or "update". <paramref name="NeedsAccountKey"/> is true for a
/// create whose entry has no key — the frontend asks the user for it before applying.</summary>
public sealed record ImportPlanAccount(string Name, string BlobEndpoint, string Action, bool NeedsAccountKey);
```

- [ ] **Step 2: Write the failing export tests**

`backend/tests/AzureStorageBackup.Api.Tests/SettingsTransferTests.cs`:

```csharp
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
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `cd backend && dotnet test --filter FullyQualifiedName~SettingsTransferTests`
Expected: build error — `SettingsTransfer` does not exist.

- [ ] **Step 4: Write the exporter**

`backend/src/AzureStorageBackup.Api/Services/SettingsTransfer.cs`:

```csharp
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

/// <summary>A settings file the server cannot accept; the message is meant for the user and becomes a 400.</summary>
public sealed class SettingsImportException(string message) : Exception(message);

/// <summary>
/// Export and import of the whole Settings area as one <see cref="SettingsDocument"/>. Preview and apply go
/// through the same validation and reconciliation, so what the preview says is what the import does.
/// </summary>
public sealed class SettingsTransfer(
    AppDbContext db,
    IEncryptionService encryption,
    ISecretReader secrets,
    IGlobalSettingsService settings,
    INotificationConfigService notifications)
{
    public async Task<SettingsDocument> ExportAsync(bool includeSecrets, CancellationToken ct)
    {
        var accounts = await db.Accounts.AsNoTracking().OrderBy(a => a.Id).ToListAsync(ct);
        var row = await settings.GetAsync(ct);
        var notif = await notifications.GetAsync(ct);

        return new SettingsDocument
        {
            Format = SettingsDocument.FormatName,
            Version = SettingsDocument.CurrentVersion,
            ExportedAt = DateTimeOffset.UtcNow,
            IncludesSecrets = includeSecrets,
            Accounts = accounts.Select(a => new SettingsAccountEntry(
                a.Name,
                a.Description,
                a.BlobEndpoint,
                a.Region,
                // RevealAccountKey throws SecretUnavailableException on an unreadable ciphertext; the middleware
                // turns that into the keyring_lost 409, which is the right answer for "export my keys" then.
                includeSecrets ? secrets.RevealAccountKey(a) : null,
                a.UseProxy,
                a.ProxyMode,
                a.ProxyHost,
                a.ProxyPort,
                a.ProxyUsername,
                includeSecrets ? secrets.RevealProxyPassword(a) : null)).ToList(),
            BackupDefaults = BackupDefaultsSettings.From(row),
            Performance = PerformanceSettings.From(row),
            Notifications = new NotificationRequest(
                notif.Enabled, notif.Url, notif.Method, notif.BodyTemplate, notif.ContentType, notif.Events, notif.ProxyUrl),
        };
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd backend && dotnet test --filter FullyQualifiedName~SettingsTransferTests`
Expected: 3 passed.

- [ ] **Step 6: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Models/SettingsDocument.cs backend/src/AzureStorageBackup.Api/Services/SettingsTransfer.cs backend/tests/AzureStorageBackup.Api.Tests/SettingsTransferTests.cs
git commit -m "feat: settings export document and exporter

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: Shared endpoint key, validation and the import plan

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Services/BlobEndpointKey.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/AccountService.cs` (`RejectEndpointAliasAsync`, the local `Normalize`)
- Modify: `backend/src/AzureStorageBackup.Api/Services/SettingsTransfer.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/SettingsTransferTests.cs`

**Interfaces:**
- Produces: `BlobEndpointKey.Normalize(string) : string`; `SettingsTransfer.PlanAsync(SettingsDocument doc, CancellationToken ct) : Task<ImportPlan>`; private `Validate(SettingsDocument)` and `BuildPlan(SettingsDocument, IReadOnlyList<Account>)` reused by Task 3.

- [ ] **Step 1: Write the failing plan tests** (append inside the class in `SettingsTransferTests.cs`)

```csharp
    private static SettingsDocument Doc(params SettingsAccountEntry[] accounts) => new()
    {
        Format = SettingsDocument.FormatName,
        Version = SettingsDocument.CurrentVersion,
        Accounts = [.. accounts],
    };

    private static SettingsAccountEntry Entry(string name, string endpoint, string? key = null, string? proxyPassword = null,
        bool useProxy = false, string? proxyUsername = null) =>
        new(name, null, endpoint, AzureRegion.Global, key, useProxy, ProxyMode.Independent,
            useProxy ? "proxy.local" : null, useProxy ? 3128 : null, proxyUsername, proxyPassword);

    [Fact]
    public async Task Plan_Rejects_Wrong_Format_And_Future_Version()
    {
        var wrongFormat = Doc() with { Format = "something-else" };
        var ex1 = await Assert.ThrowsAsync<SettingsImportException>(() => _sut.PlanAsync(wrongFormat, CancellationToken.None));
        Assert.Contains("azure-storage-backup-settings", ex1.Message);

        var future = Doc() with { Version = SettingsDocument.CurrentVersion + 1 };
        var ex2 = await Assert.ThrowsAsync<SettingsImportException>(() => _sut.PlanAsync(future, CancellationToken.None));
        Assert.Contains("version", ex2.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Plan_Rejects_Blank_Name_Blank_Endpoint_And_Duplicate_Endpoint()
    {
        await Assert.ThrowsAsync<SettingsImportException>(() =>
            _sut.PlanAsync(Doc(Entry("", "https://a.blob.core.windows.net")), CancellationToken.None));
        await Assert.ThrowsAsync<SettingsImportException>(() =>
            _sut.PlanAsync(Doc(Entry("a", " ")), CancellationToken.None));
        var dup = await Assert.ThrowsAsync<SettingsImportException>(() =>
            _sut.PlanAsync(Doc(Entry("a", "https://a.blob.core.windows.net"), Entry("b", "https://A.blob.core.windows.net/")), CancellationToken.None));
        Assert.Contains("twice", dup.Message);
    }

    [Fact]
    public async Task Plan_Classifies_Create_Update_And_Missing_Keys()
    {
        await SeedAccountAsync("prod", "https://prod.blob.core.windows.net");

        var plan = await _sut.PlanAsync(Doc(
            Entry("prod-renamed", "https://prod.blob.core.windows.net"),
            Entry("new-with-key", "https://new1.blob.core.windows.net", key: "k1"),
            Entry("new-without-key", "https://new2.blob.core.windows.net")), CancellationToken.None);

        Assert.Collection(plan.Accounts,
            a => { Assert.Equal("update", a.Action); Assert.False(a.NeedsAccountKey); Assert.Equal("prod-renamed", a.Name); },
            a => { Assert.Equal("create", a.Action); Assert.False(a.NeedsAccountKey); },
            a => { Assert.Equal("create", a.Action); Assert.True(a.NeedsAccountKey); });
        Assert.False(plan.BackupDefaults);
        Assert.False(plan.Performance);
        Assert.False(plan.Notifications);
    }

    [Fact]
    public async Task Plan_Reports_Which_Sections_The_File_Carries()
    {
        var doc = Doc() with { Performance = PerformanceSettings.From(new GlobalSettings()) };

        var plan = await _sut.PlanAsync(doc, CancellationToken.None);

        Assert.False(plan.BackupDefaults);
        Assert.True(plan.Performance);
        Assert.False(plan.Notifications);
    }

    [Fact]
    public async Task Plan_Matches_Endpoint_Ignoring_Case_And_Trailing_Slash()
    {
        await SeedAccountAsync("prod", "https://prod.blob.core.windows.net");

        var plan = await _sut.PlanAsync(Doc(Entry("prod", "https://PROD.blob.core.windows.net/")), CancellationToken.None);

        Assert.Equal("update", Assert.Single(plan.Accounts).Action);
    }

    [Fact]
    public async Task Plan_Writes_Nothing()
    {
        await SeedAccountAsync("prod", "https://prod.blob.core.windows.net");

        await _sut.PlanAsync(Doc(
            Entry("prod-renamed", "https://prod.blob.core.windows.net"),
            Entry("new", "https://new.blob.core.windows.net", key: "k")), CancellationToken.None);

        _db.ChangeTracker.Clear();
        var rows = await _db.Accounts.AsNoTracking().ToListAsync();
        Assert.Equal("prod", Assert.Single(rows).Name);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd backend && dotnet test --filter FullyQualifiedName~SettingsTransferTests`
Expected: build error — `PlanAsync` does not exist.

- [ ] **Step 3: Create the shared normaliser and use it in AccountService**

`backend/src/AzureStorageBackup.Api/Services/BlobEndpointKey.cs`:

```csharp
namespace AzureStorageBackup.Api.Services;

/// <summary>
/// The identity of a storage account for matching purposes: its endpoint, trailing slash dropped, lower-cased.
/// Used both to reject a second account on the same endpoint and to match a settings file's accounts to the
/// stored ones on import — one rule, so an import can never create what a manual add would have refused.
/// </summary>
public static class BlobEndpointKey
{
    public static string Normalize(string endpoint) => endpoint.TrimEnd('/').ToLowerInvariant();
}
```

In `AccountService.RejectEndpointAliasAsync` delete the local `static string Normalize(string e) => ...;` line and replace the two uses of `Normalize(` with `BlobEndpointKey.Normalize(`.

- [ ] **Step 4: Add validation and the plan to SettingsTransfer**

Add to `SettingsTransfer` (after `ExportAsync`):

```csharp
    public async Task<ImportPlan> PlanAsync(SettingsDocument doc, CancellationToken ct)
    {
        Validate(doc);
        var existing = await db.Accounts.AsNoTracking().ToListAsync(ct);
        return BuildPlan(doc, existing);
    }

    private static void Validate(SettingsDocument doc)
    {
        if (doc.Format != SettingsDocument.FormatName)
            throw new SettingsImportException(
                $"Not a settings file: expected \"format\": \"{SettingsDocument.FormatName}\".");
        if (doc.Version < 1 || doc.Version > SettingsDocument.CurrentVersion)
            throw new SettingsImportException(
                $"Settings file version {doc.Version} is not supported by this server (it reads up to version {SettingsDocument.CurrentVersion}).");

        var seen = new HashSet<string>();
        var i = 0;
        foreach (var a in doc.Accounts ?? [])
        {
            i++;
            if (string.IsNullOrWhiteSpace(a.Name))
                throw new SettingsImportException($"Account #{i} in the file has no name.");
            if (string.IsNullOrWhiteSpace(a.BlobEndpoint))
                throw new SettingsImportException($"Account \"{a.Name}\" in the file has no blob endpoint.");
            if (!seen.Add(BlobEndpointKey.Normalize(a.BlobEndpoint)))
                throw new SettingsImportException(
                    $"The file lists the endpoint {a.BlobEndpoint} twice (account \"{a.Name}\"); one storage account, one entry.");
        }
    }

    /// <summary>Which stored account each file entry lands on; null for a create.</summary>
    private static Dictionary<string, Account> ByEndpoint(IEnumerable<Account> existing) =>
        existing.GroupBy(a => BlobEndpointKey.Normalize(a.BlobEndpoint)).ToDictionary(g => g.Key, g => g.First());

    private static ImportPlan BuildPlan(SettingsDocument doc, IReadOnlyList<Account> existing)
    {
        var byEndpoint = ByEndpoint(existing);
        var accounts = (doc.Accounts ?? []).Select(a =>
        {
            var matched = byEndpoint.ContainsKey(BlobEndpointKey.Normalize(a.BlobEndpoint!));
            return new ImportPlanAccount(
                a.Name!,
                a.BlobEndpoint!,
                matched ? "update" : "create",
                NeedsAccountKey: !matched && string.IsNullOrEmpty(a.AccountKey));
        }).ToList();

        return new ImportPlan(accounts,
            BackupDefaults: doc.BackupDefaults is not null,
            Performance: doc.Performance is not null,
            Notifications: doc.Notifications is not null);
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~SettingsTransferTests|FullyQualifiedName~AccountServiceTests|FullyQualifiedName~AccountEndpointsTests"`
Expected: all passed (the account tests prove the shared normaliser changed nothing).

- [ ] **Step 6: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Services/BlobEndpointKey.cs backend/src/AzureStorageBackup.Api/Services/AccountService.cs backend/src/AzureStorageBackup.Api/Services/SettingsTransfer.cs backend/tests/AzureStorageBackup.Api.Tests/SettingsTransferTests.cs
git commit -m "feat: settings import preview plans accounts by endpoint

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: Import apply

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/SettingsTransfer.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/SettingsTransferTests.cs`

**Interfaces:**
- Consumes: `Validate`, `BuildPlan`, `ByEndpoint` from Task 2; `AccountTopologyGate.Gate` (`Services/AccountTopologyGate.cs`, internal static `SemaphoreSlim`).
- Produces: `SettingsTransfer.ImportAsync(SettingsDocument doc, CancellationToken ct) : Task<ImportPlan>`.

- [ ] **Step 1: Write the failing import tests** (append inside the class)

```csharp
    [Fact]
    public async Task Import_Creates_Updates_And_Leaves_Accounts_Keeping_Ids()
    {
        var prod = await SeedAccountAsync("prod", "https://prod.blob.core.windows.net");
        var untouched = await SeedAccountAsync("other", "https://other.blob.core.windows.net", key: "ok");

        var plan = await _sut.ImportAsync(Doc(
            Entry("prod-renamed", "https://prod.blob.core.windows.net"),
            Entry("fresh", "https://fresh.blob.core.windows.net", key: "fresh-key")), CancellationToken.None);

        Assert.Equal(new[] { "update", "create" }, plan.Accounts.Select(a => a.Action).ToArray());
        _db.ChangeTracker.Clear();
        var rows = await _db.Accounts.AsNoTracking().OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.Equal(prod.Id, rows[0].Id);
        Assert.Equal("prod-renamed", rows[0].Name);
        Assert.Equal(untouched.Id, rows[1].Id);
        Assert.Equal("other", rows[1].Name);
        Assert.Equal("fresh", rows[2].Name);
        Assert.Equal("fresh-key", TestSecrets.Reader.RevealAccountKey(rows[2]));
        Assert.NotEqual(default, rows[2].CreatedAt);
    }

    [Fact]
    public async Task Import_Keeps_Stored_Secrets_When_The_Entry_Has_None_And_Replaces_Them_When_It_Does()
    {
        var a = await SeedAccountAsync("prod", "https://prod.blob.core.windows.net", key: "old", proxyPassword: "old-pp");

        await _sut.ImportAsync(Doc(Entry("prod", "https://prod.blob.core.windows.net", useProxy: true, proxyUsername: "pu")), CancellationToken.None);
        _db.ChangeTracker.Clear();
        var kept = await _db.Accounts.AsNoTracking().SingleAsync(x => x.Id == a.Id);
        Assert.Equal("old", TestSecrets.Reader.RevealAccountKey(kept));
        Assert.Equal("old-pp", TestSecrets.Reader.RevealProxyPassword(kept));

        await _sut.ImportAsync(Doc(Entry("prod", "https://prod.blob.core.windows.net", key: "new", proxyPassword: "new-pp", useProxy: true, proxyUsername: "pu")), CancellationToken.None);
        _db.ChangeTracker.Clear();
        var replaced = await _db.Accounts.AsNoTracking().SingleAsync(x => x.Id == a.Id);
        Assert.Equal("new", TestSecrets.Reader.RevealAccountKey(replaced));
        Assert.Equal("new-pp", TestSecrets.Reader.RevealProxyPassword(replaced));
    }

    [Fact]
    public async Task Import_Rejects_A_New_Account_Without_A_Key_And_Writes_Nothing()
    {
        await SeedAccountAsync("prod", "https://prod.blob.core.windows.net");

        var ex = await Assert.ThrowsAsync<SettingsImportException>(() => _sut.ImportAsync(Doc(
            Entry("prod-renamed", "https://prod.blob.core.windows.net"),
            Entry("fresh", "https://fresh.blob.core.windows.net")), CancellationToken.None));

        Assert.Contains("fresh", ex.Message);
        _db.ChangeTracker.Clear();
        var rows = await _db.Accounts.AsNoTracking().ToListAsync();
        Assert.Equal("prod", Assert.Single(rows).Name);
    }

    [Fact]
    public async Task Import_Applies_Only_The_Sections_Present()
    {
        var gs = new GlobalSettingsService(_db);
        await gs.UpsertDefaultsAsync(BackupDefaultsSettings.From(new GlobalSettings()) with { DefaultMaxVersions = 7 });
        await gs.UpsertPerformanceAsync(PerformanceSettings.From(new GlobalSettings()) with { UploadConcurrency = 9 });
        await new NotificationConfigService(_db).UpsertAsync(new NotificationConfig { Enabled = true, Url = "https://old.example" });
        _db.ChangeTracker.Clear();

        var doc = Doc() with
        {
            Performance = PerformanceSettings.From(new GlobalSettings()) with { UploadConcurrency = 3 },
            Notifications = new NotificationRequest(false, "https://new.example", NotificationMethod.Post, null, null, NotificationEvents.BackupFailure, null),
        };
        var plan = await _sut.ImportAsync(doc, CancellationToken.None);

        Assert.False(plan.BackupDefaults);
        Assert.True(plan.Performance);
        Assert.True(plan.Notifications);
        _db.ChangeTracker.Clear();
        var row = await gs.GetAsync();
        Assert.Equal(7, row.DefaultMaxVersions);   // defaults section absent → untouched
        Assert.Equal(3, row.UploadConcurrency);    // performance section present → overwritten
        var n = await new NotificationConfigService(_db).GetAsync();
        Assert.False(n.Enabled);
        Assert.Equal("https://new.example", n.Url);
        Assert.Equal(NotificationEvents.BackupFailure, n.Events);
    }

    [Fact]
    public async Task Import_Of_Empty_Document_Changes_Nothing()
    {
        await SeedAccountAsync("prod", "https://prod.blob.core.windows.net");

        var plan = await _sut.ImportAsync(Doc(), CancellationToken.None);

        Assert.Empty(plan.Accounts);
        _db.ChangeTracker.Clear();
        Assert.Equal(1, await _db.Accounts.CountAsync());
    }

    [Fact]
    public async Task Import_Leaves_Unreadable_Ciphertext_Alone_When_Entry_Has_No_Key()
    {
        var a = await SeedAccountAsync("prod", "https://prod.blob.core.windows.net");
        var stale = TestSecrets.Stale("old-key");
        (await _db.Accounts.SingleAsync(x => x.Id == a.Id)).AccountKeyProtected = stale;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await _sut.ImportAsync(Doc(Entry("prod-renamed", "https://prod.blob.core.windows.net")), CancellationToken.None);

        _db.ChangeTracker.Clear();
        var row = await _db.Accounts.AsNoTracking().SingleAsync(x => x.Id == a.Id);
        Assert.Equal("prod-renamed", row.Name);
        Assert.Equal(stale, row.AccountKeyProtected);
    }

    [Fact]
    public async Task Import_Rolls_Back_Everything_When_A_Later_Account_Is_Invalid()
    {
        await SeedAccountAsync("prod", "https://prod.blob.core.windows.net");
        var doc = Doc(
            Entry("prod-renamed", "https://prod.blob.core.windows.net"),
            Entry("dup", "https://PROD.blob.core.windows.net/", key: "k")) with
        {
            Performance = PerformanceSettings.From(new GlobalSettings()) with { UploadConcurrency = 3 },
        };

        await Assert.ThrowsAsync<SettingsImportException>(() => _sut.ImportAsync(doc, CancellationToken.None));

        _db.ChangeTracker.Clear();
        Assert.Equal("prod", (await _db.Accounts.AsNoTracking().SingleAsync()).Name);
        Assert.Equal(5, (await new GlobalSettingsService(_db).GetAsync()).UploadConcurrency);
    }
```

`TestSecrets.Stale(string)` already exists (used by `AccountEndpointsTests`); it produces ciphertext the current keyring cannot read.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd backend && dotnet test --filter FullyQualifiedName~SettingsTransferTests`
Expected: build error — `ImportAsync` does not exist.

- [ ] **Step 3: Write ImportAsync**

Add to `SettingsTransfer` (after `PlanAsync`):

```csharp
    /// <summary>
    /// Applies the file. One transaction: a failure anywhere leaves the database as it was. Held under the account
    /// topology gate so a concurrent delete cannot slip between the plan and the writes. The settings and
    /// notification services share this scope's DbContext, so their writes ride the same transaction.
    /// </summary>
    public async Task<ImportPlan> ImportAsync(SettingsDocument doc, CancellationToken ct)
    {
        Validate(doc);

        await AccountTopologyGate.Gate.WaitAsync(ct);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var existing = await db.Accounts.ToListAsync(ct);
            var plan = BuildPlan(doc, existing);

            var missing = plan.Accounts.Where(p => p.NeedsAccountKey).Select(p => $"\"{p.Name}\"").ToList();
            if (missing.Count > 0)
                throw new SettingsImportException(
                    $"These accounts are new here and the file carries no key for them: {string.Join(", ", missing)}. Enter their keys and import again.");

            var byEndpoint = ByEndpoint(existing);
            foreach (var entry in doc.Accounts ?? [])
            {
                if (byEndpoint.TryGetValue(BlobEndpointKey.Normalize(entry.BlobEndpoint!), out var row))
                {
                    Apply(entry, row, isNew: false);
                }
                else
                {
                    row = new Account { CreatedAt = DateTimeOffset.UtcNow };
                    Apply(entry, row, isNew: true);
                    db.Accounts.Add(row);
                }
            }
            await db.SaveChangesAsync(ct);

            if (doc.BackupDefaults is not null)
                await settings.UpsertDefaultsAsync(doc.BackupDefaults, ct);
            if (doc.Performance is not null)
                await settings.UpsertPerformanceAsync(doc.Performance, ct);
            if (doc.Notifications is not null)
                await notifications.UpsertAsync(doc.Notifications.ToConfig(), ct);

            await tx.CommitAsync(ct);
            return plan;
        }
        finally
        {
            AccountTopologyGate.Gate.Release();
        }
    }

    /// <summary>Every non-secret field is taken from the file. A secret is replaced only when the file carries one;
    /// an empty secret on a matched account means "keep what is stored" (the file was exported without secrets, or
    /// the user chose not to re-enter it). A new account always has a key here — the check above guarantees it.</summary>
    private void Apply(SettingsAccountEntry entry, Account row, bool isNew)
    {
        row.Name = entry.Name!;
        row.Description = entry.Description;
        row.BlobEndpoint = entry.BlobEndpoint!;
        row.Region = entry.Region;
        row.UseProxy = entry.UseProxy;
        row.ProxyMode = entry.ProxyMode;
        row.ProxyHost = entry.ProxyHost;
        row.ProxyPort = entry.ProxyPort;
        row.ProxyUsername = entry.ProxyUsername;

        if (!string.IsNullOrEmpty(entry.AccountKey))
            row.AccountKeyProtected = encryption.Encrypt(entry.AccountKey);
        else if (isNew)
            row.AccountKeyProtected = string.Empty;

        if (!string.IsNullOrEmpty(entry.ProxyPassword))
            row.ProxyPasswordProtected = encryption.Encrypt(entry.ProxyPassword);
        else if (isNew)
            row.ProxyPasswordProtected = null;
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd backend && dotnet test --filter FullyQualifiedName~SettingsTransferTests`
Expected: all 16 passed.

- [ ] **Step 5: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Services/SettingsTransfer.cs backend/tests/AzureStorageBackup.Api.Tests/SettingsTransferTests.cs
git commit -m "feat: settings import applies accounts and settings in one transaction

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: Endpoints

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Endpoints/SettingsEndpoints.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Program.cs:290` (DI registration next to `IGlobalSettingsService`)
- Create: `backend/tests/AzureStorageBackup.Api.Tests/SettingsTransferEndpointsTests.cs`

**Interfaces:**
- Consumes: `SettingsTransfer.ExportAsync / PlanAsync / ImportAsync`, `SettingsImportException`, `KeyringGuard.Blocked(IKeyringHealth)` (`Endpoints/KeyringGuard.cs`).
- Produces: `GET /api/settings/export?includeSecrets=`, `POST /api/settings/import/preview`, `POST /api/settings/import`.

- [ ] **Step 1: Write the failing endpoint tests**

`backend/tests/AzureStorageBackup.Api.Tests/SettingsTransferEndpointsTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

public class SettingsTransferEndpointsTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();
    private IKeyringHealth Keyring => factory.Services.GetRequiredService<IKeyringHealth>();

    private static AccountRequest Request(string name, string endpoint) =>
        new(name, null, endpoint, AzureRegion.Global, "key==", false, ProxyMode.Independent, null, null, null, null);

    [Fact]
    public async Task Export_Is_A_Named_Json_Download_And_Omits_Secrets_By_Default()
    {
        await TestAccounts.EnsureAsync(_client, Request("export-a", "https://export-a.blob.core.windows.net"));

        var res = await _client.GetAsync("/api/settings/export");

        res.EnsureSuccessStatusCode();
        Assert.Equal("application/json", res.Content.Headers.ContentType!.MediaType);
        var disposition = res.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition!.DispositionType);
        Assert.Matches(@"^""?asb-settings-\d{8}-\d{4}\.json""?$", disposition.FileName);
        var doc = await res.Content.ReadFromJsonAsync<SettingsDocument>();
        Assert.False(doc!.IncludesSecrets);
        var entry = doc.Accounts!.Single(a => a.Name == "export-a");
        Assert.Null(entry.AccountKey);
    }

    [Fact]
    public async Task Export_With_Secrets_Carries_The_Key_And_Is_Refused_When_The_Keyring_Is_Lost()
    {
        await TestAccounts.EnsureAsync(_client, Request("export-b", "https://export-b.blob.core.windows.net"));

        var doc = await _client.GetFromJsonAsync<SettingsDocument>("/api/settings/export?includeSecrets=true");
        Assert.True(doc!.IncludesSecrets);
        Assert.Equal("key==", doc.Accounts!.Single(a => a.Name == "export-b").AccountKey);

        Keyring.Set(KeyringStatus.Lost);
        try
        {
            var res = await _client.GetAsync("/api/settings/export?includeSecrets=true");
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<KeyringLostError>();
            Assert.Equal("keyring_lost", body!.code);

            var plain = await _client.GetAsync("/api/settings/export");
            Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        }
        finally
        {
            Keyring.Set(KeyringStatus.Healthy);
        }
    }

    [Fact]
    public async Task Preview_Then_Import_Round_Trips_And_Keeps_The_Matched_Account_Id()
    {
        var id = await TestAccounts.EnsureAsync(_client, Request("rt", "https://rt.blob.core.windows.net"));
        var doc = await _client.GetFromJsonAsync<SettingsDocument>("/api/settings/export");
        var edited = doc! with
        {
            Accounts = [.. doc.Accounts!.Select(a => a.Name == "rt" ? a with { Name = "rt-renamed" } : a),
                new SettingsAccountEntry("rt-new", null, "https://rt-new.blob.core.windows.net", AzureRegion.Global,
                    "new-key==", false, ProxyMode.Independent, null, null, null, null)],
        };

        var preview = await (await _client.PostAsJsonAsync("/api/settings/import/preview", edited)).Content.ReadFromJsonAsync<ImportPlan>();
        Assert.Contains(preview!.Accounts, a => a.Name == "rt-renamed" && a.Action == "update");
        Assert.Contains(preview.Accounts, a => a.Name == "rt-new" && a.Action == "create" && !a.NeedsAccountKey);

        var applied = await _client.PostAsJsonAsync("/api/settings/import", edited);
        applied.EnsureSuccessStatusCode();

        var accounts = await _client.GetFromJsonAsync<List<AccountResponse>>("/api/accounts");
        Assert.Equal("rt-renamed", accounts!.Single(a => a.Id == id).Name);
        Assert.Contains(accounts, a => a.Name == "rt-new");
    }

    [Fact]
    public async Task Import_Rejects_A_Bad_File_With_400_And_A_Message()
    {
        var res = await _client.PostAsJsonAsync("/api/settings/import", new { format = "nope", version = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Contains("azure-storage-backup-settings", body!["error"]);
    }

    [Fact]
    public async Task Import_Rejects_Non_Object_Body_With_400()
    {
        var res = await _client.PostAsync("/api/settings/import/preview",
            new StringContent("[]", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd backend && dotnet test --filter FullyQualifiedName~SettingsTransferEndpointsTests`
Expected: 5 failed (404s; `SettingsTransfer` is not registered either).

- [ ] **Step 3: Register the service and map the endpoints**

In `Program.cs`, directly after `builder.Services.AddScoped<IGlobalSettingsService, GlobalSettingsService>();` add:

```csharp
builder.Services.AddScoped<SettingsTransfer>();
```

In `SettingsEndpoints.MapSettingsEndpoints`, before `return app;`:

```csharp
        // Settings → About: the whole Settings area as one file. Secrets only on request, and then only while the
        // keyring can read them — an export that said includesSecrets: true with every key null would be a lie.
        group.MapGet("/export", async (bool? includeSecrets, SettingsTransfer transfer, IKeyringHealth keyring,
            HttpContext http, CancellationToken ct) =>
        {
            var withSecrets = includeSecrets ?? false;
            if (withSecrets && KeyringGuard.Blocked(keyring) is { } blocked)
                return blocked;

            var doc = await transfer.ExportAsync(withSecrets, ct);
            var name = $"asb-settings-{doc.ExportedAt:yyyyMMdd-HHmm}.json";
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"{name}\"";
            return Results.Json(doc);
        });

        group.MapPost("/import/preview", async (SettingsDocument doc, SettingsTransfer transfer, CancellationToken ct) =>
        {
            try { return Results.Ok(await transfer.PlanAsync(doc, ct)); }
            catch (SettingsImportException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapPost("/import", async (SettingsDocument doc, SettingsTransfer transfer, CancellationToken ct) =>
        {
            try { return Results.Ok(await transfer.ImportAsync(doc, ct)); }
            catch (SettingsImportException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
```

`using AzureStorageBackup.Api.Services;` is already at the top of that file, and `KeyringGuard` is in the same namespace, so no new usings are needed.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~SettingsTransfer"`
Expected: all passed (16 service + 5 endpoint).

If `Import_Rejects_Non_Object_Body_With_400` returns 500 rather than 400, the minimal-API JSON binder's `BadHttpRequestException` is not being mapped; check `Program.cs` for an exception handler that swallows it and add `catch (BadHttpRequestException)` → 400 there, next to whatever maps other binder failures.

- [ ] **Step 5: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Endpoints/SettingsEndpoints.cs backend/src/AzureStorageBackup.Api/Program.cs backend/tests/AzureStorageBackup.Api.Tests/SettingsTransferEndpointsTests.cs
git commit -m "feat: settings export, import preview and import endpoints

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: Frontend API client and pure logic

**Files:**
- Modify: `frontend/src/api/settings.ts`
- Create: `frontend/src/lib/settingsTransfer.ts`
- Test: `frontend/src/lib/settingsTransfer.test.ts`

**Interfaces:**
- Consumes: `api` (`frontend/src/api/client.ts`), `NotificationConfig` (`api/notifications.ts`).
- Produces (api/settings.ts): `SettingsAccountEntry`, `SettingsDocument`, `ImportPlan`, `ImportPlanAccount`, `settingsApi.export(includeSecrets): Promise<SettingsDocument>`, `settingsApi.previewImport(doc): Promise<ImportPlan>`, `settingsApi.import(doc): Promise<ImportPlan>`.
- Produces (lib/settingsTransfer.ts): `exportFileName(now: Date): string`, `KeyPrompt`, `keyPrompts(doc, plan): KeyPrompt[]`, `EnteredKeys = Record<string, { accountKey: string; proxyPassword: string }>`, `withKeys(doc, keys): SettingsDocument`, `importSummary(plan): string`, `sectionLabel(present: boolean): string`.

- [ ] **Step 1: Extend the API client**

Append to `frontend/src/api/settings.ts` (add `import type { NotificationConfig } from './notifications'` at the top):

```ts
/** One account in a settings file: the AccountInput fields, secrets plaintext or null. */
export interface SettingsAccountEntry {
  name: string
  description: string | null
  blobEndpoint: string
  region: number
  accountKey: string | null
  useProxy: boolean
  proxyMode: number
  proxyHost: string | null
  proxyPort: number | null
  proxyUsername: string | null
  proxyPassword: string | null
}

/** The settings file. Every section is optional on import; a missing one leaves that part of the server alone. */
export interface SettingsDocument {
  format: string
  version: number
  exportedAt?: string | null
  includesSecrets?: boolean
  accounts?: SettingsAccountEntry[] | null
  backupDefaults?: BackupDefaultsSettings | null
  performance?: PerformanceSettings | null
  notifications?: NotificationConfig | null
}

export interface ImportPlanAccount {
  name: string
  blobEndpoint: string
  action: 'create' | 'update'
  /** A create whose entry has no key: the user must type one before the import can run. */
  needsAccountKey: boolean
}

export interface ImportPlan {
  accounts: ImportPlanAccount[]
  backupDefaults: boolean
  performance: boolean
  notifications: boolean
}
```

and extend `settingsApi`:

```ts
  export: (includeSecrets: boolean) =>
    api.get<SettingsDocument>(`/settings/export?includeSecrets=${includeSecrets}`),
  previewImport: (doc: SettingsDocument) => api.post<ImportPlan>('/settings/import/preview', doc),
  import: (doc: SettingsDocument) => api.post<ImportPlan>('/settings/import', doc),
```

- [ ] **Step 2: Write the failing lib tests**

`frontend/src/lib/settingsTransfer.test.ts`:

```ts
import { describe, expect, test } from 'vitest'

import type { ImportPlan, SettingsAccountEntry, SettingsDocument } from '../api/settings'
import { exportFileName, importSummary, keyPrompts, sectionLabel, withKeys } from './settingsTransfer'

const entry = (name: string, blobEndpoint: string, extra: Partial<SettingsAccountEntry> = {}): SettingsAccountEntry => ({
  name,
  description: null,
  blobEndpoint,
  region: 0,
  accountKey: null,
  useProxy: false,
  proxyMode: 0,
  proxyHost: null,
  proxyPort: null,
  proxyUsername: null,
  proxyPassword: null,
  ...extra,
})

const doc: SettingsDocument = {
  format: 'azure-storage-backup-settings',
  version: 1,
  accounts: [
    entry('kept', 'https://kept.blob.core.windows.net'),
    entry('new-plain', 'https://np.blob.core.windows.net'),
    entry('new-proxied', 'https://npx.blob.core.windows.net', { useProxy: true, proxyUsername: 'pu' }),
    entry('new-with-key', 'https://nk.blob.core.windows.net', { accountKey: 'k' }),
  ],
}

const plan: ImportPlan = {
  accounts: [
    { name: 'kept', blobEndpoint: 'https://kept.blob.core.windows.net', action: 'update', needsAccountKey: false },
    { name: 'new-plain', blobEndpoint: 'https://np.blob.core.windows.net', action: 'create', needsAccountKey: true },
    { name: 'new-proxied', blobEndpoint: 'https://npx.blob.core.windows.net', action: 'create', needsAccountKey: true },
    { name: 'new-with-key', blobEndpoint: 'https://nk.blob.core.windows.net', action: 'create', needsAccountKey: false },
  ],
  backupDefaults: true,
  performance: false,
  notifications: true,
}

describe('exportFileName', () => {
  test('is asb-settings-YYYYMMDD-HHMM.json in local time', () => {
    expect(exportFileName(new Date(2026, 9, 6, 9, 5))).toBe('asb-settings-20261006-0905.json')
  })
})

describe('keyPrompts', () => {
  test('lists only the creates that need a key, asking for a proxy password only where a proxy user is set', () => {
    expect(keyPrompts(doc, plan)).toEqual([
      { blobEndpoint: 'https://np.blob.core.windows.net', name: 'new-plain', askProxyPassword: false },
      { blobEndpoint: 'https://npx.blob.core.windows.net', name: 'new-proxied', askProxyPassword: true },
    ])
  })
})

describe('withKeys', () => {
  test('writes the typed keys into the matching entries and leaves every other entry untouched', () => {
    const out = withKeys(doc, {
      'https://np.blob.core.windows.net': { accountKey: 'np-key', proxyPassword: '' },
      'https://npx.blob.core.windows.net': { accountKey: 'npx-key', proxyPassword: 'pp' },
    })
    const by = Object.fromEntries(out.accounts!.map((a) => [a.name, a]))
    expect(by['new-plain']).toMatchObject({ accountKey: 'np-key', proxyPassword: null })
    expect(by['new-proxied']).toMatchObject({ accountKey: 'npx-key', proxyPassword: 'pp' })
    expect(by['kept']).toMatchObject({ accountKey: null })
    expect(by['new-with-key']).toMatchObject({ accountKey: 'k' })
    // The input is not mutated.
    expect(doc.accounts![1].accountKey).toBeNull()
  })
})

describe('importSummary', () => {
  test('counts creates and updates and names the sections applied', () => {
    expect(importSummary(plan)).toBe(
      '3 accounts created, 1 updated. Applied: backup defaults, notifications. Not in file: performance.',
    )
  })

  test('reads naturally with nothing to do', () => {
    expect(importSummary({ accounts: [], backupDefaults: false, performance: false, notifications: false })).toBe(
      'No accounts in file. Nothing applied; the file carried no settings sections.',
    )
  })

  test('singular forms', () => {
    expect(importSummary({
      accounts: [{ name: 'a', blobEndpoint: 'e', action: 'create', needsAccountKey: false }],
      backupDefaults: true, performance: true, notifications: true,
    })).toBe('1 account created, 0 updated. Applied: backup defaults, performance, notifications.')
  })
})

describe('sectionLabel', () => {
  test('tells overwrite from absent', () => {
    expect(sectionLabel(true)).toBe('will be overwritten')
    expect(sectionLabel(false)).toBe('not in file')
  })
})
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `cd frontend && npx vitest run src/lib/settingsTransfer.test.ts`
Expected: FAIL — module `./settingsTransfer` not found.

- [ ] **Step 4: Write the lib module**

`frontend/src/lib/settingsTransfer.ts`:

```ts
import type { ImportPlan, SettingsAccountEntry, SettingsDocument } from '../api/settings'

/** asb-settings-YYYYMMDD-HHMM.json, local time — the moment the user clicked, as they would read it. */
export function exportFileName(now: Date): string {
  const p = (n: number, w = 2) => String(n).padStart(w, '0')
  return `asb-settings-${now.getFullYear()}${p(now.getMonth() + 1)}${p(now.getDate())}-${p(now.getHours())}${p(now.getMinutes())}.json`
}

/** One account the user has to type a key for before the import can run. */
export interface KeyPrompt {
  blobEndpoint: string
  name: string
  /** The entry uses a proxy with a username, so a proxy password may be wanted too (optional). */
  askProxyPassword: boolean
}

export function keyPrompts(doc: SettingsDocument, plan: ImportPlan): KeyPrompt[] {
  const entries = new Map((doc.accounts ?? []).map((a) => [a.blobEndpoint, a]))
  return plan.accounts
    .filter((a) => a.needsAccountKey)
    .map((a) => {
      const e = entries.get(a.blobEndpoint)
      return { blobEndpoint: a.blobEndpoint, name: a.name, askProxyPassword: !!(e?.useProxy && e.proxyUsername) }
    })
}

export type EnteredKeys = Record<string, { accountKey: string; proxyPassword: string }>

/** A copy of the document with the typed keys written into the matching entries. Empty strings become null. */
export function withKeys(doc: SettingsDocument, keys: EnteredKeys): SettingsDocument {
  const accounts = (doc.accounts ?? []).map((a): SettingsAccountEntry => {
    const k = keys[a.blobEndpoint]
    if (!k) return a
    return {
      ...a,
      accountKey: k.accountKey || a.accountKey,
      proxyPassword: k.proxyPassword || a.proxyPassword || null,
    }
  })
  return { ...doc, accounts }
}

export function sectionLabel(present: boolean): string {
  return present ? 'will be overwritten' : 'not in file'
}

const sectionNames: { key: keyof Omit<ImportPlan, 'accounts'>; label: string }[] = [
  { key: 'backupDefaults', label: 'backup defaults' },
  { key: 'performance', label: 'performance' },
  { key: 'notifications', label: 'notifications' },
]

/** The one-line result shown after an import. */
export function importSummary(plan: ImportPlan): string {
  const created = plan.accounts.filter((a) => a.action === 'create').length
  const updated = plan.accounts.length - created
  const accounts =
    plan.accounts.length === 0
      ? 'No accounts in file.'
      : `${created} ${created === 1 ? 'account' : 'accounts'} created, ${updated} updated.`

  const applied = sectionNames.filter((s) => plan[s.key]).map((s) => s.label)
  const absent = sectionNames.filter((s) => !plan[s.key]).map((s) => s.label)
  const sections =
    applied.length === 0
      ? 'Nothing applied; the file carried no settings sections.'
      : `Applied: ${applied.join(', ')}.${absent.length ? ` Not in file: ${absent.join(', ')}.` : ''}`

  return `${accounts} ${sections}`
}
```

- [ ] **Step 5: Run the tests, lint and typecheck**

Run: `cd frontend && npx vitest run src/lib/settingsTransfer.test.ts && npm run lint && npx tsc -b`
Expected: 7 passed, no lint errors, no type errors.

- [ ] **Step 6: Commit**

```bash
git add frontend/src/api/settings.ts frontend/src/lib/settingsTransfer.ts frontend/src/lib/settingsTransfer.test.ts
git commit -m "feat: settings transfer API client and plan helpers

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: About page — export, import dialog, reload after import

**Files:**
- Create: `frontend/src/components/ImportSettingsDialog.tsx`
- Modify: `frontend/src/pages/AboutPage.tsx`
- Modify: `frontend/src/pages/SettingsPage.tsx` (`useSettingsHalf`, `SettingsPage`, the `AboutSection` call)

**Interfaces:**
- Consumes: `settingsApi.export / previewImport / import`, `keyPrompts`, `withKeys`, `importSummary`, `sectionLabel`, `exportFileName`, `Modal`, `Field`.
- Produces: `AboutSection` gains `onImported?: () => void`; `ImportSettingsDialog({ doc, plan, onClose, onImported })`.

- [ ] **Step 1: Write the dialog**

`frontend/src/components/ImportSettingsDialog.tsx`:

```tsx
import { useState } from 'react'
import { settingsApi, type ImportPlan, type SettingsDocument } from '../api/settings'
import { importSummary, keyPrompts, sectionLabel, withKeys, type EnteredKeys } from '../lib/settingsTransfer'
import { Field } from './Field'
import { Modal } from './Modal'

/// The import's "look before you leap" step. The server has already said what the file would do (the plan); this
/// shows it, collects a key for every new account the file has none for, and only then applies. Keys are typed
/// here rather than on the Accounts page afterwards because an account without a key is not flagged anywhere —
/// it looks normal and fails on first use.
export function ImportSettingsDialog({
  doc,
  plan,
  onClose,
  onImported,
}: {
  doc: SettingsDocument
  plan: ImportPlan
  onClose: () => void
  onImported: () => void
}) {
  const prompts = keyPrompts(doc, plan)
  const [keys, setKeys] = useState<EnteredKeys>(() =>
    Object.fromEntries(prompts.map((p) => [p.blobEndpoint, { accountKey: '', proxyPassword: '' }])),
  )
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [result, setResult] = useState<ImportPlan | null>(null)

  const ready = prompts.every((p) => keys[p.blobEndpoint]?.accountKey)

  const setKey = (endpoint: string, field: 'accountKey' | 'proxyPassword', value: string) =>
    setKeys((cur) => ({ ...cur, [endpoint]: { ...cur[endpoint], [field]: value } }))

  const run = async () => {
    setBusy(true)
    setError(null)
    try {
      const applied = await settingsApi.import(withKeys(doc, keys))
      setResult(applied)
      onImported()
    } catch (e) {
      // The document and the typed keys stay: fix the one thing that failed and press Import again.
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  if (result) {
    return (
      <Modal
        title="Settings imported"
        onClose={onClose}
        footer={
          <button type="button" className="btn-primary" onClick={onClose}>
            Close
          </button>
        }
      >
        <p>{importSummary(result)}</p>
      </Modal>
    )
  }

  return (
    <Modal
      title="Import settings"
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn-primary" onClick={run} disabled={busy || !ready}>
            {busy ? 'Importing…' : 'Import'}
          </button>
          <button type="button" onClick={onClose} disabled={busy}>
            Cancel
          </button>
        </>
      }
    >
      {error && <p className="text-danger">{error}</p>}

      <h3>Accounts</h3>
      {plan.accounts.length === 0 ? (
        <p className="text-muted">None in file.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Name</th>
              <th>Endpoint</th>
              <th>Action</th>
            </tr>
          </thead>
          <tbody>
            {plan.accounts.map((a) => (
              <tr key={a.blobEndpoint}>
                <td>{a.name}</td>
                <td className="mono">{a.blobEndpoint}</td>
                <td>{a.action === 'create' ? 'Create' : 'Update'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <p className="text-muted">
        Accounts are matched by endpoint. Matched accounts are updated in place, so the backups that use them
        are unaffected; accounts on this server that are not in the file are left alone.
      </p>

      <h3>Settings</h3>
      <ul>
        <li>Backup defaults: {sectionLabel(plan.backupDefaults)}</li>
        <li>Performance: {sectionLabel(plan.performance)}</li>
        <li>Notifications: {sectionLabel(plan.notifications)}</li>
      </ul>

      {prompts.length > 0 && (
        <>
          <h3>Keys needed</h3>
          <p className="text-muted">
            These accounts are new here and the file carries no key for them. Enter each key to create the
            account; a matched account keeps the key it already has.
          </p>
          {prompts.map((p) => (
            <div key={p.blobEndpoint}>
              <Field label={`${p.name} — account key`}>
                <input
                  className="w-lg"
                  type="password"
                  autoComplete="off"
                  value={keys[p.blobEndpoint]?.accountKey ?? ''}
                  onChange={(e) => setKey(p.blobEndpoint, 'accountKey', e.target.value)}
                />
              </Field>
              {p.askProxyPassword && (
                <Field label={`${p.name} — proxy password (optional)`}>
                  <input
                    className="w-lg"
                    type="password"
                    autoComplete="off"
                    value={keys[p.blobEndpoint]?.proxyPassword ?? ''}
                    onChange={(e) => setKey(p.blobEndpoint, 'proxyPassword', e.target.value)}
                  />
                </Field>
              )}
            </div>
          ))}
        </>
      )}
    </Modal>
  )
}
```

- [ ] **Step 2: Add reload-after-import to SettingsPage**

In `frontend/src/pages/SettingsPage.tsx`:

Change the `useSettingsHalf` signature and its effect:

```ts
/// Load, edit and save one half of the settings — the resource behind one page. `reload` is a counter: bumping it
/// refetches, which is how an import on the About page gets its new values onto these two pages.
function useSettingsHalf<T>(get: () => Promise<T>, update: (s: T) => Promise<T>, reload: number) {
  ...
  useEffect(() => {
    get()
      .then(setS)
      .catch((e) => setError(e instanceof Error ? e.message : String(e)))
  }, [get, reload])
```

In `SettingsPage`, add state and pass it through:

```ts
  const [reload, setReload] = useState(0)
  const defaults = useSettingsHalf(settingsApi.getDefaults, settingsApi.updateDefaults, reload)
  const performance = useSettingsHalf(settingsApi.getPerformance, settingsApi.updatePerformance, reload)
```

and change the About line to:

```tsx
      {tab === 'about' && (
        <AboutSection authRequired={authRequired} onLogout={onLogout} onImported={() => setReload((n) => n + 1)} />
      )}
```

- [ ] **Step 3: Add the Export / Import section to AboutPage**

Replace `frontend/src/pages/AboutPage.tsx` with:

```tsx
import { useEffect, useRef, useState } from 'react'
import { settingsApi, type ImportPlan, type SettingsDocument } from '../api/settings'
import { systemApi } from '../api/system'
import { ImportSettingsDialog } from '../components/ImportSettingsDialog'
import { exportFileName } from '../lib/settingsTransfer'

/// What this install *is*, and the one action that ends a session with it.
///
/// The version and the temp-directory map used to sit at the bottom of the Logs page, under everything the log table
/// scrolls through. They are not log data: nobody filters them, they never change while you watch, and the reason to
/// look them up — quoting a version in a bug report, or finding out which paths a docker volume has to cover — has
/// nothing to do with reading logs. Behind Logs' filter toolbar they were also the last thing on the longest page in
/// the app.
///
/// Log out shares this page rather than a fifth one of its own: it is the same category of thing — about this
/// installation and this session, not about any backup.
///
/// Settings export/import lives here too: it is about the installation as a whole, not any one settings page.
export function AboutSection({
  authRequired,
  onLogout,
  onImported,
}: {
  authRequired?: boolean
  onLogout?: () => void
  /** Called after a successful import, so the settings pages above refetch what the file changed. */
  onImported?: () => void
}) {
  const [paths, setPaths] = useState<Record<string, string>>({})
  const [version, setVersion] = useState('')

  // Failures are swallowed on purpose, as they were on the Logs page: neither figure is worth an error banner, and a
  // dead /system endpoint is already going to announce itself everywhere else.
  useEffect(() => {
    systemApi.paths().then(setPaths).catch(() => {})
    systemApi.version().then((v) => setVersion(v.version)).catch(() => {})
  }, [])

  return (
    <>
      <h2>System</h2>
      <p className="text-muted">Version: {version || '…'}</p>
      <p className="text-muted">Temp directories (map these as docker volumes):</p>
      <ul className="mono text-faint">
        {Object.entries(paths).map(([k, v]) => (
          <li key={k}>
            {k}: {v}
          </li>
        ))}
      </ul>

      <SettingsTransfer onImported={onImported} />

      {/* Log out is not in the sidebar: the phone tier's bottom bar has four slots and no room for a fifth. Desktop
          moved with it — one function in two places is what later maintenance forgets to sync. */}
      {authRequired && onLogout && (
        <>
          <h2>Session</h2>
          <button type="button" onClick={onLogout}>
            Log out
          </button>
        </>
      )}
    </>
  )
}

/// Export and import of everything under Settings as one JSON file.
///
/// The export goes through fetch and a Blob rather than an `<a href>` to the endpoint: a 409 (keyring lost, keys
/// requested) then shows up as a message next to the button, where an anchor would have navigated to a JSON error page.
function SettingsTransfer({ onImported }: { onImported?: () => void }) {
  const [includeSecrets, setIncludeSecrets] = useState(false)
  const [exporting, setExporting] = useState(false)
  const [exportError, setExportError] = useState<string | null>(null)

  const fileInput = useRef<HTMLInputElement>(null)
  const [reading, setReading] = useState(false)
  const [importError, setImportError] = useState<string | null>(null)
  const [pending, setPending] = useState<{ doc: SettingsDocument; plan: ImportPlan } | null>(null)

  const exportSettings = async () => {
    setExporting(true)
    setExportError(null)
    try {
      const doc = await settingsApi.export(includeSecrets)
      const blob = new Blob([JSON.stringify(doc, null, 2)], { type: 'application/json' })
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url
      a.download = exportFileName(new Date())
      a.click()
      URL.revokeObjectURL(url)
    } catch (e) {
      setExportError(e instanceof Error ? e.message : String(e))
    } finally {
      setExporting(false)
    }
  }

  const pickFile = async (file: File | undefined) => {
    // Clear the input first: a file input does not fire onChange for the same file twice, and "cancel the dialog,
    // pick the same file again" is a normal thing to do.
    if (fileInput.current) fileInput.current.value = ''
    if (!file) return
    setReading(true)
    setImportError(null)
    try {
      let doc: SettingsDocument
      try {
        doc = JSON.parse(await file.text()) as SettingsDocument
      } catch {
        throw new Error(`${file.name} is not valid JSON.`)
      }
      const plan = await settingsApi.previewImport(doc)
      setPending({ doc, plan })
    } catch (e) {
      setImportError(e instanceof Error ? e.message : String(e))
    } finally {
      setReading(false)
    }
  }

  return (
    <>
      <h2>Export / Import</h2>
      <p className="text-muted">
        Everything under Settings — accounts, backup defaults, performance, notifications — as one JSON file.
        Backups, groups and schedules are not included.
      </p>

      <div className="row" style={{ flexWrap: 'wrap' }}>
        <button type="button" onClick={exportSettings} disabled={exporting}>
          {exporting ? 'Exporting…' : 'Export settings'}
        </button>
        <label>
          <input type="checkbox" checked={includeSecrets} onChange={(e) => setIncludeSecrets(e.target.checked)} />{' '}
          Include account keys and proxy passwords
        </label>
      </div>
      {includeSecrets && (
        <p className="text-warn">
          The file will contain your account keys in plain text. Keep it where you would keep the keys themselves.
        </p>
      )}
      {exportError && <p className="text-danger">{exportError}</p>}

      <div className="row" style={{ marginTop: 'var(--sp-3)' }}>
        <input
          ref={fileInput}
          type="file"
          accept=".json,application/json"
          style={{ display: 'none' }}
          onChange={(e) => void pickFile(e.target.files?.[0])}
        />
        <button type="button" onClick={() => fileInput.current?.click()} disabled={reading}>
          {reading ? 'Reading…' : 'Import settings…'}
        </button>
      </div>
      <p className="text-muted">
        You will see what the file would change before anything is written. Accounts are matched by endpoint; a
        new account whose key is not in the file asks for it.
      </p>
      {importError && <p className="text-danger">{importError}</p>}

      {pending && (
        <ImportSettingsDialog
          doc={pending.doc}
          plan={pending.plan}
          onClose={() => setPending(null)}
          onImported={() => onImported?.()}
        />
      )}
    </>
  )
}
```

If `var(--sp-3)` is not a token in `index.css` (check with `grep -n "\-\-sp-" frontend/src/index.css | head`), use the nearest existing spacing token.

- [ ] **Step 4: Lint, typecheck, test, build**

Run: `cd frontend && npm run lint && npx tsc -b && npm test && npm run build`
Expected: clean.

- [ ] **Step 5: Run the backend for a hand check**

Run (two terminals, or background the first):

```bash
cd backend/src/AzureStorageBackup.Api && dotnet run
cd frontend && npm run dev
```

Open the app, Settings → About:
1. Export with the box unticked → a file `asb-settings-…json` downloads; open it, `accountKey` is `null`.
2. Tick the box → warning line appears; export → `accountKey` is plaintext.
3. Edit the unticked file: rename an account, add a new account entry with a different endpoint and `"accountKey": null`. Import it → the dialog shows Update + Create, a key box for the new one, Import disabled until the box is filled. Fill it → "Settings imported" with the summary line. Settings → Accounts shows both; Backup defaults page shows the file's values without a reload.
4. Cancel the dialog and pick the same file again → the dialog opens again (Review Focus 5).
5. Import the file a second time → every account is "Update", no key boxes.

Stop both servers. Delete `backend/src/AzureStorageBackup.Api/data/app.db*` only if it was created by this hand check (check `git status` — `data/` should be untracked/ignored; leave anything that pre-existed).

- [ ] **Step 6: Commit**

```bash
git add frontend/src/components/ImportSettingsDialog.tsx frontend/src/pages/AboutPage.tsx frontend/src/pages/SettingsPage.tsx
git commit -m "feat: export and import settings from Settings → About

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: Documentation, fold the spec, full test run

**Files:**
- Modify: `docs/operations.md` (new section after "Settings storage", before "Environment variables")
- Modify: `docs/web-ui.md` (after the `Log out` paragraph, line ~92)
- Delete: `docs/superpowers/` (spec and this plan)

- [ ] **Step 1: Write the operations section**

Insert into `docs/operations.md` between "## Settings storage" and "## Environment variables":

```markdown
## Settings export and import

Settings → About exports everything under Settings — accounts, backup defaults, performance, notifications — as
one JSON file, and imports such a file. Backups, groups and schedules are not in it: they reference accounts by
id and have their own pages.

The file (`format: "azure-storage-backup-settings"`, `version: 1`) carries no ids and no `createdAt`. Each
section is the same shape as its GET endpoint (`/api/settings/defaults`, `/api/settings/performance`,
`/api/notifications`); accounts are the `AccountRequest` shape with `accountKey` / `proxyPassword` plaintext or
null. Every section is optional on import — a hand-trimmed file carrying only `performance` changes only that.

**Accounts are matched by endpoint**, not by name and not by id: the endpoint with its trailing `/` dropped and
lower-cased, the same key `AccountService` uses to refuse a second account on one endpoint. A match is updated in
place, so the backups, groups and schedules that reference it by id are unaffected. No match creates the account.
Accounts on the server that the file does not mention are left alone — an import never deletes.

> **Rationale.** Ids are meaningless across installs and names are what people rename. The endpoint is the one
> thing that identifies a storage account to Azure itself, and the server already treats it as unique.

**Secrets** leave only on request: the export has an "Include account keys and proxy passwords" box, off by
default, and with it on the file holds them in plain text. With the keyring lost that export is refused with the
usual `keyring_lost` 409 rather than silently writing nulls. On import a non-empty key replaces the stored one; an
empty key on a matched account keeps what is stored (including an unreadable ciphertext while the keyring is
lost); an empty key on a **new** account is the one case the server refuses, because a keyless account is not
flagged anywhere — it looks normal and fails on first use. The UI runs `POST /api/settings/import/preview` first
and asks for each such key in the dialog, so a normal import never hits that refusal.

The endpoints: `GET /api/settings/export?includeSecrets=` (a named download), `POST /api/settings/import/preview`
(the plan, no writes), `POST /api/settings/import` (same body, applies). Import is one SQLite transaction under
the account topology gate; any failure leaves the database as it was.
```

- [ ] **Step 2: Note the About page entry in web-ui.md**

After the `Log out` rationale block (the paragraph starting `> **Rationale.** Four bottom-bar slots…`), add:

```markdown
**Export / Import also lives on About.** One file for all four settings pages; the import shows a preview dialog
(what is created, what is updated, which sections are overwritten) and collects a key for every new account the
file has none for before anything is written. After an import the Backup defaults and Performance pages refetch
(a reload counter passed into `useSettingsHalf`); Accounts and Notifications fetch on mount anyway. The rules
are in operations.md § Settings export and import.
```

- [ ] **Step 3: Delete the superpowers directory**

```bash
git rm -r docs/superpowers
```

In `docs/README.md` the table row for `operations.md` lists its topics; add "settings export and import" after "key ring loss and recovery" so the row reads `…key ring loss and recovery, settings export and import, 7-Zip settings…`.

- [ ] **Step 4: Full backend suite with Azurite, then frontend**

```bash
systemctl --user start azurite
cd backend && dotnet test
systemctl --user stop azurite
rm -rf ~/.local/share/azurite/{AzuriteConfig,__azurite_db_*,__*storage__}
systemctl --user is-active azurite   # must say inactive
cd ../frontend && npm run lint && npm test && npm run build
```

Expected: backend 1299 + 21 new = 1320 passed, 0 skipped (a skip count above 0 means 7zz or Azurite is missing — see README § Tests); frontend clean.

- [ ] **Step 5: Commit**

```bash
git add docs/operations.md docs/web-ui.md docs/README.md
git commit -m "docs: settings export and import

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```
