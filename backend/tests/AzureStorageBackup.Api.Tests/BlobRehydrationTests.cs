using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class BlobRehydrationTests
{
    [Fact]
    public void Rehydrate_Targets_All_Archived_Volumes_Not_Just_First()
    {
        var volumes = new (string Name, string? AccessTier, string? ArchiveStatus)[]
        {
            ("packs/p1.7z.001", "Archive", null),   // needs rehydrating
            ("packs/p1.7z.002", "Archive", "rehydrate-pending-to-hot"), // already rehydrating, skipped
            ("packs/p1.7z.003", "Hot", null),                // already online, skipped
        };
        var toBegin = BlobRehydration.SelectToBegin(volumes);
        Assert.Equal(new[] { "packs/p1.7z.001" }, toBegin);
    }
}
