using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class BackupJournalStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "asb-jstore-" + Guid.NewGuid().ToString("N"));
    private readonly BackupJournalStore _store;

    public BackupJournalStoreTests() => _store = new BackupJournalStore(_root);

    private static JournalHeader Header(string runId) => new()
    {
        RunId = runId, ConfigId = 1, StartedAt = DateTimeOffset.UnixEpoch,
        BaselineVersion = 0, LocalRoot = "/src", EncryptionIdentity = "plain",
    };

    private async Task WriteRunAsync(string runId, params JournalRecord[] records)
    {
        await using var j = await _store.CreateAsync(9, "cont", runId, Header(runId), default);
        foreach (var r in records)
            await j.AppendAsync(r, default);
    }

    [Fact]
    public async Task Lists_journals_for_the_container_only()
    {
        await WriteRunAsync("run-a");
        await using (var other = await _store.CreateAsync(9, "elsewhere", "run-b", Header("run-b"), default)) { }

        var listed = await _store.ListAsync(9, "cont", default);
        Assert.Single(listed);
        Assert.Equal("run-a", listed[0].RunId);
    }

    [Fact]
    public async Task Active_refs_union_blobs_and_packs_across_runs()
    {
        await WriteRunAsync("run-a",
            new JournalRecord { Kind = "blob", Ref = "data/aaa", Path = "p1", FullHash = "aaa" },
            new JournalRecord { Kind = "pack", Ref = "p000000010001" });
        await WriteRunAsync("run-b",
            new JournalRecord { Kind = "blob", Ref = "data/bbb", Path = "p2", FullHash = "bbb" });

        var refs = await _store.LoadActiveRefsAsync(9, "cont", default);
        Assert.Equal(["data/aaa", "data/bbb"], refs.Blobs.OrderBy(x => x));
        Assert.Equal(["p000000010001"], refs.Packs);
    }

    [Fact]
    public async Task No_journals_gives_empty_refs()
    {
        var refs = await _store.LoadActiveRefsAsync(9, "cont", default);
        Assert.Empty(refs.Blobs);
        Assert.Empty(refs.Packs);
    }

    [Fact]
    public async Task Delete_removes_one_run()
    {
        await WriteRunAsync("run-a");
        await WriteRunAsync("run-b");
        _store.Delete(9, "cont", "run-a");

        var listed = await _store.ListAsync(9, "cont", default);
        Assert.Single(listed);
        Assert.Equal("run-b", listed[0].RunId);
    }

    [Fact]
    public async Task DeleteAll_removes_the_container_folder()
    {
        await WriteRunAsync("run-a");
        _store.DeleteAll(9, "cont");
        Assert.Empty(await _store.ListAsync(9, "cont", default));
    }

    /// <summary>
    /// 头读不通的那一卷整卷作废：既不出现在列表里，也不许把它的记录混进"别删我"的名单。
    /// 后半句才是要害——名单是清理器的删除判据的另一半，多一条不该有的，就是本该退役的块永远删不掉；
    /// 而这一卷的头都解不出来，它记的东西属于哪一轮、哪个基线，根本无从谈起。
    /// </summary>
    [Fact]
    public async Task A_volume_with_an_unreadable_header_is_skipped_whole()
    {
        await WriteRunAsync("run-good",
            new JournalRecord { Kind = "blob", Ref = "data/good", Path = "p1", FullHash = "good" });
        await WriteRunAsync("run-torn");
        // 头一行不是 JSON，后面却跟着一条形状完好的记录：作废必须是整卷的事，不是"跳过坏的那一行"。
        await File.WriteAllTextAsync(
            _store.PathFor(9, "cont", "run-torn"),
            "not json at all\n{\"Kind\":\"blob\",\"Ref\":\"data/ghost\",\"Path\":\"p2\",\"FullHash\":\"ghost\"}\n");

        var listed = await _store.ListAsync(9, "cont", default);
        Assert.Single(listed);
        Assert.Equal("run-good", listed[0].RunId);

        var refs = await _store.LoadActiveRefsAsync(9, "cont", default);
        Assert.Equal(["data/good"], refs.Blobs);
    }

    /// <summary>
    /// 同一卷没变过就不许再走一遍。界面开着时这个方法每 5 秒被每个配置各调一次，而一卷 journal
    /// 能长到几百 MB——重走一遍就是每分钟几百 MB 的读，抢的还是备份自己在读的那块盘。
    /// </summary>
    [Fact]
    public async Task Peeking_an_unchanged_journal_reads_nothing_the_second_time()
    {
        await WriteRunAsync("run-a", Records(5));

        var first = await _store.PeekAsync(9, "cont", default);
        Assert.Equal(5, first[0].Records);
        var afterFirst = _store.BytesScanned;
        Assert.True(afterFirst > 0, "the first peek must actually read the volume");

        var second = await _store.PeekAsync(9, "cont", default);
        Assert.Equal(5, second[0].Records);
        Assert.Equal(afterFirst, _store.BytesScanned);   // 一个字节都没再读
    }

    /// <summary>又追加了几条：只数新增的那一段，且数出来的必须是新的总数。</summary>
    [Fact]
    public async Task Peeking_a_grown_journal_counts_only_the_new_bytes()
    {
        await WriteRunAsync("run-a", Records(5));
        Assert.Equal(5, (await _store.PeekAsync(9, "cont", default))[0].Records);
        var afterFirst = _store.BytesScanned;

        await using (var j = await _store.AppendAsync(9, "cont", "run-a", default))
            foreach (var r in Records(3))
                await j.AppendAsync(r, default);

        var peeked = await _store.PeekAsync(9, "cont", default);
        Assert.Equal(8, peeked[0].Records);
        var added = _store.BytesScanned - afterFirst;
        Assert.True(added > 0 && added < afterFirst,
            $"expected to scan only the appended tail, scanned {added} of {peeked[0].SizeBytes} bytes");
    }

    /// <summary>
    /// 半行也不许多算。这个文件不逐条 fsync，快照完全可能正落在一行中间；从文件末尾接着数，
    /// 那半行的后半截会被再当成一行算一遍，于是界面上的条数越刷越大。
    /// </summary>
    [Fact]
    public async Task A_line_that_was_only_half_written_is_not_counted_twice()
    {
        await WriteRunAsync("run-a", Records(2));
        var path = _store.PathFor(9, "cont", "run-a");
        // 半条记录，没有换行——崩在写一半上就长这样。
        await File.AppendAllTextAsync(path, "{\"Kind\":\"blob\",\"Ref\":\"data/hal");
        Assert.Equal(3, (await _store.PeekAsync(9, "cont", default))[0].Records);   // 残行照 ReadLine 的老规矩算一行

        // 后半截补上，再多写一条完整的。
        await File.AppendAllTextAsync(path, "f\",\"Path\":\"h\",\"FullHash\":\"h\"}\n");
        await File.AppendAllTextAsync(path, "{\"Kind\":\"blob\",\"Ref\":\"data/z\",\"Path\":\"z\",\"FullHash\":\"z\"}\n");
        Assert.Equal(4, (await _store.PeekAsync(9, "cont", default))[0].Records);
    }

    /// <summary>
    /// 同一个路径换了一卷（另起一轮把它重写了）：旧的行数一条都不作数，必须从头数。
    /// 判据落在头里的 StartedAt 上而不是长度上——重写出来的长度完全可能比旧的还长。
    /// </summary>
    [Fact]
    public async Task A_journal_replaced_by_another_run_is_counted_from_scratch()
    {
        await WriteRunAsync("run-a", Records(2, pad: 4000));
        Assert.Equal(2, (await _store.PeekAsync(9, "cont", default))[0].Records);

        // 同名同路径，另一轮（StartedAt 不同），字节数**更长**而条数不同：只看长度、把长了就当追加的
        // 备忘，会从上一卷数到的偏移接着数，把两卷的行数拼成一个谁也不是的数。
        await using (var j = await _store.CreateAsync(
            9, "cont", "run-a", Header("run-a") with { StartedAt = DateTimeOffset.UnixEpoch.AddDays(1) }, default))
            foreach (var r in Records(5, pad: 1800))
                await j.AppendAsync(r, default);

        Assert.Equal(5, (await _store.PeekAsync(9, "cont", default))[0].Records);

        // 反过来：换上一卷更短的，同样不许交出旧的数。
        await using (var j = await _store.CreateAsync(
            9, "cont", "run-a", Header("run-a") with { StartedAt = DateTimeOffset.UnixEpoch.AddDays(2) }, default))
            await j.AppendAsync(Records(1)[0], default);
        Assert.Equal(1, (await _store.PeekAsync(9, "cont", default))[0].Records);
    }

    /// <param name="pad">把每条记录撑到多长。默认 0（短记录）；两卷记录长度不同，才谈得上
    /// "从上一卷的偏移接着数"会数出个错的数来——长度一样的两卷会让那个错误恰好抵消掉。</param>
    private static JournalRecord[] Records(int n, int pad = 0) =>
        [.. Enumerable.Range(0, n).Select(i => new JournalRecord
        {
            Kind = "blob", Ref = "data/" + i, Path = "file-" + i + ".bin",
            FullHash = "h" + i + new string('p', pad),
            HeadHash = "hh", TailHash = "tt", Length = 1000 + i,
        })];

    // 容器名带斜杠这种事不该把 journal 写到目录树外面去。
    [Fact]
    public void PathFor_flattens_container_names()
    {
        var p = _store.PathFor(9, "a/b", "run-a");
        Assert.StartsWith(_root, p);
        Assert.DoesNotContain("a/b", p.Replace(Path.DirectorySeparatorChar, '/'));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }
}
