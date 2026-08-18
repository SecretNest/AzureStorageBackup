using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Models;

/// <summary>Blob access tier. The index tier should be Hot/Cool/Cold; the data tier may include Archive (M4 §13).</summary>
public enum StorageTier
{
    Hot = 0,
    Cool = 1,
    Cold = 2,
    Archive = 3,
}

/// <summary>
/// The local config of one backup (the output of the PRD §11 new-backup wizard).
/// Records the device-local root path and settings; the encryption password is ciphertext both in the app layer and in the database, and decryption goes only through ISecretReader (design §3.1).
/// </summary>
public class BackupConfig
{
    public int Id { get; set; }

    public int AccountId { get; set; }
    public string ContainerName { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Local root path (device-local; re-specified when restoring on another device).</summary>
    public string LocalRoot { get; set; } = string.Empty;

    /// <summary>Ciphertext of the encryption password. Empty = not encrypted. Use ISecretReader.RevealBackupPassword to get the plaintext.</summary>
    public string? PasswordProtected { get; set; }

    // Tiers are locked after creation (BackupConfigService.UpdateAsync) and are therefore **not inheritable** —
    // inheriting means following the global setting, which is exactly a change after creation. On create, the frontend prefills them from the global defaults.
    public StorageTier IndexTier { get; set; } = StorageTier.Hot;
    public StorageTier DataTier { get; set; } = StorageTier.Archive;

    // The fields below: null = inherit the global setting (PRD §3 "use default"), non-null = this config's own override.
    // On the three rule fields, "" means "explicitly no rules", which is distinct from inheriting.
    // Rules (gitignore syntax, one per line)
    public string? IgnoreRules { get; set; }
    public string? DontCompressRules { get; set; }
    public string? DontGroupRules { get; set; }

    /// <summary>Matches are allowed to be packed across directories; null = use the global default.</summary>
    public string? CrossDirGroupRules { get; set; }

    // The case-insensitive half of each list above. Extensions are the reason these exist: `*.mp4` is meant as a
    // kind of file, and a camera writing `.MP4` or `.MOV` is the same kind — but a path is a path, and on Linux
    // `Temp/` and `temp/` really are two directories, so folding case for everything would be wrong. Splitting the
    // lists lets each rule say which it is, instead of the engine guessing from the shape of the pattern.
    // Concatenated after the sensitive half into a single rule set, so `!` still overrides across the pair.
    public string? IgnoreRulesCaseInsensitive { get; set; }
    public string? DontCompressRulesCaseInsensitive { get; set; }
    public string? DontGroupRulesCaseInsensitive { get; set; }
    public string? CrossDirGroupRulesCaseInsensitive { get; set; }

    /// <summary>
    /// Backup scope (design docs/configuration.md): one `+ path` / `- path` per line,
    /// decided by longest-prefix match. null/empty = **everything** under the root.
    /// <para>
    /// Note that null here means something **different** from null on the rule fields above: those mean "inherit the global default",
    /// this one means "include everything". Scope is this backup's own business, a global default would be meaningless, so it does not go into
    /// <see cref="ResolvedBackupSettings"/>.
    /// </para>
    /// </summary>
    public string? ScopeRules { get; set; }

    public bool? IncludeSymlinks { get; set; }

    // Version retention (§10)
    public int? MaxVersions { get; set; }
    public int? MaxAgeDays { get; set; }
    public RetentionMode? RetentionMode { get; set; }

    // Grouping (§6)
    public long? SingleFileThresholdBytes { get; set; }
    public long? GroupCapBytes { get; set; }

    // null = inherit; 0 = volumes explicitly off; >0 = volume size.
    // "Off" moved from null to 0 so that null means the same thing on every inheritable field (the Settings page already says 0=off).
    public long? VolumeBytes { get; set; }

    /// <summary>Whether to write debug-level logs (they include the names of the files being operated on, kept short-term for 14 days). Off by default.</summary>
    public bool? VerboseLogging { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // Persistent status (§4.2 decision 2): Normal/Error only. Transient states (backing up, restoring, …) are not persisted; they are derived when building the DTO.
    public BackupStatus Status { get; set; } = BackupStatus.Normal;
    public string? LastError { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
}
