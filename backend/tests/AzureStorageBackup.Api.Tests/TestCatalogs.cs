using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The catalog analogue of <see cref="TestIndexFiles"/>: a throwaway <see cref="VersionCatalogStore"/> (and the
/// <see cref="VersionCatalogs"/> wired on top of it) per call site, so one test's imported version never answers
/// another test's probe.
/// <para>
/// All of them sit under one per-process directory that is removed on exit, for the same reason
/// <see cref="TestIndexFiles"/>'s does: directories outlive the process, so leaving one behind per test would
/// quietly fill the machine's temp space over a few hundred runs.
/// </para>
/// </summary>
internal static class TestCatalogs
{
    private static readonly string Root =
        Path.Combine(Path.GetTempPath(), "asb-catalog-tests-" + Environment.ProcessId);

    static TestCatalogs() =>
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort; it is temp space */ }
        };

    internal static VersionCatalogStore NewStore() => new(Path.Combine(Root, Guid.NewGuid().ToString("N")));

    internal static VersionCatalogs New(AppDbContext db, IBackupInfoStore store, VersionIndexFileStore? legacyFiles = null) =>
        new(NewStore(), legacyFiles ?? TestIndexFiles.New(), db, store);

    /// <summary>
    /// A throwaway catalog holding exactly one imported version — the shortest path from a hand-written
    /// <see cref="VersionIndex"/> to the rows the read side answers from. It goes through the real serializer and the
    /// real stream importer on purpose: a test that hand-inserted rows would be free to invent a row shape the
    /// production import never produces, and would then happily pass against a query that only works on that
    /// invention. The caller disposes it.
    /// </summary>
    internal static async Task<VersionCatalog> ImportAsync(
        VersionIndex index, long identity = 1, CancellationToken ct = default)
    {
        var dir = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var catalog = await VersionCatalog.OpenAsync(Path.Combine(dir, "catalog.db"), readOnly: false, ct);
        try
        {
            using var reader = new IndexStreamReader(new MemoryStream(IndexSerializer.SerializeIndex(index)));
            await catalog.ImportVersionAsync(index.Version, identity, reader, ct);
        }
        catch
        {
            await catalog.DisposeAsync();
            throw;
        }

        return catalog;
    }
}
