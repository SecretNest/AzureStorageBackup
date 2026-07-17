using System.Globalization;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// verbose（debug）逐文件日志的**文件**后端（PRD 3.6）：按备份分目录、按日期分文件写文本，
/// 避免每文件一次 SQLite 写（超大备份下 DB 写会成瓶颈），也把高频诊断日志与可查询的审计日志分开存放。
///
/// 布局：<c>{Root}/{container}/{yyyyMMdd}.log</c>，每行 <c>{UTC 时间戳}  {消息}</c>，追加写。
/// 追加经单一轻量门串行（文件追加远快于 DB 事务）；<see cref="Trim"/> 按文件名日期删超期文件。
/// </summary>
public sealed class VerboseFileLog(string root)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>日志根目录（供「目录」页展示，便于 Docker 卷映射）。</summary>
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

    /// <summary>删除文件名日期早于 now-maxAgeDays 的日志文件（与短存 SQLite 日志同一保留窗口）。</summary>
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

    // container 名不含路径分隔符（Azure 命名规则：小写字母/数字/连字符），仍防御性剥离，避免逃逸出根目录。
    private static string Sanitize(string container) =>
        string.Concat(container.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
