using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class BlobRehydrationTests
{
    [Fact]
    public void Rehydrate_Targets_All_Archived_Volumes_Not_Just_First()
    {
        var volumes = new (string Name, string? AccessTier, string? ArchiveStatus)[]
        {
            ("packs/p1.7z.001", "Archive", null),   // 需活化
            ("packs/p1.7z.002", "Archive", "rehydrate-pending-to-hot"), // 已在活化中，跳过
            ("packs/p1.7z.003", "Hot", null),                // 已在线，跳过
        };
        var toBegin = BlobRehydration.SelectToBegin(volumes);
        Assert.Equal(new[] { "packs/p1.7z.001" }, toBegin);
    }
}
