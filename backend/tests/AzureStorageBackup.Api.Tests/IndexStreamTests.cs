using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using static AzureStorageBackup.Api.Tests.IndexAssert;

namespace AzureStorageBackup.Api.Tests;

public class IndexStreamTests
{
    private static VersionIndex Sample() => IndexSamples.Sample();

    [Fact]
    public void Writer_matches_IndexSerializer_byte_for_byte()
    {
        var index = Sample();
        using var ms = new MemoryStream();
        using (var w = new IndexStreamWriter(ms))
        {
            w.WriteHeader(index.Version, index.Entries.Count);
            foreach (var e in index.Entries) w.WriteEntry(e);
            w.WriteEmptyDirs(index.EmptyDirs);
            w.WriteUnrecoverable(index.UnrecoverablePaths);
        }
        Assert.Equal(IndexSerializer.SerializeIndex(index), ms.ToArray());
    }

    [Fact]
    public void Reader_reads_what_IndexSerializer_wrote()
    {
        var index = Sample();
        using var r = new IndexStreamReader(new MemoryStream(IndexSerializer.SerializeIndex(index)));
        Assert.Equal(7, r.Version);
        Assert.Equal(4, r.EntryCount);
        var entries = r.Entries().ToList();
        Assert.Equal(index.Entries.Count, entries.Count);
        for (var i = 0; i < index.Entries.Count; i++)
            AssertSameEntry(index.Entries[i], entries[i]);
        Assert.Equal(index.EmptyDirs, r.ReadEmptyDirs());
        Assert.Equal(index.UnrecoverablePaths, r.ReadUnrecoverable());
    }

    [Fact]
    public void Reader_rejects_a_newer_format()
    {
        var bytes = IndexSerializer.SerializeIndex(Sample());
        bytes[0] = 99;
        Assert.Throws<NotSupportedException>(() => new IndexStreamReader(new MemoryStream(bytes)));
    }

    [Fact]
    public void Empty_index_round_trips()
    {
        var index = new VersionIndex { Version = 1 };
        using var ms = new MemoryStream();
        using (var w = new IndexStreamWriter(ms)) { w.WriteHeader(1, 0); w.WriteEmptyDirs([]); w.WriteUnrecoverable([]); }
        Assert.Equal(IndexSerializer.SerializeIndex(index), ms.ToArray());
    }
}
