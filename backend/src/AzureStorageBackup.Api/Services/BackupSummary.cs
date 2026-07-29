using System.Globalization;
using System.Text;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 备份成功那条摘要的排版。这条消息同时进操作日志和 webhook 通知，是操作员一定会看的一条，
/// 所以它得能独立回答几个问题：这轮动了哪些文件、云上多了多少数据、旧版本清掉了多少。
/// <para>
/// 摘成纯函数（而不是留在编排器里拼字符串）只有一个目的：让"零值整段消失"这条规则能被逐条测到。
/// 每轮都挂着一串 0 的话，真正有信息的那次就淹没在噪音里了——尤其是 unreadable，
/// 那一项恰恰是最需要被看见的。
/// </para>
/// </summary>
public static class BackupSummary
{
    public static string Format(BackupRunResult r)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Version {r.Version}");

        var files = new List<string>(4);
        if (r.NewFiles > 0)
            files.Add($"{r.NewFiles} new");
        if (r.ModifiedFiles > 0)
            files.Add($"{r.ModifiedFiles} modified");
        if (r.DeletedFiles > 0)
            files.Add($"{r.DeletedFiles} deleted");
        // 读不开的文件既不算变更也不算删除，索引会静默沿用旧条目——所以它必须自己占一项。
        // 少了这一句，一次"成功"的备份就把本轮根本没存下来的文件掩盖过去了。
        if (r.UnreadableFiles > 0)
            files.Add($"{r.UnreadableFiles} unreadable (skipped)");

        sb.Append("\nFiles: ").Append(files.Count > 0 ? string.Join(", ", files) : "no changes");

        // 源侧变更量与实传量都为零才省略这行。只有实传为零时**不能**省——那正是去重全命中的那一轮，
        // "改了 4.7 GB 却一个字节都没上传"是这两个口径分开报的全部意义所在。
        if (r.ChangedBytes > 0 || r.UploadedBytes > 0)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"\nData: {ByteSize.Human(r.ChangedBytes)} changed at source → {ByteSize.Human(r.UploadedBytes)} uploaded");
        }

        if (!r.Cleanup.IsEmpty)
            sb.Append('\n').Append(FormatRetention(r.Cleanup));

        return sb.ToString();
    }

    /// <summary>
    /// 保留清理那一句。公开是因为定时清理任务（TaskDispatcher）单独跑时也要报同样的数字——
    /// 同一件事在两个地方各写一遍措辞，迟早会漂成两种说法，而操作员得同时读这两条日志。
    /// 调用方须自行跳过 <see cref="CleanupReport.IsEmpty"/>：什么都没清掉时这句不该出现。
    /// </summary>
    public static string FormatRetention(CleanupReport c)
    {
        var parts = new List<string>(3);
        if (c.RetiredVersions > 0)
            parts.Add($"retired {c.RetiredVersions} version(s)");

        var objects = new List<string>(2);
        if (c.DeletedPacks > 0)
            objects.Add($"{c.DeletedPacks} pack(s)");
        if (c.DeletedBlobs > 0)
            objects.Add($"{c.DeletedBlobs} blob(s)");
        if (objects.Count > 0)
            parts.Add("deleted " + string.Join(" + ", objects));

        if (c.FreedBytes > 0)
            parts.Add($"freed {ByteSize.Human(c.FreedBytes)}");

        return "Retention: " + string.Join(", ", parts);
    }
}
