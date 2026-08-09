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
    /// A volume whose header does not read is void as a whole: it shows up in no listing, and its records must not be mixed into the "don't delete me" list.
    /// The second half is the crux — that list is the other half of the cleaner's delete test, and one entry too many means a block
    /// that should have retired can never be deleted; and when a volume's header will not even parse, which run and which baseline its records belong to is unanswerable.
    /// </summary>
    [Fact]
    public async Task A_volume_with_an_unreadable_header_is_skipped_whole()
    {
        await WriteRunAsync("run-good",
            new JournalRecord { Kind = "blob", Ref = "data/good", Path = "p1", FullHash = "good" });
        await WriteRunAsync("run-torn");
        // The first line is not JSON, yet a perfectly well-formed record follows it: voiding must be a whole-volume affair, not "skip the bad line".
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
    /// An unchanged volume must not be walked again. With the UI open this method is called once every 5 seconds for every config,
    /// and one journal can grow to hundreds of MB — rewalking means hundreds of MB of reads per minute, contending for the very disk the backup itself is reading.
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
        Assert.Equal(afterFirst, _store.BytesScanned);   // not one further byte read
    }

    /// <summary>A few more records appended: count only the new stretch, and the number that comes out must be the new total.</summary>
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
    /// A half line must not be counted twice either. This file is not fsynced per record, so a snapshot can easily land mid-line;
    /// resume counting from the end of the file and the second half of that partial line gets counted as a line all over again, so the count on screen grows with every refresh.
    /// </summary>
    [Fact]
    public async Task A_line_that_was_only_half_written_is_not_counted_twice()
    {
        await WriteRunAsync("run-a", Records(2));
        var path = _store.PathFor(9, "cont", "run-a");
        // Half a record, no newline — this is what crashing mid-write looks like.
        await File.AppendAllTextAsync(path, "{\"Kind\":\"blob\",\"Ref\":\"data/hal");
        Assert.Equal(3, (await _store.PeekAsync(9, "cont", default))[0].Records);   // the partial line counts as a line, per ReadLine's old rule

        // Complete the second half, then write one more whole record.
        await File.AppendAllTextAsync(path, "f\",\"Path\":\"h\",\"FullHash\":\"h\"}\n");
        await File.AppendAllTextAsync(path, "{\"Kind\":\"blob\",\"Ref\":\"data/z\",\"Path\":\"z\",\"FullHash\":\"z\"}\n");
        Assert.Equal(4, (await _store.PeekAsync(9, "cont", default))[0].Records);
    }

    /// <summary>
    /// The same path now holds a different volume (another run rewrote it): not one line of the old count still holds, it must be counted from scratch.
    /// The test rests on StartedAt in the header rather than on length — the rewritten file can easily be longer than the old one.
    /// </summary>
    [Fact]
    public async Task A_journal_replaced_by_another_run_is_counted_from_scratch()
    {
        await WriteRunAsync("run-a", Records(2, pad: 4000));
        Assert.Equal(2, (await _store.PeekAsync(9, "cont", default))[0].Records);

        // Same name, same path, another run (a different StartedAt), **more** bytes but a different record count: a memo that looks
        // only at length and treats "longer" as appended would resume from the previous volume's offset and splice the two volumes' line counts into a number belonging to neither.
        await using (var j = await _store.CreateAsync(
            9, "cont", "run-a", Header("run-a") with { StartedAt = DateTimeOffset.UnixEpoch.AddDays(1) }, default))
            foreach (var r in Records(5, pad: 1800))
                await j.AppendAsync(r, default);

        Assert.Equal(5, (await _store.PeekAsync(9, "cont", default))[0].Records);

        // The other way round: swap in a shorter volume, and it must not hand back the old number either.
        await using (var j = await _store.CreateAsync(
            9, "cont", "run-a", Header("run-a") with { StartedAt = DateTimeOffset.UnixEpoch.AddDays(2) }, default))
            await j.AppendAsync(Records(1)[0], default);
        Assert.Equal(1, (await _store.PeekAsync(9, "cont", default))[0].Records);
    }

    /// <param name="pad">How long to pad each record out to. Default 0 (short records); only when the two volumes' records differ in
    /// length does "resuming from the previous volume's offset" produce a wrong number — two volumes of equal length make that error cancel out exactly.</param>
    private static JournalRecord[] Records(int n, int pad = 0) =>
        [.. Enumerable.Range(0, n).Select(i => new JournalRecord
        {
            Kind = "blob", Ref = "data/" + i, Path = "file-" + i + ".bin",
            FullHash = "h" + i + new string('p', pad),
            HeadHash = "hh", TailHash = "tt", Length = 1000 + i,
        })];

    // A container name with a slash in it must not get the journal written outside the directory tree.
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
