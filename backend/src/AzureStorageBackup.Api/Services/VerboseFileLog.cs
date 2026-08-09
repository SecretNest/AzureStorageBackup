using System.Globalization;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// The **file** backend of the verbose (debug) per-file log (PRD 3.6): text written into one directory per backup and one file per date,
/// avoiding one SQLite write per file (on a huge backup DB writes become the bottleneck) and keeping high-frequency diagnostic logs stored apart from the queryable audit log.
///
/// Layout: <c>{Root}/{container}/{yyyyMMdd}.log</c>, one line per entry as <c>{UTC timestamp}  {message}</c>, appended.
/// Appends are serialized through a single lightweight gate (a file append is far faster than a DB transaction); <see cref="Trim"/> deletes expired files by the date in their name.
/// </summary>
public sealed class VerboseFileLog(string root)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The log root directory (shown on the Directories page, to make Docker volume mapping easier).</summary>
    public string Root => root;

    public async Task AppendAsync(string container, string message, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var dir = Path.Combine(root, Sanitize(container));
        var file = Path.Combine(dir, now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}";
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(dir);
            await File.AppendAllTextAsync(file, line, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes log files whose file-name date is earlier than now-maxAgeDays (the same retention window as the ephemeral SQLite log).</summary>
    public void Trim(int maxAgeDays, DateTimeOffset now)
    {
        if (!Directory.Exists(root))
            return;
        var cutoff = now.UtcDateTime.Date.AddDays(-Math.Max(0, maxAgeDays));
        foreach (var f in Directory.EnumerateFiles(root, "*.log", SearchOption.AllDirectories))
        {
            if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyyMMdd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d < cutoff)
                try { File.Delete(f); } catch { /* best effort */ }
        }
    }

    // A container name holds no path separator (Azure naming rules: lowercase letters/digits/hyphens), but strip defensively anyway so nothing can escape the root directory.
    private static string Sanitize(string container) =>
        string.Concat(container.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
