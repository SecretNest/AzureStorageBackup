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

    /// <summary>Stored accounts by their endpoint key; a file entry whose key is absent here is a create.</summary>
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
}
