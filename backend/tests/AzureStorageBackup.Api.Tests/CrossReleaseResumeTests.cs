using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit.Sdk;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The upgrade case the SQLite catalog has to survive: a backup run that release 2026.9.7 suspended half way is
/// picked up by <b>this</b> release and finishes — and the version it writes is the one the old release wrote,
/// entry for entry, in the same order, down to every hash and storage reference (the one exception is the random
/// tag in the id of a pack this run compresses itself, which no two runs can share — see
/// <see cref="CanonicalPackRef"/>).
/// <para>
/// Everything the old release left behind is replayed out of <c>Fixtures/resume-2026.9.7</c>, recorded by
/// <see cref="FixtureRecorderTests"/> on the unmodified 2026.9.7 tree: the container's blobs, the journal volume
/// with its suspend mark, the <c>.idx</c> file that build cached its previous version in, the locally authoritative
/// info bytes, and the source tree. The account id and container of a fixture cannot be the ones a test run gets, so
/// the journal and the <c>.idx</c> are copied onto this run's (account, container) and the journal header's
/// <c>LocalRoot</c> is rewritten to wherever the fixture's <c>source</c> sits now; the blob names are
/// content-addressed and go up unchanged.
/// </para>
/// <para>
/// The recorded file metadata is restored onto the source tree as well (see <see cref="RestoreRecordedMetadata"/>) —
/// a fixture checked out of git carries the checkout's mtimes and the checkout's umask, and both of those are
/// index content.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class CrossReleaseResumeTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    /// <summary>The account id the recorder ran under, and so the one the fixture's directory layout is keyed by.</summary>
    private const int FixtureAccountId = 44;

    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "asb-xrel-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best effort; it is temp space */ }
    }

    private static Account AzuriteAccount() => new()
    {
        Id = FixtureAccountId,
        Name = "azurite",
        BlobEndpoint = "http://127.0.0.1:10000/devstoreaccount1",
        AccountKeyProtected = TestSecrets.Protect(AzuriteKey),
        Region = AzureRegion.Global,
    };

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static bool SevenZip() => SevenZipArchiveCodec.TryResolveExecutable() is not null;
    private static string RandomName(string p) => p + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// The same wiring <see cref="FixtureRecorderTests"/> resumed the recorded run with, on this release's classes:
    /// a fresh in-memory local-authority database (the catalog and the local info state are seeded from the fixture,
    /// not carried over from another test), over the caller's journal store and <c>.idx</c> file store so the
    /// fixture's copies of both are what the run reads.
    /// </summary>
    private (BackupOrchestrator Orchestrator, BackupInfoStore Store, VersionCatalogs Catalogs, ILocalBackupStateStore LocalState) Build(
        BackupJournalStore journals, VersionIndexFileStore legacyFiles)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var temp = Path.Combine(_scratch, "run");
        var staging = new StagingArea(
            Path.Combine(temp, "compress"), Path.Combine(temp, "staged"), () => 200_000_000);
        var compactor = new DeadWeightCompactor(
            new BlobUploader(factory), new SevenZipCompressor(), new FileHasher(), Path.Combine(temp, "compact"),
            staging);

        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        var catalogs = TestCatalogs.New(db, store, legacyFiles);
        var localState = new LocalBackupStateStore(db);
        var tracked = new TrackedInfoStore(store, localState);

        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), compactor,
                catalogs: catalogs, trackedInfo: tracked, journals: journals),
            new FileHasher(), catalogs, tracked,
            workFactory: TestWorkDbs.New());
        return (orchestrator, store, catalogs, localState);
    }

    /// <summary>The request the recorder used, verbatim — the plan options decide what gets packed and what goes up
    /// as a single blob, so a different threshold here would produce a different (and perfectly correct) index that
    /// simply is not the one the fixture froze.</summary>
    private static BackupRequest Request(Account account, string container, string localRoot) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = localRoot,
        Name = "fixture",
        Options = new BackupEngineOptions
        {
            UploadConcurrency = 1,
            Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
        },
    };

    /// <summary>
    /// Puts the source tree back into the state the recording left it in: every file's mtime and permission bits as
    /// the recorded index has them. Git stores neither — a fresh checkout stamps its own mtimes and applies the
    /// running user's umask — and both go straight into an index entry, so without this the comparison below would
    /// be measuring the checkout instead of the resume. The mtime also decides whether the resumed run may trust a
    /// journal record without re-reading the file, which is the cheap half of the resume path.
    /// </summary>
    private static void RestoreRecordedMetadata(string source, VersionIndex recorded)
    {
        foreach (var entry in recorded.Entries)
        {
            var path = Path.Combine(source, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"the fixture's source tree is missing {entry.Path}");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32(entry.Permissions, 8));
            File.SetLastWriteTimeUtc(path, entry.Mtime.UtcDateTime);
        }
    }

    /// <summary>Copies the recorded journal volume (and its suspend mark) onto this run's account/container, with the
    /// header's <c>LocalRoot</c> rewritten to where the fixture's source tree actually is. All four header fields are
    /// adoption criteria (<see cref="BackupRunControl.OpenJournalAsync"/>); the path this run reports is the only one
    /// that cannot be the recorded one.</summary>
    private static async Task CopyJournalAsync(
        string fixtureDir, string fixtureContainer, string runId, BackupJournalStore journals, Account account,
        string container, string localRoot)
    {
        var sourceDir = Path.Combine(fixtureDir, "journal", FixtureAccountId.ToString(), fixtureContainer);
        var dest = journals.PathFor(account.Id, container, runId);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        var lines = await File.ReadAllLinesAsync(Path.Combine(sourceDir, runId + ".jsonl"));
        var header = JsonNode.Parse(lines[0])!.AsObject();
        header["LocalRoot"] = localRoot;
        lines[0] = header.ToJsonString();
        await File.WriteAllLinesAsync(dest, lines);

        File.Copy(Path.Combine(sourceDir, runId + ".jsonl.suspend"), dest + ".suspend");
    }

    [SkippableFact]
    public async Task Suspended_by_previous_release_resumes_and_writes_the_same_index()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "resume-2026.9.7");
        var fixtureContainer = (await File.ReadAllTextAsync(Path.Combine(fixture, "container.txt"))).Trim();
        var config = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixture, "config.json")))!.AsObject();
        var configId = config["configId"]!.GetValue<int>();
        var suspendedRunId = config["runId"]!.GetValue<string>();
        var source = Path.Combine(fixture, "source");

        // The index the old release produced when it finished this very run. Read first: it also carries the file
        // metadata the recording ran on, which the source tree has to be put back into.
        var expected = LegacyIndexSerializer.DeserializeIndex(
            await File.ReadAllBytesAsync(Path.Combine(fixture, "expected", "v2.idx")));
        RestoreRecordedMetadata(source, expected);

        var account = AzuriteAccount();
        var container = RandomName("xrel");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(container);
        await cc.CreateAsync();
        try
        {
            // 1. the container as the suspended run left it. Blob names are content-addressed (data/, packs/,
            //    indexes/, the info file), so they carry over between containers unchanged; only the recorder's
            //    "/" → "__" file-name encoding has to be undone.
            foreach (var file in Directory.EnumerateFiles(Path.Combine(fixture, "blobs")))
                await cc.GetBlobClient(Path.GetFileName(file).Replace("__", "/")).UploadAsync(file);

            // 2. the local state that run left behind: journal volume + suspend mark, the cached .idx of version 1,
            //    and the locally authoritative info bytes. The info ETag has to be the live one — the tracked store
            //    writes the next info file with it as If-Match.
            var journals = new BackupJournalStore(Path.Combine(_scratch, "journal"));
            var legacyFiles = new VersionIndexFileStore(Path.Combine(_scratch, "index-cache"));
            await CopyJournalAsync(fixture, fixtureContainer, suspendedRunId, journals, account, container, source);

            var destIdx = legacyFiles.PathFor(account.Id, container, 1);
            Directory.CreateDirectory(Path.GetDirectoryName(destIdx)!);
            File.Copy(
                Path.Combine(fixture, "index-cache", FixtureAccountId.ToString(), fixtureContainer, "1.idx"),
                destIdx);

            var (orchestrator, store, catalogs, localState) = Build(journals, legacyFiles);
            var infoETag = (await cc.GetBlobClient(BackupDiscovery.IndexBlobName).GetPropertiesAsync())
                .Value.ETag.ToString();
            await localState.PutAsync(
                account.Id, container, await File.ReadAllBytesAsync(Path.Combine(fixture, "info.bin")), infoETag);

            // 3. the journal is adoptable by this release: all four criteria hold, including the addressing identity —
            //    neither the scheme nor the fingerprint changed, so an unencrypted backup is "plain" here as it was there.
            var header = Assert.Single(await journals.ListHeadersAsync(account.Id, container, default)).Header;
            Assert.Equal(configId, header.ConfigId);
            Assert.Equal(1, header.BaselineVersion);
            Assert.Equal(source, header.LocalRoot);
            Assert.Equal(new BlobAddressScheme(null, null).Identity, header.EncryptionIdentity);

            // Read while the journal still exists: a completed run deletes the volumes it adopted.
            var journaledPacks = (await journals.ListAsync(account.Id, container, default))
                .SelectMany(v => v.Content.Records).Where(r => r.Kind == "pack").Select(r => r.Ref).ToList();
            Assert.NotEmpty(journaledPacks);

            // 4. resume it on this release.
            BackupRunResult result;
            await using (var control = new BackupRunControl(journals, configId, "xrel-resume"))
                result = await orchestrator.RunAsync(Request(account, container, source), null, default, control);
            Assert.Equal(2, result.Version);

            // 5. the index that landed in the cloud is the one the old release wrote — same entries, same order,
            //    same empty dirs, same unrecoverable paths.
            var info = await store.ReadInfoAsync(account, container, null);
            Assert.NotNull(info);
            var v2 = info!.Versions.Single(v => v.Version == 2);
            var actualPath = Path.Combine(_scratch, "v2-actual.idx");
            await store.ReadIndexToFileAsync(account, container, v2.IndexBlob, null, v2.IndexVolumes, actualPath);

            await using var actualFile = File.OpenRead(actualPath);
            using var actual = new IndexStreamReader(actualFile);
            Assert.Equal(expected.Entries.Count, actual.EntryCount);
            var actualEntries = actual.Entries().ToList();
            Assert.Equal(expected.Entries.Count, actualEntries.Count);
            // Paths first: an ordering defect then reports as "position N: expected a, got b" instead of as a field
            // mismatch between two unrelated entries.
            Assert.Equal(expected.Entries.Select(e => e.Path), actualEntries.Select(e => e.Path));
            var priorPacks = PriorPackRefs(fixture);
            Dictionary<string, string> expectedAliases = [], actualAliases = [];
            for (var i = 0; i < expected.Entries.Count; i++)
            {
                try
                {
                    IndexAssert.AssertSameEntry(
                        CanonicalPackRef(expected.Entries[i], priorPacks, expectedAliases),
                        CanonicalPackRef(actualEntries[i], priorPacks, actualAliases));
                }
                catch (Exception ex)
                {
                    throw new XunitException(
                        $"index entry {i} of {expected.Entries.Count} differs.{Environment.NewLine}" +
                        $"expected: {Describe(expected.Entries[i])}{Environment.NewLine}" +
                        $"actual:   {Describe(actualEntries[i])}{Environment.NewLine}{ex.Message}");
                }
            }

            Assert.Equal(expected.EmptyDirs, actual.ReadEmptyDirs());
            Assert.Equal(expected.UnrecoverablePaths, actual.ReadUnrecoverable());

            // 6. and it really was a resume: every pack the suspended run had uploaded is referenced by this index
            //    under the name it already has in the container, and the run sent less than the source had changed.
            foreach (var packRef in journaledPacks)
                Assert.Contains(actualEntries, e => e.Storage?.Ref == packRef);
            Assert.True(
                result.UploadedBytes < result.ChangedBytes,
                $"nothing was reused: uploaded {result.UploadedBytes} of {result.ChangedBytes} changed bytes");

            // 7. the catalog holds what the cloud holds — this release answers dedup, browse and the next diff out of
            //    it, so a version that is right in the cloud and absent (or short) locally is still a broken upgrade.
            await using var catalog = await catalogs.OpenAsync(account.Id, container, readOnly: true);
            var row = await catalog.GetVersionAsync(2, default);
            Assert.NotNull(row);
            Assert.Equal(expected.Entries.Count, row!.EntryCount);
            var (files, bytes) = await catalog.StatsAsync(2, default);
            Assert.Equal(expected.Entries.Count, files);
            Assert.Equal(expected.Entries.Sum(e => e.Length), bytes);
        }
        finally
        {
            try { await cc.DeleteIfExistsAsync(); } catch { /* best effort */ }
        }
    }

    /// <summary><c>p</c> + the run's random 8-hex tag + a 4-digit sequence — the shape of
    /// <c>BackupOrchestrator.RunState.NextPackId</c>.</summary>
    private static readonly Regex PackRefPattern = new(@"^p[0-9a-f]{8}[0-9]{4}$", RegexOptions.Compiled);

    /// <summary>The packs that were already in the container before the resume: version 1's, and the four the
    /// suspended run had uploaded. Their names are part of what the resume must not disturb, so they are compared
    /// literally.</summary>
    private static HashSet<string> PriorPackRefs(string fixtureDir) =>
        [.. Directory.EnumerateFiles(Path.Combine(fixtureDir, "blobs"), "packs__*")
            .Select(f => Path.GetFileName(f)["packs__".Length..].Split('.')[0])];

    /// <summary>
    /// Rewrites the tag of a pack this run created itself to a positional alias, so two runs can be compared.
    /// <para>
    /// A pack id is not content-addressed: it is <c>p</c> + a random tag drawn once per run + a sequence number
    /// (<c>BackupOrchestrator.RunState.NextPackId</c>, and 2026.9.7 did exactly the same — the fixture's own journal
    /// and its version 1 carry two different tags). So the four packs the resuming run compresses itself cannot
    /// possibly get the recorded run's names, and requiring them to would be requiring a random number to repeat.
    /// Everything the name actually means is still compared: aliases are handed out in order of first appearance, so
    /// the mapping has to be one-to-one and in the same order on both sides, and the sequence number — which pack of
    /// this run it is, and therefore that the resumed run kept counting from 5 rather than restarting at 1 — is
    /// compared verbatim. Any ref that existed before the resume is left alone and compared as it is.
    /// </para>
    /// </summary>
    private static IndexEntry CanonicalPackRef(
        IndexEntry entry, IReadOnlySet<string> priorPacks, Dictionary<string, string> aliases)
    {
        if (entry.Storage is not { Kind: "pack" } storage
            || priorPacks.Contains(storage.Ref)
            || !PackRefPattern.IsMatch(storage.Ref))
            return entry;

        var tag = storage.Ref[1..^4];
        if (!aliases.TryGetValue(tag, out var alias))
            aliases[tag] = alias = $"<this-run-{aliases.Count}>";
        return entry with { Storage = storage with { Ref = $"p{alias}{storage.Ref[^4..]}" } };
    }

    private static string Describe(IndexEntry e) =>
        JsonSerializer.Serialize(new
        {
            e.Path, e.Kind, e.Length, e.Mtime, e.Permissions, e.HeadHash, e.TailHash, e.FullHash, e.Target,
            e.UnreadableAt,
            Storage = e.Storage is null
                ? null
                : $"{e.Storage.Kind}:{e.Storage.Ref}[{e.Storage.EntryName}] vol={e.Storage.Volumes} raw={e.Storage.Raw} sizes=[{string.Join(",", e.Storage.VolumeSizes)}]",
        });
}
