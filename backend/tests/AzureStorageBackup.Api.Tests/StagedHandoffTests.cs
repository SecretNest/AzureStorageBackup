using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The ownership guard for one entry of the staged queue. Everything here is about the release happening
/// **exactly once**: the pool quota lives on a process-wide singleton, so leaking it keeps that space booked
/// until the process restarts, and since the quota gates output for every run, enough leaks stall compression
/// process-wide (see StagingArea's remarks on Hold).
/// </summary>
public sealed class StagedHandoffTests : IDisposable
{
    private readonly string _root;
    private readonly StagingArea _area;

    public StagedHandoffTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "asb-handoff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _area = new StagingArea(
            Path.Combine(_root, "compress"), Path.Combine(_root, "staged"), () => 1_000_000);
    }

    public void Dispose()
    {
        _area.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private Task<StagedItem> Stage(string name, int size) => _area.StageAsync(async (dir, ct) =>
    {
        var path = Path.Combine(dir, name);
        await File.WriteAllBytesAsync(path, new byte[size], ct);
        return (IReadOnlyList<string>)new[] { path };
    });

    [Fact]
    public async Task Dispose_Hands_The_Pool_Quota_Back()
    {
        var staged = await Stage("a", 500);
        Assert.Equal(500, _area.StagedBytes);

        using (new StagedHandoff(_area, staged)) { }

        Assert.Equal(0, _area.StagedBytes);
    }

    /// <summary>Double release drives the watermark negative, and after that backpressure never blocks compression again.</summary>
    [Fact]
    public async Task Dispose_Is_Idempotent()
    {
        var staged = await Stage("b", 500);
        var handoff = new StagedHandoff(_area, staged);

        handoff.Dispose();
        handoff.Dispose();

        Assert.Equal(0, _area.StagedBytes);
    }

    /// <summary>Discarded before it reached the cloud: the latecomers blocked on this content must be woken, or they hang for the rest of the run.</summary>
    [Fact]
    public async Task Dispose_Fails_The_Reservation_When_It_Was_Never_Settled()
    {
        var staged = await Stage("c", 100);
        Exception? abandoned = null;

        using (new StagedHandoff(_area, staged, ex => abandoned = ex)) { }

        Assert.NotNull(abandoned);
    }

    /// <summary>
    /// After a successful upload the reservation was already answered by Resolution.Complete. Failing it now would
    /// still run the reservation's release(), withdrawing the claim, and the next file with the same content would
    /// upload the very same bytes a second time.
    /// </summary>
    [Fact]
    public async Task Dispose_Leaves_A_Settled_Reservation_Alone()
    {
        var staged = await Stage("d", 100);
        var failed = false;

        var handoff = new StagedHandoff(_area, staged, _ => failed = true);
        handoff.MarkSettled();
        handoff.Dispose();

        Assert.False(failed);
        Assert.Equal(0, _area.StagedBytes);
    }

    /// <summary>7z can drop every member of a group, leaving no archive at all — the guard still has to be constructible.</summary>
    [Fact]
    public void A_Null_Archive_Releases_Nothing_And_Does_Not_Throw()
    {
        using var handoff = new StagedHandoff(_area, staged: null);
        Assert.Null(handoff.Staged);
    }
}
