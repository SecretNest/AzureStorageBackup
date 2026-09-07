using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Freezes the child processes a run has started — the 7z under a file or a pack — for as long as the operator's
/// pause stands, and thaws them when it ends.
/// <para>
/// Before this, a pause let the file under 7z finish first: aborting it would have meant killing 7z, deleting the
/// partial output and recompressing from scratch on Resume, and a pause that costs a recompression is not free
/// to press. Stopping the process instead costs nothing either way — the kernel keeps its memory, its open
/// handles and the volumes written so far exactly where they are, and SIGCONT picks up mid-byte — so the pause
/// takes effect within the file, not after it. On the streaming path the feed blocks on the full pipe within one
/// buffer, so the source read stops with it.
/// </para>
/// <para>
/// Linux only. The signals are Linux's numbers and the product ships as a Linux container; anywhere else
/// <see cref="Hold"/> is a no-op and the pause goes back to finishing the file, which is what it did before.
/// A process that refuses the signal (already gone, or not ours) is likewise let be: this is a courtesy to the
/// operator, not a correctness step, and a pause that fails to freeze a 7z has merely fallen back to waiting
/// for it.
/// </para>
/// <para>
/// Thread-safe. Every transition is under one lock so that Attach during a hold stops the newcomer at once,
/// and Hold/Release cannot cross a detach in flight. The owner may pass its own lock in, and then a stopped
/// process joining or leaving is a transition the owner sees atomically with its own state: that is what lets
/// <see cref="PauseGate"/> keep "is anything still moving" exact when the thing that stopped moving is a process.
/// </para>
/// </summary>
/// <param name="sync">The lock to serialise on; the owner's, so that <paramref name="changed"/> fires inside it.
/// Null for a lock of this object's own.</param>
/// <param name="changed">Called, under the lock, whenever the number of stopped processes has changed by an
/// attach or a detach. <see cref="Hold"/> and <see cref="Release"/> do not fire it: the owner is the one calling
/// them and already knows.</param>
public sealed class ProcessHold(Lock? sync = null, Action? changed = null)
{
    private const int SIGCONT = 18;
    private const int SIGSTOP = 19;

    private readonly Lock _lock = sync ?? new Lock();
    private readonly HashSet<Process> _attached = [];
    private bool _held;

    /// <summary>Whether this platform can freeze a process at all. Tests read it to skip on hosts where it is a no-op.</summary>
    public static bool Supported { get; } = OperatingSystem.IsLinux();

    /// <summary>Is the hold standing right now.</summary>
    public bool IsHeld { get { lock (_lock) return _held; } }

    /// <summary>How many attached processes are stopped at this instant — attached under a standing hold, on a
    /// platform where the hold does anything. What the pause accounting reads (<see cref="PauseGate"/>): a
    /// stopped process is not moving, whatever loop is waiting on it. Where the signal is a no-op the process
    /// is running, and counting it as stopped would read "Paused" over a 7z still compressing.</summary>
    public int Stopped { get { lock (_lock) return _held && Supported ? _attached.Count : 0; } }

    /// <summary>
    /// Register a process that has just started. Under a standing hold it is stopped right here, before it gets a
    /// slice of anything. Dispose the returned scope when the process is gone — inside the finally that waits for
    /// it, so that a process killed under the hold leaves the count with it.
    /// </summary>
    public IDisposable Attach(Process process)
    {
        lock (_lock)
        {
            _attached.Add(process);
            if (_held)
            {
                Signal(process, SIGSTOP);
                changed?.Invoke();
            }
        }
        return new Attached(this, process);
    }

    /// <summary>Stop every attached process, and every one attached from now on until <see cref="Release"/>.</summary>
    public void Hold()
    {
        lock (_lock)
        {
            if (_held)
                return;
            _held = true;
            foreach (var p in _attached)
                Signal(p, SIGSTOP);
        }
    }

    /// <summary>Let every attached process run again. Idempotent; a release with nothing held is nothing.</summary>
    public void Release()
    {
        lock (_lock)
        {
            if (!_held)
                return;
            _held = false;
            foreach (var p in _attached)
                Signal(p, SIGCONT);
        }
    }

    private void Detach(Process process)
    {
        lock (_lock)
        {
            if (_attached.Remove(process) && _held)
                changed?.Invoke();
        }
    }

    private static void Signal(Process process, int signal)
    {
        if (!Supported)
            return;
        int pid;
        try
        {
            pid = process.Id;
        }
        catch (InvalidOperationException)
        {
            return;   // never started, or already disposed — nothing to signal
        }
        try
        {
            _ = kill(pid, signal);   // ESRCH (gone) and EPERM (not ours) are both "let it be" — see the class remarks
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // A libc without kill(2) under that name is not a thing on Linux, but the failure mode of getting
            // this wrong is a backup that cannot pause, not one that cannot run, so it is swallowed like the rest.
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    private sealed class Attached(ProcessHold hold, Process process) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                hold.Detach(process);
        }
    }
}
