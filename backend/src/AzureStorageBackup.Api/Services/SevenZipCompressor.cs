namespace AzureStorageBackup.Api.Services;

/// <summary>一次压缩请求。Entries 为相对 SourceDirectory 的条目名（决定归档内的条目名）。</summary>
public sealed record CompressionRequest(
    string SourceDirectory,
    IReadOnlyList<string> Entries,
    string OutputArchivePath,
    string? Password = null,
    long? VolumeBytes = null,
    bool StoreOnly = false);

/// <summary>压缩结果：产出的卷文件（按名排序；单卷时仅一个）。</summary>
public sealed record CompressionResult(IReadOnlyList<string> VolumeFiles);

/// <summary>把文件压缩成 7z 归档（可加密/分卷）及解压。用于数据 blob 与分组 pack（M4 §6、§13.1）。</summary>
public interface IFileCompressor
{
    Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken ct = default);
    Task ExtractAsync(string firstVolumePath, string outputDir, string? password, CancellationToken ct = default);
}

public sealed class SevenZipCompressor : IFileCompressor
{
    private readonly string _exe;

    public SevenZipCompressor(string? executable = null)
        => _exe = executable ?? SevenZipCli.TryResolveExecutable()
            ?? throw new InvalidOperationException("No 7-Zip executable found on PATH.");

    public async Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken ct = default)
    {
        var outDir = Path.GetDirectoryName(Path.GetFullPath(request.OutputArchivePath))!;
        Directory.CreateDirectory(outDir);

        var args = new List<string> { "a", "-t7z", "-y", "-bso0", "-bsp0" };
        if (request.StoreOnly)
            args.Add("-mx0");
        if (!string.IsNullOrEmpty(request.Password))
        {
            args.Add("-p" + request.Password);
            args.Add("-mhe=on");
        }
        if (request.VolumeBytes is { } size)
            args.Add($"-v{size}b");
        args.Add(Path.GetFullPath(request.OutputArchivePath));
        args.AddRange(request.Entries);

        await SevenZipCli.RunAsync(_exe, args, ct, workingDirectory: request.SourceDirectory);

        return new CompressionResult(CollectVolumes(request.OutputArchivePath));
    }

    public Task ExtractAsync(string firstVolumePath, string outputDir, string? password, CancellationToken ct = default)
    {
        var args = new List<string> { "x", "-y", "-bso0", "-bsp0" };
        if (!string.IsNullOrEmpty(password))
            args.Add("-p" + password);
        args.Add("-o" + Path.GetFullPath(outputDir));
        args.Add(Path.GetFullPath(firstVolumePath));

        return SevenZipCli.RunAsync(_exe, args, ct);
    }

    /// <summary>
    /// 收集产出的卷文件。分卷时 7z 产出 out.7z.001/.002...；不分卷时产出 out.7z。
    /// </summary>
    private static IReadOnlyList<string> CollectVolumes(string archivePath)
    {
        var full = Path.GetFullPath(archivePath);
        var dir = Path.GetDirectoryName(full)!;
        var name = Path.GetFileName(full);

        var volumes = Directory.EnumerateFiles(dir, name + ".*")
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(f, @"\.\d{3}$"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        if (volumes.Count > 0)
            return volumes;

        return File.Exists(full) ? [full] : [];
    }
}
