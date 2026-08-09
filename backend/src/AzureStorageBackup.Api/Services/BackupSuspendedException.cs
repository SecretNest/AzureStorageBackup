namespace AzureStorageBackup.Api.Services;

/// <summary>Why a run ended up parked in the suspended state.</summary>
public enum SuspendReason
{
    /// <summary>The user deliberately clicked Suspend.</summary>
    UserRequested,

    /// <summary>Transient errors kept up past the patience threshold, so the gate downgraded.</summary>
    AutoSuspended,

    /// <summary>
    /// The process was asked to exit cleanly (<c>docker stop</c>, upgrade restart), so the run was suspended along the way.
    /// <para>
    /// Not the same thing as the <c>Crashed</c> value that got cut: at shutdown our code is **still running** and can
    /// write down its own reason; in a crash no code is running at all, and such a run can only be recognized after
    /// the fact from the journal still sitting on disk.
    /// </para>
    /// <para>
    /// **This reason alone** justifies resuming automatically without asking: it maps uniquely onto the "planned
    /// restart/upgrade" case, which is exactly and only what auto-resume is meant to cover. The converse does not
    /// hold — no mark on disk does **not** mean "the process was killed": a crash, a power cut, a shutdown whose flush
    /// timed out and left this volume half-written, the operator pressing Cancel himself (cancel flushes all the same,
    /// but writes no mark), even the mark write itself failing — they all look identical. Every one of those waits for
    /// the operator to press Resume.
    /// </para>
    /// </summary>
    ShuttingDown,

    // The design draft had a third value, Crashed (process killed / power cut). Deliberately **left out** here:
    // in a crash no code is running, so nobody can write down a reason for itself. Such a run is recognized from the
    // journal still on disk, served by GET /{id}/interrupted reading the directory directly (Task 12); no need to
    // fabricate an in-memory Suspended record with no Control and no busy lock — with a fabricated record, every
    // branch that touches it has to additionally remember "this one is fake".
}

/// <summary>
/// "This round didn't finish, but the work in progress is safe." The difference from a failure is concrete: failure
/// is an endpoint, suspension is a midpoint you can pick up from, so it must not go down the <c>RunStatus.Failed</c>
/// path — otherwise the user sees a red final verdict while the journal actually holds a whole round's worth of
/// already-uploaded content.
/// </summary>
public sealed class BackupSuspendedException(SuspendReason reason, string message) : Exception(message)
{
    public SuspendReason Reason { get; } = reason;
}
