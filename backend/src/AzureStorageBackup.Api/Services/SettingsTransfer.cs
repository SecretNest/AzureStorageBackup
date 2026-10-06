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
