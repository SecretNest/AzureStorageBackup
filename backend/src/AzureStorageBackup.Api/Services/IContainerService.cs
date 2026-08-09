using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>A container and whether a backup is present in it.</summary>
public record ContainerInfo(string Name, BackupPresence Backup)
{
    /// <summary>
    /// The name of the backup config in the local database that holds this container; null when nobody holds it.
    /// <para>
    /// <see cref="Backup"/> can only say "is the cloud info file there", and that file is written by the very last step of a
    /// backup: a container halfway through its first backup already holds this run's data while the cloud still carries no
    /// marker at all. The authority on occupancy is local — the config row is in the database from the moment it was
    /// created, with nothing to wait on in the cloud (filled in by <c>ContainerEndpoints</c>).
    /// </para>
    /// </summary>
    public string? InUseBy { get; init; }
}

/// <summary>Container management under an account (list/create/delete) plus backup discovery.</summary>
public interface IContainerService
{
    Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(Account account, CancellationToken ct = default);

    Task CreateContainerAsync(Account account, string name, CancellationToken ct = default);

    Task DeleteContainerAsync(Account account, string name, CancellationToken ct = default);
}
