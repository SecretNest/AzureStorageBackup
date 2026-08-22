using System.Runtime.InteropServices;

namespace AzureStorageBackup.Api.Services;

/// <summary>How hard this process is allowed to lean on the disk, relative to everything else on the machine.</summary>
public enum IoPriorityLevel
{
    /// <summary>Leave it alone. The default, because lowering it is a policy choice about a machine we cannot see.</summary>
    Normal,

    /// <summary>Best-effort, the lowest band in it. Still gets a share when the disk is contended, just the smallest one.</summary>
    Low,

    /// <summary>Idle class: disk time only when nobody else wants any. The strongest yield, and the one that can starve
    /// a backup outright behind a busy fileserver.</summary>
    Idle,
}

/// <summary>
/// Lowers the whole process's block-IO priority, once, at startup.
/// <para>
/// **It must happen before anything else does.** On Linux IO priority is a per-**thread** attribute that a new thread
/// inherits from the thread that created it — the same shape as nice, and the same trap already documented at
/// <see cref="SevenZipCli"/>. There is no call that says "this process and everything it will ever spawn", so the
/// only way to cover the thread pool is to set it on the main thread before the pool has any threads to create, and
/// let inheritance carry it. That in turn is why the level is read from the environment rather than from
/// <c>GlobalSettings</c>: at the moment this has to run there is no database, and a value changed later would reach
/// none of the threads already doing the reading.
/// </para>
/// <para>
/// Coverage is the point of doing it here rather than on the 7z child alone. The diff, the dedup probe's whole-file
/// reads, the uploaders' reads out of the staging pool and 7z itself are four different things on three different
/// threads, and every one of them is competing with whatever else the box is doing — on the deployment this targets,
/// a fileserver. 7z is a child forked from a pool thread, so it inherits along with the rest and needs nothing of
/// its own. The cost of the bluntness is that the uploaders' reads are lowered too, which is accepted: the classes
/// below only yield when something else actually wants the disk, so an otherwise idle array charges nothing for it.
/// </para>
/// <para>
/// **This is a request the kernel is free to ignore, and usually does.** IO priority is honoured only by the BFQ
/// scheduler; under <c>mq-deadline</c> and <c>none</c> — between them the default nearly everywhere now — the
/// syscall succeeds and the value is then never consulted. Nothing here can detect that, which is exactly why the
/// outcome is logged rather than assumed: an operator who sees no change should check
/// <c>cat /sys/block/&lt;dev&gt;/queue/scheduler</c> before looking anywhere else.
/// </para>
/// </summary>
public static class IoPriority
{
    /// <summary>Read straight from the environment, not through IConfiguration: see the remarks — the host does not
    /// exist yet at the only moment this call is worth making. The name is the one the configuration system would
    /// bind to <c>Backup:IoPriority</c> anyway, so nothing diverges if it is ever read the ordinary way too.</summary>
    public const string EnvVar = "Backup__IoPriority";

    private const int IOPRIO_WHO_PROCESS = 1;
    private const int IOPRIO_CLASS_SHIFT = 13;
    private const int IOPRIO_CLASS_BE = 2;
    private const int IOPRIO_CLASS_IDLE = 3;

    /// <summary>Lowest band within best-effort. The class carries the meaning; this only says "last among equals".</summary>
    private const int LowestBestEffortData = 7;

    /// <summary>
    /// Applies the level named by <see cref="EnvVar"/> to the calling thread. Returns a line describing what
    /// happened, for the caller to log once it has somewhere to log to. Never throws: a preference about disk
    /// scheduling has no business stopping a backup service from starting.
    /// </summary>
    public static string Apply()
    {
        var raw = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(raw))
            return $"IO priority left at the system default ({EnvVar} not set).";

        if (!Enum.TryParse<IoPriorityLevel>(raw, ignoreCase: true, out var level))
            return $"IO priority left at the system default: {EnvVar}='{raw}' is not one of " +
                   $"{string.Join(", ", Enum.GetNames<IoPriorityLevel>())}.";

        if (level == IoPriorityLevel.Normal)
            return "IO priority left at the system default (Normal requested).";

        if (!OperatingSystem.IsLinux())
            return $"IO priority {level} ignored: only Linux has one to set.";

        var call = SyscallNumber();
        if (call < 0)
            return $"IO priority {level} ignored: no ioprio_set syscall number known for " +
                   $"{RuntimeInformation.ProcessArchitecture}.";

        var value = level == IoPriorityLevel.Idle
            ? IOPRIO_CLASS_IDLE << IOPRIO_CLASS_SHIFT
            : (IOPRIO_CLASS_BE << IOPRIO_CLASS_SHIFT) | LowestBestEffortData;

        long rc;
        try
        {
            // who = 0 means the calling thread. Deliberately not the process group: in a container this process is
            // the group and the two are the same, but outside one the group is whatever shell started us, and
            // quietly re-prioritising a developer's other jobs is not this switch's business.
            rc = syscall(call, IOPRIO_WHO_PROCESS, 0, value);
        }
        catch (Exception ex)
        {
            // EntryPointNotFound on a libc without the symbol, DllNotFound on a musl image that names it differently.
            return $"IO priority {level} could not be applied: {ex.GetType().Name} calling ioprio_set.";
        }

        if (rc != 0)
            return $"IO priority {level} refused by the kernel (errno {Marshal.GetLastPInvokeError()}).";

        return $"IO priority set to {level} for this process and everything it starts. " +
               "Only the BFQ scheduler acts on it — under mq-deadline or none it is accepted and then ignored, " +
               "so check /sys/block/<dev>/queue/scheduler before concluding it did not help.";
    }

    /// <summary>
    /// glibc exposes no wrapper for this call, so it goes through <c>syscall(2)</c> and the number is per
    /// architecture. x86-64 has its own; arm64 uses the generic table, which riscv64 and loongarch64 share.
    /// An architecture not listed gets nothing rather than a guess — a wrong number is not a failed call, it is a
    /// different call.
    /// </summary>
    private static int SyscallNumber() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => 251,
        Architecture.Arm64 => 30,
        _ => -1,
    };

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long syscall(long number, long which, long who, long ioprio);
}
