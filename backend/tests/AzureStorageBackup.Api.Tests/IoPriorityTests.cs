using System.Runtime.InteropServices;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// <see cref="IoPriority"/> is a single syscall behind a lot of branching, and every branch of it is invisible from
/// inside the application — nothing the backup does can report back whether the disk is being scheduled differently.
/// So the branches that decide *whether to call at all* are asserted through the message, which is the only channel
/// the operator gets, and the call itself is asserted by reading the value back out of the kernel.
/// <para>
/// The environment variable is process-wide, so these tests set and restore it in a finally. xUnit runs the facts of
/// one class sequentially, and no other test in the suite reads this variable, which is what makes that safe — the
/// same reasoning the mutable statics in BackupRunner rest on.
/// </para>
/// </summary>
public class IoPriorityTests
{
    private static string Run(string? value)
    {
        var before = Environment.GetEnvironmentVariable(IoPriority.EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(IoPriority.EnvVar, value);
            return IoPriority.Apply();
        }
        finally
        {
            Environment.SetEnvironmentVariable(IoPriority.EnvVar, before);
        }
    }

    [Fact]
    public void Unset_Changes_Nothing() =>
        Assert.Contains("not set", Run(null));

    /// <summary>Normal is a real choice, not an absent one, and has to read as deliberate in the log — an operator
    /// who set it explicitly should not have to wonder whether the variable reached the container.</summary>
    [Fact]
    public void Normal_Changes_Nothing_And_Says_It_Was_Asked_For() =>
        Assert.Contains("Normal requested", Run("Normal"));

    /// <summary>A typo must not silently become Idle, or a fat finger costs the operator their backup throughput
    /// with nothing on screen to explain it. It names the accepted values, since the log line is the only help
    /// available at that point.</summary>
    [Fact]
    public void An_Unrecognised_Value_Is_Refused_And_Lists_The_Real_Ones()
    {
        var outcome = Run("aggressive");
        Assert.Contains("not one of", outcome);
        Assert.Contains("Idle", outcome);
    }

    [Fact]
    public void The_Value_Is_Case_Insensitive() =>
        Assert.Contains("Normal requested", Run("nOrMaL"));

    /// <summary>
    /// The call itself, verified against the kernel rather than against our own return value. <c>ioprio_get</c> is
    /// re-declared here rather than exposed from the production type on purpose: an independent second
    /// implementation catches a wrong syscall number or a wrong bit layout, which reusing the same constants could
    /// not.
    /// <para>
    /// It runs on a thread of its own because IO priority is per-thread and inherited: applying it on a pool thread
    /// would leave every later test that borrowed that thread running de-prioritised. A thread created here takes
    /// the change to the grave with it.
    /// </para>
    /// </summary>
    [Fact]
    public void Low_Really_Reaches_The_Kernel()
    {
        if (!OperatingSystem.IsLinux() || GetSyscall() < 0)
            return;   // nothing to assert on a platform with no such notion; Apply says so and the branch above covers it

        string outcome = "";
        var readBack = -1;
        var thread = new Thread(() =>
        {
            outcome = Run("Low");
            readBack = (int)ioprio_get(GetSyscall(), 1 /* IOPRIO_WHO_PROCESS */, 0 /* this thread */);
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "the priority thread did not finish");

        Assert.Contains("set to Low", outcome);
        // Best-effort class (2) in the top bits, lowest band (7) in the data bits.
        Assert.Equal((2 << 13) | 7, readBack);
    }

    /// <summary>Idle is the other class, and the one worth pinning separately: it needed privileges on kernels old
    /// enough that it is still widely believed to, and a silent refusal would look exactly like success from the
    /// return value alone.</summary>
    [Fact]
    public void Idle_Really_Reaches_The_Kernel()
    {
        if (!OperatingSystem.IsLinux() || GetSyscall() < 0)
            return;

        var readBack = -1;
        var thread = new Thread(() =>
        {
            Run("Idle");
            readBack = (int)ioprio_get(GetSyscall(), 1, 0);
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "the priority thread did not finish");

        // Idle class (3); the data bits carry no meaning in this class and the kernel reports them as zero.
        Assert.Equal(3 << 13, readBack);
    }

    /// <summary>ioprio_get, not _set: x86-64 251/252, arm64 (generic table) 30/31.</summary>
    private static int GetSyscall() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => 252,
        Architecture.Arm64 => 31,
        _ => -1,
    };

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long ioprio_get(long number, long which, long who);
}
