using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The per-task memory cap for labelled uploads (volume-identity.md): a run divides the global limit evenly across
/// its upload streams, and a volume that fits its stream's share is hashed and sent from memory, while a bigger one
/// is hashed first and re-read from disk for the send.
/// </summary>
public sealed class UploadMemoryBudgetTests
{
    private const long MB = 1024 * 1024;

    [Fact]
    public void Splits_The_Limit_Evenly_Across_The_Streams()
    {
        Assert.Equal(200 * MB, UploadMemoryBudget.PerStream(1200 * MB, 6));
    }

    [Fact]
    public void One_Stream_Gets_The_Whole_Limit()
    {
        Assert.Equal(1024 * MB, UploadMemoryBudget.PerStream(1024 * MB, 1));
    }

    [Fact]
    public void Never_Drops_Below_The_Floor_When_A_Limit_Is_Set()
    {
        // 1 MB across 100 streams is 10 KB each — below the floor, which is the size of one read chunk: buffering
        // anything smaller than that in memory is exactly what the streaming path does anyway.
        Assert.Equal(UploadMemoryBudget.FloorBytes, UploadMemoryBudget.PerStream(1 * MB, 100));
        Assert.Equal(80 * 1024, UploadMemoryBudget.FloorBytes);
    }

    [Fact]
    public void Zero_Means_Nothing_Is_Ever_Held_In_Memory()
    {
        Assert.Equal(0, UploadMemoryBudget.PerStream(0, 6));
        Assert.Equal(0, UploadMemoryBudget.PerStream(-5, 6));
    }

    [Fact]
    public void A_Stream_Count_Below_One_Counts_As_One()
    {
        Assert.Equal(512 * MB, UploadMemoryBudget.PerStream(512 * MB, 0));
    }
}
