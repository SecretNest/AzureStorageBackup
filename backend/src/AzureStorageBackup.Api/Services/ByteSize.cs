using System.Globalization;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Lays byte counts out for people to read. The units are **binary** (base 1024) and written KB/MB/GB, as Windows Explorer and the
/// `ls -h` family write them. The deciding case is the archive volume: 7z splits in 1024s (<c>DefaultVolumeBytes</c> is
/// <c>100 * 1024 * 1024</c>, passed to 7zz as a byte count), so every volume this program produces is 100 MiB, and with decimal units
/// each of them read as "104.9 MB" on screen — an odd number for a limit the operator typed as 100. The frontend's <c>formatBytes</c>
/// counts the same way; the two must never drift, or the same run reads 7% apart on the screen and in the push message.
/// </summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

    public static string Human(long bytes)
    {
        // Negative numbers should not occur in a backup, but this function runs on the wrap-up path after "the backup already succeeded" —
        // throwing here would report a successful backup as a failure. Print it as-is and do no arithmetic (taking the absolute value of
        // long.MinValue overflows), letting the thing that should not be there show itself.
        if (bytes < 0)
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        if (bytes < 1024)
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // It can only reach 1024 after being rounded to one decimal place (1_048_536 B = 1023.96 KB → "1024.0 KB"). That is not a number
        // anyone would write, so the carry has to be checked once more **after rounding**, not just on the pre-rounding value.
        if (Math.Round(value, 1) >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString("0.0", CultureInfo.InvariantCulture) + " " + Units[unit];
    }
}
