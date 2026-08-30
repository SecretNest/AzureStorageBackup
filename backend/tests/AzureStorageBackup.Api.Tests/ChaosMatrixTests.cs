using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
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
        var account = await (await _client.PostAsJsonAsync("/api/accounts", new AccountRequest(
                "azurite-chaos-" + Guid.NewGuid().ToString("N")[..6], null, AzuriteEndpoint, AzureRegion.Global,
                AzuriteKey, false, ProxyMode.Independent, null, null, null, null)))
            .Content.ReadFromJsonAsync<AccountResponse>();
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

    private async Task BackupWorker(int configId, Random rng, ConcurrentDictionary<int, Dictionary<string, string>> snapshots, CancellationToken until)
    {
        while (!until.IsCancellationRequested)
        {
            try
            {
                // Mutate, snapshot, run. Only this worker touches the source tree, so the snapshot is exact.
                var files = Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).ToList();
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
                        files = Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).ToList();
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
                await Task.Delay(200, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Violation("backup", ex.ToString());
            }
        }
    }

    private async Task RestoreWorker(int configId, Random rng, ConcurrentDictionary<int, Dictionary<string, string>> snapshots, CancellationToken until)
    {
        var restoreBase = Path.Combine(_base, "restores");
        while (!until.IsCancellationRequested)
        {
            var versions = snapshots.Keys.OrderBy(v => v).ToList();
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
                    VerifyRestore(version, target, snapshots);
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

    private void VerifyRestore(int version, string target, ConcurrentDictionary<int, Dictionary<string, string>> snapshots)
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
                Violation("restore", $"v{version}: '{path}' missing from a Completed restore");
            else if (got != hash)
                Violation("restore", $"v{version}: '{path}' content mismatch (old/new volume mix?)");
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

    // ---- Polling helpers ---------------------------------------------------------------------------

    private async Task<BackupRunResponse?> WaitTerminalAsync(string url, string worker, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var res = await _client.GetAsync(url, CancellationToken.None);
            if (!res.IsSuccessStatusCode)
                return null; // no run registered at all
            var run = await res.Content.ReadFromJsonAsync<BackupRunResponse>(CancellationToken.None);
            if (run is not null && run.Status != "Running")
                return run;
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
