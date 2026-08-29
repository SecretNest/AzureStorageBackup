using System.Diagnostics;

namespace AzureStorageBackup.Api.Services;

/// <summary>Shared invocation layer for the official 7-Zip binary: PATH discovery + process execution (never through a shell).</summary>
internal static class SevenZipCli
{
    /// <summary>Probes PATH for the 7-Zip executable (7zz→7z→7za); returns null when none is found.</summary>
    public static string? TryResolveExecutable()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var dirs = pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var candidate in new[] { "7zz", "7z", "7za" })
        {
            foreach (var dir in dirs)
            {
                var full = Path.Combine(dir, candidate);
                if (File.Exists(full))
                    return full;
            }
        }
        return null;
    }

    /// <summary>Runs 7z with the arguments passed through ArgumentList (safe even when the password contains special characters). An exit code >=2 counts as failure and throws.
    /// The exit code is returned so the caller can verify for itself: **1 does not mean "nothing happened"** — 7z uses it for warnings, and "a member that could not be read was silently
    /// dropped while a valid archive was produced all the same" is exactly this exit code, discoverable only by comparing what actually ended up in the archive.</summary>
    public static async Task<SevenZipRun> RunAsync(
        string exe, IReadOnlyList<string> args, CancellationToken ct, string? workingDirectory = null,
        Func<ProcessPriorityClass>? priority = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{exe}'.");
        ApplyPriority(proc, priority);

        // Close stdin: if the archive is encrypted and no password was given, 7z waits for input — give it EOF so it fails instead of hanging.
        proc.StandardInput.Close();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Cancelling WaitForExitAsync only means "stop waiting" — 7z does not stop because of it, and
            // Process.Dispose does not kill it either. The user pressed Stop in the UI while the compression process
            // is still chewing through tens of GB on the NAS and writing volumes into the temp directory:
            // CPU/IO stay occupied, and the directory the caller deletes next is exactly that one, so it runs into a file that is still being written.
            // So on cancellation kill the whole process tree explicitly — 7z forks children, and killing only the parent leaves orphans behind.
            KillTree(proc);
            // Wait for it to really finish without a ct: pass one and the wait is cancelled immediately again, and cleanup still runs into a live process.
            await proc.WaitForExitAsync(CancellationToken.None);
            await ObserveAsync(stdoutTask, stderrTask);
            throw;
        }
        var stderr = await stderrTask;
        var stdout = await stdoutTask;

        // 7z exit codes: 0=OK, 1=warning, >=2=error (including a wrong password).
        if (proc.ExitCode >= 2)
            throw new InvalidOperationException($"7-Zip failed (exit {proc.ExitCode}): {stderr.Trim()}");

        return new SevenZipRun(proc.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Runs 7z and hands stdin/stdout to the caller **as they are**, without buffering them into strings.
    /// <para>
    /// <see cref="RunAsync"/> collects stdout with <c>ReadToEndAsync</c>, which is a disaster for `x -so`: what runs
    /// on that stream is the member content itself, so a single-file blob of tens of GB gets read entirely into
    /// memory — worse than staging it to disk and reading it back.
    /// The opposite direction, `a -si`, is the same: the source file's bytes have to be fed straight into stdin, with no "pile it up first" step in between.
    /// </para>
    /// <para>
    /// All three pipes need someone tending them. stdout is read by the caller, and whatever is left once it is done
    /// (or stops reading early) is drained here; the draining runs concurrently with writing stdin — let 7z really
    /// fill the pipe writing stdout while we are stuck writing stdin, and both sides wait forever.
    /// stderr is still read in full (little traffic, and not reading it fills that pipe just as well).
    /// </para>
    /// </summary>
    public static async Task<SevenZipRun> RunStreamingAsync(
        string exe, IReadOnlyList<string> args, CancellationToken ct,
        Func<Stream, CancellationToken, Task>? writeStdin = null,
        Func<Stream, CancellationToken, Task>? readStdout = null,
        string? workingDirectory = null,
        Func<ProcessPriorityClass>? priority = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{exe}'.");
        ApplyPriority(proc, priority);

        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        var stdoutTask = Task.Run(async () =>
        {
            if (readStdout is not null)
                await readStdout(proc.StandardOutput.BaseStream, ct);
            await proc.StandardOutput.BaseStream.CopyToAsync(Stream.Null, ct);
        }, ct);

        try
        {
            // With nothing to feed stdin, give EOF immediately: same reasoning as RunAsync, so a missing password makes it fail instead of hanging on input.
            if (writeStdin is not null)
                await writeStdin(proc.StandardInput.BaseStream, ct);
            proc.StandardInput.Close();   // closing = telling 7z "that is the end of the content", and it must come after the write
            await stdoutTask;
            await proc.WaitForExitAsync(ct);
        }
        catch
        {
            // Cancellation, a read/write failure, the caller throwing on its own — the outcome is the same: this 7z
            // process is still chewing the disk, while the temp directory the caller deletes next is exactly the one
            // it is writing to/reading from. Kill the whole tree, then let the exception keep going up (as in RunAsync).
            // Especially a source-file read failure while feeding stdin: it must never be swallowed here, or a half-finished compression gets taken for a success.
            KillTree(proc);
            await proc.WaitForExitAsync(CancellationToken.None);
            try { proc.StandardInput.Close(); } catch { /* already gone along with the process */ }
            await ObserveAsync(stdoutTask, stderrTask);
            throw;
        }

        var stderr = await stderrTask;
        if (proc.ExitCode >= 2)
            throw new InvalidOperationException($"7-Zip failed (exit {proc.ExitCode}): {stderr.Trim()}");

        // stdout was already handed to the caller, so there is nothing to fill in here.
        return new SevenZipRun(proc.ExitCode, "", stderr);
    }

    /// <summary>
    /// Applies the priority to the 7z process that has just started. <paramref name="priority"/> null = leave it alone (keep the value inherited from this process).
    /// <para>
    /// <b>Failures are always swallowed.</b> The process may already have exited within those few microseconds
    /// (<see cref="InvalidOperationException"/>), and the system may refuse
    /// (<see cref="System.ComponentModel.Win32Exception"/>). Failing to change the priority is not a compression
    /// failure — letting a backup blow up over a performance preference is far worse than it running a little faster.
    /// </para>
    /// <para>
    /// <b>On Linux, nice is a per-thread attribute.</b> <c>setpriority(PRIO_PROCESS, pid)</c> only lands on the main
    /// thread, and 7z's LZMA worker threads inherit the nice value **of the thread that created them** at that
    /// moment. We set it right up against Process.Start, when 7z is still doing dynamic linking and parsing arguments
    /// and the worker threads do not exist yet, so in practice they all inherit it.
    /// The worst case (losing this race) is just a few threads that were not lowered: reduced effect, no impact on correctness.
    /// </para>
    /// </summary>
    private static void ApplyPriority(Process proc, Func<ProcessPriorityClass>? priority)
    {
        if (priority is null)
            return;

        ProcessPriorityClass wanted;
        try
        {
            wanted = priority();
        }
        catch
        {
            // Failing to get the value = failing to read the setting (database unavailable or the like). That has
            // nothing whatsoever to do with this compression and must not decide whether this backup succeeds — keep the inherited priority and carry on.
            return;
        }

        try
        {
            proc.PriorityClass = wanted;
        }
        catch (InvalidOperationException) { }                    // it exited on its own before we could set it
        catch (System.ComponentModel.Win32Exception) { }         // the system refused (permissions/already reaped)
        catch (PlatformNotSupportedException) { }                // this platform does not support changing priority
    }

    /// <summary>Kills the process and its children. Already exited (a race) or refusing to die are both fine — cancellation is already propagating outwards,
    /// and the cleanup step must not paper over it with an unrelated exception.</summary>
    private static void KillTree(Process proc)
    {
        try
        {
            proc.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { } // it exited on its own before the Kill
        catch (System.ComponentModel.Win32Exception) { } // the system refused (permissions/already reaped): nothing to do but let it go
    }

    /// <summary>Observes the outcome of the two reader tasks. Once the process is killed they either cancel along with the ct or fail because the pipe closed;
    /// not awaiting them turns those into unobserved task exceptions. Only these two expected outcomes are swallowed here, anything else is rethrown.</summary>
    private static async Task ObserveAsync(params Task[] tasks)
    {
        foreach (var t in tasks)
        {
            try
            {
                await t;
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }

    /// <summary>Lists the entry names inside an archive (`l -slt`), with separators normalized to '/'.
    /// Encrypted backups use -mhe=on (the header is encrypted too), so without the password not even the entry names can be listed, which is why the password must be passed along.
    /// For a split archive pass the first volume (.001) and 7z finds the remaining volumes itself.</summary>
    public static async Task<HashSet<string>> ListEntriesAsync(
        string exe, string firstVolumePath, string? password, CancellationToken ct,
        Func<ProcessPriorityClass>? priority = null)
        => [.. (await ListEntryDetailsAsync(exe, firstVolumePath, password, ct, priority)).Select(e => e.Name)];

    /// <summary>
    /// Lists archive members **keeping the in-archive order**, carrying sizes and a directory flag.
    /// The order is load-bearing: with no member name given, `x -so` outputs the members' contents concatenated in
    /// exactly this order, and cutting the stream by size is the only way to recover each member.
    /// Note that this order is not necessarily the order of the arguments given at compression time.
    /// </summary>
    public static async Task<IReadOnlyList<ArchiveEntry>> ListEntryDetailsAsync(
        string exe, string firstVolumePath, string? password, CancellationToken ct,
        Func<ProcessPriorityClass>? priority = null)
    {
        var args = new List<string> { "l", "-slt", "-y" };
        if (!string.IsNullOrEmpty(password))
            args.Add("-p" + password);
        args.Add("--"); // file names are data, never grammar — see SevenZipCompressor.CompressAsync
        args.Add(Path.GetFullPath(firstVolumePath));

        var run = await RunAsync(exe, args, ct, priority: priority);
        return ParseEntryDetails(run.StdOut);
    }

    // KNOWN LIMITATION (documented in the README): 7-Zip accepts passwords only via the -p switch, so the
    // password rides the child's argv and is readable through /proc/<pid>/cmdline by root (or the same UID)
    // for the process's lifetime. There is no file/stdin/env alternative in 7zz's CLI; inside the product's
    // single-user container the exposure is moot, and shelling differently would not remove it.

    /// <summary>Entry names that appear more than once in a listing. Our writer never produces a duplicate
    /// (a pack's members are distinct paths; single-file archives hold one member), so a duplicate is a
    /// malformed — after /import, plausibly hostile — archive. The danger of ignoring it is a FALSE CLEAN:
    /// the streaming verifier hashes the first occurrence while an extraction to disk keeps the last, so a
    /// check could pass a pack whose restore delivers different bytes for the same path. Every consumer of a
    /// listing treats a duplicated name as damage instead of picking a winner.</summary>
    public static HashSet<string> DuplicatedEntryNames(IEnumerable<string> names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var dup = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in names)
            if (!seen.Add(n))
                dup.Add(n);
        return dup;
    }

    /// <summary>Parses the output of `l -slt`. The information block for the archive **itself** also has a line "Path = &lt;archive file name&gt;",
    /// while the member blocks all come after the first "----------" separator line — so we must skip forward to that line first,
    /// otherwise the archive's own file name slips in as a member. Every "Path = " line opens a new member block.</summary>
    private static IReadOnlyList<ArchiveEntry> ParseEntryDetails(string listing)
    {
        const string pathPrefix = "Path = ";
        const string sizePrefix = "Size = ";
        const string attrPrefix = "Attributes = ";
        const string folderPrefix = "Folder = ";

        var entries = new List<ArchiveEntry>();
        var inEntries = false;
        string? name = null;
        long size = 0;
        var isDir = false;

        void FlushPending()
        {
            if (name is not null)
                entries.Add(new ArchiveEntry(name, size, isDir));
            name = null;
            size = 0;
            isDir = false;
        }

        foreach (var raw in listing.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!inEntries)
            {
                if (line.StartsWith("----------", StringComparison.Ordinal))
                    inEntries = true;
                continue;
            }

            if (line.StartsWith(pathPrefix, StringComparison.Ordinal))
            {
                FlushPending();
                name = NormalizeEntryName(line[pathPrefix.Length..]);
            }
            else if (line.StartsWith(sizePrefix, StringComparison.Ordinal))
            {
                _ = long.TryParse(line[sizePrefix.Length..].Trim(), out size);
            }
            else if (line.StartsWith(attrPrefix, StringComparison.Ordinal))
            {
                // A directory's attribute string starts with 'D' (Unix: "D drwxr-xr-x", Windows: "D....").
                var attr = line[attrPrefix.Length..].Trim();
                isDir |= attr.StartsWith('D');
            }
            else if (line.StartsWith(folderPrefix, StringComparison.Ordinal))
            {
                isDir |= line[folderPrefix.Length..].Trim() == "+"; // some 7z versions use this field
            }
        }
        FlushPending();
        return entries;
    }

    /// <summary>On Windows 7z lists entry names with '\', while the entry names callers hand in use '/'.</summary>
    public static string NormalizeEntryName(string entry) => entry.Trim().Replace('\\', '/');
}

/// <summary>The result of one 7z invocation.</summary>
internal sealed record SevenZipRun(int ExitCode, string StdOut, string StdErr);
