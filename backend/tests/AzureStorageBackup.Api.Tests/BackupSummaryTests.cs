using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The summary line for a successful backup is the one line the operator is certain to read (it goes to both the
/// operation log and the webhook notification). Pulling its layout out of the orchestrator into a pure function is
/// exactly what lets it be pinned down here item by item: which numbers must appear, and which must make **the whole
/// segment disappear** when they are zero.
/// Without omitting zeros, a routine incremental backup drags along three lines of all-zero noise, and the round that actually carries information drowns in it.
/// </summary>
public class BackupSummaryTests
{
    private static BackupRunResult Result(
        int version = 12, int changed = 340, long changedBytes = 4_700_000_000,
        int unreadable = 0, int added = 128, int modified = 212, int deleted = 35,
        long deletedBytes = 2_100_000_000, long uploaded = 1_200_000_000, CleanupReport? cleanup = null) =>
        new(version, changed, changedBytes, unreadable)
        {
            NewFiles = added,
            ModifiedFiles = modified,
            DeletedFiles = deleted,
            DeletedBytes = deletedBytes,
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
        // Both figures have to be there and be distinguishable: the source-side change volume answers "how much did I
        // change", the uploaded volume answers "how much more do I pay the cloud this month". Report only one and the
        // question is only half answered.
        Assert.Contains("4.7 GB", text);
        Assert.Contains("1.2 GB", text);
    }

    /// <summary>
    /// 35 deleted files can be 35 empty log stubs or 35 disk images, and the count alone cannot tell those apart.
    /// It is the one item on the Files line with no sense of scale behind it — new and modified at least have
    /// "changed at source" standing next to them.
    /// </summary>
    [Fact]
    public void Reports_How_Much_The_Deleted_Files_Weighed()
    {
        var text = BackupSummary.Format(Result(deleted: 35, deletedBytes: 2_100_000_000));

        Assert.Contains("35 deleted (2.1 GB)", text);
    }

    /// <summary>The same "a zero makes its item disappear" rule, applied to the parenthesis: a round that deleted
    /// twelve empty files or symlinks should not be made to carry "(0 B)" around.</summary>
    [Fact]
    public void Omits_The_Deleted_Size_When_Those_Files_Weighed_Nothing()
    {
        var text = BackupSummary.Format(Result(deleted: 12, deletedBytes: 0));

        Assert.Contains("12 deleted", text);
        Assert.DoesNotContain("(0 B)", text);
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

    /// <summary>Say nothing when the unreadable count is zero — hanging a "0 unreadable" off every round means nobody notices the round where files really were unreadable.</summary>
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
        // When not a single byte moved, "0 B changed at source → 0 B uploaded" is pure noise.
        Assert.DoesNotContain("uploaded", text);
    }

    /// <summary>
    /// The round where dedup hits: the source side really did change a lot, but the cloud did not grow by a single
    /// byte. That is precisely the point of reporting these two figures separately, so the Data line must **not**
    /// disappear when the uploaded volume is zero — if it does, "changed 4.7 GB yet uploaded nothing" is invisible.
    /// </summary>
    [Fact]
    public void Keeps_Data_Line_When_Everything_Deduplicated()
    {
        var text = BackupSummary.Format(Result(uploaded: 0));

        Assert.Contains("4.7 GB", text);
        Assert.Contains("0 B", text);
    }

    /// <summary>
    /// added + modified must be identically equal to ChangedFiles. The identity is maintained deliberately: files found
    /// unreadable only post-diff are not subtracted from those two items, because otherwise the log would show
    /// "340 changed" next to "128 + 209 != 340" — books that only make sense with the source code open. Keeping the
    /// unreadable number as an item of its own lets anyone balance the books for themselves.
    /// </summary>
    [Fact]
    public void New_Plus_Modified_Adds_Up_To_Changed()
    {
        var r = Result(changed: 340, added: 128, modified: 212);
        Assert.Equal(r.ChangedFiles, r.NewFiles + r.ModifiedFiles);
    }
}
