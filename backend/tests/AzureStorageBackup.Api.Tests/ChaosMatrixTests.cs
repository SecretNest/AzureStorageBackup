using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Chaos storm for the reader/rewriter matrix (real Azurite + real 7-Zip, driven through the production
/// HTTP surface — runners, busy tracker, dispatcher, everything wired as deployed). Four workers hammer one
/// backup config concurrently for a bounded, seeded stretch:
///   backups (mutating the source tree between runs, tight MaxVersions so retention actually retires),
///   restores of random known versions (verified byte-for-byte against the tree snapshot taken at commit),
///   checks (the oracle: the cloud must NEVER show a missing/bad referenced object — nothing in this storm
///   corrupts the cloud, so any MissingOrBad finding means a rewriter deleted what a live version references),
///   scheduled cleanups through the task dispatcher (the standalone RetentionCleaner path),
/// plus a metadata browser (/tree, /file-versions) that must never see a 500.
/// Acceptable outcomes are explicit: busy refusals and version-retired failures are the matrix WORKING;
/// a mid-download 404, a hash mismatch, a dirty check or a leaked busy/reader mark is the matrix BROKEN.
/// Every run prints its seed; CHAOS_SEED replays a schedule, CHAOS_SECONDS stretches the storm.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ChaosMatrixTests(TestWebAppFactory factory, ITestOutputHelper output)
    : IClassFixture<TestWebAppFactory>, IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    private const string AzuriteEndpoint = "http://127.0.0.1:10000/devstoreaccount1";

    private readonly HttpClient _client = factory.CreateClient();
    private readonly string _base = Path.Combine(Path.GetTempPath(), "asb-chaos-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly ConcurrentQueue<string> _log = new();
    private readonly ConcurrentBag<string> _violations = [];

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
    }

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static bool SevenZip() => SevenZipArchiveCodec.TryResolveExecutable() is not null;

    private void Log(string worker, string message) =>
        _log.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} [{worker}] {message}");

    /// <summary>Both storms run against the one Azurite endpoint, and endpoint uniqueness (a product rule:
    /// one storage account, one entry) refuses a second registration when they share a host in a full-suite
    /// run — so take the account that is already there instead. Containers stay per-storm.</summary>
    private async Task<AccountResponse> GetOrCreateAzuriteAccountAsync(string tag)
    {
        var res = await _client.PostAsJsonAsync("/api/accounts", new AccountRequest(
            "azurite-" + tag + "-" + Guid.NewGuid().ToString("N")[..6], null, AzuriteEndpoint, AzureRegion.Global,
            AzuriteKey, false, ProxyMode.Independent, null, null, null, null));
        if (res.IsSuccessStatusCode)
            return (await res.Content.ReadFromJsonAsync<AccountResponse>())!;
        var all = await _client.GetFromJsonAsync<List<AccountResponse>>("/api/accounts");
        return all!.First(a => a.BlobEndpoint == AzuriteEndpoint);
    }

    private void Violation(string worker, string message)
    {
        _violations.Add($"[{worker}] {message}");
        Log(worker, "VIOLATION: " + message);
    }

    // ---- Source tree -------------------------------------------------------------------------------

    private string Root => Path.Combine(_base, "src");

    /// <summary>Mixed content: half compressible text, half incompressible noise — packs and raw blobs both appear.</summary>
    private void WriteSourceFile(Random rng, string rel)
    {
        var full = Path.Combine(Root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // Big enough that a version's restore spans real time (the races need a window), small enough
        // that the whole storm stays in the tens of megabytes on disk.
        var size = rng.Next(20_000, 150_000);
        byte[] bytes;
        if (rng.Next(2) == 0)
        {
            bytes = new byte[size];
            rng.NextBytes(bytes);
        }
        else
        {
            bytes = System.Text.Encoding.UTF8.GetBytes(
                string.Concat(Enumerable.Repeat($"line of {rel} rolled {rng.Next():x8}\n", size / 40 + 1)));
        }
        File.WriteAllBytes(full, bytes);
    }

    private Dictionary<string, string> SnapshotTree()
    {
        var snap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            using var s = File.OpenRead(f);
            snap[Path.GetRelativePath(Root, f)] = Convert.ToHexString(SHA256.HashData(s));
        }
        return snap;
    }

    // ---- The storm ---------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_Seeded_Storm_Of_Backup_Restore_Check_Cleanup_Upholds_The_Matrix()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not found");
        // The storm writes restore copies and Azurite extents; refuse to run into a nearly-full disk.
        Skip.IfNot(new DriveInfo(Path.GetTempPath()).AvailableFreeSpace > 1_000_000_000, "under 1 GB free in temp");

        var seed = int.TryParse(Environment.GetEnvironmentVariable("CHAOS_SEED"), out var s) ? s : Random.Shared.Next();
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("CHAOS_SECONDS"), out var d) ? d : 45;
        output.WriteLine($"CHAOS_SEED={seed} CHAOS_SECONDS={seconds} (set both to replay this schedule)");

        Directory.CreateDirectory(Root);
        var seedRng = new Random(seed);
        for (var i = 0; i < 40; i++)
            WriteSourceFile(seedRng, $"d{i % 5}/f{i:D3}.bin");

        var containerName = "chaos-" + Guid.NewGuid().ToString("N")[..8];
        var account = await GetOrCreateAzuriteAccountAsync("chaos");
        var config = await (await _client.PostAsJsonAsync("/api/backup-configs", new BackupConfigRequest(
                account!.Id, containerName, "chaos", null, Root, null,
                StorageTier.Hot, StorageTier.Hot,
                MaxVersions: 3, MaxAgeDays: 180, RetentionMode: RetentionMode.EitherTriggers,
                SingleFileThresholdBytes: 262_144,  // everything in this tree packs → compaction has material
                VolumeBytes: 131_072)))             // small volumes → real families, real ReplaceAsync shapes
            .Content.ReadFromJsonAsync<BackupConfigResponse>();
        var configId = config!.Id;

        var cleanupTask = await (await _client.PostAsJsonAsync("/api/tasks", new TaskRequest(
                TaskTargetKind.Backup, account.Id, containerName, null,
                ScheduledTaskType.Cleanup, "0 3 * * *", true)))
            .Content.ReadFromJsonAsync<TaskIdOnly>();

        // version → tree snapshot at commit time. Retired versions are pruned as restores discover them gone.
        var snapshots = new ConcurrentDictionary<int, Dictionary<string, string>>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));

        var factoryClient = new BlobClientFactory(TestSecrets.Reader);
        var azurite = new Account { BlobEndpoint = AzuriteEndpoint, AccountKeyProtected = TestSecrets.Protect(AzuriteKey), Region = AzureRegion.Global };
        var cloud = factoryClient.CreateServiceClient(azurite).GetBlobContainerClient(containerName);
        try
        {
            var workers = new[]
            {
                BackupWorker(configId, new Random(seed ^ 0x1111), snapshots, stop.Token),
                RestoreWorker(configId, new Random(seed ^ 0x2222), snapshots, stop.Token),
                CheckWorker(configId, stop.Token),
                CleanupWorker(cleanupTask!.id, stop.Token),
                BrowseWorker(configId, new Random(seed ^ 0x3333), snapshots, stop.Token),
            };
            await Task.WhenAll(workers);

            // ---- Settle and judge ------------------------------------------------------------------
            await WaitTerminalAsync($"/api/backup-configs/{configId}/run", "settle", TimeSpan.FromMinutes(3));

            var busy = factory.Services.GetRequiredService<BackupBusyTracker>();
            for (var i = 0; i < 100 && (busy.IsBusy(account.Id, containerName) || busy.HasReaders(account.Id, containerName)); i++)
                await Task.Delay(100); // runners release busy marks in their tails, moments after the state turns terminal
            if (busy.IsBusy(account.Id, containerName))
                Violation("settle", $"busy mark leaked: {busy.CurrentActivity(account.Id, containerName)}");
            if (busy.HasReaders(account.Id, containerName))
                Violation("settle", "reader count leaked after every restore finished");

            // Closing oracle: one full check of everything the storm left committed must come back clean.
            var final = await RunCheckAsync(configId, "final-check", CancellationToken.None);
            if (final is null)
                Violation("final-check", "the closing check could not be started or did not complete");

            if (!_violations.IsEmpty)
            {
                foreach (var line in _log)
                    output.WriteLine(line);
                Assert.Fail($"seed {seed}: {_violations.Count} violation(s):\n  " + string.Join("\n  ", _violations));
            }
            output.WriteLine($"storm clean: {snapshots.Count} versions verified live at the end, seed {seed}");
        }
        finally
        {
            await cloud.DeleteIfExistsAsync();
        }
    }

    private sealed record TaskIdOnly(int id);

    // ---- Workers -----------------------------------------------------------------------------------

    private async Task BackupWorker(int configId, Random rng, ConcurrentDictionary<int, Dictionary<string, string>> snapshots, CancellationToken until, SemaphoreSlim? treeLock = null)
    {
        while (!until.IsCancellationRequested)
        {
            if (treeLock is not null)
                await treeLock.WaitAsync(CancellationToken.None);
            try
            {
                // Mutate, snapshot, run. The tree lock (damage storm only) keeps the vandal's frozen
                // cycle exact; the vandal/ directory is its heal material and is never touched here.
                var files = Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
                    .Where(f => !Path.GetRelativePath(Root, f).StartsWith("vandal", StringComparison.Ordinal)).ToList();
                var edits = rng.Next(5, 15);
                for (var i = 0; i < edits; i++)
                {
                    var roll = rng.Next(10);
                    if (roll < 6 && files.Count > 0)
                        WriteSourceFile(rng, Path.GetRelativePath(Root, files[rng.Next(files.Count)]));
                    else if (roll < 8)
                        WriteSourceFile(rng, $"d{rng.Next(5)}/n{rng.Next():x8}.bin");
                    else if (files.Count > 20)
                    {
                        File.Delete(files[rng.Next(files.Count)]);
                        files = Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
                            .Where(f => !Path.GetRelativePath(Root, f).StartsWith("vandal", StringComparison.Ordinal)).ToList();
                    }
                }
                var snapshot = SnapshotTree();

                var res = await _client.PostAsync($"/api/backup-configs/{configId}/run", null, CancellationToken.None);
                if (!res.IsSuccessStatusCode)
                {
                    Log("backup", $"start refused: {(int)res.StatusCode}");
                    await Task.Delay(300, CancellationToken.None);
                    continue;
                }
                // The POSTed state is not necessarily OUR run: re-clicking inside the settle window hands back
                // the PREVIOUS run's still-unsettled (already terminal) state — by design (the Start guard).
                // Track the RunId the POST answered with; only a completion of that very run may record a
                // snapshot, and TryAdd (a version commits exactly once) keeps any echo from overwriting the
                // tree that version was actually built from.
                var started = await res.Content.ReadFromJsonAsync<BackupRunResponse>(CancellationToken.None);
                if (started is null || started.Status != "Running")
                {
                    Log("backup", $"echo of a settling run (RunId {started?.RunId}, {started?.Status}) — retrying");
                    await Task.Delay(300, CancellationToken.None);
                    continue;
                }
                var run = await WaitTerminalAsync($"/api/backup-configs/{configId}/run", "backup", TimeSpan.FromMinutes(3));
                if (run?.RunId != started.RunId)
                {
                    Log("backup", "lost sight of our run (replaced mid-poll) — snapshot dropped");
                }
                else if (run.Status == "Completed" && run.Version is { } v)
                {
                    if (snapshots.TryAdd(v, snapshot))
                        Log("backup", $"version {v} committed ({snapshot.Count} files)");
                    else
                        Violation("backup", $"version {v} reported committed twice by distinct runs");
                }
                else if (run.Status == "Failed" && run.Error?.Contains("busy", StringComparison.OrdinalIgnoreCase) != true)
                {
                    Violation("backup", $"run failed: {run.Error}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Violation("backup", ex.ToString());
            }
            finally
            {
                treeLock?.Release();
            }
            await Task.Delay(200, CancellationToken.None);
        }
    }

    private async Task RestoreWorker(int configId, Random rng, ConcurrentDictionary<int, Dictionary<string, string>> snapshots, CancellationToken until, Func<int>? safeBelow = null)
    {
        var restoreBase = Path.Combine(_base, "restores");
        while (!until.IsCancellationRequested)
        {
            // The damage storm's vandal publishes a floor: content introduced at version F is carried
            // forward by dedup into every later version, so while F's object lies deliberately broken,
            // only versions BELOW F restore against an honest cloud — the strict byte oracle stays valid.
            var versions = snapshots.Keys.Where(v => v < (safeBelow?.Invoke() ?? int.MaxValue)).OrderBy(v => v).ToList();
            if (versions.Count == 0)
            {
                try { await Task.Delay(200, until); } catch (OperationCanceledException) { break; }
                continue;
            }
            // Aim at the retirement edge: with MaxVersions=3, versions latest-2 … latest-4 are exactly what
            // the next cleanup retires — restoring those is what collides with the deletes. The latest is
            // thrown in some of the time (it collides with compaction rewriting still-live packs instead).
            var latest = versions[^1];
            var edge = versions.Where(v => v >= latest - 4 && v < latest).ToList();
            var version = rng.Next(10) < 3 || edge.Count == 0 ? latest : edge[rng.Next(edge.Count)];
            var target = Path.Combine(restoreBase, $"v{version}-{Guid.NewGuid().ToString("N")[..6]}");
            Directory.CreateDirectory(target);
            try
            {
                var res = await _client.PostAsJsonAsync($"/api/backup-configs/{configId}/restore",
                    new RestoreRequestBody(target, version), CancellationToken.None);
                if (!res.IsSuccessStatusCode)
                {
                    Log("restore", $"v{version} start refused: {(int)res.StatusCode}");
                    continue;
                }
                var state = await PollRestoreAsync(configId, TimeSpan.FromMinutes(3));
                if (state is null)
                {
                    Violation("restore", $"v{version} never reached a terminal state");
                }
                else if (state.Status == "Completed")
                {
                    VerifyRestore(version, target, snapshots,
                        $"restored={state.RestoredFiles} skipped={state.SkippedFiles} failed={state.FailedFiles}");
                }
                else if (state.Error is { } err && err.Contains("busy", StringComparison.OrdinalIgnoreCase))
                {
                    Log("restore", $"v{version} refused busy (matrix at work): {err}");
                }
                else if (state.Error is { } gone &&
                         (gone.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                          gone.Contains("no longer", StringComparison.OrdinalIgnoreCase)))
                {
                    // Retired before the run resolved it — the acceptable, clean shape of that outcome.
                    Log("restore", $"v{version} already retired: {gone}");
                    snapshots.TryRemove(version, out _);
                }
                else
                {
                    // A 404 mid-download, a mixed volume family, an extraction error: the matrix broke.
                    Violation("restore", $"v{version} failed dirty: status={state.Status} error={state.Error}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Violation("restore", ex.ToString());
            }
            finally
            {
                try { Directory.Delete(target, recursive: true); } catch { /* keep the disk small either way */ }
            }
        }
    }

    private void VerifyRestore(int version, string target, ConcurrentDictionary<int, Dictionary<string, string>> snapshots, string? runDetail = null)
    {
        if (!snapshots.TryGetValue(version, out var expected))
            return; // pruned while we restored; nothing to compare against
        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
        {
            using var s = File.OpenRead(f);
            actual[Path.GetRelativePath(target, f)] = Convert.ToHexString(SHA256.HashData(s));
        }
        foreach (var (path, hash) in expected)
        {
            if (!actual.TryGetValue(path, out var got))
                Violation("restore", $"v{version}: '{path}' missing from a Completed restore ({runDetail}; cloud marks now: {CloudMarksAsync(version).GetAwaiter().GetResult()})");
            else if (got != hash)
                Violation("restore", $"v{version}: '{path}' content mismatch (old/new volume mix?) ({runDetail})");
        }
        foreach (var path in actual.Keys.Where(p => !expected.ContainsKey(p)))
            Violation("restore", $"v{version}: extraneous '{path}' in a Completed restore");
        Log("restore", $"v{version} verified byte-for-byte ({expected.Count} files)");
    }

    private async Task CheckWorker(int configId, CancellationToken until)
    {
        while (!until.IsCancellationRequested)
        {
            try
            {
                await RunCheckAsync(configId, "check", until);
                await Task.Delay(500, until);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Violation("check", ex.ToString());
            }
        }
    }

    /// <summary>Starts a check and, when it completes, applies the oracle: no referenced object may be
    /// missing or bad in the cloud — nothing in this storm damages the cloud on purpose, so a finding
    /// can only mean a rewriter deleted or tore something a committed version references.</summary>
    private async Task<CheckRunResponse?> RunCheckAsync(int configId, string worker, CancellationToken ct)
    {
        var res = await _client.PostAsync($"/api/backup-configs/{configId}/check?cloud=ExistenceSize", null, CancellationToken.None);
        if (!res.IsSuccessStatusCode)
        {
            Log(worker, $"start refused: {(int)res.StatusCode}"); // busy or gated — both fine
            return null;
        }
        for (var waited = 0; waited < 180_000; waited += 250)
        {
            var run = await (await _client.GetAsync($"/api/backup-configs/{configId}/check", CancellationToken.None))
                .Content.ReadFromJsonAsync<CheckRunResponse>(CancellationToken.None);
            if (run is null || run.Status == "Running")
            {
                await Task.Delay(250, CancellationToken.None);
                continue;
            }
            if (run.Status == "Completed" && run.Report is { } report)
            {
                var bad = report.Findings.Where(f => f.Cloud == CloudState.MissingOrBad).ToList();
                foreach (var f in bad)
                    Violation(worker, $"cloud lost referenced object: {f.Path} ({f.Ref})");
                Log(worker, $"v{report.Version} checked: {(bad.Count == 0 ? "clean" : $"{bad.Count} BAD")}");
            }
            else
            {
                Log(worker, $"ended {run.Status}: {run.Error}");
            }
            return run;
        }
        Violation(worker, "check never reached a terminal state");
        return null;
    }

    private async Task CleanupWorker(int taskId, CancellationToken until)
    {
        while (!until.IsCancellationRequested)
        {
            try
            {
                // The dispatcher awaits the whole cleanup; busy targets are skipped with a warning, and the
                // cleaner itself stands down for readers — every outcome is legal, the check is the judge.
                var res = await _client.PostAsync($"/api/tasks/{taskId}/run", null, CancellationToken.None);
                Log("cleanup", $"dispatched: {(int)res.StatusCode}");
                await Task.Delay(700, until);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Violation("cleanup", ex.ToString());
            }
        }
    }

    private async Task BrowseWorker(int configId, Random rng, ConcurrentDictionary<int, Dictionary<string, string>> snapshots, CancellationToken until)
    {
        while (!until.IsCancellationRequested)
        {
            try
            {
                var versions = snapshots.Keys.ToList();
                var url = rng.Next(3) switch
                {
                    0 when versions.Count > 0 => $"/api/backup-configs/{configId}/tree?version={versions[rng.Next(versions.Count)]}",
                    1 => $"/api/backup-configs/{configId}/tree",
                    _ => $"/api/backup-configs/{configId}/file-versions?path=d0/f000.bin",
                };
                var res = await _client.GetAsync(url, CancellationToken.None);
                if ((int)res.StatusCode >= 500)
                    Violation("browse", $"{url} → {(int)res.StatusCode}");
                await Task.Delay(150, until);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Violation("browse", ex.ToString());
            }
        }
    }

    // ---- The damage-repair storm -------------------------------------------------------------------

    private Account? _probeAccount;
    private string? _probeContainer;

    /// <summary>Ground truth for the mark diagnosis: read the CLOUD index of each version directly
    /// (no local cache layer, no HTTP surface) and report its UnrecoverablePaths.</summary>
    private async Task<string> CloudMarksAsync(params int[] versions)
    {
        if (_probeAccount is null || _probeContainer is null)
            return "(no probe target)";
        try
        {
            var store = new BackupInfoStore(new BlobClientFactory(TestSecrets.Reader), new SevenZipArchiveCodec());
            var info = await store.ReadInfoAsync(_probeAccount, _probeContainer, null);
            var parts = new List<string>();
            foreach (var v in versions)
            {
                var ver = info?.Versions.FirstOrDefault(x => x.Version == v);
                if (ver is null)
                {
                    parts.Add($"v{v}:absent");
                    continue;
                }
                var idx = await store.ReadIndexAsync(_probeAccount, _probeContainer, ver.IndexBlob, null, ver.IndexVolumes);
                parts.Add($"v{v}:[{string.Join(",", idx.UnrecoverablePaths)}]");
            }
            return string.Join(" ", parts);
        }
        catch (Exception ex)
        {
            return $"(probe failed: {ex.Message})";
        }
    }

    /// <summary>While an object referenced from version F onward lies deliberately broken, the strict
    /// restore oracle only holds below F (dedup carries F's content into every later version).</summary>
    private volatile int _damageFloor = int.MaxValue;

    /// <summary>
    /// The other half of the matrix, which the retention storm cannot exercise: deliberate cloud damage
    /// driving the check→repair gate to a full heal while restores and backups keep running. Each cycle,
    /// under the tree lock (so nothing else commits and the heal material stays frozen): write fresh
    /// raw-route bait files in vandal/ (a directory no other worker touches), commit them as version V,
    /// and break the newest data/ object — provably V's bait, provably matching its local copy. Then,
    /// with the storm live again: the check must flag it, the repair must heal it with zero unrecoverable
    /// (its busy/reader refusals along the way ARE the matrix and are retried), the gate must already read
    /// Repaired the moment the repair polls Completed (round four's atomic reconciliation, observed through
    /// production wiring), and a recheck must come back clean — only then do restores ≥ V become fair game again.
    /// </summary>
    [SkippableFact]
    public async Task A_Damage_And_Repair_Storm_Heals_Under_Concurrent_Restores()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not found");
        Skip.IfNot(new DriveInfo(Path.GetTempPath()).AvailableFreeSpace > 1_000_000_000, "under 1 GB free in temp");

        var seed = int.TryParse(Environment.GetEnvironmentVariable("CHAOS_SEED"), out var s) ? s : Random.Shared.Next();
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("CHAOS_SECONDS"), out var d) ? d : 45;
        output.WriteLine($"CHAOS_SEED={seed} CHAOS_SECONDS={seconds} (set both to replay this schedule)");

        Directory.CreateDirectory(Root);
        var seedRng = new Random(seed);
        for (var i = 0; i < 40; i++)
            WriteSourceFile(seedRng, $"d{i % 5}/f{i:D3}.bin");

        var containerName = "chaosdr-" + Guid.NewGuid().ToString("N")[..8];
        var account = await GetOrCreateAzuriteAccountAsync("chaosdr");
        var configRes = await _client.PostAsJsonAsync("/api/backup-configs", new BackupConfigRequest(
            account!.Id, containerName, "chaos-dr", null, Root, null,
            StorageTier.Hot, StorageTier.Hot,
            MaxVersions: 100, MaxAgeDays: 3650, RetentionMode: RetentionMode.EitherTriggers, // no retirement:
            SingleFileThresholdBytes: 262_144,  // the retention interplay is the OTHER storm's subject
            VolumeBytes: 131_072));
        Assert.True(configRes.IsSuccessStatusCode,
            $"config creation: {(int)configRes.StatusCode} {await configRes.Content.ReadAsStringAsync()}");
        var config = await configRes.Content.ReadFromJsonAsync<BackupConfigResponse>();
        var configId = config!.Id;

        var snapshots = new ConcurrentDictionary<int, Dictionary<string, string>>();
        var treeLock = new SemaphoreSlim(1, 1);
        _damageFloor = int.MaxValue;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));

        var factoryClient = new BlobClientFactory(TestSecrets.Reader);
        var azurite = new Account { BlobEndpoint = AzuriteEndpoint, AccountKeyProtected = TestSecrets.Protect(AzuriteKey), Region = AzureRegion.Global };
        var cloud = factoryClient.CreateServiceClient(azurite).GetBlobContainerClient(containerName);
        _probeAccount = azurite;
        _probeContainer = containerName;
        try
        {
            await Task.WhenAll(
                BackupWorker(configId, new Random(seed ^ 0x1111), snapshots, stop.Token, treeLock),
                RestoreWorker(configId, new Random(seed ^ 0x2222), snapshots, stop.Token, () => _damageFloor),
                BrowseWorker(configId, new Random(seed ^ 0x3333), snapshots, stop.Token),
                VandalWorker(configId, cloud, new Random(seed ^ 0x4444), snapshots, treeLock, stop.Token));

            await WaitTerminalAsync($"/api/backup-configs/{configId}/run", "settle", TimeSpan.FromMinutes(3));

            var busy = factory.Services.GetRequiredService<BackupBusyTracker>();
            for (var i = 0; i < 100 && (busy.IsBusy(account.Id, containerName) || busy.HasReaders(account.Id, containerName)); i++)
                await Task.Delay(100);
            if (busy.IsBusy(account.Id, containerName))
                Violation("settle", $"busy mark leaked: {busy.CurrentActivity(account.Id, containerName)}");
            if (busy.HasReaders(account.Id, containerName))
                Violation("settle", "reader count leaked after every restore finished");

            // Every cycle runs to completion (only the loop head observes the deadline), so the storm
            // ends healed: the closing check of the latest version must find nothing.
            var final = await RunCheckAsync(configId, "final-check", CancellationToken.None);
            if (final is null)
                Violation("final-check", "the closing check could not be started or did not complete");

            if (!_violations.IsEmpty)
            {
                foreach (var line in _log)
                    output.WriteLine(line);
                Assert.Fail($"seed {seed}: {_violations.Count} violation(s):\n  " + string.Join("\n  ", _violations));
            }
            output.WriteLine($"damage storm clean: {snapshots.Count} versions committed, seed {seed}");
        }
        finally
        {
            await cloud.DeleteIfExistsAsync();
        }
    }

    private async Task VandalWorker(int configId, BlobContainerClient cloud, Random rng,
        ConcurrentDictionary<int, Dictionary<string, string>> snapshots, SemaphoreSlim treeLock, CancellationToken until)
    {
        var cycle = 0;
        while (!until.IsCancellationRequested)
        {
            cycle++;
            var version = -1;
            string? victim = null;
            await treeLock.WaitAsync(CancellationToken.None);
            try
            {
                // Fresh bait: raw-route files (over the pack threshold, so one object each — no pack
                // members entangled with the mutating d*/ files) that only this worker ever writes.
                for (var i = 0; i < 3; i++)
                {
                    var full = Path.Combine(Root, "vandal", $"v{i}.bin");
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    var bytes = new byte[300_000 + rng.Next(50_000)];
                    rng.NextBytes(bytes);
                    File.WriteAllBytes(full, bytes);
                }
                var snapshot = SnapshotTree();

                // The lock excludes every NEW committer, but a run the backup worker started before we
                // queued on the lock may still be in flight — and a POST now would hand back an echo of
                // THAT run, mapping its (bait-less) version onto our bait snapshot. Wait it out first:
                // after this, the target is idle and stays idle until our own POST.
                var sw0 = System.Diagnostics.Stopwatch.StartNew();
                while (sw0.Elapsed < TimeSpan.FromMinutes(4))
                {
                    var inflight = await _client.GetAsync($"/api/backup-configs/{configId}/run", CancellationToken.None);
                    if (!inflight.IsSuccessStatusCode)
                        break; // no run has ever been registered
                    var st = await inflight.Content.ReadFromJsonAsync<BackupRunResponse>(CancellationToken.None);
                    if (st is null || st.Status != "Running")
                        break;
                    await Task.Delay(250, CancellationToken.None);
                }

                // Now the POST is ours alone; an echo of the settled previous run is retried, the same
                // dance the backup worker does.
                for (var attempt = 0; attempt < 40 && version < 0; attempt++)
                {
                    var res = await _client.PostAsync($"/api/backup-configs/{configId}/run", null, CancellationToken.None);
                    var started = res.IsSuccessStatusCode
                        ? await res.Content.ReadFromJsonAsync<BackupRunResponse>(CancellationToken.None)
                        : null;
                    if (started is null || started.Status != "Running")
                    {
                        await Task.Delay(250, CancellationToken.None);
                        continue;
                    }
                    var done = await WaitTerminalAsync($"/api/backup-configs/{configId}/run", "vandal", TimeSpan.FromMinutes(3));
                    if (done?.RunId == started.RunId && done.Status == "Completed" && done.Version is { } v)
                    {
                        _damageFloor = v; // floor BEFORE the snapshot is published: no restore may pick the bait
                        snapshots.TryAdd(v, snapshot);
                        version = v;
                    }
                    else if (done?.Status == "Failed")
                    {
                        Violation("vandal", $"cycle {cycle}: bait backup failed: {done.Error}");
                        break;
                    }
                }
                if (version < 0)
                    continue; // the finally releases; nothing was damaged

                // Address the bait EXACTLY: the storm is unencrypted, so the bait file's object is
                // data/{fullHash} and its volumes carry .NNN suffixes — no guessing by LastModified,
                // which loses same-second ties to heal-in-passing re-uploads of other versions' objects
                // (and then this cycle damages a bystander the bait-version check rightly calls clean).
                var baitHash = await new FileHasher().FullHashAsync(Path.Combine(Root, "vandal", $"v{rng.Next(3)}.bin"), CancellationToken.None);
                var volumes = new List<string>();
                await foreach (var b in cloud.GetBlobsAsync(BlobTraits.None, BlobStates.None, $"data/{baitHash}", CancellationToken.None))
                    volumes.Add(b.Name);
                if (volumes.Count == 0)
                {
                    Violation("vandal", $"cycle {cycle}: bait object data/{baitHash} has no volumes in the cloud");
                    continue;
                }
                victim = volumes[rng.Next(volumes.Count)];
                if (rng.Next(2) == 0)
                {
                    await cloud.GetBlobClient(victim).DeleteIfExistsAsync();
                    Log("vandal", $"cycle {cycle}: deleted {victim} (v{version}'s bait)");
                }
                else
                {
                    var garbage = new byte[100];
                    rng.NextBytes(garbage);
                    await cloud.GetBlobClient(victim).UploadAsync(new BinaryData(garbage), overwrite: true);
                    Log("vandal", $"cycle {cycle}: truncated {victim} (v{version}'s bait)");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Violation("vandal", $"cycle {cycle}: {ex}");
            }
            finally
            {
                treeLock.Release();
            }
            if (victim is null || version < 0)
                continue;

            try
            {
                // 1. The storm is live again (backups mutate, restores run below the floor) — the check must see the damage.
                var report = await RunCheckUntilTerminalAsync(configId, version);
                var bad = report?.Report?.Findings.Count(f => f.Cloud == CloudState.MissingOrBad) ?? 0;
                if (report?.Status != "Completed" || bad == 0)
                {
                    Violation("vandal", $"cycle {cycle}: damage to {victim} not detected (status={report?.Status}, bad={bad})");
                    continue; // floor stays down: nothing above the bait is trustworthy now
                }
                Log("vandal", $"cycle {cycle}: check flagged {bad} path(s); cloud marks: {await CloudMarksAsync(version - 3, version - 2, version - 1, version)}");

                // 2. Repair: refused while a restore holds readers — that refusal IS the matrix; retry into a gap.
                RepairRunResponse? repaired = null;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromMinutes(3) && repaired is null)
                {
                    var res = await _client.PostAsync($"/api/backup-configs/{configId}/repair?version={version}&cloud=ExistenceSize", null, CancellationToken.None);
                    if (!res.IsSuccessStatusCode)
                    {
                        await Task.Delay(300, CancellationToken.None);
                        continue;
                    }
                    while (sw.Elapsed < TimeSpan.FromMinutes(3))
                    {
                        var run = await (await _client.GetAsync($"/api/backup-configs/{configId}/repair", CancellationToken.None))
                            .Content.ReadFromJsonAsync<RepairRunResponse>(CancellationToken.None);
                        if (run is null || run.Status == "Running")
                        {
                            await Task.Delay(250, CancellationToken.None);
                            continue;
                        }
                        if (run.Status == "Completed")
                            repaired = run;
                        else if (run.Error?.Contains("busy", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            Log("vandal", $"cycle {cycle}: repair refused busy (matrix at work) — retrying");
                            await Task.Delay(300, CancellationToken.None);
                        }
                        else
                            Violation("vandal", $"cycle {cycle}: repair ended {run.Status}: {run.Error}");
                        break;
                    }
                    if (repaired is null && _violations.Count > 0 && sw.Elapsed > TimeSpan.FromMinutes(2))
                        break;
                }
                if (repaired is null)
                {
                    Violation("vandal", $"cycle {cycle}: repair never completed");
                    continue;
                }
                if (repaired.Unrecoverable is { Count: > 0 } unrec)
                    Violation("vandal", $"cycle {cycle}: {unrec.Count} unrecoverable despite untouched local copies: {string.Join(",", unrec)}");

                // 3. The gate already reads Repaired the instant the repair polls Completed.
                var gate = await (await _client.GetAsync($"/api/backup-configs/{configId}/check", CancellationToken.None))
                    .Content.ReadFromJsonAsync<CheckRunResponse>(CancellationToken.None);
                if (gate?.Resolution != "Repaired")
                    Violation("vandal", $"cycle {cycle}: gate reads '{gate?.Resolution}' after a completed repair");

                // 4. A recheck of the bait version must come back clean; only then is the cloud honest
                // again above the floor, and restores of the bait become fair game.
                var recheck = await RunCheckUntilTerminalAsync(configId, version);
                var still = recheck?.Report?.Findings.Count(f => f.Cloud == CloudState.MissingOrBad) ?? -1;
                if (recheck?.Status != "Completed" || still != 0)
                {
                    Violation("vandal", $"cycle {cycle}: recheck after repair: status={recheck?.Status} bad={still}");
                    continue;
                }
                Log("vandal", $"cycle {cycle}: healed and verified clean; cloud marks: {await CloudMarksAsync(version - 3, version - 2, version - 1, version)}");
                _damageFloor = int.MaxValue;
                await Task.Delay(300, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Violation("vandal", $"cycle {cycle}: {ex}");
            }
        }
    }

    /// <summary>Start a check of one version and poll it to a terminal state, retrying refused starts
    /// (busy target, or the gate still holding a pending report). No cleanliness judgement here — the
    /// vandal reads the findings itself, before and after its repair.</summary>
    private async Task<CheckRunResponse?> RunCheckUntilTerminalAsync(int configId, int version)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromMinutes(3))
        {
            var res = await _client.PostAsync(
                $"/api/backup-configs/{configId}/check?cloud=ExistenceSize&version={version}", null, CancellationToken.None);
            if (!res.IsSuccessStatusCode)
            {
                await Task.Delay(300, CancellationToken.None);
                continue;
            }
            while (sw.Elapsed < TimeSpan.FromMinutes(3))
            {
                var run = await (await _client.GetAsync($"/api/backup-configs/{configId}/check", CancellationToken.None))
                    .Content.ReadFromJsonAsync<CheckRunResponse>(CancellationToken.None);
                if (run is null || run.Status == "Running")
                {
                    await Task.Delay(250, CancellationToken.None);
                    continue;
                }
                if (run.Status == "Failed" && run.Error?.Contains("busy", StringComparison.OrdinalIgnoreCase) == true)
                    break; // lost the busy race after acceptance — start over
                return run;
            }
        }
        return null;
    }

    // ---- Polling helpers ---------------------------------------------------------------------------

    private async Task<BackupRunResponse?> WaitTerminalAsync(string url, string worker, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var nextProgressLog = TimeSpan.FromSeconds(15);
        while (sw.Elapsed < timeout)
        {
            var res = await _client.GetAsync(url, CancellationToken.None);
            if (!res.IsSuccessStatusCode)
                return null; // no run registered at all
            var run = await res.Content.ReadFromJsonAsync<BackupRunResponse>(CancellationToken.None);
            if (run is not null && run.Status != "Running")
                return run;
            if (sw.Elapsed > nextProgressLog)
            {
                // A backup of this storm's few megabytes should land in seconds — if one is dragging,
                // the stage it is dragging IN is the whole diagnosis.
                Log(worker, $"still running after {sw.Elapsed:mm\\:ss}: {run?.Progress}");
                nextProgressLog += TimeSpan.FromSeconds(15);
            }
            await Task.Delay(250, CancellationToken.None);
        }
        Violation(worker, $"{url} still Running after {timeout}");
        return null;
    }

    private async Task<RestoreRunResponse?> PollRestoreAsync(int configId, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var res = await _client.GetAsync($"/api/backup-configs/{configId}/restore", CancellationToken.None);
            if (!res.IsSuccessStatusCode)
                return null;
            var run = await res.Content.ReadFromJsonAsync<RestoreRunResponse>(CancellationToken.None);
            if (run is not null && run.Status != "Running")
                return run;
            await Task.Delay(250, CancellationToken.None);
        }
        return null;
    }
}
