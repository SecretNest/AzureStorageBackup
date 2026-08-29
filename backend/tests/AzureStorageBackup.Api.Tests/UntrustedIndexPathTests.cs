using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// F4 (final review): the "the index is untrusted" threat model previously landed only on **restore writes**; the local **read** side was never covered.
/// <c>/import</c> lets anyone turn a container they fabricated into local index data (design §5), which makes a join like
/// <c>Path.Combine(localRoot, &lt;the path from the index&gt;)</c> an entry point for escaping <c>Backup__Root</c>:
/// most of the sites have a hash gate and are merely a confirmation oracle for "some file exists and its content equals X"; the two inside dead-weight compaction are worse —
/// one is a pure existence probe with no hash gate, and the other, <c>CopyInto</c>, is an **arbitrary write** of pack content outside the compose
/// directory.
/// <para>These cases all use fakes and need neither Azurite nor 7z: what they assert is "not one step was taken outside",
/// and actually compressing and uploading would only drown out the decision points.</para>
/// </summary>
public sealed class UntrustedIndexPathTests : IDisposable
{
    private readonly string _base;
    private readonly string _local;
    private readonly string _temp;

    public UntrustedIndexPathTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "asb-untrusted-" + Guid.NewGuid().ToString("N"));
        _local = Path.Combine(_base, "local");
        _temp = Path.Combine(_base, "temp");
        Directory.CreateDirectory(_local);
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Records every path it is asked to hash — used to prove that files outside the root were never even read.</summary>
    private sealed class RecordingHasher : IFileHasher
    {
        public List<string> Hashed { get; } = [];

        public Task<string> HeadHashAsync(string path, int headBytes, CancellationToken ct = default)
            => Record(path);

        public Task<string> TailHashAsync(string path, int tailBytes, CancellationToken ct = default)
            => Record(path);

        public Task<string> FullHashAsync(string path, CancellationToken ct = default, IProgress<long>? onRead = null)
            => Record(path);

        private Task<string> Record(string path)
        {
            lock (Hashed) Hashed.Add(path);
            // Return a value that cannot possibly match: should the guard ever fail, the test must not slip through because "the hash happened not to line up".
            return Task.FromResult("recording-hasher");
        }
    }

    private sealed class RecordingCompressor : IFileCompressor
    {
        public CompressionRequest? LastRequest { get; private set; }

        public Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            throw new InvalidOperationException("compression must not be reached in these tests");
        }

        public Task ExtractAsync(string firstVolumePath, string outputDir, string? password, CancellationToken ct = default)
            => throw new InvalidOperationException("extraction must not be reached in these tests");

        public Task<IReadOnlyList<ArchiveEntry>> ListEntriesAsync(
            string firstVolumePath, string? password, CancellationToken ct = default)
            => throw new InvalidOperationException("listing must not be reached in these tests");

        public Task<long> ExtractToStreamAsync(
            string firstVolumePath, string? entryName, string? password, Stream destination,
            CancellationToken ct = default)
            => throw new InvalidOperationException("extraction must not be reached in these tests");

        public Task<CompressionResult> CompressStreamAsync(
            StreamCompressionRequest request, Func<Stream, CancellationToken, Task<long>> writeSource,
            CancellationToken ct = default)
            => throw new InvalidOperationException("streaming compression must not be reached in these tests");
    }

    private sealed class StubCodec : IArchiveCodec
    {
        public Task<byte[]> EncodeAsync(byte[] content, string? password, CancellationToken ct = default)
            => throw new InvalidOperationException("codec must not be reached in these tests");

        public Task<byte[]> DecodeAsync(byte[] archive, string? password, CancellationToken ct = default)
            => throw new InvalidOperationException("codec must not be reached in these tests");
    }

    private sealed class ThrowingUploader : IBlobUploader
    {
        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => throw new InvalidOperationException("upload must not be reached in these tests");

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => throw new InvalidOperationException("upload must not be reached in these tests");
    }

    /// <summary>
    /// Dead-weight compaction: one member name escaping the compose directory → compaction of the whole pack is abandoned.
    /// <para>
    /// The assertions come in three layers, matching the three holes that were plugged:
    /// one, the file outside the root was **never read even once** (<c>LocalPath</c>'s existence probe + the hash confirmation oracle);
    /// two, the compressor was never called, meaning <c>CopyInto</c> never ran → no write happened outside the compose directory;
    /// three, the pack itself is untouched and only the dead weight is recorded — abandoning is a safe no-op, not data loss.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Compaction_Is_Abandoned_When_A_Members_Entry_Name_Escapes_The_Compose_Directory()
    {
        // The "secret" outside the root: the oracle exists the moment it gets stat'ed or hashed even once.
        var secret = Path.Combine(_base, "secret.txt");
        await File.WriteAllTextAsync(secret, "outside the root");
        await File.WriteAllTextAsync(Path.Combine(_local, "b.txt"), new string('b', 2000));

        var info = new BackupInfoFile
        {
            Backup = new BackupMeta { Name = "t", CreatedAt = DateTimeOffset.UtcNow },
            Packs =
            {
                ["p0001"] = new PackInfo
                {
                    Blob = "packs/p0001.7z",
                    Members = ["hash-b", "hash-escape"],
                    OriginalBytes = 6000,
                    Volumes = 1,
                },
            },
        };

        // Live members 4000 bytes / original 6000 → dead weight 1/3 > threshold 0.3, so the recompression path is certain to be entered.
        var live = new Dictionary<string, Dictionary<string, LivePackMember>>
        {
            ["p0001"] = new(StringComparer.Ordinal)
            {
                ["b.txt"] = new LivePackMember("b.txt", 2000, "hash-b"),
                // `../secret.txt`: relative to localRoot it points at _base/secret.txt (read),
                // relative to composeDir it points one level above compose (write).
                ["../secret.txt"] = new LivePackMember("../secret.txt", 2000, "hash-escape"),
            },
        };

        var hasher = new RecordingHasher();
        var compressor = new RecordingCompressor();
        var compactor = new DeadWeightCompactor(
            new ThrowingUploader(), compressor, hasher, Path.Combine(_temp, "compact"),
            new StagingArea(Path.Combine(_temp, "c"), Path.Combine(_temp, "s"), () => 200_000_000));

        await compactor.CompactAsync(
            new Account
            {
                Name = "a", BlobEndpoint = "http://127.0.0.1:1/x",
                AccountKeyProtected = "", Region = AzureRegion.Global,
            },
            new Azure.Storage.Blobs.BlobContainerClient(new Uri("http://127.0.0.1:1/x/c")),
            password: null, info, live, AccessTier.Hot, volumeBytes: null, threshold: 0.3,
            localRoot: _local, allowDownload: true, CancellationToken.None);

        Assert.DoesNotContain(hasher.Hashed, p => !PathBoundary.IsWithin(_local, p));
        Assert.Empty(hasher.Hashed);
        Assert.Null(compressor.LastRequest);

        var pack = info.Packs["p0001"];
        Assert.Equal(2000, pack.DeadBytes);
        Assert.Equal(6000, pack.OriginalBytes);
        Assert.Equal(1, pack.Volumes);
        Assert.Equal(["hash-b", "hash-escape"], pack.Members);
    }

    /// <summary>
    /// The checker's local axis: an index entry whose path escapes the local root → judged Missing, and the file is **not read**.
    /// An out-of-bounds entry would otherwise become a confirmation oracle for "some path exists and its content hash equals X" (Content level)
    /// or for "exists + size + permissions" (Attributes level).
    /// </summary>
    [Fact]
    public async Task Local_Check_Treats_An_Entry_Escaping_The_Local_Root_As_Missing_Without_Reading_It()
    {
        var secret = Path.Combine(_base, "secret.txt");
        await File.WriteAllTextAsync(secret, "outside the root");

        var hasher = new RecordingHasher();
        var checker = new BackupChecker(
            new BlobClientFactory(TestSecrets.Reader),
            new BackupInfoStore(new BlobClientFactory(TestSecrets.Reader), new StubCodec()),
            hasher: hasher);

        var state = await LocalCheckAsync(checker, new IndexEntry
        {
            Path = "../secret.txt",
            Kind = "file",
            Permissions = "0644",
            Length = 16,
            FullHash = "whatever",
        });

        Assert.Equal(LocalState.Missing, state);
        Assert.Empty(hasher.Hashed);
    }

    /// <summary>Control group: when the same path lands inside the root the local axis works as usual (reads the file, compares the hash) —
    /// proving the Missing above came from the boundary decision, not from the local axis being broken outright.</summary>
    [Fact]
    public async Task Local_Check_Still_Reads_An_Entry_Inside_The_Local_Root()
    {
        await File.WriteAllTextAsync(Path.Combine(_local, "a.txt"), "alpha");

        var hasher = new RecordingHasher();
        var checker = new BackupChecker(
            new BlobClientFactory(TestSecrets.Reader),
            new BackupInfoStore(new BlobClientFactory(TestSecrets.Reader), new StubCodec()),
            hasher: hasher);

        var state = await LocalCheckAsync(checker, new IndexEntry
        {
            Path = "a.txt",
            Kind = "file",
            Permissions = "0644",
            Length = 5,
            FullHash = "recording-hasher",
        });

        Assert.Equal(LocalState.Ok, state);
        Assert.Equal([Path.Combine(_local, "a.txt")], hasher.Hashed);
    }

    /// <summary>A candidate whose length differs from the recorded content cannot hash-match, so it must not be
    /// read at all. This is not a micro-optimization: in the field the candidate was a ~100 GB appended file, and
    /// "give this file up" cost a full read of it before repair could conclude "does not match" — the user watched
    /// a motionless Repairing state for the length of a 100 GB disk scan whose answer was knowable from a stat.</summary>
    [Fact]
    public async Task Repairing_A_Blob_Does_Not_Read_A_Candidate_Of_The_Wrong_Length()
    {
        await File.WriteAllTextAsync(Path.Combine(_local, "grown.bin"), "much longer than the recorded five");

        var index = new VersionIndex
        {
            Version = 1,
            Entries =
            [
                new IndexEntry
                {
                    // The hash deliberately does NOT match RecordingHasher's return value: today's behavior is
                    // "hash it, find it different, mark unrecoverable" — the assertion pins that the hashing
                    // step itself disappears once the length has already answered.
                    Path = "grown.bin", Kind = "file", Permissions = "0644",
                    Length = 5, FullHash = "someone-elses-hash",
                    Storage = new StorageRef { Kind = "blob", Ref = "data/x" },
                },
            ],
        };

        var (repairer, hasher, _) = Repairer();
        var unrecoverable = new List<string>();
        await InvokeAsync(repairer, "RepairBlobAsync",
        [
            SampleAccount(), SampleContainer(), "data/x",
            new Dictionary<int, VersionIndex> { [1] = index }, _local, null,
            new BlobAddressScheme(null, null), AccessTier.Hot, null, null,
            new List<string>(), unrecoverable, new HashSet<int>(),
            StagingLease(), CancellationToken.None,
            null, // the optional StageTracker — progress is not what these boundary tests are about
            null, // the optional VolumeUploadScope — parallel transfer, likewise
            null, // the optional pause gate
        ]);

        Assert.Contains("grown.bin", unrecoverable);
        Assert.Empty(hasher.Hashed);
    }

    /// <summary>
    /// Repairing a single-file blob: an index entry whose path escapes the local root → that candidate source is skipped outright, no usable local source remains,
    /// and the entry is marked unrecoverable. It was otherwise a confirmation oracle for "somewhere locally there is a file whose content hash equals X",
    /// and on a hit it would go on to compress and upload the content of that out-of-root file to the cloud.
    /// </summary>
    [Fact]
    public async Task Repairing_A_Blob_Skips_A_Source_Path_That_Escapes_The_Local_Root()
    {
        // The hash deliberately matches RecordingHasher's return value: if the guard fails, this path would be taken as a usable source,
        // so the assertion fails for the reason "the out-of-bounds path was accepted", not "the hash happened not to line up".
        await File.WriteAllTextAsync(Path.Combine(_base, "secret.txt"), "outside the root");

        var index = new VersionIndex
        {
            Version = 1,
            Entries =
            [
                new IndexEntry
                {
                    Path = "../secret.txt",
                    Kind = "file",
                    Permissions = "0644",
                    Length = 16,
                    FullHash = "recording-hasher",
                    Storage = new StorageRef { Kind = "blob", Ref = "data/x" },
                },
            ],
        };

        var (repairer, hasher, compressor) = Repairer();
        var unrecoverable = new List<string>();
        await InvokeAsync(repairer, "RepairBlobAsync",
        [
            SampleAccount(), SampleContainer(), "data/x",
            new Dictionary<int, VersionIndex> { [1] = index }, _local, null,
            new BlobAddressScheme(null, null), AccessTier.Hot, null, null,
            new List<string>(), unrecoverable, new HashSet<int>(),
            // Repair's compression output now goes through the staging area (global compression lock + budget), hence the extra lease parameter.
            // This case should be stopped by the boundary decision before it ever touches the staging area; the lease is only there to make the call go through.
            StagingLease(), CancellationToken.None,
            null, // the optional StageTracker — progress is not what these boundary tests are about
            null, // the optional VolumeUploadScope — parallel transfer, likewise
            null, // the optional pause gate
        ]);

        Assert.Empty(hasher.Hashed);
        Assert.Null(compressor.LastRequest);
        Assert.Equal(["../secret.txt"], unrecoverable);
        Assert.Contains("../secret.txt", index.UnrecoverablePaths);
    }

    /// <summary>
    /// Repairing a pack: a member name escaping the local root → that member is handled as "not obtainable locally" (marked unrecoverable), neither reading the out-of-root file
    /// nor <c>File.Copy</c>ing it outside the compose directory (<c>dest</c> and <c>local</c> are built from the same piece of string).
    /// All members unobtainable → the whole pack is removed from the info file, consistent with the existing semantics.
    /// </summary>
    [Fact]
    public async Task Repairing_A_Pack_Skips_A_Member_Whose_Entry_Name_Escapes_The_Local_Root()
    {
        await File.WriteAllTextAsync(Path.Combine(_base, "secret.txt"), "outside the root");

        var index = new VersionIndex
        {
            Version = 1,
            Entries =
            [
                new IndexEntry
                {
                    Path = "victim.txt",
                    Kind = "file",
                    Permissions = "0644",
                    Length = 16,
                    FullHash = "recording-hasher",
                    Storage = new StorageRef
                    {
                        Kind = "pack", Ref = "p0001", EntryName = "../secret.txt",
                    },
                },
            ],
        };
        var info = new BackupInfoFile
        {
            Backup = new BackupMeta { Name = "t", CreatedAt = DateTimeOffset.UtcNow },
            Packs = { ["p0001"] = new PackInfo { Blob = "packs/p0001.7z", OriginalBytes = 16 } },
        };

        var (repairer, hasher, compressor) = Repairer();
        var unrecoverable = new List<string>();
        await InvokeAsync(repairer, "RepairPackAsync",
        [
            SampleAccount(), SampleContainer(), "packs/p0001.7z", info,
            new Dictionary<int, VersionIndex> { [1] = index }, _local, null,
            AccessTier.Hot, null, new List<string>(), unrecoverable,
            new HashSet<int>(), StagingLease(), CancellationToken.None,
            null, // the optional StageTracker — progress is not what these boundary tests are about
            null, // the optional VolumeUploadScope — parallel transfer, likewise
            null, // the optional pause gate
        ]);

        Assert.Empty(hasher.Hashed);
        Assert.Null(compressor.LastRequest);
        Assert.Equal(["victim.txt"], unrecoverable);
        Assert.False(info.Packs.ContainsKey("p0001"));
    }

    private static Account SampleAccount() => new()
    {
        Name = "a", BlobEndpoint = "http://127.0.0.1:1/x",
        AccountKeyProtected = "", Region = AzureRegion.Global,
    };

    private static Azure.Storage.Blobs.BlobContainerClient SampleContainer() =>
        new(new Uri("http://127.0.0.1:1/x/c"));

    private (BackupRepairer Repairer, RecordingHasher Hasher, RecordingCompressor Compressor) Repairer()
    {
        var hasher = new RecordingHasher();
        var compressor = new RecordingCompressor();
        var factory = new BlobClientFactory(TestSecrets.Reader);
        return (new BackupRepairer(
            factory, new BackupInfoStore(factory, new StubCodec()), compressor, hasher,
            new ThrowingUploader(), Path.Combine(_temp, "repair"),
            new StagingArea(Path.Combine(_temp, "rc"), Path.Combine(_temp, "rs"), () => 200_000_000)),
            hasher, compressor);
    }

    /// <summary>
    /// The local axis and both repair branches are private (their public entry points require a real container and a full check run respectively).
    /// Driving them directly through reflection is the shortest path to nailing down these three decision points in isolation without bringing up Azurite or 7z;
    /// renaming a method yields an explicit failure message rather than silently going dead.
    /// </summary>
    private async Task<LocalState> LocalCheckAsync(BackupChecker checker, IndexEntry entry)
    {
        var method = typeof(BackupChecker).GetMethod(
            "LocalCheckAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BackupChecker.LocalCheckAsync not found");

        var task = (Task<LocalState>)method.Invoke(
            checker, [entry, _local, LocalCheckLevel.Content, CancellationToken.None])!;
        return await task;
    }

    /// <summary>A throwaway staging lease: both cases are stopped by the boundary decision before ever touching the staging area, so the lease exists only to satisfy the signature.</summary>
    private StagingArea.StagingLease StagingLease() =>
        new StagingArea(Path.Combine(_temp, "lc"), Path.Combine(_temp, "ls"), () => 200_000_000).AcquireLease();

    private static async Task InvokeAsync(object target, string name, object?[] args)
    {
        var method = target.GetType().GetMethod(
            name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{target.GetType().Name}.{name} not found");

        await (Task)method.Invoke(target, args)!;
    }
}
