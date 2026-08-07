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
