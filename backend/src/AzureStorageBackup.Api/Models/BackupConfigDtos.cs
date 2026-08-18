using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Models;

/// <summary>
/// Response body of a backup config. Deliberately carries no Password, only HasPassword (whether it is encrypted).
/// <c>Status</c>/<c>LastError</c>/<c>LastErrorAt</c> are persistent state (§4.2 decision 2);
/// <c>Activity</c> is a derived transient state (Idle/BackingUp/Restoring/Checking/Repairing), not persisted — the caller computes it as needed and passes it in.
/// </summary>
public record BackupConfigResponse(
    int Id,
    int AccountId,
    string ContainerName,
    string Name,
    string? Description,
    string LocalRoot,
    bool HasPassword,
    StorageTier IndexTier,
    StorageTier DataTier,
    string? IgnoreRules,
    string? DontCompressRules,
    string? DontGroupRules,
    bool? IncludeSymlinks,
    int? MaxVersions,
    int? MaxAgeDays,
    RetentionMode? RetentionMode,
    long? SingleFileThresholdBytes,
    long? GroupCapBytes,
    long? VolumeBytes,
    bool? VerboseLogging,
    DateTimeOffset CreatedAt,
    BackupStatus Status,
    string? LastError,
    DateTimeOffset? LastErrorAt,
    string Activity,
    bool SecretsUnavailable,
    ResolvedBackupSettings Effective,
    string? CrossDirGroupRules = null,
    string? ScopeRules = null,
    // The case-insensitive half of each list. Appended rather than paired next to its sibling so the positional
    // arguments already in use keep their meaning.
    string? IgnoreRulesCaseInsensitive = null,
    string? DontCompressRulesCaseInsensitive = null,
    string? DontGroupRulesCaseInsensitive = null,
    string? CrossDirGroupRulesCaseInsensitive = null)
{
    /// <summary>
    /// <paramref name="secretsUnavailable"/> must be passed according to whether this config's ciphertext can actually be
    /// decrypted (see <see cref="SecretAvailability"/>); do not pass the global Lost status — halfway through recovery,
    /// a backup that has already been reset successfully must stop showing "needs reset". A backup with no password has no ciphertext to lose, so it is always false.
    ///
    /// <paramref name="settings"/> is required rather than optional: the UI relies on Effective to show the current effective value of inherited fields,
    /// and missing it at a single call site makes it display GlobalSettings' compile-time defaults instead of the defaults the user actually configured. Making it required
    /// forces the compiler to point out every call site.
    /// </summary>
    public static BackupConfigResponse From(
        BackupConfig c, GlobalSettings? settings, string activity = "Idle", bool secretsUnavailable = false) => new(
        c.Id, c.AccountId, c.ContainerName, c.Name, c.Description, c.LocalRoot,
        !string.IsNullOrEmpty(c.PasswordProtected), c.IndexTier, c.DataTier,
        c.IgnoreRules, c.DontCompressRules, c.DontGroupRules, c.IncludeSymlinks,
        c.MaxVersions, c.MaxAgeDays, c.RetentionMode,
        c.SingleFileThresholdBytes, c.GroupCapBytes, c.VolumeBytes, c.VerboseLogging, c.CreatedAt,
        c.Status, c.LastError, c.LastErrorAt, activity,
        secretsUnavailable && !string.IsNullOrEmpty(c.PasswordProtected),
        ResolvedBackupSettings.From(c, settings), c.CrossDirGroupRules, c.ScopeRules,
        c.IgnoreRulesCaseInsensitive, c.DontCompressRulesCaseInsensitive,
        c.DontGroupRulesCaseInsensitive, c.CrossDirGroupRulesCaseInsensitive);
}

/// <summary>Restore request body. An empty TargetRoot means the config's local root; an empty Version means the latest version.
/// An empty SelectedPaths restores the whole version; non-empty restores exactly those paths (requirement B: a pack is downloaded once and only the selected members are written).
/// Conflict is the conflict mode (decision 3); RehydratePriority is the Archive rehydration priority.</summary>
public record RestoreRequestBody(
    string? TargetRoot,
    int? Version,
    Dictionary<string, int>? Substitutions = null,
    List<string>? SelectedPaths = null,
    RestoreConflictMode Conflict = RestoreConflictMode.OverwriteIfChanged,
    RestoreRehydratePriority RehydratePriority = RestoreRehydratePriority.Standard);

/// <summary>Restore-estimate request body (§4.1b, requirement A): estimated download/uncompressed volume for the selected paths. An empty Version means the latest version.</summary>
public record RestoreEstimateRequestBody(int? Version, List<string> Paths);

/// <summary>Request to import an existing backup: read the container's info file to rebuild the config (roadmap, PRD 1.5). An encrypted backup requires the password.</summary>
/// <param name="CheckAfterImport">Once the import finishes, verify the cloud data while we are at it (existence + size, no download).
/// Omitted means true: the import only guarantees that the **ledger** the cloud keeps has been fetched in full; whether the things written in that ledger are all still there
/// can only be answered by asking the cloud, and there is no reason to make the user click that separately.</param>
public record ImportRequest(int AccountId, string ContainerName, string? Password, bool? CheckAfterImport = null);

/// <summary>The result of an import: the config that was created, plus the two things the import itself discovered.</summary>
/// <param name="CheckStarted">The cloud verification is already running in the background, so the frontend can open the check panel directly
/// instead of making the user hunt for that button.</param>
/// <param name="UnreadableVersions">Version numbers whose file list could not be read. These versions can be neither restored nor checked;
/// the rest are unaffected, and the details are in the operation log.</param>
public record ImportResponse(
    BackupConfigResponse Config, bool CheckStarted, IReadOnlyList<int> UnreadableVersions);

/// <summary>Backup password reset request. It has to be the very password that encrypted the cloud archives — changing the password is not supported (design decisions 6 and 8).</summary>
public record ResetBackupPasswordRequest(string Password);

/// <summary>Create/update request body for a backup config. On update, an empty Password means keep the existing value.
/// null on any of the 12 inheritable fields means "use the default" — it is stored as null and resolved at runtime through
/// <see cref="ResolvedBackupSettings"/>. IndexTier/DataTier are not inheritable and stay required.</summary>
public record BackupConfigRequest(
    int AccountId,
    string ContainerName,
    string Name,
    string? Description,
    string LocalRoot,
    string? Password,
    StorageTier IndexTier,
    StorageTier DataTier,
    string? IgnoreRules = null,
    string? DontCompressRules = null,
    string? DontGroupRules = null,
    bool? IncludeSymlinks = null,
    int? MaxVersions = null,
    int? MaxAgeDays = null,
    RetentionMode? RetentionMode = null,
    long? SingleFileThresholdBytes = null,
    long? GroupCapBytes = null,
    long? VolumeBytes = null,
    bool? VerboseLogging = null,
    string? CrossDirGroupRules = null,
    string? ScopeRules = null,
    string? IgnoreRulesCaseInsensitive = null,
    string? DontCompressRulesCaseInsensitive = null,
    string? DontGroupRulesCaseInsensitive = null,
    string? CrossDirGroupRulesCaseInsensitive = null)
{
    /// <summary>The Password in the request body is plaintext; it is encrypted the moment it lands on the entity (design §3.1: the entity holds ciphertext only).</summary>
    public BackupConfig ToConfig(IEncryptionService encryption) => new()
    {
        VolumeBytes = VolumeBytes,
        VerboseLogging = VerboseLogging,
        AccountId = AccountId,
        ContainerName = ContainerName,
        Name = Name,
        Description = Description,
        LocalRoot = LocalRoot,
        PasswordProtected = string.IsNullOrEmpty(Password) ? null : encryption.Encrypt(Password),
        IndexTier = IndexTier,
        DataTier = DataTier,
        IgnoreRules = IgnoreRules,
        DontCompressRules = DontCompressRules,
        DontGroupRules = DontGroupRules,
        CrossDirGroupRules = CrossDirGroupRules,
        ScopeRules = ScopeRules,
        IgnoreRulesCaseInsensitive = IgnoreRulesCaseInsensitive,
        DontCompressRulesCaseInsensitive = DontCompressRulesCaseInsensitive,
        DontGroupRulesCaseInsensitive = DontGroupRulesCaseInsensitive,
        CrossDirGroupRulesCaseInsensitive = CrossDirGroupRulesCaseInsensitive,
        IncludeSymlinks = IncludeSymlinks,
        MaxVersions = MaxVersions,
        MaxAgeDays = MaxAgeDays,
        RetentionMode = RetentionMode,
        SingleFileThresholdBytes = SingleFileThresholdBytes,
        GroupCapBytes = GroupCapBytes,
    };
}

/// <summary>The verdict of a local-root migration check (design docs/configuration.md).</summary>
public enum LocalRootVerdict
{
    /// <summary>Sampled match rate ≥95%: let it straight through.</summary>
    Ok = 0,

    /// <summary>Match rate falls in [5%, 95%): needs the user to confirm (Force).</summary>
    NeedsConfirm = 1,

    /// <summary>Match rate &lt;5% (including finding nothing at all): refused by default, but Force can still override it.</summary>
    Rejected = 2,

    /// <summary>No baseline to compare against (the current root is empty, or there are no versions at all); only the path itself was validated.</summary>
    NoBaseline = 3,

    /// <summary>This backup really does have historical versions, but its index cannot be read (corrupt info file, decryption failure, index blob read failure, and so on),
    /// so no comparison could be made — which is precisely the case that most deserves a second look, so it is treated as needing confirmation instead of being let straight through like NoBaseline.</summary>
    BaselineUnreadable = 4,
}

/// <summary>
/// The validation report for a local-root migration. <c>MtimeDiffers</c> is informational only and **plays no part in the verdict** — when moving across
/// file systems, mtime precision and preservation are frequently inconsistent, so using it as a criterion causes widespread false alarms, while the real
/// consequence of a mismatch is merely that the next backup re-uploads those files.
/// </summary>
/// <param name="Examples">At most 10 mismatching relative paths. This is not decoration: the user is on a NAS with no command line,
/// so the UI has to put "which files exactly do not match" right in front of them, otherwise a 68% match rate gives them nothing to judge whether to force it.</param>
public record LocalRootPreviewResponse(
    string Verdict,
    int Sampled,
    int Matched,
    int Missing,
    int SizeMismatch,
    int MtimeDiffers,
    double MatchRate,
    string? Reason,
    IReadOnlyList<string> Examples);

/// <summary>Local-root migration request. <c>Force</c> is used to get past NeedsConfirm / Rejected.</summary>
public record LocalRootChangeRequest(string NewRoot, bool Force = false);

/// <summary>Request body for the preview endpoint.</summary>
public record LocalRootPreviewRequest(string NewRoot);

/// <summary>
/// One run that stopped partway, for the UI to list while it waits for the user to decide.
/// </summary>
/// <param name="Blocks">The number of blocks the journal confirms are already in the cloud. Roughly what resuming would save.</param>
/// <param name="Resumable">
/// A preview of the cheap pre-checks: whether configId and the local root line up.
/// **Not a promise** — the baseline version and the encryption identity need the index and the password to verify, and that only happens when the run actually opens the journal (Task 10).
/// It is possible for this to be true and for the journal to still be discarded at open time, so the UI must not present it as "this will definitely resume".
/// </param>
public sealed record InterruptedRunResponse(
    string RunId, DateTimeOffset StartedAt, int Blocks, long JournalBytes, bool Resumable);
