using System.Diagnostics;
using System.Text;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

[Trait("Category", "Integration")]
public sealed class SevenZipCompressorTests : IDisposable
{
    private readonly string _dir;
    private static readonly string? Exe = SevenZipArchiveCodec.TryResolveExecutable();

    public SevenZipCompressorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "asb-zc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private SevenZipCompressor Compressor()
    {
        Skip.If(Exe is null, "7z executable not found on PATH.");
        return new SevenZipCompressor(Exe);
    }

    private string SourceDir()
    {
        var d = Path.Combine(_dir, "src");
        Directory.CreateDirectory(d);
        return d;
    }

    private static string WriteInto(string dir, string name, byte[] content)
    {
        var full = Path.Combine(dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    [SkippableFact]
    public async Task Compresses_Multiple_Files_And_Extracts_RoundTrip()
    {
        var compressor = Compressor();
        var src = SourceDir();
        WriteInto(src, "a.txt", Encoding.UTF8.GetBytes("alpha"));
        WriteInto(src, "sub/b.txt", Encoding.UTF8.GetBytes("bravo"));

        var archive = Path.Combine(_dir, "out.7z");
        var result = await compressor.CompressAsync(new CompressionRequest(src, ["a.txt", "sub/b.txt"], archive));

        Assert.Single(result.VolumeFiles);

        var outDir = Path.Combine(_dir, "extracted");
        await compressor.ExtractAsync(result.VolumeFiles[0], outDir, password: null);

        Assert.Equal("alpha", File.ReadAllText(Path.Combine(outDir, "a.txt")));
        Assert.Equal("bravo", File.ReadAllText(Path.Combine(outDir, "sub", "b.txt")));
    }

    [SkippableFact]
    public async Task Splits_Into_Volumes_When_Volume_Size_Set()
    {
        var compressor = Compressor();
        var src = SourceDir();
        // 25KB in store mode (no compression), 10KB volumes → 3 volumes.
        WriteInto(src, "big.bin", new byte[25_000]);

        var archive = Path.Combine(_dir, "vol.7z");
        var result = await compressor.CompressAsync(
            new CompressionRequest(src, ["big.bin"], archive, VolumeBytes: 10_000, StoreOnly: true));

        Assert.True(result.VolumeFiles.Count >= 2, $"expected multiple volumes, got {result.VolumeFiles.Count}");
        Assert.All(result.VolumeFiles, f => Assert.Matches(@"\.7z\.\d{3}$", f));

        var outDir = Path.Combine(_dir, "extracted");
        await compressor.ExtractAsync(result.VolumeFiles[0], outDir, password: null);
        Assert.Equal(25_000, new FileInfo(Path.Combine(outDir, "big.bin")).Length);
    }

    /// <summary>
    /// Past the 999th volume 7-Zip stops zero-padding to three digits and writes <c>.1000</c>, <c>.1001</c>… — four
    /// digits and more. A collector that matches "exactly three digits" therefore silently caps the family at 999,
    /// and a collector that sorts the names as plain strings puts <c>.1000</c> between <c>.100</c> and <c>.101</c>.
    /// <para>
    /// Both failures are silent where it matters most. The volumes 7z produced are all still on the temp disk, so
    /// the archive opens and verifies perfectly **in place** — the truncation only exists in the list handed to the
    /// uploader. That is why this test extracts from a copy holding **only the collected volumes**: that copy is
    /// what the container ends up with, and it is the only place the defect is visible.
    /// </para>
    /// <para>
    /// The threshold is 999 × volume size, so with the default 100 MiB volumes it takes a ~97.6 GiB file to reach —
    /// which is exactly why it went unnoticed. 1000-byte volumes reproduce it in about a second.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Collects_Every_Volume_Past_The_Three_Digit_Boundary()
    {
        var compressor = Compressor();
        var src = SourceDir();
        // Store mode, 1000-byte volumes → a little over 1000 volumes, i.e. safely past .999.
        WriteInto(src, "big.bin", new byte[1_005_000]);

        var archive = Path.Combine(_dir, "wide.7z");
        var result = await compressor.CompressAsync(
            new CompressionRequest(src, ["big.bin"], archive, VolumeBytes: 1_000, StoreOnly: true));

        Assert.True(result.VolumeFiles.Count > 999,
            $"the family runs past .999, but only {result.VolumeFiles.Count} volume(s) were collected");

        // Ascending by volume **number**, not by name: the uploader names the i-th file of this list .00(i+1), so a
        // list ordered as text would map .1000's content onto the blob named .100 and corrupt the archive silently.
        var numbers = result.VolumeFiles
            .Select(f => int.Parse(Path.GetExtension(f)[1..], System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        Assert.Equal(Enumerable.Range(1, result.VolumeFiles.Count), numbers);

        // Only what was collected reaches the container. Extract from exactly that set.
        var uploaded = Path.Combine(_dir, "uploaded");
        Directory.CreateDirectory(uploaded);
        foreach (var v in result.VolumeFiles)
            File.Copy(v, Path.Combine(uploaded, Path.GetFileName(v)));

        var outDir = Path.Combine(_dir, "wide-extracted");
        await compressor.ExtractAsync(
            Path.Combine(uploaded, Path.GetFileName(result.VolumeFiles[0])), outDir, password: null);
        Assert.Equal(1_005_000, new FileInfo(Path.Combine(outDir, "big.bin")).Length);
    }

    /// <summary>
    /// The guard behind the collector: 7z fills every volume but the last, so a final volume that is still exactly
    /// full means the family was cut short. Asserted here through the ordinary success path — a correct compression
    /// must not trip it.
    /// </summary>
    [SkippableFact]
    public async Task A_Complete_Family_Ends_On_A_Short_Volume()
    {
        var compressor = Compressor();
        var src = SourceDir();
        WriteInto(src, "big.bin", new byte[25_000]);

        var archive = Path.Combine(_dir, "tail.7z");
        var result = await compressor.CompressAsync(
            new CompressionRequest(src, ["big.bin"], archive, VolumeBytes: 10_000, StoreOnly: true));

        Assert.True(new FileInfo(result.VolumeFiles[^1]).Length < 10_000,
            "the last volume of a finished archive is the short one");
    }

    [SkippableFact]
    public async Task Encrypted_Archive_RoundTrips_And_Requires_Password()
    {
        var compressor = Compressor();
        var src = SourceDir();
        WriteInto(src, "secret.txt", Encoding.UTF8.GetBytes("classified"));

        var archive = Path.Combine(_dir, "enc.7z");
        var result = await compressor.CompressAsync(new CompressionRequest(src, ["secret.txt"], archive, Password: "pw"));

        var ok = Path.Combine(_dir, "ok");
        await compressor.ExtractAsync(result.VolumeFiles[0], ok, password: "pw");
        Assert.Equal("classified", File.ReadAllText(Path.Combine(ok, "secret.txt")));

        var bad = Path.Combine(_dir, "bad");
        await Assert.ThrowsAnyAsync<Exception>(() => compressor.ExtractAsync(result.VolumeFiles[0], bad, password: null));
    }

    /// <summary>For a member it cannot read, 7z only raises a **warning**: exit code 1, the member silently dropped, and a perfectly valid archive produced all the same.
    /// Going by the exit code alone (previously only >= 2 counted as failure) lets a pack that is missing a member be uploaded as a normal product while the index claims that member
    /// is in there — exposed only by a restore or a deep check. The compressor must verify the archive's actual contents itself.</summary>
    [SkippableFact]
    public async Task Reports_A_Member_The_Archiver_Silently_Dropped()
    {
        Skip.If(OperatingSystem.IsWindows(), "Relies on Unix permission bits.");
        var compressor = Compressor();
        var src = SourceDir();
        WriteInto(src, "readable.txt", Encoding.UTF8.GetBytes("fine"));
        var locked = WriteInto(src, "locked.txt", Encoding.UTF8.GetBytes("cannot be read"));
        WriteInto(src, "other.txt", Encoding.UTF8.GetBytes("also fine"));
        File.SetUnixFileMode(locked, UnixFileMode.None); // a real permission denial, not a fake exception thrown by a stub

        try
        {
            var archive = Path.Combine(_dir, "dropped.7z");
            var ex = await Assert.ThrowsAsync<ArchiveMembersMissingException>(() =>
                compressor.CompressAsync(new CompressionRequest(
                    src, ["readable.txt", "locked.txt", "other.txt"], archive)));

            // Report only the one that is genuinely absent: readable members must not be dragged in with it (otherwise the whole group gets pointlessly recompressed/downgraded).
            Assert.Equal(["locked.txt"], ex.MissingEntries);

            // That mutilated archive must not be left on disk: it is unusable, and keeping it only eats space and risks being mistaken for a product.
            Assert.False(File.Exists(archive));
        }
        finally { File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    }

    /// <summary>The worst case, likewise confirmed by a real probe: when **every** member is unreadable, 7z still exits 1
    /// and produces a valid, empty archive. Without verification, what gets uploaded is an empty pack that "looks normal".</summary>
    [SkippableFact]
    public async Task Reports_Every_Member_When_The_Archive_Comes_Out_Empty()
    {
        Skip.If(OperatingSystem.IsWindows(), "Relies on Unix permission bits.");
        var compressor = Compressor();
        var src = SourceDir();
        var a = WriteInto(src, "a.txt", Encoding.UTF8.GetBytes("aaa"));
        var b = WriteInto(src, "b.txt", Encoding.UTF8.GetBytes("bbb"));
        File.SetUnixFileMode(a, UnixFileMode.None);
        File.SetUnixFileMode(b, UnixFileMode.None);

        try
        {
            var ex = await Assert.ThrowsAsync<ArchiveMembersMissingException>(() =>
                compressor.CompressAsync(new CompressionRequest(src, ["a.txt", "b.txt"], Path.Combine(_dir, "empty.7z"))));

            Assert.Equal(["a.txt", "b.txt"], ex.MissingEntries.OrderBy(m => m, StringComparer.Ordinal));
        }
        finally
        {
            File.SetUnixFileMode(a, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(b, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>Encrypted backups use -mhe=on (even the header is encrypted), so without the password not even the entry names can be listed. The verification logic must pass the password
    /// to `l` as well, or encrypted backups either fail verification forever or hang while 7z waits for password input.</summary>
    [SkippableFact]
    public async Task Accepts_A_Complete_Encrypted_Archive_Without_False_Alarm()
    {
        var compressor = Compressor();
        var src = SourceDir();
        WriteInto(src, "s1.txt", Encoding.UTF8.GetBytes("one"));
        WriteInto(src, "s2.txt", Encoding.UTF8.GetBytes("two"));

        var archive = Path.Combine(_dir, "enc-ok.7z");
        var result = await compressor.CompressAsync(
            new CompressionRequest(src, ["s1.txt", "s2.txt"], archive, Password: "pw"));

        Assert.Single(result.VolumeFiles); // a complete encrypted archive is produced as usual, not misjudged as missing a member
    }
    /// <summary>The compression method switches are tunable (Backup__SevenZipMethodArgs): memory and CPU are both tight on a NAS,
    /// so changing the algorithm, shrinking the dictionary and capping threads are real needs. Archives are self-describing, so changing them does not affect restoring existing versions.</summary>
    [SkippableFact]
    public async Task Method_Args_From_Configuration_Are_Passed_To_7z()
    {
        Skip.If(Exe is null, "7z executable not found on PATH.");
        var src = SourceDir();
        // Highly compressible content: the output sizes of -mx9 and -mx0 are bound to differ noticeably.
        WriteInto(src, "z.bin", new byte[200_000]);

        var packed = await new SevenZipCompressor(Exe).CompressAsync(
            new CompressionRequest(src, ["z.bin"], Path.Combine(_dir, "default.7z")));
        var stored = await new SevenZipCompressor(Exe, "-mx0").CompressAsync(
            new CompressionRequest(src, ["z.bin"], Path.Combine(_dir, "stored.7z")));

        Assert.True(new FileInfo(stored.VolumeFiles[0]).Length > new FileInfo(packed.VolumeFiles[0]).Length,
            "-mx0 should not have compressed the input");
    }

    /// <summary>Only switches starting with -m are accepted. The rest decide how we talk to 7z (-y/-bso0/-si/-t7z),
    /// and one slip of the finger can wreck output parsing or produce an archive we cannot restore — so it is caught at construction time, failing at startup.</summary>
    [Theory]
    [InlineData("-o/tmp/evil")]
    [InlineData("-mx9 -y")]
    [InlineData("-m")]
    public void Non_Method_Arguments_Are_Rejected_Up_Front(string args)
    {
        var ex = Assert.Throws<ArgumentException>(() => new SevenZipCompressor(Exe ?? "/bin/true", args));
        Assert.Contains("-m", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>If the configuration gives -md explicitly, follow the configuration: the automatically computed dictionary is only "the default when nobody says otherwise",
    /// and must not override the value the operator set against their own machine's memory.</summary>
    [SkippableFact]
    public async Task Explicit_Dictionary_Size_Survives_Streaming_Compression()
    {
        Skip.If(Exe is null, "7z executable not found on PATH.");
        var compressor = new SevenZipCompressor(Exe, "-mx9 -md=1m");
        var payload = new byte[300_000];

        var result = await compressor.CompressStreamAsync(
            new StreamCompressionRequest("s.bin", Path.Combine(_dir, "stream-md.7z"), ExpectedBytes: payload.Length),
            async (stdin, ct) => { await stdin.WriteAsync(payload, ct); return payload.Length; });

        var entries = await compressor.ListEntriesAsync(result.VolumeFiles[0], null);
        Assert.Equal(payload.Length, Assert.Single(entries).Size);
    }

    /// <summary>
    /// Lowering the priority must not break the normal paths — compression, listing and extraction all have to work as usual.
    /// <para>
    /// We do not read <c>PriorityClass</c> back to assert on it: that is a race (the process may already have exited),
    /// and the nice value itself may well not be adjustable inside a container/CI. What is guarded here is something
    /// else: the extra setpriority call, and the try/catch around it, must not make any of the 7z paths fail. When it
    /// cannot be set it must stay **silent**, because a performance preference should never blow up a backup.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Priority_Setting_Does_Not_Disturb_Any_Path()
    {
        Skip.If(Exe is null, "7z executable not found on PATH.");
        var compressor = new SevenZipCompressor(Exe, priority: () => ProcessPriorityClass.Idle);
        var src = SourceDir();
        WriteInto(src, "a.txt", Encoding.UTF8.GetBytes("alpha"));

        var archive = Path.Combine(_dir, "prio.7z");
        var result = await compressor.CompressAsync(new CompressionRequest(src, ["a.txt"], archive));

        var entries = await compressor.ListEntriesAsync(result.VolumeFiles[0], null);
        Assert.Equal("a.txt", Assert.Single(entries).Name);

        var outDir = Path.Combine(_dir, "out");
        await compressor.ExtractAsync(result.VolumeFiles[0], outDir, null);
        Assert.Equal("alpha", await File.ReadAllTextAsync(Path.Combine(outDir, "a.txt")));
    }

    /// <summary>A throwing priority delegate must not affect compression either: exceptions on this path come from reading the settings (the database)
    /// and have nothing whatsoever to do with the archive's content.</summary>
    [SkippableFact]
    public async Task Throwing_Priority_Provider_Does_Not_Fail_Compression()
    {
        Skip.If(Exe is null, "7z executable not found on PATH.");
        var compressor = new SevenZipCompressor(
            Exe, priority: () => throw new TimeoutException("settings unavailable"));
        var src = SourceDir();
        WriteInto(src, "a.txt", Encoding.UTF8.GetBytes("alpha"));

        var result = await compressor.CompressAsync(
            new CompressionRequest(src, ["a.txt"], Path.Combine(_dir, "prio-throw.7z")));

        var entries = await compressor.ListEntriesAsync(result.VolumeFiles[0], null);
        Assert.Equal("a.txt", Assert.Single(entries).Name);
    }

    /// <summary>
    /// A cancelled compression must not leave its half-written volumes behind.
    /// <para>
    /// 7z writes straight into the shared compression temp area, and the caller (StagingArea) treats
    /// whatever it finds there as the product — so an abandoned volume is not merely wasted disk, it is a
    /// fragment sitting where a finished archive is expected. The streaming path has always cleaned up
    /// after itself; this path had not, and only cleaned up on the "7z dropped a member" exit code.
    /// </para>
    /// <para>
    /// It went unnoticed because a cancellation mid-compression used to need <c>Stop now</c>. Once a plain
    /// Suspend cancels the compression stage, this became an ordinary occurrence — and on a NAS, where the
    /// process restart that sweeps the temp area may be months apart, the fragments accumulate.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_Canceled_Compression_Leaves_No_Volumes_Behind()
    {
        var compressor = Compressor();
        var src = SourceDir();
        // Incompressible, so 7z is still working when the cancel lands rather than having finished instantly.
        var bytes = new byte[24 * 1024 * 1024];
        Random.Shared.NextBytes(bytes);
        WriteInto(src, "big.bin", bytes);
        var archive = Path.Combine(_dir, "canceled.7z");

        using var cts = new CancellationTokenSource();
        var running = compressor.CompressAsync(
            new CompressionRequest(src, ["big.bin"], archive, VolumeBytes: 1_000_000), cts.Token);

        // Cancel only once 7z has actually put something on disk — cancelling before that would pass
        // whether or not anything cleans up.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!Directory.EnumerateFiles(_dir, "canceled.7z*").Any())
        {
            Assert.True(DateTime.UtcNow < deadline, "7z never wrote a volume, so the cancel would prove nothing.");
            await Task.Delay(20);
        }

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        var leftovers = Directory.EnumerateFiles(_dir, "canceled.7z*").Select(Path.GetFileName).ToList();
        Assert.True(leftovers.Count == 0, "left behind: " + string.Join(", ", leftovers));
    }
}
