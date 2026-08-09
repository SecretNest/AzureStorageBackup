using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class LocalDedupResolverTests
{
    private static readonly BlobAddressScheme Plain = new(null, null);

    private static VersionIndex IndexWith(params IndexEntry[] entries) =>
        new() { Version = 1, Entries = [.. entries] };

    private static IndexEntry Blob(string fullHash, long len, string head, string tail, string @ref, bool raw = false, int volumes = 1) =>
        new()
        {
            Path = @ref, Kind = "file", Length = len, Permissions = "644",
            HeadHash = head, TailHash = tail, FullHash = fullHash,
            Storage = new StorageRef { Kind = "blob", Ref = @ref, Raw = raw, Volumes = volumes },
        };

    [Fact]
    public async Task Dedups_Against_Prior_Version_Content()
    {
        var r = LocalDedupResolver.Build(Plain, [IndexWith(
            Blob("xxh128:h", 100, "xxh128:hd", "xxh128:tl", "data/xxh128:h", raw: true, volumes: 3))]);

        var res = await r.ResolveAsync("xxh128:h", 100, "xxh128:hd", "xxh128:tl");

        Assert.True(res.Exists);
        Assert.Equal("data/xxh128:h", res.Ref);
        Assert.True(res.Existing!.Raw);          // inherits the existing blob's raw
        Assert.Equal(3, res.Existing.Volumes);   // inherits the existing volume count (no cloud CountVolumes)
        Assert.False(res.Collision);
    }

    [Fact]
    public async Task New_Content_Claims_Base_Address()
    {
        var r = LocalDedupResolver.Build(Plain, []);
        var res = await r.ResolveAsync("xxh128:new", 10, "xxh128:h", "xxh128:t");

        Assert.False(res.Exists);                 // needs uploading
        Assert.Equal("data/xxh128:new", res.Ref);
        Assert.False(res.Collision);
    }

    [Fact]
    public async Task Same_Hash_Different_Content_Avoids_To_Suffix()
    {
        // Existing blob: same hash, length 100. New file has the same hash but length 200 (a collision) → step aside to …~1.
        var r = LocalDedupResolver.Build(Plain, [IndexWith(
            Blob("xxh128:h", 100, "xxh128:hd", "xxh128:tl", "data/xxh128:h"))]);

        var res = await r.ResolveAsync("xxh128:h", 200, "xxh128:hd2", "xxh128:tl2");

        Assert.False(res.Exists);
        Assert.Equal("data/xxh128:h~1", res.Ref); // the fallback name after stepping aside
        Assert.True(res.Collision);
    }

    /// <summary>
    /// Folded in when Task 10 adopts a journal: blocks in <c>confirmed</c> must get exactly the same dedup
    /// treatment as blocks in the index. This one covers the first of the three — <c>byContent</c>: a direct
    /// cross-version dedup hit.
    /// </summary>
    [Fact]
    public async Task Confirmed_Blob_Dedups_Like_An_Indexed_One()
    {
        var confirmed = new[]
        {
            new ConfirmedBlob("xxh128:h", 100, "xxh128:hd", "xxh128:tl",
                new ResolvedBlob("data/xxh128:h", Raw: true, Volumes: 2, VolumeSizes: [60, 40])),
        };
        var r = LocalDedupResolver.Build(Plain, [], confirmed);

        var res = await r.ResolveAsync("xxh128:h", 100, "xxh128:hd", "xxh128:tl");

        Assert.True(res.Exists);
        Assert.Equal("data/xxh128:h", res.Ref);
        Assert.True(res.Existing!.Raw);
        Assert.Equal(2, res.Existing.Volumes);
        Assert.False(res.Collision);
    }

    /// <summary>
    /// Second of the three — <c>refs</c>: the address a confirmed block occupies must fend off collisions just the
    /// same. Feed in byContent but not refs and a new file with the same hash but different content will claim that
    /// address as if it were free instead of stepping aside to …~1 — which means writing the new content straight
    /// onto the address the confirmed block is holding.
    /// </summary>
    [Fact]
    public async Task Confirmed_Blob_Ref_Is_Guarded_Against_Collision()
    {
        var confirmed = new[]
        {
            new ConfirmedBlob("xxh128:h", 100, "xxh128:hd", "xxh128:tl",
                new ResolvedBlob("data/xxh128:h", Raw: true, Volumes: 1, VolumeSizes: [100])),
        };
        var r = LocalDedupResolver.Build(Plain, [], confirmed);

        // Same hash (the address scheme only looks at fullHash), different content (length/head/tail all changed) → collision, must step aside.
        var res = await r.ResolveAsync("xxh128:h", 200, "xxh128:hd2", "xxh128:tl2");

        Assert.False(res.Exists);
        Assert.Equal("data/xxh128:h~1", res.Ref);
        Assert.True(res.Collision);
    }

    /// <summary>
    /// Third of the three — <c>heads</c>: the prescreen must be able to see confirmed blocks, otherwise a file with
    /// the same content at a different path gets ruled "no candidate" right at the prescreen and is recompressed for
    /// nothing (see the notes on JournalResume.ConfirmedBlobs).
    /// </summary>
    [Fact]
    public void Confirmed_Blob_Participates_In_Prescreen()
    {
        var confirmed = new[]
        {
            new ConfirmedBlob("xxh128:h", 100, "xxh128:hd", "xxh128:tl",
                new ResolvedBlob("data/xxh128:h", Raw: true, Volumes: 1, VolumeSizes: [100])),
        };
        var r = LocalDedupResolver.Build(Plain, [], confirmed);

        Assert.True(r.MayDeduplicate(100, "xxh128:hd"));
    }

    [Fact]
    public async Task Same_Run_Duplicate_Waits_For_First_Uploader()
    {
        var r = LocalDedupResolver.Build(Plain, []);

        var first = await r.ResolveAsync("xxh128:d", 5, "xxh128:h", "xxh128:t");
        Assert.False(first.Exists); // the first one → claims and uploads

        // The second one with the same content: it must not finish resolving before the first one Completes (it waits on the uploader).
        var secondTask = r.ResolveAsync("xxh128:d", 5, "xxh128:h", "xxh128:t");
        Assert.False(secondTask.IsCompleted);

        first.Complete(raw: true, volumes: 2, volumeSizes: [111, 222]); // the first upload succeeded
        var second = await secondTask;

        Assert.True(second.Exists);                  // same-run dedup
        Assert.Equal(first.Ref, second.Ref);
        Assert.True(second.Existing!.Raw);           // gets the same raw as the first
        Assert.Equal(2, second.Existing.Volumes);    // the same volume count as the first
        Assert.Equal([111L, 222L], second.Existing.VolumeSizes); // the same volume sizes as the first
    }

    [Fact]
    public async Task Same_Run_Duplicate_Fails_If_First_Upload_Fails()
    {
        var r = LocalDedupResolver.Build(Plain, []);
        var first = await r.ResolveAsync("xxh128:d", 5, "xxh128:h", "xxh128:t");
        var secondTask = r.ResolveAsync("xxh128:d", 5, "xxh128:h", "xxh128:t");

        first.Fail(new InvalidOperationException("upload boom"));

        // The latecomer fails along with it, never deduping onto a blob that was not uploaded successfully.
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await secondTask);
    }

    /// <summary>Task 7 regression: the gate retries a failed upload as the same whole work item, and the retry uses
    /// the **same** content identity. After the first claim fails the ref must be given back — if it is not, the
    /// retrier runs into that already-dead claim, waits on a Completion that will never succeed and replays the very
    /// same exception, and the gate's "back off and try once more" becomes decoration:
    /// however long it waits, however many times it lets work through, the second real upload attempt never happens.</summary>
    [Fact]
    public async Task Retry_After_Failure_Gets_A_Fresh_Claim_Not_The_Stale_One()
    {
        var r = LocalDedupResolver.Build(Plain, []);

        var first = await r.ResolveAsync("xxh128:d", 5, "xxh128:h", "xxh128:t");
        Assert.False(first.Exists);
        first.Fail(new InvalidOperationException("upload boom"));

        // Whole-item retry: the same content identity comes round again and must get a brand-new claim that has
        // never failed, not a replay of last time's exception.
        var retry = await r.ResolveAsync("xxh128:d", 5, "xxh128:h", "xxh128:t");
        Assert.False(retry.Exists);
        Assert.Equal(first.Ref, retry.Ref);

        // This claim really can be completed by a later upload — proof it is not sharing the previous, already-dead TaskCompletionSource.
        retry.Complete(raw: false, volumes: 1, volumeSizes: [5]);
    }

    /// <summary>
    /// Giving the ref back quietly turned the reservation table's indexer from **total** into **partial**: a
    /// latecomer that loses the claim race then reads <c>_run[refName]</c> once, and the holder may fail and pull
    /// that record right between those two steps.
    /// <para>
    /// The consequence of hitting that is not "one more retry": <see cref="KeyNotFoundException"/> is not among
    /// <see cref="TransientErrors"/>'s transient criteria, so the gate cannot catch it at all and the whole backup
    /// run is declared dead — precisely the outcome the gate exists to oppose. And it only shows up in a failure
    /// storm (several workers, the same content, failures and table lookups crowded together), which is exactly the
    /// moment the gate is most needed.
    /// </para>
    /// <para>
    /// The window is only the few instructions between "lost the claim" and "read the table", and a single
    /// starting-gun stampede never hits it (tried it: the holder was done withdrawing long before the latecomer even
    /// touched the table). So this churns **continuously** instead: a crowd of workers repeatedly "claim it, then
    /// kill it on the spot" on the same address, so at any instant someone is withdrawing a claim while someone else
    /// is stuck just past a lost race — the two already crowd the same stretch of code, no contriving needed.
    /// Failure is decided by an assertion, not by a timeout: a single <see cref="KeyNotFoundException"/>
    /// turns it red.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Losing_The_Claim_Race_Survives_The_Holder_Failing_At_That_Instant()
    {
        var r = LocalDedupResolver.Build(Plain, []);
        var stop = DateTime.UtcNow.AddMilliseconds(1500);
        var workers = Math.Max(4, Environment.ProcessorCount);

        var tasks = new Task[workers];
        for (var i = 0; i < workers; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                while (DateTime.UtcNow < stop)
                {
                    try
                    {
                        var res = await r.ResolveAsync("xxh128:d", 5, "xxh128:h", "xxh128:t");
                        // Claim it and kill it on the spot: that one call both withdraws the claim and releases every
                        // latecomer waiting on it, and the address is immediately free for the next taker — that is
                        // where the churn comes from.
                        if (!res.Exists)
                            res.Fail(new InvalidOperationException("upload boom"));
                    }
                    catch (InvalidOperationException)
                    {
                        // Latecomers fail along with the holder: existing behaviour, unchanged.
                    }
                    // Not one KeyNotFoundException is allowed, so any other exception propagates as-is.
                }
            });
        }

        await Task.WhenAll(tasks);
    }
}
