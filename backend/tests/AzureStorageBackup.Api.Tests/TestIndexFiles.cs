using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// A throwaway <see cref="VersionIndexFileStore"/> per call site. The version-index cache is backed by files rather
/// than a table, so every test that builds a <see cref="LocalIndexCache"/> now needs somewhere to put them; a shared
/// directory would let one test's cached index answer another's read.
/// <para>
/// All of them sit under one per-process directory that is removed on exit. Unlike the in-memory SQLite connections
/// <c>TestLocalAuthority</c> leaves to the process (see the note there), directories outlive the process, so leaving
/// one behind per test would quietly fill the machine's temp space over a few hundred runs.
/// </para>
/// </summary>
internal static class TestIndexFiles
{
    private static readonly string Root =
        Path.Combine(Path.GetTempPath(), "asb-idxcache-tests-" + Environment.ProcessId);

    static TestIndexFiles() =>
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort; it is temp space */ }
        };

    internal static VersionIndexFileStore New() => new(Path.Combine(Root, Guid.NewGuid().ToString("N")));
}
