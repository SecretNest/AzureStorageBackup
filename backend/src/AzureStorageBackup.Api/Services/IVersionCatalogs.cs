using AzureStorageBackup.Api.Models;
using Microsoft.Extensions.Logging;

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
    /// <summary>Opens the container's catalog read-only, once a caller already knows the version(s) it wants are in
    /// it (typically after <see cref="EnsureVersionAsync"/>, or for a read that tolerates a miss). Only
    /// <c>readOnly: true</c> is accepted — see <see cref="VersionCatalogStore.OpenAsync"/>.</summary>
    Task<VersionCatalog> OpenAsync(int accountId, string container, bool readOnly, CancellationToken ct = default);

    /// <summary>Opens the container's catalog for writing, with the container's write lock in hand; see
    /// <see cref="VersionCatalogStore.OpenForWriteAsync"/>.</summary>
    Task<VersionCatalog> OpenForWriteAsync(
        CatalogWriteLock held, int accountId, string container, CancellationToken ct = default);

    /// <summary>Guarantees <paramref name="version"/> is in the catalog with the given identity: catalog hit → the
    /// version's <c>.idx</c> file → the legacy <c>CachedVersionIndexes</c> row → the cloud. Idempotent, and safe to
    /// call from multiple callers racing on the same (account, container, version) at once — only one of them pays
    /// for the migration; the rest find the row already there once they reach the write lock.</summary>
    Task EnsureVersionAsync(Account account, string container, BackupVersion version, long identityTicks, string? password, CancellationToken ct = default);

    /// <summary>Drops one version from the catalog and every older home it might still occupy (the retention policy
    /// retiring it).</summary>
    Task RemoveVersionAsync(int accountId, string container, int version, CancellationToken ct = default);

    /// <summary>
    /// Brings the catalog back into agreement with the info file, which is the only authority on which versions
    /// exist: every version in the catalog that <paramref name="keepVersions"/> does not list is removed, exactly as
    /// <see cref="RemoveVersionAsync"/> would remove it.
    /// <para>
    /// This is not housekeeping, it is a correctness precondition. Dedup, collision avoidance and the prescreen all
    /// answer out of the whole catalog with no version predicate, so a version the info file retired — whose blobs
    /// a cleanup has already deleted from the cloud — would go on offering those deleted blobs as dedup hits, and
    /// the new index would record a file as backed up at an address that holds nothing. A cleanup interrupted
    /// between its cloud deletes and its catalog removal (a Stop, a shutdown, one 5xx) leaves exactly that state.
    /// </para>
    /// </summary>
    Task ReconcileAsync(
        int accountId, string container, IReadOnlyCollection<int> keepVersions, CancellationToken ct = default);

    /// <summary>Drops a whole container's catalog and every older home it might still occupy (its backup config
    /// being deleted).</summary>
    Task RemoveContainerAsync(int accountId, string container, CancellationToken ct = default);

    /// <summary>
    /// Records patches whose index is already in the cloud, and — if that fails — makes sure the catalog cannot go
    /// on answering with the pre-patch rows. The cloud write happened first (the checker and the repairer both
    /// serialize → upload → info file → patch), so a failure here leaves the catalog holding an identity that says
    /// "already imported" over rows the cloud has since moved past; nothing would ever re-import it, and the marks
    /// the cloud carries would stay invisible to dedup exclusion, restore substitution and the next check. Dropping
    /// the affected versions instead costs one re-download on next use and cannot lie.
    /// </summary>
    Task ApplyPatchesOrInvalidateAsync(
        // Named `log` rather than `logger` so the implementation can still reach its own injected one; null means
        // "use that one", which is what both callers pass — neither the checker nor the repairer takes a logger.
        int accountId, string container, IReadOnlyList<CatalogPatch> patches, ILogger? log,
        CancellationToken ct = default);

    /// <summary>Reserves the container's single write slot; see <see cref="VersionCatalogStore.LockForWriteAsync"/>
    /// for what it protects and which calls must never be made while holding it.</summary>
    Task<CatalogWriteLock> LockForWriteAsync(int accountId, string container, CancellationToken ct = default);
}
