using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class VersionIndexFileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "asb-idxstore-" + Guid.NewGuid().ToString("N"));
    private readonly VersionIndexFileStore _store;

    public VersionIndexFileStoreTests() => _store = new VersionIndexFileStore(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static byte[] Body(string marker) => System.Text.Encoding.UTF8.GetBytes(marker);

    [Fact]
    public async Task Round_trips_a_body_under_a_matching_identity()
    {
        await _store.WriteAsync(1, "photos", 3, identityTicks: 100, Body("index-bytes"));

        Assert.Equal(Body("index-bytes"), await _store.ReadAsync(1, "photos", 3, 100));
    }

    [Fact]
    public async Task Absent_entry_is_a_miss_rather_than_an_error()
    {
        Assert.Null(await _store.ReadAsync(1, "photos", 3, 100));
        Assert.Null(await _store.ReadAsync(9, "never-written", 1, 0));
    }

    /// <summary>The whole point of the header: a rebuilt container's identity moves, and the old entry must not be served.
    /// Rejecting it costs 24 bytes of reading, where the row this replaced had to load the entire index first.</summary>
    [Fact]
    public async Task Identity_mismatch_is_a_miss()
    {
        await _store.WriteAsync(1, "photos", 3, identityTicks: 100, Body("old"));

        Assert.Null(await _store.ReadAsync(1, "photos", 3, identityTicks: 200));
    }

    [Fact]
    public async Task Writing_again_replaces_the_entry_and_leaves_no_temporary_file()
    {
        await _store.WriteAsync(1, "photos", 3, 100, Body("first"));
        await _store.WriteAsync(1, "photos", 3, 100, Body("second"));

        Assert.Equal(Body("second"), await _store.ReadAsync(1, "photos", 3, 100));
        var dir = Path.GetDirectoryName(_store.PathFor(1, "photos", 3))!;
        Assert.Empty(Directory.EnumerateFiles(dir, "*.tmp-*"));
    }

    /// <summary>A file cut short — a power failure mid-write, a filesystem that lost the tail — must read as a miss and
    /// send the caller to the cloud, not deserialize into a plausible-looking index that is missing half its entries.</summary>
    [Fact]
    public async Task A_truncated_entry_is_a_miss()
    {
        await _store.WriteAsync(1, "photos", 3, 100, Body("a much longer body than the header"));

        var path = _store.PathFor(1, "photos", 3);
        using (var f = new FileStream(path, FileMode.Open, FileAccess.Write))
            f.SetLength(f.Length - 5);

        Assert.Null(await _store.ReadAsync(1, "photos", 3, 100));
    }

    [Fact]
    public async Task A_file_that_is_not_ours_is_a_miss()
    {
        var path = _store.PathFor(1, "photos", 3);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "this is not a version index at all");

        Assert.Null(await _store.ReadAsync(1, "photos", 3, 100));
    }

    [Fact]
    public async Task Remove_drops_one_version_and_leaves_the_others()
    {
        await _store.WriteAsync(1, "photos", 1, 100, Body("v1"));
        await _store.WriteAsync(1, "photos", 2, 100, Body("v2"));

        _store.Remove(1, "photos", 1);

        Assert.Null(await _store.ReadAsync(1, "photos", 1, 100));
        Assert.Equal(Body("v2"), await _store.ReadAsync(1, "photos", 2, 100));
    }

    [Fact]
    public void Removing_something_that_is_not_there_is_not_an_error()
    {
        _store.Remove(1, "photos", 1);
        _store.RemoveForContainer(1, "photos");
    }

    [Fact]
    public async Task RemoveForContainer_spares_other_containers_and_other_accounts()
    {
        await _store.WriteAsync(1, "photos", 1, 100, Body("target"));
        await _store.WriteAsync(1, "photos", 2, 100, Body("target"));
        await _store.WriteAsync(1, "docs", 1, 100, Body("other container"));
        await _store.WriteAsync(2, "photos", 1, 100, Body("other account"));

        _store.RemoveForContainer(1, "photos");

        Assert.Null(await _store.ReadAsync(1, "photos", 1, 100));
        Assert.Null(await _store.ReadAsync(1, "photos", 2, 100));
        Assert.Equal(Body("other container"), await _store.ReadAsync(1, "docs", 1, 100));
        Assert.Equal(Body("other account"), await _store.ReadAsync(2, "photos", 1, 100));
    }

    /// <summary>
    /// Container names are flattened before they become a path segment. Azure will not hand us a name containing a
    /// separator today, but <see cref="VersionIndexFileStore.RemoveForContainer"/> is a recursive delete, and one
    /// <c>..</c> reaching a path segment would take a sibling container's cache with it.
    /// </summary>
    [Fact]
    public async Task Container_names_cannot_escape_their_own_directory()
    {
        await _store.WriteAsync(1, "photos", 1, 100, Body("must survive"));
        await _store.WriteAsync(1, "../photos", 1, 100, Body("hostile"));

        _store.RemoveForContainer(1, "../photos");

        Assert.Equal(Body("must survive"), await _store.ReadAsync(1, "photos", 1, 100));
        Assert.StartsWith(Path.GetFullPath(_root), Path.GetFullPath(_store.PathFor(1, "../photos", 1)));
    }
}
