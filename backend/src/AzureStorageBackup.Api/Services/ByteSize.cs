using System.Globalization;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 把字节数排成给人看的样子。单位用**十进制**（KB/MB/GB，1000 进制）而非二进制（KiB/MiB）：
/// 这些数字出现在备份摘要里，操作员多半是拿它去和 Azure 的用量账单对照的，而账单按十进制计。
/// 用二进制单位会让两边差出 7%（GB 一级），足以让人以为是哪里算错了。
/// </summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

    public static string Human(long bytes)
    {
        // 负数在备份里不该出现，但这个函数跑在"备份已经成功"之后的收尾路径上——
        // 在这里抛异常等于让一次成功的备份报告成失败。原样印出来，不做算术（long.MinValue
        // 取绝对值会溢出），让不该出现的东西自己显形。
        if (bytes < 0)
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        if (bytes < 1000)
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";

        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < Units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        // 保留一位小数之后才可能达到 1000（999_960 B = 999.96 KB → "1000.0 KB"）。那不是任何人
        // 会写的数字，所以进位要在**舍入之后**再判一次，而不是只看舍入前的值。
        if (Math.Round(value, 1) >= 1000 && unit < Units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return value.ToString("0.0", CultureInfo.InvariantCulture) + " " + Units[unit];
    }
}
