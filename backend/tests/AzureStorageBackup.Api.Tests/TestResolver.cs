using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Builds a <see cref="LocalDedupResolver"/> the way the product does — over a real <see cref="VersionCatalog"/> and
/// a real <see cref="RunWorkDb"/> — out of the plain <see cref="VersionIndex"/> objects the resolver's tests are
/// written in terms of. The resolver no longer takes indexes, but "given these versions, what does dedup answer" is
/// still the question every one of those tests asks, so the translation lives here once instead of in each of them.
/// <para>
/// Both files land under one per-process directory that is removed on exit, for the same reason
/// <see cref="TestCatalogs"/>'s does: directories outlive the process, and one left behind per test would quietly
/// fill the machine's temp space over a few hundred runs.
/// </para>
/// </summary>
internal static class TestResolver
{
    private static readonly string Root =
        Path.Combine(Path.GetTempPath(), "asb-dedup-tests-" + Environment.ProcessId);

    static TestResolver() =>
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort; it is temp space */ }
        };

    /// <summary>
    /// Imports <paramref name="indexes"/> into a fresh catalog and <paramref name="confirmed"/> into a fresh run
    /// work database, and returns a resolver over the two. Dispose the second item to close both files and remove
    /// them.
    /// </summary>
    internal static async Task<(LocalDedupResolver Resolver, IAsyncDisposable Cleanup)> From(
        BlobAddressScheme addressing, IReadOnlyList<VersionIndex> indexes,
        IReadOnlyList<ConfirmedBlob>? confirmed = null, CancellationToken ct = default)
    {
        var (catalog, work, cleanup) = await OpenAsync(ct);
        try
        {
            // Imported under the list's own position rather than under index.Version: Build folded the indexes in
            // the order it was handed them (oldest first), and every "which version wins" rule in the catalog is an
            // ORDER BY version — so the position is the faithful translation, and a test that writes Version = 1 on
            // two indexes still gets two versions instead of one overwriting the other.
            for (var i = 0; i < indexes.Count; i++)
            {
                using var reader = new IndexStreamReader(new MemoryStream(IndexSerializer.SerializeIndex(indexes[i])));
                await catalog.ImportVersionAsync(i + 1, identity: i + 1, reader, ct);
            }

            // The journal's confirmed blocks: the same rows JournalResume would have filed, each under a path of its
            // own because that is what resume_blobs is keyed by (a ConfirmedBlob has dropped the path — from there
            // on only the content identity matters).
            var seq = 0;
            foreach (var c in confirmed ?? [])
            {
                await work.InsertResumeRecordAsync(
                    new JournalRecord
                    {
                        Kind = "blob", Path = $"confirmed/{seq++}", Ref = c.Blob.Ref, FullHash = c.FullHash,
                        HeadHash = c.HeadHash, TailHash = c.TailHash, Length = c.Length, Raw = c.Blob.Raw,
                        Volumes = c.Blob.Volumes, VolumeSizes = c.Blob.VolumeSizes,
                    }, ct);
            }

            // The work database's writer batches, so without this the rows just enqueued would still be invisible to
            // the first probe a test makes.
            await work.FlushAsync(ct);
            return (new LocalDedupResolver(addressing, catalog, work), cleanup);
        }
        catch
        {
            await cleanup.DisposeAsync();
            throw;
        }
    }

    /// <summary>The two databases on their own, for the tests that have to reach past the resolver — flushing the
    /// work database, or reading back the reservation a finished upload wrote.</summary>
    internal static async Task<(VersionCatalog Catalog, RunWorkDb Work, IAsyncDisposable Cleanup)> OpenAsync(
        CancellationToken ct)
    {
        var dir = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var catalog = await VersionCatalog.OpenAsync(Path.Combine(dir, "catalog.db"), readOnly: false, ct);
        try
        {
            var work = await new RunWorkDbFactory(dir).CreateAsync("run", ct);
            return (catalog, work, new Files(catalog, work, dir));
        }
        catch
        {
            await catalog.DisposeAsync();
            throw;
        }
    }

    private sealed class Files(VersionCatalog catalog, RunWorkDb work, string dir) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await catalog.DisposeAsync();
            await work.DisposeAsync();   // which deletes the scratch file itself
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* a leaked handle must not fail a test that already passed */ }
        }
    }
}
