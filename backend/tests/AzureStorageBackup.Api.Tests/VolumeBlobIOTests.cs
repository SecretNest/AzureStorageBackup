using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class VolumeBlobIOTests
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private static Account AzuriteAccount() => new()
    {
        Name = "azurite",
        BlobEndpoint = "http://127.0.0.1:10000/devstoreaccount1",
        AccountKeyProtected = TestSecrets.Protect(AzuriteKey),
        Region = AzureRegion.Global,
    };

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static string RandomName(string p) => p + Guid.NewGuid().ToString("N")[..8];

    /// <summary>A fake uploader that records the upload order.</summary>
    private sealed class RecordingUploader : IBlobUploader
    {
        public List<string> Order { get; } = [];

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            Order.Add(blobName);
            return Task.FromResult(true);
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            Order.Add(blobName);
            return Task.CompletedTask;
        }
    }

    private static Account Acc() => new() { Name = "a", BlobEndpoint = "http://x", AccountKeyProtected = TestSecrets.Protect("k") };

    /// <summary>A concurrency peak probe: nothing is let through until <paramref name="expectPeak"/> uploads are
    /// in flight at once. No sleeps guessing at timing — if parallelism never happens this waits until the
    /// timeout, <c>Max</c> stays at 1, and the assertion fails on its own.</summary>
    private sealed class ConcurrencyProbe(int expectPeak) : IBlobUploader
    {
        private readonly TaskCompletionSource _peak = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _gate = new();
        private int _current;

        public int Max { get; private set; }
        public List<string> Order { get; } = [];

        public async Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            var now = Interlocked.Increment(ref _current);
            lock (_gate)
            {
                Max = Math.Max(Max, now);
                Order.Add(blobName);
            }
            if (now >= expectPeak)
                _peak.TrySetResult();
            await Task.WhenAny(_peak.Task, Task.Delay(TimeSpan.FromSeconds(5), ct));
            Interlocked.Decrement(ref _current);
            return true;
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => throw new NotSupportedException();
    }

    private static VolumeUploadScope Scope(VolumeUploadGate gate, int perItem) =>
        new(gate, new StageTracker("Uploading", 0, static _ => { }), perItem);

    /// <summary>
    /// Records the order in which volumes start uploading and holds each one open until the test hands it back.
    /// Nothing finishes on a clock: the test says when a slot is released, which is what lets the case below
    /// arrange the contention it is about instead of racing the scheduler for it.
    /// </summary>
    private sealed class SteppedProbe : IBlobUploader
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, TaskCompletionSource> _held = new(StringComparer.Ordinal);
        private readonly List<string> _started = [];
        private bool _open;

        public int StartedCount { get { lock (_gate) return _started.Count; } }

        public IReadOnlyList<string> Started { get { lock (_gate) return [.. _started]; } }

        public async Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            TaskCompletionSource tcs;
            lock (_gate)
            {
                _started.Add(blobName);
                tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_open)
                    tcs.TrySetResult();
                else
                    _held[blobName] = tcs;
            }
            // A hang detector, not a timing assumption: in the healthy case the test hands this back explicitly.
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            return true;
        }

        /// <summary>Let one named volume finish, which releases its gate slot.</summary>
        public void Finish(string blobName)
        {
            TaskCompletionSource tcs;
            lock (_gate)
                tcs = _held[blobName];
            tcs.TrySetResult();
        }

        /// <summary>Let everything still held finish, and everything starting afterwards go straight through.</summary>
        public void FinishAll()
        {
            List<TaskCompletionSource> all;
            lock (_gate)
            {
                _open = true;
                all = [.. _held.Values];
            }
            foreach (var tcs in all)
                tcs.TrySetResult();
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => throw new NotSupportedException();
    }

    /// <summary>Poll until <paramref name="condition"/> holds, failing with <paramref name="what"/> if it never does.</summary>
    private static async Task WaitFor(Func<bool> condition, Func<string> what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"timed out waiting for {what()}");
            await Task.Delay(5);
        }
    }

    /// <summary>
    /// When two volume families contend for slots, the older one **essentially uploads straight through as a
    /// whole family** and the newer one cannot cut in.
    /// <para>
    /// This guards the cost of an interruption. The journal is written and the in-flight ledger cleared only after
    /// the whole family is uploaded and the cloud has confirmed, so "how many families are half-done at once" is
    /// how many already-compressed, already-uploaded bytes a <c>Stop now</c> / suspend / crash throws away.
    /// First-come-first-served spreads the slots thin across every family in flight, leaving several stuck
    /// halfway; arbitrating by item age leaves essentially one.
    /// </para>
    /// <para>
    /// **Why the releases are stepped rather than timed.** A finished volume releases its slot in
    /// <c>RunAsync</c>'s finally, while this family's next volume cannot queue up until the <c>WhenAny</c>
    /// continuation gets to run — there is a crack between those two things, and a slot freed inside it goes to
    /// whoever is already on the gate. The relief line <c>WindowPerItem</c> keeps queued is what covers it. This
    /// case drives one release at a time so that what it measures is the arbitration and nothing else: an earlier
    /// version let uploads finish on a 20 ms timer and asserted a tolerance of "fewer than 2", which made a loaded
    /// machine, not the gate, decide the result — it failed 1 run in 4 under six busy loops.
    /// <para>
    /// So each release waits for the older family to be provably back on the gate (<c>WaitingOnSlot</c> is the
    /// gate's own waiter count, published by <c>BeginWait</c>/<c>EndWait</c>) before the next slot is freed. The
    /// expected number of cut-ins is exactly 0, and the assertion is the whole start order rather than a bound.
    /// Under first-come-first-served the second release lands on <c>data/newer.001</c> instead of
    /// <c>data/older.004</c> and that assertion fails on its first element — the two sides are still far apart.
    /// A wave of releases arriving together is the other half of the story, and it has a case of its own:
    /// <see cref="A_Wave_Of_Completions_Does_Not_Hand_A_Slot_To_The_Newer_Family"/>.
    /// </para>
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_Older_Archive_Is_Not_Interleaved_With_A_Newer_One()
    {
        const int capacity = 2;
        var up = new SteppedProbe();
        var gate = new VolumeUploadGate(capacity);
        StageProgress? latest = null;
        // The injected clock also switches off the heartbeat timer, so the only snapshots published are the forced
        // ones from BeginWait/EndWait — i.e. exactly the gate's own arrivals and departures.
        using var tracker = new StageTracker("Uploading", 0, p => Volatile.Write(ref latest, p)) { Clock = () => 0 };
        var scope = new VolumeUploadScope(gate, tracker, maxParallelPerItem: capacity);
        // The older family is deliberately longer than its window, so that refilling it takes a real changeover
        // continuation rather than handing the whole family to the gate up front.
        var older = Enumerable.Range(1, 8).Select(i => $"/tmp/a.{i:000}").ToList();
        var newer = Enumerable.Range(1, 4).Select(i => $"/tmp/b.{i:000}").ToList();

        int Queued() => Volatile.Read(ref latest)?.WaitingOnSlot ?? 0;
        string Order() => string.Join(", ", up.Started);

        // The ticket is taken in UploadAsync's synchronous section, so the family called first is guaranteed to
        // be the older one — no sleeps guessing at timing.
        var first = VolumeBlobIO.UploadAsync(
            up, Acc(), "c", "data/older", older, AccessTier.Hot, scope: scope);
        var second = VolumeBlobIO.UploadAsync(
            up, Acc(), "c", "data/newer", newer, AccessTier.Hot, scope: scope);

        // The scene: both families have filled their window. The older family's first two volumes hold both slots;
        // everything else it queued — its relief line — and the newer family's whole window are on the gate.
        var onTheGate = scope.WindowPerItem - capacity + scope.WindowPerItem;
        await WaitFor(() => up.StartedCount == capacity && Queued() == onTheGate,
            () => $"both windows to reach the gate (started {Order()}, {Queued()} of {onTheGate} queued)");

        // Release 1. The best waiter with the older ticket is older.003, which has been on the gate since the
        // scene was built, so this one cannot fall into the changeover crack in either implementation.
        up.Finish("data/older.001");
        // Waiting for the count to come back is waiting for the older family's WhenAny continuation to have queued
        // its next volume. That is the precondition release 2 needs and the one the old version raced for.
        await WaitFor(() => up.StartedCount == 3 && Queued() == onTheGate,
            () => $"the freed slot to be taken and the next volume queued (started {Order()}, {Queued()} queued)");

        // Release 2 — the one that discriminates. Waiting on the gate now: the older family's relief line on the
        // smaller ticket, and the newer family's window. Age-based arbitration hands it to older.004;
        // first-come-first-served hands it to newer.001, which has been queued longer.
        up.Finish("data/older.002");
        await WaitFor(() => up.StartedCount == 4, () => $"a fourth volume to start (started {Order()})");

        Assert.Equal(
            ["data/older.001", "data/older.002", "data/older.003", "data/older.004"],
            up.Started);

        up.FinishAll();
        await Task.WhenAll(first, second);
        Assert.Equal(older.Count + newer.Count, up.StartedCount);
        Assert.Equal(capacity, gate.Free);   // every slot returned
    }

    /// <summary>
    /// **A whole wave of the older family's volumes landing at once must not leak a single slot.**
    /// <para>
    /// The case above releases one slot at a time and waits for the relief volume to be back on the gate before
    /// freeing the next. Production does not do that. Volumes are equal-sized (100 MB by default) and share one
    /// link, so the family's whole in-flight set is started together and finishes together — a wave of up to
    /// <c>capacity</c> releases inside one instant, with the changeover continuations still queued on the thread
    /// pool behind them. Every slot in that wave is handed out synchronously inside <c>Release</c>, to whoever is
    /// on the gate at that moment.
    /// </para>
    /// <para>
    /// With a relief line one volume deep, the first release lands on it and the rest of the wave lands on the
    /// newer family — its volume 1 first, since within a ticket the lowest volume wins. On screen that is the
    /// symptom this case exists for: a file that has only just finished preparing puts its <c>(1/xxx)</c> into the
    /// transfer list and finishes it ahead of every volume the previous file has left. The relief line therefore
    /// has to be as deep as the number of slots the family can hold, which is what <see cref="VolumeUploadScope.WindowPerItem"/> sizes it to.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_Wave_Of_Completions_Does_Not_Hand_A_Slot_To_The_Newer_Family()
    {
        const int capacity = 3;
        var up = new SteppedProbe();
        var gate = new VolumeUploadGate(capacity);
        StageProgress? latest = null;
        using var tracker = new StageTracker("Uploading", 0, p => Volatile.Write(ref latest, p)) { Clock = () => 0 };
        var scope = new VolumeUploadScope(gate, tracker, maxParallelPerItem: capacity);
        var older = Enumerable.Range(1, 20).Select(i => $"/tmp/a.{i:000}").ToList();
        var newer = Enumerable.Range(1, 20).Select(i => $"/tmp/b.{i:000}").ToList();

        int Queued() => Volatile.Read(ref latest)?.WaitingOnSlot ?? 0;
        string Order() => string.Join(", ", up.Started);

        var first = VolumeBlobIO.UploadAsync(
            up, Acc(), "c", "data/older", older, AccessTier.Hot, scope: scope);
        var second = VolumeBlobIO.UploadAsync(
            up, Acc(), "c", "data/newer", newer, AccessTier.Hot, scope: scope);

        // The scene: the older family holds every slot, and both families have their whole window on the gate —
        // the older one's relief line (window minus the slots it holds) and all of the newer one's.
        var onTheGate = scope.WindowPerItem - capacity + scope.WindowPerItem;
        await WaitFor(() => up.StartedCount == capacity && Queued() == onTheGate,
            () => $"both windows to reach the gate (started {Order()}, {Queued()} of {onTheGate} queued)");

        // The wave. Nothing is awaited between these: the three slots are freed inside one instant, which is what
        // three equal volumes sharing one link do, and none of the older family's changeover continuations has run.
        foreach (var v in new[] { "data/older.001", "data/older.002", "data/older.003" })
            up.Finish(v);

        await WaitFor(() => up.StartedCount == capacity * 2, () => $"the wave to be taken up (started {Order()})");
        // Sorted, because the gate decides **which** volumes are admitted and the thread pool decides in which
        // order their continuations get as far as the uploader. Only the first of those is this case's subject;
        // asserting the recording order as well would be asserting the scheduler.
        Assert.Equal(
            ["data/older.004", "data/older.005", "data/older.006"],
            up.Started.Skip(capacity).Take(capacity).OrderBy(v => v, StringComparer.Ordinal));

        up.FinishAll();
        await Task.WhenAll(first, second);
        Assert.Equal(capacity, gate.Free);   // every slot returned
    }

    /// <summary>
    /// Every volume goes up; nothing is required about the order. The first volume used to be sent last on its
    /// own as the "the whole family is here" commit marker, and that semantic was deleted along with cloud-side
    /// existence dedup — what it bought was doubling the upload time of 2–5 volume files (only one stream moves
    /// on that final trip), while dedup now looks only at the local authoritative index and never asks what the
    /// cloud has.
    /// </summary>
    [Fact]
    public async Task Every_Volume_Goes_Up_Regardless_Of_Order()
    {
        var up = new RecordingUploader();

        await VolumeBlobIO.UploadAsync(
            up, Acc(), "c", "data/h", ["/tmp/a.001", "/tmp/a.002", "/tmp/a.003"], AccessTier.Hot);

        Assert.Equal(["data/h.001", "data/h.002", "data/h.003"], [.. up.Order.Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// The volumes of one archive must upload in parallel, with the in-flight stream count bounded by the gate.
    /// <para>
    /// This test guards a real failure: when a large file split into thousands of volumes, it used to occupy
    /// **one** slot for that whole stretch, one volume finishing before the next began — the "concurrency 5" in
    /// the settings was meaningless while a large file uploaded, measured at only the 4–6 MB/s of a single TCP
    /// connection to Azure. Once slots were handed out per volume, the in-flight stream count no longer depends
    /// on whether the queue holds one huge file or ten thousand small ones.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Volumes_Of_One_Archive_Ride_The_Gate_In_Parallel()
    {
        var up = new ConcurrencyProbe(expectPeak: 2);
        var gate = new VolumeUploadGate(2);
        var files = Enumerable.Range(1, 7).Select(i => $"/tmp/a.{i:000}").ToList();

        await VolumeBlobIO.UploadAsync(
            up, Acc(), "c", "data/h", files, AccessTier.Hot, scope: Scope(gate, perItem: 4));

        Assert.Equal(2, up.Max);              // really parallel (>1) and never past the gate (≤2)
        Assert.Equal(7, up.Order.Count);
        Assert.Equal(2, gate.Free);   // every slot returned
    }

    /// <summary>
    /// Two volumes must really run concurrently too — this is exactly where sending the first volume separately
    /// at the end was most expensive.
    /// <para>
    /// With the default 100 MB volumes and concurrency 5, a 100–500 MB file splits into 2–5 volumes. Holding the
    /// first volume back to the end means such a file is always "one parallel round for the rest, then another
    /// round on its own for the first volume", doubling the item's total time — and that size band is the bulk of
    /// a real backup. The probe only lets go once 2 volumes are in flight at once: under the old implementation
    /// the peak could only be 1, so this case would hang until the timeout and fail.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_Volumes_Go_Up_At_The_Same_Time()
    {
        var up = new ConcurrencyProbe(expectPeak: 2);
        var gate = new VolumeUploadGate(5);

        await VolumeBlobIO.UploadAsync(
            up, Acc(), "c", "data/h", ["/tmp/a.001", "/tmp/a.002"], AccessTier.Hot,
            scope: Scope(gate, perItem: 5));

        Assert.Equal(2, up.Max);
        Assert.Equal(5, gate.Free);
    }

    /// <summary>Calls with no scope (repair/replace and other non-backup paths) keep the old behaviour: serial, one volume at a time.</summary>
    [Fact]
    public async Task Without_A_Scope_Volumes_Still_Go_Up_One_At_A_Time()
    {
        var up = new ConcurrencyProbe(expectPeak: 1);

        await VolumeBlobIO.UploadAsync(
            up, Acc(), "c", "data/h", ["/tmp/a.001", "/tmp/a.002", "/tmp/a.003"], AccessTier.Hot);

        Assert.Equal(1, up.Max);
    }

    [Fact]
    public async Task Single_Volume_Uploads_Base_Name()
    {
        var up = new RecordingUploader();

        await VolumeBlobIO.UploadAsync(up, Acc(), "c", "data/h", ["/tmp/a.7z"], AccessTier.Hot);

        Assert.Equal(["data/h"], up.Order);
    }

    /// <summary>A fake progress callback used to record whether each call returns a distinct instance.</summary>
    private sealed class SpyProgress : IProgress<long>
    {
        public long LastReported { get; private set; } = -1;
        public void Report(long value) => LastReported = value;
    }

    /// <summary>
    /// The symptom users actually saw (before the fix): if <c>DownloadAsync</c> takes the progress callback once
    /// and shares it across volumes, the bytes of a later volume are mistaken by the <c>DeltaProgress</c> inside
    /// <see cref="StageTracker"/> for "the previous volume rewinding and re-sending" and booked wrongly, so the
    /// restore/verify speed readout is distorted (see the method header comment on
    /// <c>VolumeBlobIO.DownloadAsync</c>).
    /// <para>
    /// This pins the literal contract of Part 1 — "the factory is called once per volume and hands back distinct
    /// instances" — rather than taking a detour through the total bytes <c>StageTracker</c> accumulates
    /// downstream: the latter was checked by mutation testing and is not sensitive to the size sequence 7z
    /// volumes naturally produce in this project, "every volume equal except the last, which is smallest"
    /// (<c>DeltaProgress</c>'s rewind detection self-corrects on such a sequence, so even a shared instance adds
    /// up to the right total and the defect goes undetected). The factory call count / instance identity is the
    /// only signal from this change guaranteed to show up under mutation.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task DownloadAsync_Calls_Progress_Factory_Once_Per_Volume_With_A_Fresh_Instance()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var account = AzuriteAccount();
        var name = RandomName("vbio-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            const string baseRef = "data/multi";
            var sizes = new[] { 5_000, 7_000, 3_000, 1_234 };
            for (var i = 0; i < sizes.Length; i++)
                await container.GetBlobClient($"{baseRef}.{i + 1:D3}")
                    .UploadAsync(new BinaryData(new byte[sizes[i]]), overwrite: true);

            var instances = new List<SpyProgress>();
            Func<IProgress<long>> makeProgress = () =>
            {
                var spy = new SpyProgress();
                instances.Add(spy);
                return spy;
            };

            var workDir = Path.Combine(Path.GetTempPath(), "asb-vbio-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);
            try
            {
                await VolumeBlobIO.DownloadAsync(container, baseRef, workDir, CancellationToken.None, makeProgress);
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
            }

            // Factory call count = volume count: if the implementation hoisted progress() out of the loop and
            // called it once, this would be 1 instead of 4.
            Assert.Equal(sizes.Length, instances.Count);
            // Every instance is distinct — in the ReferenceEquals sense this really is "one per volume", not the
            // same reference queued over and over.
            Assert.Equal(instances.Count, instances.Distinct().Count());
            // Each instance really received the final cumulative byte count of its own volume, proving the
            // factory's return value was actually wired into that volume's download, rather than an unused
            // instance being created while the real download shared some other callback.
            for (var i = 0; i < sizes.Length; i++)
                Assert.Equal(sizes[i], instances[i].LastReported);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>One volume hangs and never moves while the rest go through as usual; records who finished.</summary>
    private sealed class OneStuckVolume(string stuck, int expectOthers) : IBlobUploader
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _others = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _done;

        public Task OthersFinished => _others.Task;
        public void Release() => _release.TrySetResult();
        public List<string> Order { get; } = [];

        public async Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            if (blobName == stuck)
                await _release.Task.WaitAsync(ct);
            lock (Order) Order.Add(blobName);
            if (blobName != stuck && Interlocked.Increment(ref _done) >= expectOthers)
                _others.TrySetResult();
            return true;
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Volume concurrency slots are a **sliding window**: one volume finishes and one more starts immediately,
    /// without waiting for the rest of its batch.
    /// <para>
    /// It used to go batch by batch (<c>Task.WhenAll</c> as a barrier every N volumes), and the slowest volume in
    /// a batch made the other streams spin idle waiting for it. Volumes never take the same time — retries, block
    /// parallelism, server-side throttling all differ — so what showed on screen was "5 streams counting down to
    /// 0 one by one, then 5 more appearing" instead of holding steadily at 5.
    /// </para>
    /// <para>
    /// This test aims straight at that failure: window 3, 10 volumes total, the second one hanging forever. With
    /// refill working, the remaining 9 still all finish (the slow volume only occupies one slot); go back to the
    /// batched implementation and the very first batch is stuck on the slow volume, at most 2 volumes finish, and
    /// the wait below times out.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_Slow_Volume_Does_Not_Stall_The_Others()
    {
        // All ten volumes enter the window (the first is no longer the commit marker sent separately at the
        // end); .002 is the one that hangs, and the other 9 should keep running.
        var up = new OneStuckVolume("data/h.002", expectOthers: 9);
        var gate = new VolumeUploadGate(3);
        var files = Enumerable.Range(1, 10).Select(i => $"/tmp/a.{i:D3}").ToList();

        var upload = VolumeBlobIO.UploadAsync(
            up, Acc(), "c", "data/h", files, AccessTier.Hot, scope: Scope(gate, 3));

        await up.OthersFinished.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(upload.IsCompleted, "the slow volume is still hanging, so the whole item must not be done");

        up.Release();
        await upload.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(10, up.Order.Count);
    }

    /// <summary>One volume dies.</summary>
    /// <summary>
    /// Volume 1 succeeds first; volume 4 only fails <b>after</b> it, so both are complete by the time the loop
    /// reaches its first <c>WhenAny</c> — and <c>WhenAny</c> hands back the one that finished first, the success.
    /// That is precisely the arrangement under which a check that only inspects the returned task never sees the
    /// failure at all.
    /// </summary>
    private sealed class FailsAfterAnotherSucceeds : IBlobUploader
    {
        private readonly TaskCompletionSource _firstFinished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Order { get; } = [];

        public async Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            lock (Order) Order.Add(blobName);
            if (blobName == "data/h.004")
            {
                await _firstFinished.Task;
                throw new IOException("volume died");
            }

            await Task.Yield();
            if (blobName == "data/h.001")
                _firstFinished.SetResult();
            return true;
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// "Once a volume dies, no new ones start" has to hold whichever task WhenAny happens to return.
    /// <para>
    /// It returns <b>one</b> of the tasks that completed, and when several land together which one is unspecified.
    /// So a volume could fail while the loop was looking at a sibling that succeeded, and the loop would carry on
    /// starting volumes over a dead upload. In production every volume takes seconds to minutes, so the faulted one
    /// was nearly always the one WhenAny returned and the gap never showed; it surfaced on CI, where the doubles
    /// complete instantly and the ordering is arbitrary, as an intermittent red on
    /// <see cref="A_Dead_Volume_Stops_New_Ones_And_Leaves_Nothing_Running"/>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_failure_stops_new_volumes_even_when_a_sibling_completes_first()
    {
        var up = new FailsAfterAnotherSucceeds();
        var gate = new VolumeUploadGate(3);
        var files = Enumerable.Range(1, 8).Select(i => $"/tmp/a.{i:D3}").ToList();

        // The window is MaxParallelPerItem * 2 (see WindowPerItem), and it is sized to 4 here on purpose: this
        // volume dies on the **first changeover**, so the loop has to be made to reach one. Volumes 1-4 are opened
        // unconditionally and the fifth is the first the loop must stop at — which is where it looks at what
        // WhenAny handed back. Widen the window past 4 and the loop runs on to volume 6 before it ever pauses, and
        // what this asserts becomes a question of how fast the pool ran the doubles' continuations.
        await Assert.ThrowsAsync<IOException>(() => VolumeBlobIO.UploadAsync(
            up, Acc(), "c", "data/h", files, AccessTier.Hot, scope: Scope(gate, 2)));

        // Volume 4 is dead by that first changeover, so the fifth must never be reached.
        Assert.Equal(4, up.Order.Count);
        Assert.Equal(3, gate.Free);
    }

    private sealed class FailingVolume(string bad) : IBlobUploader
    {
        public List<string> Order { get; } = [];
        public int Finished;

        public async Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            await Task.Yield();
            lock (Order) Order.Add(blobName);
            if (blobName == bad)
                throw new IOException("volume died");
            Interlocked.Increment(ref Finished);
            return true;
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// When a volume dies: no new volumes start, and the ones already in flight are awaited before throwing.
    /// Letting go halfway leaves orphan tasks nobody observes, still holding gate slots and still reading volume
    /// files off the temp disk — while the layer above, having received the exception, is about to release the
    /// staging area.
    /// </summary>
    /// <summary>
    /// Ordering is pinned rather than hoped for. This assertion used to read <c>Order.Count &lt; files.Count</c>
    /// against volumes that all completed instantly, and it went red on CI roughly one run in ten: the loop only
    /// creates tasks, so under an unlucky schedule it can start every volume before the doomed one has run far
    /// enough to fault, and nothing is left to notice. Widening the window the loop checks does not fix that —
    /// a failure that has not happened yet cannot be observed.
    /// <para>
    /// So the double makes the failure happen first and hold everything else behind it: volume 4 throws the moment
    /// it is entered, and volumes 1-3 only return once it has. By the first changeover the fault is therefore a
    /// fact, and both routes out of the loop are exercised — whether WhenAny hands back the faulted task or one of
    /// the successes, the loop must stop. The gate is sized to admit all four so nothing queues behind a slot.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_Dead_Volume_Stops_New_Ones_And_Leaves_Nothing_Running()
    {
        var up = new FailsBeforeTheOthersFinish("data/h.004");
        var gate = new VolumeUploadGate(4);
        var files = Enumerable.Range(1, 8).Select(i => $"/tmp/a.{i:D3}").ToList();

        await Assert.ThrowsAsync<IOException>(() => VolumeBlobIO.UploadAsync(
            up, Acc(), "c", "data/h", files, AccessTier.Hot, scope: Scope(gate, 3)));

        // Volume 4 throws before it awaits anything, so its task is already faulted when the loop returns from
        // opening it — the check in front of the next volume sees it and stops there. Exactly four ever ran, and
        // that holds whatever the window is: it is the fault check, not the window, that ends this loop.
        Assert.Equal(4, up.Order.Count);
        // Every gate slot returned = no volume is still holding one. If anything were still running at the throw, this would come up short.
        Assert.Equal(4, gate.Free);
    }

    /// <summary>
    /// Throws on the named volume the instant it is entered, and keeps every other volume from completing until
    /// that has happened — which makes "the failure is already a fact at the first changeover" true by
    /// construction instead of by scheduling luck.
    /// </summary>
    private sealed class FailsBeforeTheOthersFinish(string bad) : IBlobUploader
    {
        private readonly TaskCompletionSource _failed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Order { get; } = [];

        public async Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            lock (Order) Order.Add(blobName);
            if (blobName == bad)
            {
                _failed.SetResult();
                throw new IOException("volume died");
            }

            await _failed.Task;
            return true;
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// When the progress sink breaks, that upload slot must still go back to the gate.
    /// <para>
    /// Progress reporting is not a side channel — <c>EndItem</c>/<c>EndWait</c> both call straight into the
    /// publish the caller supplied, which is external code (writing the database, pushing SSE) with a non-zero
    /// chance of throwing, and <c>StageProgress</c> states outright that non-heartbeat paths deliberately let it
    /// propagate. <c>gate.Release()</c> used to be the second statement after <c>EndItem</c> in the same
    /// <c>finally</c>, so a throw from the first skipped it entirely: the slot never came back. And this leak is
    /// silent — the exception travels up into the "file cannot be read" catch-all
    /// (<c>MarkPostDiffUnreadableAsync</c> catches IOException) and is swallowed there, the backup keeps running,
    /// just one stream short. Accumulate as many as the configured concurrency and all uploads stall at the gate
    /// forever: the UI shows "nothing is uploading while the staging pool is piled high", and it never heals
    /// itself.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_Broken_Progress_Sink_Does_Not_Swallow_The_Upload_Slot()
    {
        var gate = new VolumeUploadGate(1);
        var tracker = new StageTracker("Uploading", 0, static _ => throw new IOException("progress sink broke"))
        {
            Clock = () => 0,
        };
        var scope = new VolumeUploadScope(gate, tracker, 1);

        await Assert.ThrowsAsync<IOException>(() =>
            scope.RunAsync("data/h.001", _ => Task.CompletedTask, CancellationToken.None));

        Assert.Equal(1, gate.Free);
    }

    /// <summary>
    /// Same as above, but breaking at the moment it **has just been let off the queue**: the slot is already in
    /// hand when <c>EndWait</c> throws. That stretch lives in a different <c>finally</c>, an independent path from
    /// the one above.
    /// </summary>
    [Fact]
    public async Task A_Broken_Progress_Sink_Does_Not_Swallow_The_Slot_It_Just_Waited_For()
    {
        var gate = new VolumeUploadGate(1);
        var publishes = 0;
        // The first call is BeginWait — no slot in hand yet, so throwing there takes nothing with it; let it pass.
        // The second is EndWait, by which point the gate has let it through and that slot is being held.
        var tracker = new StageTracker("Uploading", 0, _ =>
        {
            if (Interlocked.Increment(ref publishes) >= 2)
                throw new IOException("progress sink broke");
        })
        {
            Clock = () => 0,
        };
        var scope = new VolumeUploadScope(gate, tracker, 1);

        await gate.AcquireAsync(0, 0, CancellationToken.None);  // take the only slot so it is forced to queue
        var run = scope.RunAsync("data/h.001", _ => Task.CompletedTask, CancellationToken.None);
        gate.Release();          // let it through: the waiter wakes up and immediately hits the broken sink

        await Assert.ThrowsAsync<IOException>(() => run);
        Assert.Equal(1, gate.Free);
    }

    [Theory]
    // Own volumes: the base name, and volume suffixes (including more than 3 digits)
    [InlineData("data/abc", "data/abc", true)]
    [InlineData("data/abc", "data/abc.001", true)]
    [InlineData("data/abc", "data/abc.1000", true)]
    [InlineData("packs/1.7z", "packs/1.7z.002", true)]
    // Collision-avoidance siblings: same prefix but different content, must be excluded
    // (ReplaceAsync must not delete them by mistake while clearing leftover volumes)
    [InlineData("data/abc", "data/abc~1", false)]
    [InlineData("data/abc", "data/abc~1.001", false)]
    [InlineData("data/abc~1", "data/abc~10", false)]
    [InlineData("data/abc~1", "data/abc~1.001", true)]
    // Other same-prefix noise
    [InlineData("data/abc", "data/abcd", false)]
    [InlineData("data/abc", "data/abc.00x", false)]
    [InlineData("data/abc", "data/abc.", false)]
    public void IsVolumeOf_Matches_Only_Own_Volumes_Not_Collision_Siblings(string baseRef, string name, bool expected)
        => Assert.Equal(expected, VolumeBlobIO.IsVolumeOf(baseRef, name));
}
