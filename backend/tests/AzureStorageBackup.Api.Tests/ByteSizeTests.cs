using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 备份摘要里的字节数要给人看，不是给机器解析的。单位取十进制（KB/MB/GB）而非二进制（KiB/MiB），
/// 与 Azure 账单的口径对齐——操作员拿这个数字去和账单对照时，不该还要自己换算一次。
/// </summary>
public class ByteSizeTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(512, "512 B")]
    [InlineData(999, "999 B")]           // 最后一个仍用 B 的值
    [InlineData(1_000, "1.0 KB")]        // 进位的第一个值
    [InlineData(1_500, "1.5 KB")]
    [InlineData(1_000_000, "1.0 MB")]
    [InlineData(4_700_000_000, "4.7 GB")]
    [InlineData(5_200_000_000, "5.2 GB")]
    [InlineData(1_000_000_000_000, "1.0 TB")]
    [InlineData(1_000_000_000_000_000, "1.0 PB")]
    public void Formats_With_Decimal_Units(long bytes, string expected) =>
        Assert.Equal(expected, ByteSize.Human(bytes));

    /// <summary>
    /// 四舍五入的进位不能停在本级单位上：999_960 是 999.96 KB，保留一位小数就成了 "1000.0 KB"——
    /// 一个没人这样写的数字。舍入后达到 1000 必须再往上进一级。
    /// </summary>
    [Theory]
    [InlineData(999_960, "1.0 MB")]
    [InlineData(999_960_000, "1.0 GB")]
    public void Carries_Into_Next_Unit_When_Rounding_Reaches_1000(long bytes, string expected) =>
        Assert.Equal(expected, ByteSize.Human(bytes));

    /// <summary>long.MaxValue 也得有单位可用，不能溢出到 "9223372036854775807 B"。</summary>
    [Fact]
    public void Handles_MaxValue() => Assert.Equal("9.2 EB", ByteSize.Human(long.MaxValue));

    /// <summary>
    /// 备份里的字节数不会是负的，但格式化函数绝不该因为一个不该出现的输入而抛异常——
    /// 它跑在备份成功之后的收尾路径上，在这里抛等于把一次成功的备份变成失败。
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void Does_Not_Throw_On_Negative(long bytes) => Assert.False(string.IsNullOrEmpty(ByteSize.Human(bytes)));
}
