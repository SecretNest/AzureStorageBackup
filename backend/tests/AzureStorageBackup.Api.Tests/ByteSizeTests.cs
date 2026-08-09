using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The byte counts in a backup summary are for people to read, not for machines to parse. The units are decimal (KB/MB/GB) rather than
/// binary (KiB/MiB), to line up with how Azure bills — an operator holding this number against the bill should not have to convert it first.
/// </summary>
public class ByteSizeTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(512, "512 B")]
    [InlineData(999, "999 B")]           // the last value still shown in B
    [InlineData(1_000, "1.0 KB")]        // the first value that carries over
    [InlineData(1_500, "1.5 KB")]
    [InlineData(1_000_000, "1.0 MB")]
    [InlineData(4_700_000_000, "4.7 GB")]
    [InlineData(5_200_000_000, "5.2 GB")]
    [InlineData(1_000_000_000_000, "1.0 TB")]
    [InlineData(1_000_000_000_000_000, "1.0 PB")]
    public void Formats_With_Decimal_Units(long bytes, string expected) =>
        Assert.Equal(expected, ByteSize.Human(bytes));

    /// <summary>
    /// Rounding must not carry within the current unit and stop there: 999_960 is 999.96 KB, and one decimal place turns it into "1000.0 KB" —
    /// a number nobody writes. Once rounding reaches 1000, it has to carry up one more unit.
    /// </summary>
    [Theory]
    [InlineData(999_960, "1.0 MB")]
    [InlineData(999_960_000, "1.0 GB")]
    public void Carries_Into_Next_Unit_When_Rounding_Reaches_1000(long bytes, string expected) =>
        Assert.Equal(expected, ByteSize.Human(bytes));

    /// <summary>long.MaxValue must have a unit available too; it must not spill out as "9223372036854775807 B".</summary>
    [Fact]
    public void Handles_MaxValue() => Assert.Equal("9.2 EB", ByteSize.Human(long.MaxValue));

    /// <summary>
    /// Byte counts in a backup are never negative, but the formatter must never throw over an input that should not have occurred —
    /// it runs on the wrap-up path after a backup has already succeeded, and throwing here turns a successful backup into a failed one.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void Does_Not_Throw_On_Negative(long bytes) => Assert.False(string.IsNullOrEmpty(ByteSize.Human(bytes)));
}
