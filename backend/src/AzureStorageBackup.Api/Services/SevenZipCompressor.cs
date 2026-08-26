using System.Diagnostics;

namespace AzureStorageBackup.Api.Services;

/// <summary>One compression request. Entries are names relative to SourceDirectory (they decide the entry names inside the archive).</summary>
public sealed record CompressionRequest(
    string SourceDirectory,
    IReadOnlyList<string> Entries,
    string OutputArchivePath,
    string? Password = null,
    long? VolumeBytes = null,
    bool StoreOnly = false);

/// <summary>Compression result: the volume files produced (sorted by name; just one when the archive is not split).</summary>
public sealed record CompressionResult(IReadOnlyList<string> VolumeFiles);

/// <summary>
/// The archive is missing a member that should have been in it. For a member it cannot read, 7z only raises a
/// **warning** (exit code 1): it silently drops the member and still produces a perfectly valid archive — three
/// real chmod 000 probes confirmed it, and even when "not a single member is readable" it still produces a
/// 59-byte empty archive and returns 1. Without verification we would upload a pack that is missing a member
/// while the index claims that member is in there, exposed only by a restore or a deep check.
/// It inherits IOException: this really is a source-file read failure, it just happened inside the 7z process,
/// so all we can do is infer it after the fact from what actually ended up in the archive.
/// </summary>
public sealed class ArchiveMembersMissingException(IReadOnlyList<string> missingEntries, string message)
    : IOException(message)
{
    /// <summary>Entry names confirmed absent from the archive (same namespace as <see cref="CompressionRequest.Entries"/>).</summary>
    public IReadOnlyList<string> MissingEntries { get; } = missingEntries;
}

/// <summary>One entry inside an archive. Size is the **uncompressed** byte count; the separators in Name are normalized to '/'.</summary>
public sealed record ArchiveEntry(string Name, long Size, bool IsDirectory);

/// <summary>
/// One streaming compression request: the caller writes the content into 7z's stdin, and the archive holds exactly one member, <paramref name="EntryName"/>.
/// The entry name keeps the full relative path (same as when compressing by file), so restore and check do not need to tell the two kinds of output apart when locating a member.
/// </summary>
public sealed record StreamCompressionRequest(
    string EntryName,
    string OutputArchivePath,
    string? Password = null,
    long? VolumeBytes = null,
    bool StoreOnly = false,
    long? ExpectedBytes = null);

/// <summary>Compresses files into 7z archives (optionally encrypted/split) and extracts them again. Used for data blobs and grouped packs (M4 §6, §13.1).</summary>
public interface IFileCompressor
{
    Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken ct = default);
    Task ExtractAsync(string firstVolumePath, string outputDir, string? password, CancellationToken ct = default);

    /// <summary>
    /// Streaming compression: <paramref name="writeSource"/> writes the content into the stream handed to it
    /// (7z's stdin) and returns how many bytes it wrote.
    /// The source file is therefore read only once — the caller can hash it in that same pass instead of reading it again after compressing.
    /// <para>
    /// Exceptions from the writing side (source read failure, cancellation) must propagate unchanged and must never
    /// be taken for "compression finished": a half-written archive is a valid 7z file, and the exit code alone
    /// cannot tell them apart. The implementation must delete the volumes it already produced when it fails.
    /// </para>
    /// </summary>
    /// <returns>The volume files produced (sorted by name).</returns>
    Task<CompressionResult> CompressStreamAsync(
        StreamCompressionRequest request, Func<Stream, CancellationToken, Task<long>> writeSource,
        CancellationToken ct = default);

    /// <summary>Lists archive members, keeping the in-archive order and carrying sizes (see <see cref="SevenZipCli.ListEntryDetailsAsync"/>).</summary>
    Task<IReadOnlyList<ArchiveEntry>> ListEntriesAsync(
        string firstVolumePath, string? password, CancellationToken ct = default);

    /// <summary>
    /// Streaming extraction into <paramref name="destination"/>, never touching disk. When
    /// <paramref name="entryName"/> is null it pulls out **every** member (concatenated in archive order). Returns the number of bytes written.
    /// <para>
    /// Warning: when the member does not exist, 7z writes no output and **exits 0**, and this method likewise
    /// returns 0 instead of failing — the same class of trap as the "member dropped, exit 1, silently passes" one
    /// this project already walked into. The caller **must** check the byte count and hash itself, and may not
    /// treat "no exception was thrown" as grounds for passing.
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
    private readonly Func<ProcessPriorityClass>? _priority;

    /// <param name="methodArgs">
    /// Overrides the compression method switches (<c>-m…</c>); defaults to <c>-mx9</c> (PRD 3.3.2.1 requires maximum compression).
    /// Changing the algorithm, tuning the dictionary and capping threads all go through here, e.g. <c>-mx7 -m0=lzma2 -md=32m -mmt=2</c>.
    /// <para>
    /// Only switches starting with <c>-m</c> are accepted: the rest decide how we talk to 7z (<c>-y</c> auto-answer,
    /// <c>-bso0/-bsp0</c> silence, <c>-si</c> read from stdin, <c>-t7z</c> archive format), and making those
    /// configurable means one slip of the finger can wreck output parsing or produce an archive we cannot restore.
    /// Encryption (<c>-p</c>/<c>-mhe=on</c>) and splitting (<c>-v</c>) follow the backup configuration and likewise
    /// do not come from here.
    /// </para>
    /// <para>A malformed value throws at construction time — failing at startup beats blowing up halfway through a backup.</para>
    /// </param>
    /// <param name="priority">
    /// CPU priority for each 7z process. A delegate rather than a value: once the global setting is changed and saved
    /// in the UI, the next process should already run at the new level instead of waiting for a container restart
    /// (the same reasoning as the cap in <see cref="StagingArea"/>). null = leave the priority alone.
    /// </param>
    public SevenZipCompressor(
        string? executable = null, string? methodArgs = null, Func<ProcessPriorityClass>? priority = null)
    {
        _exe = executable ?? SevenZipCli.TryResolveExecutable()
            ?? throw new InvalidOperationException("No 7-Zip executable found on PATH.");
        _methodArgs = ParseMethodArgs(methodArgs);
        _priority = priority;
    }

    /// <summary>The <c>-m…</c> switches this instance will actually use (StoreOnly aside, see <see cref="MethodArgs"/>).
    /// Used to assert that the configuration really bound to here — the link that actually breaks is configuration binding, and a perfectly correct class is worthless if the key name is misspelled.</summary>
    public IReadOnlyList<string> ConfiguredMethodArgs => _methodArgs;

    /// <summary>
    /// Validates a string of method switches and throws when the configuration is malformed. For use at startup: the
    /// DI factory is lazy, so without checking once here, a malformed <c>Backup__SevenZipMethodArgs</c> would only
    /// blow up when the first backup runs — by which time the user already believes everything is fine.
    /// An empty <paramref name="methodArgs"/> = use the default, which is valid.
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

    /// <summary>The <c>-m…</c> switches this particular compression will use. StoreOnly (the do-not-compress list) is always <c>-mx0</c>:
    /// that is the conclusion of "compressing this kind of file is wasted effort", not a tunable preference.</summary>
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

        IReadOnlyList<string> volumes;
        try
        {
            var run = await SevenZipCli.RunAsync(_exe, args, ct, workingDirectory: request.SourceDirectory, priority: _priority);
            volumes = CollectVolumes(request.OutputArchivePath, request.VolumeBytes);

            // An archive that exited 0 is necessarily complete, so this extra listing is only paid for on 1 — and 1
            // is exactly the exit code 7z gives when it drops a member it could not read (it also covers other
            // harmless warnings, so we must compare contents rather than fail on sight of a 1).
            if (run.ExitCode == 1)
            {
                var missing = await FindMissingEntriesAsync(volumes, request, ct);
                if (missing.Count > 0)
                    throw new ArchiveMembersMissingException(missing,
                        $"7-Zip left {missing.Count} member(s) out of the archive: {string.Join(", ", missing)}");
            }
        }
        catch
        {
            // Same rule as the streaming path below: not one byte of a half-written archive may survive, because
            // the caller (StagingArea) collects whatever is in compress-temp as the product — an abandoned volume
            // is not merely wasted disk, it is a fragment sitting where a finished archive is expected.
            // SevenZipCli kills the process tree on cancellation but does no filesystem cleanup of its own; that
            // is deliberately left here, where the output paths are known.
            // This used to be unreachable in practice because interrupting a compression took Stop now. Once a
            // plain Suspend cancels the compression stage it became ordinary — and on a NAS the process restart
            // that sweeps compress-temp may be months away, so the fragments would accumulate.
            //
            // The re-check is inside this try, not after it: FindMissingEntriesAsync runs a **second** 7z (the
            // listing) on the same token, so a Suspend landing in that window used to walk out past every cleanup
            // — and unlike the interrupted-write case what it left behind was a complete-looking set of finished
            // .NNN volumes, the one shape nothing downstream can tell from a product.
            // The mutilated-archive case comes through here too, and deletes by prefix rather than volume by
            // volume as it used to: 7z renames each volume only when it is complete, so a partial one is
            // name.7z.NNN.tmp, which CollectVolumes does not match and the old loop therefore could not remove.
            DeleteArchiveRemnants(request.OutputArchivePath);
            throw;
        }

        return new CompressionResult(volumes);
    }

    /// <summary>Compares what is actually in the archive against the requested entries and returns the names **confirmed absent**.
    /// Uses a subset test rather than set equality: 7z may additionally write directory entries for intermediate path components, and extra entries are harmless.</summary>
    private async Task<IReadOnlyList<string>> FindMissingEntriesAsync(
        IReadOnlyList<string> volumes, CompressionRequest request, CancellationToken ct)
    {
        // No archive was produced at all → not a single member made it in.
        if (volumes.Count == 0)
            return [.. request.Entries];

        var present = await SevenZipCli.ListEntriesAsync(_exe, volumes[0], request.Password, ct, _priority);
        return [.. request.Entries.Where(e => !present.Contains(SevenZipCli.NormalizeEntryName(e)))];
    }

    public async Task<CompressionResult> CompressStreamAsync(
        StreamCompressionRequest request, Func<Stream, CancellationToken, Task<long>> writeSource,
        CancellationToken ct = default)
    {
        var outDir = Path.GetDirectoryName(Path.GetFullPath(request.OutputArchivePath))!;
        Directory.CreateDirectory(outDir);

        // -si{name} makes 7z read the content from stdin and use name as the entry name inside the archive. Encryption/header encryption/splitting are fully compatible with it.
        var args = new List<string> { "a", "-t7z", "-y", "-bso0", "-bsp0" };
        var method = MethodArgs(request.StoreOnly).ToList();
        args.AddRange(method);
        args.Add("-si" + request.EntryName);
        // We must supply the dictionary size ourselves. When compressing a **file**, 7z shrinks the dictionary down to
        // the input size; reading from stdin it has no idea how much is coming, so it allocates -mx9's 64 MB every
        // time — a 6 MB file therefore pays nearly an extra second for nothing (measured 0.10s → 0.30s), and this
        // path is exactly where the thousands of files that just cleared the 5 MB threshold run. Take the power of
        // two at or above the length we stat'ed before compressing and cap it at 64 MB, matching what 7z picks
        // itself for a file of that size: byte-for-byte identical output, only without the pointless wait on that allocation.
        // If the configuration gives -md explicitly, follow the configuration: the operator set that against their own machine's memory, which outranks the guess made here.
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
        IReadOnlyList<string> volumes;
        try
        {
            await SevenZipCli.RunStreamingAsync(_exe, args, ct,
                writeStdin: async (stdin, token) => written = await writeSource(stdin, token),
                priority: _priority);
            // Inside the same try as the run: collection is where an incomplete family is caught
            // (see EnsureFamilyComplete), and that verdict has to leave the temp area as clean as a failed run does,
            // or the fragments it rejected stay behind looking exactly like a finished product.
            volumes = CollectVolumes(request.OutputArchivePath, request.VolumeBytes);
        }
        catch
        {
            // Not one byte of a half-written archive may survive: the caller (StagingArea) collects whatever is in compress-temp as the product.
            // Remnants rather than volumes: a cancelled 7z leaves `.001.tmp`, which CollectVolumes does not match.
            DeleteArchiveRemnants(request.OutputArchivePath);
            throw;
        }

        // The entry must really be in the archive, and its uncompressed size must equal the byte count we fed in.
        // Those fed bytes are exactly the bytes we hashed, so once this check passes there is no gap left between
        // "what the index records" and "what is in the archive".
        // One listing costs a single read of the archive header, negligible next to the compression itself.
        var entry = (await SevenZipCli.ListEntryDetailsAsync(_exe, volumes.Count > 0 ? volumes[0] : request.OutputArchivePath, request.Password, ct, _priority))
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

    /// <summary>Between -mx9's default dictionary (64 MB) and the smallest one (1 MB), take the power of two that is not below the input length.
    /// A dictionary bigger than the input only wastes memory and allocation time; one smaller than the input is what costs compression ratio — hence round up only, and cap only.</summary>
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
        => SevenZipCli.ListEntryDetailsAsync(_exe, firstVolumePath, password, ct, _priority);

    public async Task<long> ExtractToStreamAsync(
        string firstVolumePath, string? entryName, string? password, Stream destination,
        CancellationToken ct = default)
    {
        // -so sends the member content to stdout, whereupon 7z automatically moves its own messages to stderr; -bso0/-bsp0 then silence the progress noise.
        var args = new List<string> { "x", "-so", "-y", "-bso0", "-bsp0" };
        if (!string.IsNullOrEmpty(password))
            args.Add("-p" + password);
        args.Add(Path.GetFullPath(firstVolumePath));
        if (entryName is not null)
            args.Add(entryName);

        long written = 0;
        await SevenZipCli.RunStreamingAsync(_exe, args, ct, priority: _priority, readStdout: async (stdout, token) =>
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

        await SevenZipCli.RunAsync(_exe, args, ct, priority: _priority);
        EnsureReadable(Path.GetFullPath(outputDir));
    }

    /// <summary>
    /// Patches the extraction output up to "the current user can read it".
    /// <para>
    /// The permission bits in the archive are not necessarily usable: 7-Zip 23.01 (and Debian's p7zip) writes the
    /// attributes as 0 when compressing from stdin (<c>-si</c>), so the extracted file comes out as <c>----------</c>
    /// — and we then cannot even open what we just extracted ourselves. The attributes are baked into the archive at
    /// compression time and extracting with another version does not rescue them (measured: an archive built by
    /// 23.01 still comes out 000 when extracted by 26.00), so the only place to fix it is after extraction.
    /// </para>
    /// <para>
    /// We only grant "the current user can read it, and can enter directories" and touch no other bit: the
    /// permissions in the extraction area never meant anything anyway — restore resets them from the index's
    /// Permissions once it writes to the target, and check and repack merely read the content once.
    /// Symlinks are skipped entirely: chmod would follow through to the link target, and an archive may be one that was imported and is not trustworthy.
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

        // Fix up this level's directory before enumerating: a directory without the x bit cannot even be listed.
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
            // If we cannot patch it, let the subsequent read failure do the reporting: the error on that path carries the file name, which is more useful than anything here.
        }
    }

    /// <summary>
    /// Delete everything this archive left in the temp area, 7-Zip's own in-progress files included.
    /// <para>
    /// <see cref="CollectVolumes"/> deliberately matches only **finished** volumes — names ending in three
    /// digits — because that is what a successful run produces. A cancelled run leaves something else:
    /// 7-Zip writes each volume as <c>name.7z.001.tmp</c> and renames it only once the volume is complete,
    /// so cleaning up by CollectVolumes alone misses precisely the file a cancellation creates. That was
    /// measured, not assumed: the cancellation test failed with <c>canceled.7z.001.tmp</c> left behind.
    /// </para>
    /// <para>
    /// Matching on the archive name as a prefix is safe here because compression is globally serial (one
    /// lock in StagingArea) and every archive gets its own name, so nothing else in the temp area can share
    /// this prefix.
    /// </para>
    /// </summary>
    private static void DeleteArchiveRemnants(string archivePath)
    {
        var full = Path.GetFullPath(archivePath);
        var dir = Path.GetDirectoryName(full)!;
        var name = Path.GetFileName(full);
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, name + "*"))
            {
                try { File.Delete(f); } catch { /* best effort */ }
            }
        }
        catch { /* the directory itself may already be gone */ }
    }

    /// <summary>
    /// Collects the volume files produced. When splitting, 7z produces out.7z.001/.002...; when not, out.7z.
    /// <para>
    /// **Three digits is the padding, not the width.** 7-Zip zero-pads to three and then simply keeps counting:
    /// .999 is followed by .1000, .1001, and so on. Matching "exactly three digits" therefore caps the family at
    /// 999 volumes, and ordering the names as plain text puts .1000 between .100 and .101. Neither failure is
    /// visible where it happens: every volume 7z wrote is still on the temp disk, so the archive opens and verifies
    /// perfectly in place — the damage is confined to the list handed to the uploader, and surfaces only as an
    /// unopenable archive in the container months later (the 7z end header lives in the **last** volume, so a
    /// truncated family is not a partial restore but no restore at all).
    /// </para>
    /// <para>
    /// With the default 100 MiB volumes it takes a ~97.6 GiB file to reach 999, which is why this went unnoticed.
    /// </para>
    /// </summary>
    /// <param name="volumeBytes">The split size this archive was written with, for <see cref="EnsureFamilyComplete"/>.
    /// null = the caller did not split, so there is nothing to verify.</param>
    private static IReadOnlyList<string> CollectVolumes(string archivePath, long? volumeBytes = null)
    {
        var full = Path.GetFullPath(archivePath);
        var dir = Path.GetDirectoryName(full)!;
        var name = Path.GetFileName(full);

        // Ordered by volume **number**, never by name: the uploader gives the i-th file of this list the blob name
        // .00(i+1), so a text ordering would store .1000's content under the name .100 — an archive that is complete,
        // correctly sized, and shuffled, which no later check can detect.
        var volumes = Directory.EnumerateFiles(dir, name + ".*")
            .Select(f => (File: f, Number: VolumeNumber(f)))
            .Where(x => x.Number > 0)
            .OrderBy(x => x.Number)
            .ToList();

        if (volumes.Count > 0)
        {
            EnsureFamilyComplete(volumes, volumeBytes);
            return [.. volumes.Select(x => x.File)];
        }

        return File.Exists(full) ? [full] : [];
    }

    /// <summary>The volume number in <c>name.7z.NNN</c>, or -1 when this is not a finished volume of that archive.
    /// Digits are tested with <see cref="char.IsAsciiDigit"/> rather than a <c>\d</c> regex, which in .NET also
    /// accepts non-ASCII digits that would then fail to parse — the same rule <see cref="VolumeBlobIO.IsVolumeOf"/>
    /// applies to blob names. 7z's in-progress <c>.NNN.tmp</c> files fall out here too, since their extension is
    /// <c>.tmp</c>.</summary>
    private static int VolumeNumber(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Length < 4)             // '.' plus at least the three digits 7z pads to
            return -1;
        var digits = ext[1..];
        return digits.All(char.IsAsciiDigit) && int.TryParse(digits, out var n) ? n : -1;
    }

    /// <summary>
    /// The backstop for the whole class of defect above: whatever the collector matched, does it actually add up to
    /// a whole archive?
    /// <para>
    /// Two things are asserted, both free. **The numbers run 1..N with no gaps** — a family missing a volume in the
    /// middle. And **the last volume is short** — 7z fills every volume it writes except the final one, so a family
    /// whose last volume is still exactly full is a family that was cut off, which is precisely the shape a capped
    /// collector produces.
    /// </para>
    /// <para>
    /// This has to live here rather than in the callers' own verification, because that verification is structurally
    /// blind to it: <see cref="CompressStreamAsync"/> proves the archive holds the right member at the right size by
    /// **listing the archive on disk**, where every volume is still present — so it passes with flying colours over
    /// a list that is missing its tail. Nothing downstream can see it either; the index records whatever count it is
    /// given, and check verifies the volumes against that same count.
    /// </para>
    /// </summary>
    private static void EnsureFamilyComplete(IReadOnlyList<(string File, int Number)> volumes, long? volumeBytes)
    {
        for (var i = 0; i < volumes.Count; i++)
        {
            if (volumes[i].Number != i + 1)
                throw new InvalidOperationException(
                    $"7-Zip volume family '{Path.GetFileName(volumes[0].File)}' is not contiguous: "
                    + $"expected volume {i + 1}, found {volumes[i].Number}.");
        }

        // A single volume is a complete archive in its own right and is never padded to the split size.
        if (volumeBytes is not { } size || volumes.Count < 2)
            return;

        var last = new FileInfo(volumes[^1].File).Length;
        if (last >= size)
            throw new InvalidOperationException(
                $"7-Zip volume family '{Path.GetFileName(volumes[0].File)}' looks truncated: it ends at volume "
                + $"{volumes[^1].Number} with a full {last}-byte volume, so more volumes were written than were "
                + "collected. Refusing to upload a partial archive.");
    }
}
