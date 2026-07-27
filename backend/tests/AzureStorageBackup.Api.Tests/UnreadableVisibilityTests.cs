using System.Net.Http.Json;
using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// `IndexEntry.UnreadableAt` 曾是**纯只写字段**：写入方只有编排器，序列化占了索引 format 4，
/// 读取方一个都没有。于是"这个版本里这份内容是旧的"这件事，操作员在任何界面上都看不到——
/// 还原时静默拿到更早的内容，检查时看到 Changed 却以为是本地被改了。
/// 本文件走 HTTP 端到端验证三个出口：运行结果计数、/unreadable 列表、还原树节点标注。
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

    /// <summary>触发一次备份并等它到达终态。</summary>
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

        // v1：两个文件都读得到。
        var first = await RunBackupAsync(cfg!.Id);
        Assert.Equal("Completed", first.status);
        Assert.Equal(1, first.version);
        Assert.Equal(0, first.unreadableFiles); // 全部读到 → 计数为 0，不能误报

        // v2：其中一个读不开了 → 索引沿用 v1 的条目并打上 UnreadableAt。
        File.SetUnixFileMode(locked, UnixFileMode.None);
        var second = await RunBackupAsync(cfg.Id);

        Assert.Equal("Completed", second.status);
        Assert.Equal(2, second.version);
        // 出口一：运行结果。此前只有 Version 进了状态，"这轮跳过了文件"在界面上完全看不到。
        Assert.Equal(1, second.unreadableFiles);

        // 出口二：/unreadable 列表。此前没有任何端点能问出"哪些文件的内容是旧的"。
        var rows = await _client.GetFromJsonAsync<List<UnreadableRow>>(
            $"/api/backup-configs/{cfg.Id}/unreadable?version=2");
        var row = Assert.Single(rows!);
        Assert.Equal("locked.txt", row.path);
        Assert.NotEqual(default, row.unreadableAt);

        // 读得到的文件绝不能被列进来。
        Assert.DoesNotContain(rows!, r => r.path == "plain.txt");

        // 出口三：还原树。选择还原内容的那一刻，标注必须就在文件旁边。
        var tree = await _client.GetFromJsonAsync<List<Node>>(
            $"/api/backup-configs/{cfg.Id}/tree?version=2");
        Assert.NotNull(tree);
        Assert.Equal(row.unreadableAt, tree.Single(n => n.name == "locked.txt").unreadableAt);
        Assert.Null(tree.Single(n => n.name == "plain.txt").unreadableAt);

        // v1 里这个文件是正常备份的，那一版的树/列表都不该带标注。
        Assert.Empty((await _client.GetFromJsonAsync<List<UnreadableRow>>(
            $"/api/backup-configs/{cfg.Id}/unreadable?version=1"))!);
    }
}
