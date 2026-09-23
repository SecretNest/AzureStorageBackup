using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The byte counts in a backup summary are for people to read, not for machines to parse. The units are binary (base 1024) and written
/// KB/MB/GB, as the operator's own file manager writes them — a 100 MiB volume limit has to read as "100.0 MB", not "104.9 MB".
/// </summary>
public class ByteSizeTests
{
    private const long KB = 1024;
    private const long MB = KB * 1024;
    private const long GB = MB * 1024;
    private const long TB = GB * 1024;
    private const long PB = TB * 1024;

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]         // the last value still shown in B
    [InlineData(KB, "1.0 KB")]           // the first value that carries over
    [InlineData(1536, "1.5 KB")]
    [InlineData(MB, "1.0 MB")]
    [InlineData(100 * MB, "100.0 MB")]   // the default volume limit reads as the number that was typed
    [InlineData(4_700_000_000, "4.4 GB")]
    [InlineData(5_046_586_573, "4.7 GB")]
    [InlineData(TB, "1.0 TB")]
    [InlineData(PB, "1.0 PB")]
    public void Formats_With_Binary_Units(long bytes, string expected) =>
        Assert.Equal(expected, ByteSize.Human(bytes));

    /// <summary>
    /// Rounding must not carry within the current unit and stop there: 1_048_536 is 1023.96 KB, and one decimal place turns it into
    /// "1024.0 KB" — a number nobody writes. Once rounding reaches 1024, it has to carry up one more unit.
    /// </summary>
    [Theory]
    [InlineData(1_048_536, "1.0 MB")]
    [InlineData(1_073_700_000, "1.0 GB")]
    public void Carries_Into_Next_Unit_When_Rounding_Reaches_1024(long bytes, string expected) =>
        Assert.Equal(expected, ByteSize.Human(bytes));

    /// <summary>long.MaxValue must have a unit available too; it must not spill out as "9223372036854775807 B".</summary>
    [Fact]
    public void Handles_MaxValue() => Assert.Equal("8.0 EB", ByteSize.Human(long.MaxValue));

    /// <summary>
    /// Byte counts in a backup are never negative, but the formatter must never throw over an input that should not have occurred —
    /// it runs on the wrap-up path after a backup has already succeeded, and throwing here turns a successful backup into a failed one.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void Does_Not_Throw_On_Negative(long bytes) => Assert.False(string.IsNullOrEmpty(ByteSize.Human(bytes)));
}
