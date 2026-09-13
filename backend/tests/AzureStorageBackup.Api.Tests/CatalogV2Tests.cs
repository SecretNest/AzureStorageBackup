using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class CatalogV2Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "asb-catalog-v2-tests", Guid.NewGuid().ToString("N"));
    public CatalogV2Tests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private Task<VersionCatalog> OpenAsync() =>
        VersionCatalog.OpenAsync(Path.Combine(_dir, "catalog.db"), readOnly: false, CancellationToken.None);

    internal static IndexEntry Entry(string path, long length, string? hash = null, StorageRef? storage = null) => new()
    {
        Path = path, Kind = "file", Length = length, Mtime = DateTimeOffset.UnixEpoch.AddSeconds(length), Permissions = "0644",
        HeadHash = hash is null ? null : hash + ":head", TailHash = hash is null ? null : hash + ":tail", FullHash = hash,
        Storage = storage,
    };

    [Fact]
    public void SameEntry_compares_every_field_including_the_volume_sizes()
    {
        var a = Entry("a.bin", 5, "xxh128:a", new StorageRef { Kind = "blob", Ref = "data/a", Volumes = 2, VolumeSizes = [3, 2] });
        var same = a with { Storage = new StorageRef { Kind = "blob", Ref = "data/a", Volumes = 2, VolumeSizes = [3, 2] } };
        var sizes = a with { Storage = new StorageRef { Kind = "blob", Ref = "data/a", Volumes = 2, VolumeSizes = [3, 3] } };
        var mtime = a with { Mtime = a.Mtime.AddSeconds(1) };
        var unreadable = a with { UnreadableAt = DateTimeOffset.UnixEpoch };
        var noStorage = a with { Storage = null };

        Assert.True(EntryRowMapper.SameEntry(a, same));
        Assert.False(EntryRowMapper.SameEntry(a, sizes));
        Assert.False(EntryRowMapper.SameEntry(a, mtime));
        Assert.False(EntryRowMapper.SameEntry(a, unreadable));
        Assert.False(EntryRowMapper.SameEntry(a, noStorage));
        Assert.True(EntryRowMapper.SameEntry(noStorage, noStorage with { }));
    }

    [Fact]
    public void An_index_stream_reader_exposes_its_input_for_a_second_pass()
    {
        var bytes = LegacyIndexSerializer.SerializeIndex(IndexSamples.Sample());
        using var stream = new MemoryStream(bytes);
        using var reader = new IndexStreamReader(stream);
        Assert.Same(stream, reader.Input);
        foreach (var _ in reader.Entries()) { }
        Assert.Equal(2, reader.ReadEmptyDirs().Count);
        stream.Position = 0;
        using var again = new IndexStreamReader(reader.Input);
        Assert.Equal(reader.EntryCount, again.Entries().Count());
    }
}
