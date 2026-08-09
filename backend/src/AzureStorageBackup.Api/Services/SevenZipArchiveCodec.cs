using System.Diagnostics;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Archive codec implemented with the official 7-Zip binary (M4 decision §13.1).
/// The archive always holds a single entry named "content". When password is non-empty: AES-256 + header encryption (-mhe=on).
/// Arguments go through ArgumentList (never a shell), so a password containing spaces/special characters is safe too.
/// </summary>
public sealed class SevenZipArchiveCodec : IArchiveCodec
{
    private const string EntryName = "content";

    private readonly string _exe;
    private readonly string _tempRoot;
    private readonly Func<ProcessPriorityClass>? _priority;

    /// <param name="priority">CPU priority for each 7z process, taken as a delegate so that a changed setting takes effect immediately
    /// (see the parameter of the same name on <see cref="SevenZipCompressor"/>). null = leave the priority alone.</param>
    public SevenZipArchiveCodec(
        string? executable = null, string? tempRoot = null, Func<ProcessPriorityClass>? priority = null)
    {
        _exe = executable ?? TryResolveExecutable()
            ?? throw new InvalidOperationException("No 7-Zip executable found on PATH.");
        _tempRoot = tempRoot ?? Path.Combine(Path.GetTempPath(), "asb-7z");
        _priority = priority;
    }

    /// <summary>Probes PATH for the 7-Zip executable (7zz→7z→7za); returns null when none is found.</summary>
    public static string? TryResolveExecutable() => SevenZipCli.TryResolveExecutable();

    public async Task<byte[]> EncodeAsync(byte[] content, string? password, CancellationToken ct = default)
    {
        var work = NewWorkDir();
        try
        {
            var input = Path.Combine(work, EntryName);
            var archive = Path.Combine(work, "out.7z");
            await File.WriteAllBytesAsync(input, content, ct);

            var args = new List<string> { "a", "-t7z", "-y", "-bso0", "-bsp0", "-mx9" }; // maximum compression (PRD 3.3.2.1)
            if (!string.IsNullOrEmpty(password))
            {
                args.Add("-p" + password);
                args.Add("-mhe=on");
            }
            args.Add(archive);
            args.Add(input);

            // The same verification as SevenZipCompressor: on exit code 1, 7z may already have silently dropped the
            // only entry and left behind a valid but empty archive. The input here is a temp file we just wrote, so
            // the odds of hitting it are tiny, but an index/info file losing its content makes the whole backup unreadable, which is not worth gambling to save one listing.
            var run = await RunAsync(args, ct);
            if (run.ExitCode == 1
                && !(await SevenZipCli.ListEntriesAsync(_exe, archive, password, ct, _priority)).Contains(EntryName))
            {
                throw new ArchiveMembersMissingException([EntryName],
                    "7-Zip left the payload out of the archive.");
            }
            return await File.ReadAllBytesAsync(archive, ct);
        }
        finally
        {
            TryDelete(work);
        }
    }

    public async Task<byte[]> DecodeAsync(byte[] archive, string? password, CancellationToken ct = default)
    {
        var work = NewWorkDir();
        try
        {
            var input = Path.Combine(work, "in.7z");
            var outDir = Path.Combine(work, "out");
            await File.WriteAllBytesAsync(input, archive, ct);

            var args = new List<string> { "x", "-y", "-bso0", "-bsp0" };
            if (!string.IsNullOrEmpty(password))
                args.Add("-p" + password);
            args.Add("-o" + outDir);
            args.Add(input);

            await RunAsync(args, ct);
            return await File.ReadAllBytesAsync(Path.Combine(outDir, EntryName), ct);
        }
        finally
        {
            TryDelete(work);
        }
    }

    private string NewWorkDir()
    {
        var dir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private Task<SevenZipRun> RunAsync(IReadOnlyList<string> args, CancellationToken ct) =>
        SevenZipCli.RunAsync(_exe, args, ct, priority: _priority);

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
