using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The permission bits inside an archive can be 0. 7-Zip 23.01 (and Debian's p7zip just the same) writes the
/// attributes as 0 when compressing from stdin (<c>-si</c>), so the extracted file comes out <c>----------</c> — and
/// the single-file blob path is exactly this one, so restore, deep check and repack all fail to read what they just
/// extracted themselves, while the backup looked fine at the time and the check (streaming comparison, never touching
/// disk) reported green as well: only a restore exposes it.
/// <para>
/// The attributes are baked into the archive, and extracting with a newer 7z does not rescue them (an archive built by
/// 23.01 still comes out 000 when extracted by 26.00), so an archive produced by 23.01 is used as a fixture to nail
/// this down — whichever 7z version is installed on this machine makes no difference to these tests.
/// </para>
/// </summary>
public sealed class ZeroAttributeArchiveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "asb-zeroattr-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // What was extracted may carry no permission bits, so patch them back before deleting, or even the cleanup cannot run.
        if (!OperatingSystem.IsWindows() && Directory.Exists(_dir))
        {
            foreach (var p in Directory.EnumerateFileSystemEntries(_dir, "*", SearchOption.AllDirectories))
            {
                try { File.SetUnixFileMode(p, File.GetUnixFileMode(p) | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
                catch { /* best effort */ }
            }
        }
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [SkippableFact]
    public async Task Extracting_An_Archive_Whose_Entries_Have_No_Permission_Bits_Still_Yields_Readable_Files()
    {
        Skip.IfNot(SevenZipCli.TryResolveExecutable() is not null, "7z not found");

        var compressor = new SevenZipCompressor();
        await compressor.ExtractAsync(Fixture("zero-attr-si.7z"), _dir, password: null);

        var extracted = Path.Combine(_dir, "deep", "dir", "payload.txt");
        Assert.True(File.Exists(extracted));
        // Before the fix this line threw UnauthorizedAccessException — the attributes in the archive are 0, and 7z faithfully copies them over.
        Assert.Equal("zero-attribute payload", File.ReadAllText(extracted));
    }

    [SkippableFact]
    public async Task Extraction_Does_Not_Widen_Permissions_Beyond_The_Owner()
    {
        Skip.IfNot(SevenZipCli.TryResolveExecutable() is not null, "7z not found");
        Skip.If(OperatingSystem.IsWindows(), "Unix permissions only");

        var compressor = new SevenZipCompressor();
        await compressor.ExtractAsync(Fixture("zero-attr-si.7z"), _dir, password: null);

        // What is granted is "I can read it myself", not "anyone can read it": someone else's private files may be lying in the extraction area.
        var mode = File.GetUnixFileMode(Path.Combine(_dir, "deep", "dir", "payload.txt"));
        Assert.Equal(UnixFileMode.UserRead, mode & UnixFileMode.UserRead);
        Assert.Equal(UnixFileMode.None, mode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead));
    }
}
