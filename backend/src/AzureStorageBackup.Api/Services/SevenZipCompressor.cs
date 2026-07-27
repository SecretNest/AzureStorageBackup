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

/// <summary>
/// 归档里少了本该在里面的成员。7z 对读不了的成员只报**警告**（退出码 1）：它把成员静默丢掉，
/// 仍产出一个完全有效的归档——三次真实 chmod 000 探针证实过，连"全部成员都读不了"时它也照样
/// 产出一个 59 字节的空归档并返回 1。不验收就会上传一个缺成员的包，而索引声称该成员在里面，
/// 只有还原或深度检查时才暴露。
/// 继承 IOException：这确实是一次源文件读失败，只不过失败发生在 7z 进程内部，
/// 我们只能事后从归档实际内容反推出来。
/// </summary>
public sealed class ArchiveMembersMissingException(IReadOnlyList<string> missingEntries, string message)
    : IOException(message)
{
    /// <summary>确认不在归档里的条目名（与 <see cref="CompressionRequest.Entries"/> 同名字空间）。</summary>
    public IReadOnlyList<string> MissingEntries { get; } = missingEntries;
}

/// <summary>归档内的一个条目。Size 为**解压后**的字节数；Name 分隔符已归一化为 '/'。</summary>
public sealed record ArchiveEntry(string Name, long Size, bool IsDirectory);

/// <summary>把文件压缩成 7z 归档（可加密/分卷）及解压。用于数据 blob 与分组 pack（M4 §6、§13.1）。</summary>
public interface IFileCompressor
{
    Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken ct = default);
    Task ExtractAsync(string firstVolumePath, string outputDir, string? password, CancellationToken ct = default);

    /// <summary>列出归档成员，保持归档内顺序并带尺寸（见 <see cref="SevenZipCli.ListEntryDetailsAsync"/>）。</summary>
    Task<IReadOnlyList<ArchiveEntry>> ListEntriesAsync(
        string firstVolumePath, string? password, CancellationToken ct = default);

    /// <summary>
    /// 流式解压到 <paramref name="destination"/>，不落磁盘。<paramref name="entryName"/> 为 null 时
    /// 取出**全部**成员（按归档内顺序首尾相接）。返回写出的字节数。
    /// <para>
    /// 警告：成员不存在时 7z 输出为空且**退出码 0**，这里也照样返回 0 而不报错——
    /// 与项目已经踩过的「丢成员却退出 1 静默通过」是同一类坑。调用方**必须**自行核对
    /// 字节数与 hash，不得以"没抛异常"作为通过依据。
    /// </para>
    /// </summary>
    Task<long> ExtractToStreamAsync(
        string firstVolumePath, string? entryName, string? password, Stream destination,
        CancellationToken ct = default);
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

        // 压缩级别默认最大（PRD 3.3.2.1）；不压缩列表用 -mx0（仅封装）。
        var args = new List<string> { "a", "-t7z", "-y", "-bso0", "-bsp0", request.StoreOnly ? "-mx0" : "-mx9" };
        if (!string.IsNullOrEmpty(request.Password))
        {
            args.Add("-p" + request.Password);
            args.Add("-mhe=on");
        }
        if (request.VolumeBytes is { } size)
            args.Add($"-v{size}b");
        args.Add(Path.GetFullPath(request.OutputArchivePath));
        args.AddRange(request.Entries);

        var run = await SevenZipCli.RunAsync(_exe, args, ct, workingDirectory: request.SourceDirectory);
        var volumes = CollectVolumes(request.OutputArchivePath);

        // 退出码 0 的归档必然齐全，所以这次额外的列举只在 1 时付出——而 1 恰恰是 7z 丢掉
        // 读不了的成员时给出的退出码（也用于其它无害警告，故必须比对内容而不是见 1 就报错）。
        if (run.ExitCode == 1)
        {
            var missing = await FindMissingEntriesAsync(volumes, request, ct);
            if (missing.Count > 0)
            {
                // 不把这个残缺归档留在压缩临时区：它不可用，留着只会占磁盘并可能被误当成产物。
                foreach (var v in volumes)
                {
                    try { File.Delete(v); } catch { /* best effort */ }
                }
                throw new ArchiveMembersMissingException(missing,
                    $"7-Zip left {missing.Count} member(s) out of the archive: {string.Join(", ", missing)}");
            }
        }

        return new CompressionResult(volumes);
    }

    /// <summary>比对归档实际内容与请求的条目，返回**确认缺席**的条目名。
    /// 用子集判定而非集合相等：7z 可能额外写入路径中间的目录条目，多出来的条目无害。</summary>
    private async Task<IReadOnlyList<string>> FindMissingEntriesAsync(
        IReadOnlyList<string> volumes, CompressionRequest request, CancellationToken ct)
    {
        // 归档压根没产出 → 一个成员都没进去。
        if (volumes.Count == 0)
            return [.. request.Entries];

        var present = await SevenZipCli.ListEntriesAsync(_exe, volumes[0], request.Password, ct);
        return [.. request.Entries.Where(e => !present.Contains(SevenZipCli.NormalizeEntryName(e)))];
    }

    public Task<IReadOnlyList<ArchiveEntry>> ListEntriesAsync(
        string firstVolumePath, string? password, CancellationToken ct = default)
        => SevenZipCli.ListEntryDetailsAsync(_exe, firstVolumePath, password, ct);

    public async Task<long> ExtractToStreamAsync(
        string firstVolumePath, string? entryName, string? password, Stream destination,
        CancellationToken ct = default)
    {
        // -so 把成员内容送到 stdout，7z 的消息随之自动改走 stderr；-bso0/-bsp0 再压掉进度噪音。
        var args = new List<string> { "x", "-so", "-y", "-bso0", "-bsp0" };
        if (!string.IsNullOrEmpty(password))
            args.Add("-p" + password);
        args.Add(Path.GetFullPath(firstVolumePath));
        if (entryName is not null)
            args.Add(entryName);

        long written = 0;
        await SevenZipCli.RunStreamingAsync(_exe, args, async (stdout, token) =>
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await stdout.ReadAsync(buffer, token)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), token);
                written += read;
            }
        }, ct);
        return written;
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
