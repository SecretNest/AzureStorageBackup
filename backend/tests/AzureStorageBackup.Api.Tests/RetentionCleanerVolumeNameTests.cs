using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// <see cref="RetentionCleaner.BaseRef"/> must recognize every volume suffix the uploader can produce. Volume
/// names come from VolumeBlobIO.VolumeName's <c>{index:D3}</c>: three digits is the padding, not the width, so
/// .999 is followed by .1000. A normalizer that only strips exactly three digits leaves .1000 and later
/// un-normalized, and the sweep then deletes those volumes of a still-referenced blob as orphans — the same
/// "three digits is padding, not width" failure the compress-side collector had.
/// </summary>
public sealed class RetentionCleanerVolumeNameTests
{
    [Theory]
    [InlineData("data/abc.001")]
    [InlineData("data/abc.999")]
    [InlineData("data/abc.1000")]
    [InlineData("data/abc.1001")]
    [InlineData("data/abc.12345")]
    public void A_volume_suffix_of_any_width_normalizes_to_the_base_name(string blobName)
        => Assert.Equal("data/abc", RetentionCleaner.BaseRef(blobName));

    [Theory]
    [InlineData("data/abc")]      // no suffix at all
    [InlineData("data/abc.7z")]   // not digits
    [InlineData("data/abc.12")]   // shorter than the D3 padding — never produced by the uploader
    [InlineData("data/abc.99a")]  // digits then a letter
    public void A_non_volume_name_is_returned_unchanged(string blobName)
        => Assert.Equal(blobName, RetentionCleaner.BaseRef(blobName));
}
