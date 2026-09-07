using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// The one door every consumer walks through to get at a container's <see cref="VersionCatalog"/>: it knows how to
/// migrate a version into the catalog the first time something asks for it, from whichever of the three older
/// homes still holds it — today's <c>.idx</c> file, a pre-<c>.idx</c> row in <c>app.db</c>, or, failing both, the
/// cloud — so every reader of the catalog can assume the version is already there rather than repeating that
/// three-way fallback itself.
/// </summary>
public interface IVersionCatalogs
{
    /// <summary>Opens the container's catalog directly, once a caller already knows the version(s) it wants are in
    /// it (typically after <see cref="EnsureVersionAsync"/>, or for a read that tolerates a miss).</summary>
    Task<VersionCatalog> OpenAsync(int accountId, string container, bool readOnly, CancellationToken ct = default);

    /// <summary>Guarantees <paramref name="version"/> is in the catalog with the given identity: catalog hit → the
    /// version's <c>.idx</c> file → the legacy <c>CachedVersionIndexes</c> row → the cloud. Idempotent, and safe to
    /// call from multiple callers racing on the same (account, container, version) at once — only one of them pays
    /// for the migration; the rest find the row already there once they reach the write lock.</summary>
    Task EnsureVersionAsync(Account account, string container, BackupVersion version, long identityTicks, string? password, CancellationToken ct = default);

    /// <summary>Drops one version from the catalog and every older home it might still occupy (the retention policy
    /// retiring it).</summary>
    Task RemoveVersionAsync(int accountId, string container, int version, CancellationToken ct = default);

    /// <summary>Drops a whole container's catalog and every older home it might still occupy (its backup config
    /// being deleted).</summary>
    Task RemoveContainerAsync(int accountId, string container, CancellationToken ct = default);

    /// <summary>Reserves the container's single write slot; see <see cref="VersionCatalogStore.LockForWriteAsync"/>
    /// for what it protects and which calls must never be made while holding it.</summary>
    Task<IDisposable> LockForWriteAsync(int accountId, string container, CancellationToken ct = default);
}
