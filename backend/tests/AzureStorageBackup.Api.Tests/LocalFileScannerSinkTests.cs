using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The scanner no longer owns "the whole tree as a list" — it hands each entry to a sink and lets the sink decide
/// what to do with it. These tests pin the two things that changed: a <see cref="ListScanSink"/> gives entries back
/// in walk order, not sorted (sorting became the caller's job); and a <see cref="WorkDbScanSink"/> classifies each
/// entry on the way in and writes it straight into the run's scratch database.
/// </summary>
public sealed class LocalFileScannerSinkTests : IDisposable
{
    private readonly string _scanRoot;
    private readonly string _workRoot;

    private static CancellationToken Ct => CancellationToken.None;

    public LocalFileScannerSinkTests()
    {
        var unique = Guid.NewGuid().ToString("N");
        _scanRoot = Path.Combine(Path.GetTempPath(), "asb-scan-sink-" + unique);
        _workRoot = Path.Combine(Path.GetTempPath(), "asb-scan-sink-work-" + unique);
        Directory.CreateDirectory(_scanRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scanRoot, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_workRoot, recursive: true); } catch { /* best effort */ }
    }

    private string WriteText(string relative, string content)
    {
        var full = Path.Combine(_scanRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>A file of an exact size, without allocating that many bytes in memory — <c>SetLength</c> makes a
    /// sparse file, which is all the scanner looks at (it only reads <c>FileInfo.Length</c> here, never content).</summary>
    private string WriteSized(string relative, long length)
    {
        var full = Path.Combine(_scanRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using (var fs = new FileStream(full, FileMode.Create))
            fs.SetLength(length);
        return full;
    }

    [Fact]
    public async Task Scanner_feeds_entries_to_the_sink_in_directory_walk_order_and_the_sink_sorts()
    {
        // A ListScanSink does not sort on its own: whatever order AddAsync was called in is the order Entries comes
        // back in. The old scanner sorted the list itself after the walk finished; that responsibility moved to
        // whoever reads the sink, not into the sink.
        var sink = new ListScanSink();
        var c = new ScannedEntry("c.txt", EntryKind.File, 1, DateTimeOffset.UnixEpoch, "0644");
        var a = new ScannedEntry("a.txt", EntryKind.File, 1, DateTimeOffset.UnixEpoch, "0644");
        var b = new ScannedEntry("b.txt", EntryKind.File, 1, DateTimeOffset.UnixEpoch, "0644");
        await sink.AddAsync(c, Ct);
        await sink.AddAsync(a, Ct);
        await sink.AddAsync(b, Ct);

        Assert.Equal([c, a, b], sink.Entries);

        // The scanner itself: what used to come back sorted from ScanAsync's old ScanResult still has to be
        // reachable — it is just that now it takes an explicit sort, applied by the caller, not the scanner.
        WriteText("z.txt", "z");
        WriteText("m/n.txt", "n");
        WriteText("a.txt", "a");

        var treeSink = new ListScanSink();
        var summary = await new LocalFileScanner().ScanAsync(_scanRoot, new IgnoreRuleSet([]), treeSink);

        Assert.Equal(3, summary.Entries);
        var expected = new[] { "a.txt", "m/n.txt", "z.txt" };
        // Order-agnostic: the walk order is whatever the filesystem enumerates, not necessarily sorted.
        Assert.Equal(expected.ToHashSet(), treeSink.Entries.Select(e => e.Path).ToHashSet());
        // Sorted, the sink's own contents reproduce exactly what the old sorted ScanResult would have held.
        Assert.Equal(expected, treeSink.Entries.OrderBy(e => e.Path, StringComparer.Ordinal).Select(e => e.Path));
    }

    [Fact]
    public async Task WorkDbScanSink_writes_category_and_group_key()
    {
        WriteText("d/small1.txt", "one");
        WriteText("d/small2.txt", "two");
        WriteSized("big.bin", 6L * 1024 * 1024); // over the 5M single-file threshold

        await using var db = await new RunWorkDbFactory(_workRoot).CreateAsync("run", Ct);
        var sink = new WorkDbScanSink(db, new PlanOptions());

        await new LocalFileScanner().ScanAsync(_scanRoot, new IgnoreRuleSet([]), sink);
        await db.FlushAsync(Ct);

        var byPath = new Dictionary<string, ScanRow>(StringComparer.Ordinal);
        await foreach (var row in db.ScanOrderedAsync(Ct))
            byPath[row.Path] = row;

        Assert.Equal(3, byPath.Count);
        Assert.Equal((FileCategory.DirectoryGroup, "d"), (byPath["d/small1.txt"].Category, byPath["d/small1.txt"].GroupKey));
        Assert.Equal((FileCategory.DirectoryGroup, "d"), (byPath["d/small2.txt"].Category, byPath["d/small2.txt"].GroupKey));
        Assert.Equal((FileCategory.SingleFile, (string?)null), (byPath["big.bin"].Category, byPath["big.bin"].GroupKey));

        var candidates = new List<(string Dir, int Count)>();
        await foreach (var c in db.DirectoryCandidatesAsync(Ct))
            candidates.Add(c);

        Assert.Equal([("d", 2)], candidates);
    }
}
