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

/// <summary>
/// 一次流式压缩请求：内容由调用方写进 7z 的 stdin，归档里只有 <paramref name="EntryName"/> 一个成员。
/// 条目名保留完整相对路径（与按文件压缩时一致），因此还原与检查定位成员的逻辑不必区分两种产出。
/// </summary>
public sealed record StreamCompressionRequest(
    string EntryName,
    string OutputArchivePath,
    string? Password = null,
    long? VolumeBytes = null,
    bool StoreOnly = false,
    long? ExpectedBytes = null);

/// <summary>把文件压缩成 7z 归档（可加密/分卷）及解压。用于数据 blob 与分组 pack（M4 §6、§13.1）。</summary>
public interface IFileCompressor
{
    Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken ct = default);
    Task ExtractAsync(string firstVolumePath, string outputDir, string? password, CancellationToken ct = default);

    /// <summary>
    /// 流式压缩：<paramref name="writeSource"/> 把内容写进给它的流（7z 的 stdin），返回写了多少字节。
    /// 源文件因此只被读一遍——调用方可以在同一遍里顺便算 hash，不必压完再读一次。
    /// <para>
    /// 写入侧的异常（源文件读失败、取消）必须原样传出，绝不能被当成"压完了"：半截的归档
    /// 是有效的 7z 文件，光看退出码分辨不出来。实现须在失败时删掉已产出的卷。
    /// </para>
    /// </summary>
    /// <returns>产出的卷文件（按名排序）。</returns>
    Task<CompressionResult> CompressStreamAsync(
        StreamCompressionRequest request, Func<Stream, CancellationToken, Task<long>> writeSource,
        CancellationToken ct = default);

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
    private readonly IReadOnlyList<string> _methodArgs;

    /// <param name="methodArgs">
    /// 覆盖压缩方法参数（<c>-m…</c>），默认 <c>-mx9</c>（PRD 3.3.2.1 要求最大压缩）。
    /// 换算法、调字典、限线程都从这里走，例如 <c>-mx7 -m0=lzma2 -md=32m -mmt=2</c>。
    /// <para>
    /// 只收 <c>-m</c> 开头的参数：其余开关决定的是我们怎么和 7z 对话（<c>-y</c> 自动应答、
    /// <c>-bso0/-bsp0</c> 静音、<c>-si</c> 走 stdin、<c>-t7z</c> 归档格式），让它们可配等于
    /// 让一次手滑毁掉输出解析或产出还原不了的归档。加密（<c>-p</c>/<c>-mhe=on</c>）与分卷
    /// （<c>-v</c>）按备份配置走，同样不从这里来。
    /// </para>
    /// <para>写错了在构造时就抛——启动即失败，好过备份跑到一半才炸。</para>
    /// </param>
    public SevenZipCompressor(string? executable = null, string? methodArgs = null)
    {
        _exe = executable ?? SevenZipCli.TryResolveExecutable()
            ?? throw new InvalidOperationException("No 7-Zip executable found on PATH.");
        _methodArgs = ParseMethodArgs(methodArgs);
    }

    /// <summary>本实例实际会用的 <c>-m…</c> 参数（StoreOnly 除外，见 <see cref="MethodArgs"/>）。
    /// 用于断言配置确实绑到了这里——真正会坏的是配置绑定那一环，键名写错的话类本身再正确也没用。</summary>
    public IReadOnlyList<string> ConfiguredMethodArgs => _methodArgs;

    /// <summary>
    /// 校验一串方法参数，配置写错就抛。给启动期用：DI 工厂是懒的，不在这里先验一次的话，
    /// 一个写错的 <c>Backup__SevenZipMethodArgs</c> 要等到第一次备份跑起来才炸——
    /// 那时用户已经以为一切正常了。<paramref name="methodArgs"/> 为空＝用默认值，合法。
    /// </summary>
    public static void ValidateMethodArgs(string? methodArgs) => ParseMethodArgs(methodArgs);

    private static IReadOnlyList<string> ParseMethodArgs(string? methodArgs)
    {
        if (string.IsNullOrWhiteSpace(methodArgs))
            return ["-mx9"];

        var parsed = methodArgs.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var a in parsed)
        {
            if (!a.StartsWith("-m", StringComparison.Ordinal) || a.Length <= 2)
                throw new ArgumentException(
                    $"7-Zip compression arguments may only be method switches (-m…); got '{a}'.", nameof(methodArgs));
        }
        return parsed;
    }

    /// <summary>本次压缩实际要用的 <c>-m…</c> 参数。StoreOnly（不压缩列表）恒为 <c>-mx0</c>：
    /// 那是"这类文件压了也白压"的判断结果，不是可调的偏好。</summary>
    private IEnumerable<string> MethodArgs(bool storeOnly) => storeOnly ? ["-mx0"] : _methodArgs;

    public async Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken ct = default)
    {
        var outDir = Path.GetDirectoryName(Path.GetFullPath(request.OutputArchivePath))!;
        Directory.CreateDirectory(outDir);

        var args = new List<string> { "a", "-t7z", "-y", "-bso0", "-bsp0" };
        args.AddRange(MethodArgs(request.StoreOnly));
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

    public async Task<CompressionResult> CompressStreamAsync(
        StreamCompressionRequest request, Func<Stream, CancellationToken, Task<long>> writeSource,
        CancellationToken ct = default)
    {
        var outDir = Path.GetDirectoryName(Path.GetFullPath(request.OutputArchivePath))!;
        Directory.CreateDirectory(outDir);

        // -si{name} 让 7z 从 stdin 读内容、以 name 作为归档内的条目名。加密/头加密/分卷与它完全兼容。
        var args = new List<string> { "a", "-t7z", "-y", "-bso0", "-bsp0" };
        var method = MethodArgs(request.StoreOnly).ToList();
        args.AddRange(method);
        args.Add("-si" + request.EntryName);
        // 词典大小必须自己给。压缩一个**文件**时 7z 会把词典缩到输入大小；从 stdin 读它不知道会来多少，
        // 于是每次都照 -mx9 的 64 MB 分配——一个 6 MB 的文件因此凭空多付近一秒（实测 0.10s → 0.30s），
        // 而这条路径上跑的正是成千上万个刚过 5 MB 阈值的文件。按压之前 stat 到的长度取 2 的幂并封顶
        // 64 MB，与 7z 自己对同尺寸文件的选择一致：产出逐字节相同，只是不再白等那次分配。
        // 配置里显式给了 -md 就照配置来：那是运维按自己机器的内存定的，比这里的自动推算权威。
        if (!request.StoreOnly && request.ExpectedBytes is { } expected
            && !method.Any(a => a.StartsWith("-md", StringComparison.Ordinal)))
        {
            args.Add($"-md={DictionaryBytes(expected)}b");
        }
        if (!string.IsNullOrEmpty(request.Password))
        {
            args.Add("-p" + request.Password);
            args.Add("-mhe=on");
        }
        if (request.VolumeBytes is { } size)
            args.Add($"-v{size}b");
        args.Add(Path.GetFullPath(request.OutputArchivePath));

        long written = 0;
        try
        {
            await SevenZipCli.RunStreamingAsync(_exe, args, ct,
                writeStdin: async (stdin, token) => written = await writeSource(stdin, token));
        }
        catch
        {
            // 半截的归档一个字节都不能留：调用方（StagingArea）会把 compress-temp 里的东西当作产物收走。
            foreach (var v in CollectVolumes(request.OutputArchivePath))
            {
                try { File.Delete(v); } catch { /* best effort */ }
            }
            throw;
        }

        var volumes = CollectVolumes(request.OutputArchivePath);

        // 归档里必须真的有这个条目，且解压后尺寸等于我们喂进去的字节数。喂进去的字节又正是
        // 算 hash 的那些字节，所以这一条查过之后，"索引记的内容"与"归档里的内容"就再无缝隙。
        // 一次列举的代价只是读一遍归档头，和压缩本身比可以忽略。
        var entry = (await SevenZipCli.ListEntryDetailsAsync(_exe, volumes.Count > 0 ? volumes[0] : request.OutputArchivePath, request.Password, ct))
            .FirstOrDefault(e => e.Name == SevenZipCli.NormalizeEntryName(request.EntryName));
        if (volumes.Count == 0 || entry is null || entry.Size != written)
        {
            foreach (var v in volumes)
            {
                try { File.Delete(v); } catch { /* best effort */ }
            }
            throw new ArchiveMembersMissingException([request.EntryName],
                entry is null
                    ? $"7-Zip did not put '{request.EntryName}' into the archive."
                    : $"'{request.EntryName}' is {entry.Size} byte(s) in the archive but {written} byte(s) were fed to it.");
        }

        return new CompressionResult(volumes);
    }

    /// <summary>-mx9 的默认词典（64 MB）与最小词典（1 MB）之间，取不小于输入长度的 2 的幂。
    /// 比输入大的词典只是白占内存与分配时间，比输入小才会损失压缩率——所以只往上取整、只封顶。</summary>
    private static long DictionaryBytes(long expectedBytes)
    {
        const long min = 1L << 20;
        const long max = 64L << 20;
        var size = min;
        while (size < expectedBytes && size < max)
            size <<= 1;
        return size;
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
        await SevenZipCli.RunStreamingAsync(_exe, args, ct, readStdout: async (stdout, token) =>
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await stdout.ReadAsync(buffer, token)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), token);
                written += read;
            }
        });
        return written;
    }

    public async Task ExtractAsync(string firstVolumePath, string outputDir, string? password, CancellationToken ct = default)
    {
        var args = new List<string> { "x", "-y", "-bso0", "-bsp0" };
        if (!string.IsNullOrEmpty(password))
            args.Add("-p" + password);
        args.Add("-o" + Path.GetFullPath(outputDir));
        args.Add(Path.GetFullPath(firstVolumePath));

        await SevenZipCli.RunAsync(_exe, args, ct);
        EnsureReadable(Path.GetFullPath(outputDir));
    }

    /// <summary>
    /// 把解压产物补成"当前用户读得了"。
    /// <para>
    /// 归档里的权限位不一定能用：7-Zip 23.01（以及 Debian 的 p7zip）从 stdin 压缩（<c>-si</c>）时
    /// 把属性写成 0，解出来的文件就是 <c>----------</c>——我们随后连自己刚解出来的东西都打不开。
    /// 属性是压缩时写死进归档里的，换个版本解压也救不回来（实测 23.01 建的归档由 26.00 解压
    /// 依然是 000），所以只能在解压之后补。
    /// </para>
    /// <para>
    /// 只补"当前用户能读、目录能进"，其余位一律不动：解压区的权限本就不代表任何东西——
    /// 还原写到目标后会按索引里的 Permissions 重设，检查与重打包只是把内容读一遍。
    /// 符号链接整个跳过：chmod 会跟随到链接目标上去，而归档可以是导入进来的、不可信的。
    /// </para>
    /// </summary>
    private static void EnsureReadable(string dir)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(dir))
            return;

        var info = new DirectoryInfo(dir);
        if (info.LinkTarget is not null)
            return;

        Grant(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // 先把本级目录补好再枚举：没有 x 位的目录连列都列不出来。
        foreach (var sub in info.EnumerateDirectories())
            EnsureReadable(sub.FullName);
        foreach (var file in info.EnumerateFiles())
        {
            if (file.LinkTarget is null)
                Grant(file.FullName, UnixFileMode.UserRead);
        }
    }

    private static void Grant(string path, UnixFileMode wanted)
    {
        try
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & wanted) != wanted)
                File.SetUnixFileMode(path, mode | wanted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 补不上就让后续的读失败去报错：那条路径上的错误信息带着文件名，比这里更有用。
        }
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
