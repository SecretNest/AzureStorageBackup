using System.Globalization;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Lays byte counts out for people to read. The units are **decimal** (KB/MB/GB, base 1000) rather than binary (KiB/MiB):
/// these numbers show up in backup summaries, and an operator will most likely hold them against the Azure usage bill, which counts in decimal.
/// Binary units would put the two 7% apart (at the GB level) — enough to make someone think something is miscalculated.
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
        if (bytes < 1000)
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";

        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < Units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        // It can only reach 1000 after being rounded to one decimal place (999_960 B = 999.96 KB → "1000.0 KB"). That is not a number
        // anyone would write, so the carry has to be checked once more **after rounding**, not just on the pre-rounding value.
        if (Math.Round(value, 1) >= 1000 && unit < Units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return value.ToString("0.0", CultureInfo.InvariantCulture) + " " + Units[unit];
    }
}
