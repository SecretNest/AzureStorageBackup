using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// <see cref="IndexEntry"/> and <see cref="StorageRef"/> are records, but <see cref="StorageRef.VolumeSizes"/> is a
/// <c>List&lt;long&gt;</c>, and record-generated equality compares list members by reference, not by value. So
/// <c>Assert.Equal</c> on two otherwise-identical entries can fail (or, if the same list instance sneaks into both
/// sides, silently pass without actually checking anything). This compares every field explicitly, including
/// <c>VolumeSizes</c> element-wise, so streaming round-trip tests here and in later tasks get a real comparison.
/// </summary>
internal static class IndexAssert
{
    public static void AssertSameEntry(IndexEntry expected, IndexEntry actual)
    {
        Assert.Equal(expected.Path, actual.Path);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(expected.Mtime, actual.Mtime);
        Assert.Equal(expected.Permissions, actual.Permissions);
        Assert.Equal(expected.HeadHash, actual.HeadHash);
        Assert.Equal(expected.TailHash, actual.TailHash);
        Assert.Equal(expected.FullHash, actual.FullHash);
        Assert.Equal(expected.Target, actual.Target);
        Assert.Equal(expected.UnreadableAt, actual.UnreadableAt);
        AssertSameStorage(expected.Storage, actual.Storage);
    }

    private static void AssertSameStorage(StorageRef? expected, StorageRef? actual)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Ref, actual.Ref);
        Assert.Equal(expected.EntryName, actual.EntryName);
        Assert.Equal(expected.Volumes, actual.Volumes);
        Assert.Equal(expected.Raw, actual.Raw);
        Assert.Equal(expected.VolumeSizes, actual.VolumeSizes);
    }
}
