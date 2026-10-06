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
}
