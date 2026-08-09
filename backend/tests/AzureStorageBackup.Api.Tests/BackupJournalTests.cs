using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class BackupJournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "asb-journal-" + Guid.NewGuid().ToString("N"));

    private string Path_(string name) => System.IO.Path.Combine(_dir, name);

    private static JournalHeader Header() => new()
    {
        RunId = "r1",
        ConfigId = 7,
        StartedAt = DateTimeOffset.UnixEpoch,
        BaselineVersion = 3,
        LocalRoot = "/data/src",
        EncryptionIdentity = "plain",
    };

    public BackupJournalTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task Round_trips_header_and_records()
    {
        var file = Path_("a.jsonl");
        await using (var j = await BackupJournal.CreateAsync(file, Header(), default))
        {
            await j.AppendAsync(new JournalRecord
            {
                Kind = "blob", Ref = "data/aaa", Path = "x/y.bin", FullHash = "aaa",
                HeadHash = "h", TailHash = "t", Length = 10, Volumes = 2, Raw = true,
                VolumeSizes = [4, 6],
            }, default);
            await j.AppendAsync(new JournalRecord
            {
                Kind = "pack", Ref = "p123456780001", StoreOnly = true, Volumes = 1,
                VolumeSizes = [99],
                Members = [new JournalMember("a.txt", "0001_a.txt", "hh", 5)],
            }, default);
        }

        var content = await BackupJournal.ReadAsync(file, default);
        Assert.NotNull(content);
        Assert.Equal(7, content!.Header.ConfigId);
        Assert.Equal(3, content.Header.BaselineVersion);
        Assert.Equal(2, content.Records.Count);
        Assert.Equal("data/aaa", content.Records[0].Ref);
        Assert.Equal([4L, 6L], content.Records[0].VolumeSizes);
        Assert.True(content.Records[1].StoreOnly);
        Assert.Equal("0001_a.txt", content.Records[1].Members[0].EntryName);
    }

    // The cost of not fsyncing: on a crash the last line may be a half-written stub. The read side has to survive it.
    [Fact]
    public async Task Truncated_last_line_is_skipped()
    {
        var file = Path_("b.jsonl");
        await using (var j = await BackupJournal.CreateAsync(file, Header(), default))
            await j.AppendAsync(new JournalRecord { Kind = "blob", Ref = "data/aaa", Path = "p", FullHash = "aaa" }, default);
        await File.AppendAllTextAsync(file, "{\"Kind\":\"blob\",\"Ref\":\"data/bb");

        var content = await BackupJournal.ReadAsync(file, default);
        Assert.NotNull(content);
        Assert.Single(content!.Records);
        Assert.Equal("data/aaa", content.Records[0].Ref);
    }

    /// <summary>
    /// Appending to a journal whose "last line is a half-written stub": the newly recorded line must still read back.
    /// <para>
    /// This is the entire reason <see cref="BackupJournal.OpenForAppendAsync"/> emits a newline first. Without it, the stub and
    /// the newly written line glue into one, so **the new one** fails to parse as well — and it records content this run just
    /// confirmed uploaded, so losing it means the next run uploads that block for nothing, with nothing on disk vouching for it any more.
    /// And "the last line is a half-written stub" is exactly the normal state a crash leaves behind: this file is not fsynced per record.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Appending_after_a_torn_last_line_does_not_swallow_the_next_record()
    {
        var file = Path_("torn.jsonl");
        await using (var j = await BackupJournal.CreateAsync(file, Header(), default))
            await j.AppendAsync(
                new JournalRecord { Kind = "blob", Ref = "data/aaa", Path = "p", FullHash = "aaa" }, default);
        await File.AppendAllTextAsync(file, "{\"Kind\":\"blob\",\"Ref\":\"data/hal");   // crashed mid-write

        await using (var j = await BackupJournal.OpenForAppendAsync(file, default))
            await j.AppendAsync(
                new JournalRecord { Kind = "blob", Ref = "data/zzz", Path = "q", FullHash = "zzz" }, default);

        var content = await BackupJournal.ReadAsync(file, default);
        Assert.NotNull(content);
        Assert.Equal(["data/aaa", "data/zzz"], content!.Records.Select(r => r.Ref));
    }

    [Fact]
    public async Task Corrupt_header_voids_the_whole_journal()
    {
        var file = Path_("c.jsonl");
        await File.WriteAllTextAsync(file, "not json at all\n{\"Kind\":\"blob\",\"Ref\":\"data/aaa\"}\n");
        Assert.Null(await BackupJournal.ReadAsync(file, default));
    }

    [Fact]
    public async Task Empty_file_reads_as_null()
    {
        var file = Path_("d.jsonl");
        await File.WriteAllTextAsync(file, "");
        Assert.Null(await BackupJournal.ReadAsync(file, default));
    }

    [Fact]
    public async Task Missing_file_reads_as_null()
        => Assert.Null(await BackupJournal.ReadAsync(Path_("nope.jsonl"), default));

    [Fact]
    public async Task Flush_makes_records_readable_while_still_open()
    {
        var file = Path_("e.jsonl");
        await using var j = await BackupJournal.CreateAsync(file, Header(), default);
        await j.AppendAsync(new JournalRecord { Kind = "blob", Ref = "data/aaa", Path = "p", FullHash = "aaa" }, default);
        await j.FlushAsync(fsync: true, default);

        var content = await BackupJournal.ReadAsync(file, default);
        Assert.Single(content!.Records);
    }

    // Flushing to the OS on every append is about **the process dying**: on a kill, an OOM, or a container stop, the bytes in
    // the process buffer go with the process while the ones in the page cache do not. And "in the cloud, not yet in the index"
    // blocks are born exactly that way — the process never reached the index commit. Skip this flush and the next run reads a
    // journal missing its last few lines: the blocks those lines recorded are unclaimed, so they get re-uploaded, and the
    // cleanup test (which honours the journal) no longer protects them either.
    //
    // It is not about "the cleaner reading while the backup is writing": BackupBusyTracker.TryAcquire already serializes backup
    // and cleanup on the same (account, container) (TaskDispatcher / BackupRunner),
    // and those two are CleanupAsync's only production callers.
    [Fact]
    public async Task Append_is_visible_to_another_reader_without_an_explicit_flush()
    {
        var file = Path_("f.jsonl");
        await using var j = await BackupJournal.CreateAsync(file, Header(), default);
        await j.AppendAsync(new JournalRecord { Kind = "blob", Ref = "data/aaa", Path = "p", FullHash = "aaa" }, default);

        var content = await BackupJournal.ReadAsync(file, default);
        Assert.Single(content!.Records);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }
}

public class BlobAddressSchemeIdentityTests
{
    [Fact]
    public void Unkeyed_identity_is_plain()
        => Assert.Equal("plain", new BlobAddressScheme(null, null).Identity);

    [Fact]
    public void Same_password_and_salt_give_same_identity()
    {
        var salt = new byte[16];
        Assert.Equal(new BlobAddressScheme("pw", salt).Identity, new BlobAddressScheme("pw", salt).Identity);
    }

    [Fact]
    public void Different_password_gives_different_identity()
    {
        var salt = new byte[16];
        Assert.NotEqual(new BlobAddressScheme("pw", salt).Identity, new BlobAddressScheme("other", salt).Identity);
    }
}
