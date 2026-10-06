using System.Text.Json.Nodes;

namespace AzureStorageBackup.Api.Models;

/// <summary>
/// The settings file: everything under Settings (accounts, backup defaults, performance, notifications) as one
/// JSON document. Carries no ids — accounts are matched on import by endpoint, so backups, groups and schedules
/// that reference an account by id keep working after an import onto a database that already has it.
/// Every section is optional on the way in: a missing one leaves that part of the database untouched.
/// <para>
/// The sections are JSON objects, not typed records, on purpose: a field missing from a section keeps its
/// current value on import. Typed binding would turn an absent number into 0 and an absent string into null —
/// a hand-trimmed file, or one from a build that did not yet have the field, would silently zero settings it never
/// mentioned. The merge is <see cref="Services.SettingsTransfer"/>'s job; the shapes are still those of
/// <see cref="BackupDefaultsSettings"/>, <see cref="PerformanceSettings"/> and <see cref="NotificationRequest"/>.
/// </para>
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
    public JsonObject? BackupDefaults { get; init; }
    public JsonObject? Performance { get; init; }
    public JsonObject? Notifications { get; init; }
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
