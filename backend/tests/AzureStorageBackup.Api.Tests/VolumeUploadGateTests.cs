using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The arbitration rules of the upload slot gate. These cases guard not performance but **how much work gets
/// thrown away on an interruption**: the journal is written and the in-flight ledger cleared only after the
/// whole volume family is uploaded and the cloud has confirmed, so "how many items are half-done at once"
/// directly decides how many already-compressed, already-uploaded bytes a <c>Stop now</c> / suspend / crash loses.
/// <para>
/// First-come-first-served spreads the slots thin across every item in flight (compression is globally serial,
/// so the steady state is 1 item compressing + N items uploading), leaving N items half-done at once;
/// arbitrating by item age pushes that number down to 1~2 typically.
/// </para>
/// </summary>
public sealed class VolumeUploadGateTests
{
    /// <summary>
    /// This one is the whole reason the change exists: **the older item that asked later gets the slot first**.
    /// The newer item (larger ticket) queues up first, the older item (smaller ticket) arrives after, and when a
    /// slot is released it must land on the older one.
    /// </summary>
    [Fact]
    public async Task An_Older_Item_Wins_The_Slot_Even_Though_It_Asked_Later()
    {
        var gate = new VolumeUploadGate(1);
        // Take the only slot up front so the next two can do nothing but queue.
        await gate.AcquireAsync(ticket: 0, volume: 0, CancellationToken.None);

        var newer = gate.AcquireAsync(ticket: 9, volume: 0, CancellationToken.None);
        var older = gate.AcquireAsync(ticket: 2, volume: 0, CancellationToken.None);
        Assert.False(newer.IsCompleted);
        Assert.False(older.IsCompleted);

        gate.Release();

        await older.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(newer.IsCompleted);   // the newer item keeps waiting, even though it asked first

        gate.Release();
        await newer.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Within one item, ascending volume number. Not optional tidiness: the in-flight list on screen reads in
    /// this order, and it only makes sense when each item visibly advances one volume after another.
    /// </summary>
    [Fact]
    public async Task Within_One_Item_The_Lowest_Volume_Number_Goes_First()
    {
        var gate = new VolumeUploadGate(1);
        await gate.AcquireAsync(ticket: 0, volume: 0, CancellationToken.None);

        var third = gate.AcquireAsync(ticket: 7, volume: 3, CancellationToken.None);
        var first = gate.AcquireAsync(ticket: 7, volume: 1, CancellationToken.None);
        var second = gate.AcquireAsync(ticket: 7, volume: 2, CancellationToken.None);

        gate.Release();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(second.IsCompleted);
        Assert.False(third.IsCompleted);

        gate.Release();
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(third.IsCompleted);

        gate.Release();
        await third.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Slots must never be over-issued: at no moment may more than the capacity be running.
    /// <para>
    /// Deliberately **not** hammering it with a pile of concurrent tasks — this test class runs alongside a batch
    /// of integration cases that have wall-clock budgets, and fighting them for the thread pool would only turn
    /// the neighbours red while still not measuring what we want here. Driven single-threaded and in order
    /// instead: fill the capacity, ask for a few more, release them one by one, asserting the books at every
    /// step. It pins over-issue just as well, and it is completely deterministic.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Never_Hands_Out_More_Slots_Than_Its_Capacity()
    {
        const int capacity = 3;
        var gate = new VolumeUploadGate(capacity);

        // Fill the capacity first: every one of these should be handed over on the spot.
        for (var i = 0; i < capacity; i++)
            Assert.True(gate.AcquireAsync(ticket: i, volume: 0, CancellationToken.None).IsCompletedSuccessfully);
        Assert.Equal(0, gate.Free);

        // Anything beyond the capacity queues; not one extra slot may be issued.
        var queued = Enumerable.Range(capacity, 5)
            .Select(i => gate.AcquireAsync(ticket: i, volume: 0, CancellationToken.None))
            .ToList();
        Assert.All(queued, t => Assert.False(t.IsCompleted));

        // Release one at a time: each release promotes exactly one waiter, the rest keep waiting.
        for (var i = 0; i < queued.Count; i++)
        {
            gate.Release();
            await queued[i].WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, gate.Free);
            Assert.All(queued.Skip(i + 1), t => Assert.False(t.IsCompleted));
        }

        // Still holding capacity slots (capacity + 5 handed out, 5 returned); returning them all brings the count back to full — none leaked.
        for (var i = 0; i < capacity; i++)
            gate.Release();
        Assert.Equal(capacity, gate.Free);
    }

    /// <summary>
    /// A cancelled waiter must not eat a slot. After cancelling it still lies in the priority queue, so on
    /// release it has to be skipped and the slot has to land on the next live waiter — otherwise one
    /// cancellation permanently leaks one stream.
    /// </summary>
    [Fact]
    public async Task A_Cancelled_Waiter_Does_Not_Consume_The_Slot_It_Was_Queued_For()
    {
        var gate = new VolumeUploadGate(1);
        await gate.AcquireAsync(ticket: 0, volume: 0, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        // Smallest ticket = highest priority, so it is guaranteed to be the first one popped on release.
        var doomed = gate.AcquireAsync(ticket: 1, volume: 0, cts.Token);
        var survivor = gate.AcquireAsync(ticket: 5, volume: 0, CancellationToken.None);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => doomed);

        gate.Release();
        await survivor.WaitAsync(TimeSpan.FromSeconds(5));

        gate.Release();
        Assert.Equal(1, gate.Free);
    }

    /// <summary>
    /// Once every waiter has cancelled, the queue holds nothing but corpses. The count must return to full, and
    /// **latecomers must not be wedged shut by the corpses** — that is exactly the deadlock a "pop one and stop"
    /// implementation walks into: free slots, a non-empty queue, and nobody able to get one.
    /// </summary>
    [Fact]
    public async Task A_Queue_Full_Of_Cancelled_Waiters_Does_Not_Wedge_The_Gate()
    {
        var gate = new VolumeUploadGate(1);
        await gate.AcquireAsync(ticket: 0, volume: 0, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var dead = Enumerable.Range(1, 5)
            .Select(i => gate.AcquireAsync(ticket: i, volume: 0, cts.Token))
            .ToList();

        await cts.CancelAsync();
        foreach (var d in dead)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => d);

        gate.Release();
        Assert.Equal(1, gate.Free);

        // The corpses are still lying in the queue; a latecomer must still get through.
        var latecomer = gate.AcquireAsync(ticket: 99, volume: 0, CancellationToken.None);
        await latecomer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, gate.Free);
    }

    /// <summary>
    /// An empty gate must not queue — the caller relies on "the returned Task is already completed" to decide
    /// not to report "waiting for a slot", and a big item has thousands of volumes, so reporting once means
    /// thousands of forced publishes.
    /// </summary>
    [Fact]
    public void An_Empty_Gate_Hands_The_Slot_Over_Synchronously()
    {
        var gate = new VolumeUploadGate(2);
        Assert.True(gate.AcquireAsync(ticket: 1, volume: 0, CancellationToken.None).IsCompletedSuccessfully);
        Assert.True(gate.AcquireAsync(ticket: 2, volume: 0, CancellationToken.None).IsCompletedSuccessfully);
        Assert.False(gate.AcquireAsync(ticket: 3, volume: 0, CancellationToken.None).IsCompleted);
    }

    /// <summary>Tickets increase strictly, otherwise "which one is older" means nothing.</summary>
    [Fact]
    public void Tickets_Are_Handed_Out_In_Increasing_Order()
    {
        var gate = new VolumeUploadGate(1);
        var tickets = Enumerable.Range(0, 100).Select(_ => gate.NextTicket()).ToList();
        Assert.Equal(tickets.OrderBy(t => t), tickets);
        Assert.Equal(100, tickets.Distinct().Count());
    }

    /// <summary>
    /// A double release must blow up on the spot rather than quietly inflating the slot count. The
    /// <c>SemaphoreSlim</c> that was replaced had this safety net (<c>SemaphoreFullException</c>), and swapping
    /// the implementation must not lose it: slots appearing out of nowhere means the number of in-flight streams
    /// silently exceeds the concurrency the user set, and that failure mode is a silent one.
    /// </summary>
    [Fact]
    public void Releasing_More_Than_Was_Acquired_Throws_Instead_Of_Inflating_The_Capacity()
    {
        var gate = new VolumeUploadGate(2);
        Assert.True(gate.AcquireAsync(1, 0, CancellationToken.None).IsCompletedSuccessfully);
        gate.Release();
        Assert.Equal(2, gate.Free);
        Assert.Throws<InvalidOperationException>(gate.Release);
        Assert.Equal(2, gate.Free);
    }
}
