using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class VerboseFileLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "asb-vlog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Appends_Per_Container_Dated_File()
    {
        var log = new VerboseFileLog(_root);
        await log.AppendAsync("mybackup", "Backed up a.txt");
        await log.AppendAsync("mybackup", "Backed up b.txt");

        var dir = Path.Combine(_root, "mybackup");
        var file = Directory.EnumerateFiles(dir, "*.log").Single();
        var lines = await File.ReadAllLinesAsync(file);

        Assert.EndsWith(".log", file);
        Assert.Equal(8, Path.GetFileNameWithoutExtension(file).Length); // yyyyMMdd
        Assert.Equal(2, lines.Length);
        Assert.Contains("Backed up a.txt", lines[0]);
        Assert.Contains("Backed up b.txt", lines[1]);
    }

    [Fact]
    public async Task Concurrent_Appends_Do_Not_Lose_Lines()
    {
        var log = new VerboseFileLog(_root);
        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => log.AppendAsync("c", $"file {i}")));

        var file = Directory.EnumerateFiles(Path.Combine(_root, "c"), "*.log").Single();
        Assert.Equal(200, (await File.ReadAllLinesAsync(file)).Length);
    }

    [Fact]
    public void Trim_Deletes_Only_Files_Older_Than_Cutoff()
    {
        var dir = Path.Combine(_root, "c");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "20260101.log"), "old\n");   // 早于窗口
        File.WriteAllText(Path.Combine(dir, "20260715.log"), "fresh\n"); // 窗口内

        var now = new DateTimeOffset(2026, 7, 17, 0, 0, 0, TimeSpan.Zero);
        new VerboseFileLog(_root).Trim(maxAgeDays: 14, now); // cutoff = 2026-07-03

        Assert.False(File.Exists(Path.Combine(dir, "20260101.log")));
        Assert.True(File.Exists(Path.Combine(dir, "20260715.log")));
    }
}
