using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 备份成功那条摘要是操作员一定会看的一条（它同时进操作日志和 webhook 通知）。把它的排版
/// 从编排器里摘成纯函数，就是为了能在这里逐条钉死：哪些数字必须出现、哪些为零时必须**整段消失**。
/// 零值不省略的话，一次日常的增量备份会带出三行全是 0 的噪音，真正有信息的那次就淹没在里面了。
/// </summary>
public class BackupSummaryTests
{
    private static BackupRunResult Result(
        int version = 12, int changed = 340, long changedBytes = 4_700_000_000,
        int unreadable = 0, int added = 128, int modified = 212, int deleted = 35,
        long uploaded = 1_200_000_000, CleanupReport? cleanup = null) =>
        new(version, changed, changedBytes, unreadable)
        {
            NewFiles = added,
            ModifiedFiles = modified,
            DeletedFiles = deleted,
            UploadedBytes = uploaded,
            Cleanup = cleanup ?? CleanupReport.Empty,
        };

    [Fact]
    public void Reports_Version_File_Counts_And_Both_Byte_Figures()
    {
        var text = BackupSummary.Format(Result());

        Assert.Contains("Version 12", text);
        Assert.Contains("128 new", text);
        Assert.Contains("212 modified", text);
        Assert.Contains("35 deleted", text);
        // 两个口径都要在，且分得清哪个是哪个：源侧变更量回答"我改了多少东西"，
        // 实传量回答"云上这个月要多付多少钱"。只报一个就答不全。
        Assert.Contains("4.7 GB", text);
        Assert.Contains("1.2 GB", text);
    }

    [Fact]
    public void Omits_Retention_Line_When_Nothing_Was_Cleaned()
    {
        var text = BackupSummary.Format(Result(cleanup: CleanupReport.Empty));
        Assert.DoesNotContain("Retention", text);
    }

    [Fact]
    public void Reports_Retention_Counts_Separately_For_Packs_And_Blobs()
    {
        var text = BackupSummary.Format(Result(cleanup: new CleanupReport(2, 37, 412, 5_200_000_000)));

        Assert.Contains("Retention", text);
        Assert.Contains("2 version(s)", text);
        Assert.Contains("37 pack(s)", text);
        Assert.Contains("412 blob(s)", text);
        Assert.Contains("5.2 GB", text);
    }

    /// <summary>读不开的文件为零时不提——每轮都挂一句 "0 unreadable" 会让真的有文件读不开时无人察觉。</summary>
    [Fact]
    public void Mentions_Unreadable_Only_When_Nonzero()
    {
        Assert.DoesNotContain("unreadable", BackupSummary.Format(Result(unreadable: 0)));
        Assert.Contains("3 unreadable", BackupSummary.Format(Result(unreadable: 3)));
    }

    [Fact]
    public void Says_No_Changes_When_Nothing_Moved()
    {
        var text = BackupSummary.Format(Result(
            changed: 0, changedBytes: 0, added: 0, modified: 0, deleted: 0, uploaded: 0));

        Assert.Contains("Version 12", text);
        Assert.Contains("no changes", text);
        // 一个字节都没动时，"0 B changed at source → 0 B uploaded" 是纯噪音。
        Assert.DoesNotContain("uploaded", text);
    }

    /// <summary>
    /// 去重命中的那一轮：源侧确实改了很多，但云端一个字节都没涨。这正是这两个口径分开报的意义，
    /// 所以实传为零时 Data 行**不能**消失——消失了就看不出"改了 4.7 GB 却没上传"这件事。
    /// </summary>
    [Fact]
    public void Keeps_Data_Line_When_Everything_Deduplicated()
    {
        var text = BackupSummary.Format(Result(uploaded: 0));

        Assert.Contains("4.7 GB", text);
        Assert.Contains("0 B", text);
    }

    /// <summary>
    /// added + modified 必须恒等于 ChangedFiles。这条恒等式是刻意维持的：post-diff 才发现读不开的
    /// 文件不从这两项里扣除，否则日志上会出现 "340 changed" 但 "128 + 209 ≠ 340" 这种要人对着
    /// 源码才能理解的账。不可读的数字单独成项，谁都能自己把账算平。
    /// </summary>
    [Fact]
    public void New_Plus_Modified_Adds_Up_To_Changed()
    {
        var r = Result(changed: 340, added: 128, modified: 212);
        Assert.Equal(r.ChangedFiles, r.NewFiles + r.ModifiedFiles);
    }
}
