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
