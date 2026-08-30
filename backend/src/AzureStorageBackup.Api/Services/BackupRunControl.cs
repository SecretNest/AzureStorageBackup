using System.Collections.Concurrent;

namespace AzureStorageBackup.Api.Services;

/// <summary>How to stop.</summary>
public enum StopKind
{
    None,

    /// <summary>Deliberate pause: finish the item in hand, flush to disk, exit as Suspended.</summary>
    Suspend,

    /// <summary>Cancel, but finish the file currently uploading (including all of its volumes) before stopping.</summary>
    FinishCurrentFiles,

    /// <summary>Cancel, interrupt the in-flight upload immediately, and delete the residual volumes it left behind.</summary>
    StopNow,
}

/// <summary>
/// The "external handle" on one backup run: the orchestrator does not know about the run registry, and should not;
/// it only knows about this one object. Holds the journal and the pause gate; later tasks add stop intent to it.
/// </summary>
public sealed class BackupRunControl(
    BackupJournalStore store, int configId, string runId, PauseGate? gate = null) : IAsyncDisposable
{
    private BackupJournal? _journal;
    private int _accountId;
    private string _container = "";

    /// <summary>The runIds of old journals adopted by this round. When this round commits its index successfully, they are deleted along with our own volume.</summary>
    private readonly List<string> _adopted = [];

    /// <summary>What the previous round (or rounds) already confirmed as uploaded. Empty when there is no volume to adopt.</summary>
    public JournalResume Resume { get; private set; } = JournalResume.Empty;

    /// <summary>We adopted or voided an old volume when opening the journal, or this is this config's first round on
    /// this container → the container most likely holds orphan blocks, so the closing cleanup should sweep once (Task 11).</summary>
    public bool SweepNeeded { get; private set; }

    /// <summary>The remediation hold: while a check report awaits repair, the backup-tail orphan sweep is the
    /// one deleter that judges by EXACT volume names — a suspended repair's replacement volumes can outnumber
    /// the recorded family and would read as orphans to it. Retirement's own deletions normalize to base refs
    /// and stay safe, so only the sweep is held; it fires once the report retires (drop or full repair).</summary>
    public bool SweepSuppressed { get; set; }

    /// <summary>The pause gate for transient errors. Self-heals at 30s/1m/5m/every 5m by default, and downgrades if it has not recovered in 10 minutes.</summary>
    public PauseGate Gate { get; } = gate ?? new PauseGate();

    public string RunId => runId;

    /// <summary>Fired by every stop kind: stops the diff (there is no point in reading more from disk).</summary>
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Fired by Stop now **only**: interrupts the in-flight upload.
    /// Suspend and Finish current files must never touch it, or "finish the current item, then stop" is an empty promise.</summary>
    private readonly CancellationTokenSource _abort = new();

    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);

    private int _stopKind;

    /// <summary>-1 = nobody has issued a Suspend yet. A sentinel rather than a default value, so that "the first
    /// request wins" can be expressed as a single CAS (see <see cref="RequestStop"/>).</summary>
    private int _suspendReason = -1;

    public StopKind Stop => (StopKind)Volatile.Read(ref _stopKind);

    /// <summary>Why this suspension happened. Reports UserRequested when nobody ever issued a Suspend — a suspension
    /// only exists if one was issued anyway, and this value just spares every reader from first checking "is there one".</summary>
    public SuspendReason SuspendReason
    {
        get
        {
            var v = Volatile.Read(ref _suspendReason);
            return v < 0 ? Services.SuspendReason.UserRequested : (SuspendReason)v;
        }
    }

    public CancellationToken StopToken => _stop.Token;
    public CancellationToken AbortToken => _abort.Token;

    /// <summary>Register/clear "this block of content is currently uploading". Stop now uses it to delete residual volumes when it settles.</summary>
    public void TrackInFlight(string blobRef) => _inFlight[blobRef] = 1;
    public void ClearInFlight(string blobRef) => _inFlight.TryRemove(blobRef, out _);
    public IReadOnlyCollection<string> InFlight => _inFlight.Keys.ToList();

    /// <summary>
    /// Issue a stop intent. **Escalate only, never de-escalate**: a stronger stop kind overrides a weaker one, and a
    /// weaker or identical one is ignored outright.
    /// <para>
    /// The two directions are asymmetric, so this cannot be written as "only the first one counts". Clicking Suspend
    /// after Stop now really is meaningless — residual volumes already interrupted and already deleted cannot come back
    /// to life because of a gentler request. But the other direction is the user **escalating**: he clicked Suspend,
    /// found himself stuck behind a multi-volume file of tens of GB, and switched to Stop now.
    /// First-request-wins would silently drop that escalation, while <see cref="BackupRunner.CancelAsync"/> still
    /// returns true once the terminal state arrives — the API reports success, but a different stop kind is what
    /// actually took effect.
    /// </para>
    /// <para>
    /// Escalation only ever **tightens**, so it can never revive work already given up on: <see cref="StopKind"/>'s
    /// members are ordered by strength, and the decision is a plain comparison; the CAS loop guarantees the strongest
    /// one survives concurrent requests, and firing is the job of **the thread that wins the CAS**, so escalating to
    /// Stop now is guaranteed to fire <see cref="AbortToken"/> — even if <see cref="StopToken"/> was already fired long
    /// ago for the earlier, weaker request (<c>Cancel()</c> is idempotent, repeat calls have no side effects).
    /// </para>
    /// </summary>
    /// <param name="reason">
    /// Only meaningful for <see cref="StopKind.Suspend"/>: the suspend reason recorded on disk next to this volume
    /// (the shutdown path passes <see cref="SuspendReason.ShuttingDown"/>).
    /// <para>
    /// It does **not** take part in the escalation rule above; it goes through a separate CAS instead: the reason of
    /// the **first** Suspend request wins. A run already settling for "the user pressed pause" must not be rewritten to
    /// ShuttingDown by a shutdown that arrives afterwards — that value is exactly what the next startup uses to decide
    /// "should we restart this one for him", and getting it wrong means revoking the pause the user pressed for him.
    /// Conversely, the reason is written **before** the stop kind lands, so any thread that sees
    /// <c>Stop == Suspend</c> can also read the matching reason; there is no window where the stop kind has arrived and
    /// the reason has not.
    /// </para>
    /// </param>
    public void RequestStop(StopKind kind, SuspendReason reason = SuspendReason.UserRequested)
    {
        if (kind == StopKind.None)
            return;
        if (kind == StopKind.Suspend)
            Interlocked.CompareExchange(ref _suspendReason, (int)reason, -1);
        while (true)
        {
            var current = Volatile.Read(ref _stopKind);
            if ((int)kind <= current)
                return;     // weaker or identical: ignore
            if (Interlocked.CompareExchange(ref _stopKind, (int)kind, current) == current)
                break;
        }
        // Workers stuck at the gate waiting to retry have to be woken up, otherwise they sit there until the next
        // self-heal timer fires.
        //
        // This line fires for Suspend / Finish current files too, so a piece of work **stuck at the gate waiting to
        // self-heal** gets given up on right here rather than "finishing the current item". That is a deliberate
        // trade-off: without waking it, the stop the user pressed takes up to 5 minutes (the last step of the self-heal
        // timer) to have any effect, and that piece of work was stuck on a transient error that has not cleared anyway,
        // so waiting is most likely waiting for nothing. The exception finally thrown is still corrected by the
        // orchestrator's SettleStopAsync according to the stop kind, so the externally visible behavior (Suspended /
        // Canceled, journal flushed, residue cleaned) is all correct — the only thing that does not hold is the literal
        // meaning of "finish the current item" for the piece of work sitting at the gate.
        Gate.Downgrade();
        _stop.Cancel();
        if (kind == StopKind.StopNow)
            _abort.Cancel();
    }

    /// <summary>
    /// Open the journal volume. Must not be called until the orchestrator has worked out the baseline version and the
    /// addressing identity — those two are preconditions for resuming; if they cannot go into the header, this journal
    /// volume cannot be safely reused.
    /// </summary>
    /// <param name="firstRun">
    /// No local authoritative state established yet = this is this config's first round on this container (newly
    /// created, or **the config was deleted and recreated**). It has to trigger an orphan sweep just the same; the
    /// reasoning is at the assignment to <see cref="SweepNeeded"/> below.
    /// </param>
    public async Task OpenJournalAsync(
        int accountId, string container, int baselineVersion, string localRoot, string encryptionIdentity,
        DateTimeOffset startedAt, CancellationToken ct, bool firstRun = false)
    {
        _accountId = accountId;
        _container = container;

        // Adopt the ones that match; delete the ones that don't on the spot.
        //
        // A different configId gets deleted too: (AccountId, ContainerName) is a unique index in AppDbContext, so one
        // container has at most one config — meaning that can only be a leftover from "the config was deleted and
        // recreated on the same container". Keeping it would protect those blocks from cleanup forever (the cleanup
        // criterion looks at the journal, not at configId).
        // The day several configs are allowed to share one container, this has to go back to "if it isn't ours, don't
        // touch it at all", or we would turn the work of someone else's currently suspended run into orphans.
        var voided = false;
        // The volume we adopted is this round's own volume (same runId) → append to it, never open a new one over it.
        var reopenMine = false;
        var adopted = new List<JournalContent>();
        var myPath = store.PathFor(accountId, container, runId);
        foreach (var (oldRunId, content) in await store.ListAsync(accountId, container, ct))
        {
            var h = content.Header;
            // What is compared is **the path it lands on**, not the two runId strings: file names are flattened
            // through BackupJournalStore.Safe, and two different runIds can perfectly well land on the same file — at
            // which point they are the same volume.
            var mine = string.Equals(
                store.PathFor(accountId, container, oldRunId), myPath, StringComparison.Ordinal);
            if (h.ConfigId == configId
                && h.BaselineVersion == baselineVersion
                && string.Equals(h.LocalRoot, localRoot, StringComparison.Ordinal)
                && string.Equals(h.EncryptionIdentity, encryptionIdentity, StringComparison.Ordinal))
            {
                adopted.Add(content);
                // The same-name volume does **not** go into _adopted: it is this round's own volume, and CompleteAsync already deletes it by runId.
                if (mine)
                {
                    reopenMine = true;
                }
                else
                {
                    _adopted.Add(oldRunId);
                    // Adopting also wipes the old volume's suspend mark: that mark says why **the round that has now
                    // been superseded** stopped, and "this volume now belongs to the current round" is precisely the
                    // event that voids it — the operator pressed Run, or startup auto-resumed a round.
                    //
                    // Without wiping, it sticks around until some round really succeeds (old volumes are only deleted
                    // in CompleteAsync). The fallout lands on the auto-resume criterion: that criterion requires
                    // **every** volume under this config to say ShuttingDown, so one stale AutoSuspended /
                    // UserRequested can veto every subsequent planned restart — and the longer-running a config is,
                    // the less likely it is to ever finish a whole round, the more easily it gets stuck in this state —
                    // exactly the configs this feature is meant to rescue.
                    //
                    // Wiping does not leave this volume without a mark from then on: when the current round really
                    // suspends, it rewrites this round's reason onto it as well (see MarkSuspended). So "what state
                    // this config stopped in" is always decided by **the current round**.
                    //
                    // On most paths this line is a redundant defense covered by the MarkSuspended half — this round's
                    // suspend overwrites the stale reason anyway, and deleting it turns only one mechanism-level test
                    // red. **Don't delete it as scaffolding**: it guards the window after adoption but before this
                    // round gets a chance to suspend (this round is SIGKILLed / blows past the shutdown cap), when
                    // nobody would overwrite this volume and the stale AutoSuspended would stay on disk and keep
                    // vetoing.
                    store.ClearSuspendMark(accountId, container, oldRunId);
                }
            }
            else
            {
                // Same name but the criteria don't match, delete it all the same: by this round's criteria it is void,
                // and the new volume opened below was going to land on this very path anyway (FileMode.Create), so
                // deleting or not makes no difference.
                store.Delete(accountId, container, oldRunId);
                voided = true;
            }
        }
        // Adopted something, or voided something → this container most likely holds blocks that "exist in the cloud
        // but not in the index". The closing cleanup uses this to decide whether to run an orphan sweep (see Task 11).
        //
        // The first round sweeps too, and that clause is not an afterthought: deleting a config (keeping the container)
        // throws away all of that container's journals, so those blocks lose their protection from then on, and what
        // the delete-config endpoint promises is exactly "once this container has a config again, the first cleanup
        // will sweep the real orphans away". Without this clause that promise is empty — the first cleanup after
        // recreating the config is the **backup's own closing** one, and at that point the journal directory has just
        // been emptied, nothing was adopted and nothing was voided, so both of the above are false; only a standalone
        // scheduled Cleanup job would sweep, and the user may well have no cleanup schedule configured at all.
        //
        // The cost is bounded: this is two LISTs (data/ and packs/) that happen **exactly once** per config per
        // container, and the container of a brand new backup is empty anyway; the genuinely large containers are
        // precisely the "config deleted and recreated" case — precisely the one that has to be swept.
        SweepNeeded = voided || adopted.Count > 0 || firstRun;
        // Adoption is **read-only**: this round still opens its own volume and leaves the old ones exactly as they
        // are. That way the reused records don't have to be copied over again, and there is no "crashed halfway through
        // copying" half-state. The old volumes get deleted once this round commits its index successfully.
        Resume = JournalResume.FromVolumes(adopted);

        // When the runId collides with the volume just adopted, **append**; do not open a new one: CreateAsync is
        // FileMode.Create and would truncate the volume just adopted on the spot.
        //
        // It cannot happen today, and **no caller today can make it happen**: runId always comes from
        // BackupRunState.RunId, a freshly generated GUID prefix per round, and no path reuses the previous round's.
        // Auto-resume at startup (AutoResumeService) is no exception — it goes through the **adoption** branch above
        // (_adopted) and opens its own volume.
        // This branch is here for the day someone really does make a round reuse an old runId: to keep the run identity
        // shown in the UI stable across a suspension, say.
        // After truncation this round's in-memory Resume is still complete (it was read in above), so the round itself
        // runs on without error; what breaks is the guarantee **on disk**: suspend once more and the new volume vouches
        // for none of those blocks, so the next round re-uploads all of them, and the cleanup/orphan sweep that decides
        // "is this block claimed by anyone" from the journal deletes them outright as garbage.
        //
        // When appending, the header line is **not** rewritten (see BackupJournal.OpenForAppendAsync): deciding "this
        // is mine" uses the path it lands on rather than the runId string (the `mine` computation above), and two
        // different runIds can perfectly well land on the same file — so the header on disk may record **the earlier**
        // runId and its StartedAt. Nobody reads those two fields today, but anyone later reading Header.RunId /
        // Header.StartedAt expecting this round's values would get a leftover.
        _journal = reopenMine
            ? await store.AppendAsync(accountId, container, runId, ct)
            : await store.CreateAsync(accountId, container, runId, new JournalHeader
            {
                RunId = runId,
                ConfigId = configId,
                StartedAt = startedAt,
                BaselineVersion = baselineVersion,
                LocalRoot = localRoot,
                EncryptionIdentity = encryptionIdentity,
            }, ct);
    }

    /// <summary>Record a single-file blob. **Only** call this after the upload confirmation has returned.</summary>
    /// <param name="mtimeUtc">The source file's last-write time **from before the read** that produced the hashes
    /// (see <see cref="JournalRecord.MtimeUtcTicks"/>) — the caller already has it on the <c>BlobContent</c> that
    /// carries those hashes, captured for exactly this reason.</param>
    public async Task RecordBlobAsync(
        string path, string blobRef, string fullHash, string headHash, string tailHash, long length,
        DateTimeOffset mtimeUtc, int volumes, bool raw, IReadOnlyList<long> volumeSizes, CancellationToken ct)
    {
        if (_journal is null)
            return;
        await _journal.AppendAsync(new JournalRecord
        {
            Kind = "blob", Ref = blobRef, Path = path, FullHash = fullHash, HeadHash = headHash,
            TailHash = tailHash, Length = length, Volumes = volumes, Raw = raw, VolumeSizes = volumeSizes,
            MtimeUtcTicks = mtimeUtc.UtcTicks,
        }, ct);
    }

    /// <summary>Record a pack. Likewise, **only** call this after the upload confirmation has returned.</summary>
    public async Task RecordPackAsync(
        string packId, IReadOnlyList<JournalMember> members, IReadOnlyList<long> volumeSizes, bool storeOnly,
        CancellationToken ct)
    {
        if (_journal is null)
            return;
        await _journal.AppendAsync(new JournalRecord
        {
            Kind = "pack", Ref = packId, Members = members, VolumeSizes = volumeSizes,
            Volumes = Math.Max(1, volumeSizes.Count), StoreOnly = storeOnly,
        }, ct);
    }

    /// <summary>
    /// Write "why this volume stopped" next to the journal. The in-memory copy of the reason dies with the process,
    /// and "the process is gone" is exactly the case the next startup has to judge, so a copy must land on disk.
    /// <para>
    /// A run suspended before the journal was even opened (stopped during the scan) writes nothing: there is no journal
    /// volume on disk at all, so the mark would only become an orphan pointing at a journal that does not exist, and
    /// would force everyone who reads it to add one more null check.
    /// </para>
    /// <para>
    /// What gets written is **every volume under this round's name**: our own, plus every old volume adopted when the
    /// journal was opened. The reason is that marks are recorded per volume and the criterion is read per volume
    /// (<see cref="AutoResumeService.PickResumableAsync"/> requires every volume to say ShuttingDown), and after
    /// adoption these volumes are the working state of one and the same run — they stop together and resume together,
    /// and no single volume may stop for a different reason. Writing only our own volume would leave the adopted old
    /// ones on "the empty mark that was wiped when the journal was opened", so **from the second restart onward** the
    /// criterion can never be met again — one planned restart resumes, the second silently does not.
    /// </para>
    /// </summary>
    public void MarkSuspended(SuspendReason reason)
    {
        if (_journal is null)
            return;
        store.MarkSuspended(_accountId, _container, runId, reason);
        foreach (var old in _adopted)
            store.MarkSuspended(_accountId, _container, old, reason);
    }

    public async Task FlushAsync(bool fsync, CancellationToken ct)
    {
        if (_journal is not null)
            await _journal.FlushAsync(fsync, ct);
    }

    /// <summary>
    /// Successful end of a run: the index is committed, so the journal is of no more use.
    /// It has to be deleted **after** the info file is committed and **before** retention cleanup — get the order wrong
    /// and cleanup sees a gap where content is "referenced neither by the index nor by the journal", and deletes what
    /// was just uploaded.
    /// </summary>
    public async Task CompleteAsync()
    {
        if (_journal is null)
            return;
        await _journal.DisposeAsync();
        _journal = null;
        store.Delete(_accountId, _container, runId);
        // The adopted old volumes retire too — everything they recorded is now in the committed index.
        foreach (var old in _adopted)
            store.Delete(_accountId, _container, old);
        _adopted.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        Gate.Dispose();
        if (_journal is not null)
            await _journal.DisposeAsync();
        _journal = null;
        _stop.Dispose();
        _abort.Dispose();
    }
}
