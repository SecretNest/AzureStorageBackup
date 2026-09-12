using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Whether a volume is labelled at all is decided by the backup's encryption, not by the memory limit
/// (volume-identity.md, "Encrypted backups"): 7z's per-run random IV makes every encrypted volume different
/// bytes, so a label on one can never match a later recompression and would only be paid for — the volume read
/// whole into memory, or read twice — for nothing.
/// </summary>
public sealed class VolumeLabellingTests
{
    [Fact]
    public void An_Unencrypted_Backup_Labels_Its_Volumes_Within_The_Streams_Share()
    {
        var labelling = VolumeLabelling.For(password: null, inMemoryLimitBytes: 100);
        Assert.True(labelling.Label);
        Assert.Equal(100, labelling.InMemoryLimitBytes);
        Assert.Equal(labelling, VolumeLabelling.For(password: "", inMemoryLimitBytes: 100));
    }

    [Fact]
    public void An_Encrypted_Backup_Labels_Nothing_Whatever_The_Limit()
    {
        Assert.Equal(VolumeLabelling.None, VolumeLabelling.For(password: "pw", inMemoryLimitBytes: 100));
        Assert.Equal(VolumeLabelling.None, VolumeLabelling.For(password: "pw", inMemoryLimitBytes: 0));
        Assert.False(VolumeLabelling.None.Label);
    }

    /// <summary>An unlabelled volume is never read into memory: with no hash to compute over the bytes sent, the
    /// disk route's own buffer is all the memory the upload needs, and the share is not spent.</summary>
    [Fact]
    public void An_Unlabelled_Volume_Is_Never_Held_In_Memory()
    {
        Assert.False(BlobUploader.HoldsInMemory(fileLength: 1, VolumeLabelling.None));
        Assert.False(BlobUploader.HoldsInMemory(fileLength: 0, VolumeLabelling.None));
        Assert.True(BlobUploader.HoldsInMemory(fileLength: 100, VolumeLabelling.Labelled(100)));
        Assert.False(BlobUploader.HoldsInMemory(fileLength: 101, VolumeLabelling.Labelled(100)));
    }
}
