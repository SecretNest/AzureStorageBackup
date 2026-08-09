using System.Net.Http.Json;
using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// `IndexEntry.UnreadableAt` used to be a **purely write-only field**: the only writer was the orchestrator, its serialization took up index format 4,
/// and there were zero readers. So the fact "this content is stale in this version" was invisible to the operator on every screen —
/// a restore would silently hand back older content, and a check would show Changed while the operator assumed the local file had been edited.
/// This file verifies three outlets end to end over HTTP: the run result count, the /unreadable list, and the restore tree node annotation.
/// </summary>
[Trait("Category", "Integration")]
public class UnreadableVisibilityTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>, IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly HttpClient _client = factory.CreateClient();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "asb-vis-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                try { File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best effort */ }
        }
        catch { /* best effort */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static bool SevenZip() => SevenZipArchiveCodec.TryResolveExecutable() is not null;

    private sealed record UnreadableRow(string path, DateTimeOffset unreadableAt);
    private sealed record RunState(string status, int? version, int? unreadableFiles, string? error);
    private sealed record Node(string name, string path, bool isDir, long? length, DateTimeOffset? unreadableAt);

    /// <summary>Kick off one backup and wait for it to reach a terminal state.</summary>
    private async Task<RunState> RunBackupAsync(int configId)
    {
        (await _client.PostAsync($"/api/backup-configs/{configId}/run", null)).EnsureSuccessStatusCode();

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var state = await _client.GetFromJsonAsync<RunState>($"/api/backup-configs/{configId}/run");
            if (state is not null && state.status != "Running")
                return state;
            await Task.Delay(150);
        }
        throw new TimeoutException("Backup did not reach a terminal state.");
    }

    [SkippableFact]
    public async Task A_Carried_Forward_Entry_Is_Visible_In_The_Run_The_List_And_The_Restore_Tree()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");
        Skip.If(OperatingSystem.IsWindows(), "Relies on Unix permission bits.");

        Directory.CreateDirectory(_root);
        var locked = Path.Combine(_root, "locked.txt");
        await File.WriteAllTextAsync(locked, "readable during the first backup");
        await File.WriteAllTextAsync(Path.Combine(_root, "plain.txt"), "always readable");

        var container = "vis-" + Guid.NewGuid().ToString("N")[..8];
        var acct = await (await _client.PostAsJsonAsync("/api/accounts", new AccountRequest(
            Name: "vis-" + Guid.NewGuid().ToString("N")[..8], Description: null,
            BlobEndpoint: "http://127.0.0.1:10000/devstoreaccount1", Region: AzureRegion.Global,
            AccountKey: AzuriteKey, UseProxy: false, ProxyMode: ProxyMode.Independent,
            ProxyHost: null, ProxyPort: null, ProxyUsername: null, ProxyPassword: null)))
            .Content.ReadFromJsonAsync<AccountResponse>();

        var cfg = await (await _client.PostAsJsonAsync("/api/backup-configs", new
        {
            AccountId = acct!.Id,
            ContainerName = container,
            Name = "visibility-test",
            LocalRoot = _root,
            IndexTier = StorageTier.Hot,
            DataTier = StorageTier.Hot,
        })).Content.ReadFromJsonAsync<BackupConfigResponse>();

        // v1: both files are readable.
        var first = await RunBackupAsync(cfg!.Id);
        Assert.Equal("Completed", first.status);
        Assert.Equal(1, first.version);
        Assert.Equal(0, first.unreadableFiles); // everything was read → the count is 0, no false positives

        // v2: one of them became unreadable → the index carries v1's entry forward and stamps UnreadableAt on it.
        File.SetUnixFileMode(locked, UnixFileMode.None);
        var second = await RunBackupAsync(cfg.Id);

        Assert.Equal("Completed", second.status);
        Assert.Equal(2, second.version);
        // Outlet one: the run result. Only Version used to make it into the status, so "this run skipped files" was completely invisible in the UI.
        Assert.Equal(1, second.unreadableFiles);

        // Outlet two: the /unreadable list. There used to be no endpoint at all that could answer "which files' content is stale".
        var rows = await _client.GetFromJsonAsync<List<UnreadableRow>>(
            $"/api/backup-configs/{cfg.Id}/unreadable?version=2");
        var row = Assert.Single(rows!);
        Assert.Equal("locked.txt", row.path);
        Assert.NotEqual(default, row.unreadableAt);

        // Files that were readable must never be listed here.
        Assert.DoesNotContain(rows!, r => r.path == "plain.txt");

        // Outlet three: the restore tree. At the moment you pick what to restore, the annotation has to be right next to the file.
        var tree = await _client.GetFromJsonAsync<List<Node>>(
            $"/api/backup-configs/{cfg.Id}/tree?version=2");
        Assert.NotNull(tree);
        Assert.Equal(row.unreadableAt, tree.Single(n => n.name == "locked.txt").unreadableAt);
        Assert.Null(tree.Single(n => n.name == "plain.txt").unreadableAt);

        // In v1 this file was backed up normally, so neither that version's tree nor its list should carry the annotation.
        Assert.Empty((await _client.GetFromJsonAsync<List<UnreadableRow>>(
            $"/api/backup-configs/{cfg.Id}/unreadable?version=1"))!);
    }
}
