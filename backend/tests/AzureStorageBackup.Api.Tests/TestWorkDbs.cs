using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// A throwaway <see cref="RunWorkDbFactory"/> per call site. Every backup run now opens a scratch database, so every
/// test that builds a <see cref="BackupOrchestrator"/> has to say where those files may go; a shared directory would
/// let two tests running in parallel collide on a run id.
/// <para>
/// All of them sit under one per-process directory that is removed on exit, the same arrangement (and for the same
/// reason) as <see cref="TestIndexFiles"/>: a run deletes its own database, but a test that kills a run part way
/// through need not, and directories outlive the process.
/// </para>
/// </summary>
internal static class TestWorkDbs
{
    private static readonly string Root =
        Path.Combine(Path.GetTempPath(), "asb-runwork-tests-" + Environment.ProcessId);

    static TestWorkDbs() =>
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort; it is temp space */ }
        };

    internal static RunWorkDbFactory New() => new(Path.Combine(Root, Guid.NewGuid().ToString("N")));
}
