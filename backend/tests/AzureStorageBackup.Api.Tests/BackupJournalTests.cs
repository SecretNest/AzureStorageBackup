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

    // 不 fsync 的代价：崩溃时最后一行可能是半截的。读取端必须扛得住。
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
    /// 接着往一卷"最后一行是半截"的 journal 后面写：新记的那一条必须还读得出来。
    /// <para>
    /// 这是 <see cref="BackupJournal.OpenForAppendAsync"/> 先补一个换行的全部理由。不补的话，
    /// 半截行和新写的这一条会粘成一行，于是**新的这条**也跟着解析不出来——而它记的是本轮刚刚
    /// 确认上传的内容，丢了就是下一轮把那块白传一遍，且盘上没有任何人再为它作保。
    /// 而"最后一行是半截"恰恰是崩溃留下的常态：这个文件不逐条 fsync。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Appending_after_a_torn_last_line_does_not_swallow_the_next_record()
    {
        var file = Path_("torn.jsonl");
        await using (var j = await BackupJournal.CreateAsync(file, Header(), default))
            await j.AppendAsync(
                new JournalRecord { Kind = "blob", Ref = "data/aaa", Path = "p", FullHash = "aaa" }, default);
        await File.AppendAllTextAsync(file, "{\"Kind\":\"blob\",\"Ref\":\"data/hal");   // 崩在写一半上

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

    // 每追加一条就刷到 OS，为的是**进程死掉**：被 kill、OOM、容器停机时，进程缓冲里的字节
    // 随进程一起没了，页缓存里的不会。而"云上有、索引里还没有"的块正是这么来的——进程没能走到
    // 提交索引那一步。少刷这一下，下一轮读到的就是一卷少了最后几行的 journal：那几行记的块
    // 没人认领，于是重传一遍，而清理判据（认 journal）也不会再保它们。
    //
    // 不是为了防"清理器正读着、备份正写着"：BackupBusyTracker.TryAcquire 已经把同一个
    // (account, container) 上的备份与清理串起来了（TaskDispatcher / BackupRunner），
    // 而那两处是 CleanupAsync 仅有的生产调用方。
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
