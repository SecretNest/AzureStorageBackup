using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 归档里的权限位可能是 0。7-Zip 23.01（Debian 的 p7zip 同样）从 stdin 压缩（<c>-si</c>）时
/// 把属性写成 0，解出来的文件是 <c>----------</c>——单文件 blob 走的正是这条路，于是还原、
/// 深度检查、重打包全都读不了自己刚解出来的东西，而备份当时一切正常、检查（流式比对，不落盘）
/// 也报绿：只有还原才暴露。
/// <para>
/// 属性写死在归档里，换新版 7z 解压救不回来（23.01 建的归档由 26.00 解压依然是 000），
/// 所以用一个 23.01 产出的归档做固件把这件事钉死——本机装的是哪个 7z 版本都不影响这组测试。
/// </para>
/// </summary>
public sealed class ZeroAttributeArchiveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "asb-zeroattr-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // 解压出来的东西可能没有权限位，先补回来再删，否则连清理都做不了。
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
        // 修复之前这一行抛 UnauthorizedAccessException——归档里的属性是 0，7z 忠实照搬。
        Assert.Equal("zero-attribute payload", File.ReadAllText(extracted));
    }

    [SkippableFact]
    public async Task Extraction_Does_Not_Widen_Permissions_Beyond_The_Owner()
    {
        Skip.IfNot(SevenZipCli.TryResolveExecutable() is not null, "7z not found");
        Skip.If(OperatingSystem.IsWindows(), "Unix permissions only");

        var compressor = new SevenZipCompressor();
        await compressor.ExtractAsync(Fixture("zero-attr-si.7z"), _dir, password: null);

        // 补的是"我自己读得了"，不是"谁都读得了"：解压区里可能躺着别人的私有文件。
        var mode = File.GetUnixFileMode(Path.Combine(_dir, "deep", "dir", "payload.txt"));
        Assert.Equal(UnixFileMode.UserRead, mode & UnixFileMode.UserRead);
        Assert.Equal(UnixFileMode.None, mode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead));
    }
}
