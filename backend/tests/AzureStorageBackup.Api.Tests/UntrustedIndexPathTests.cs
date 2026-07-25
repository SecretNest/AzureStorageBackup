using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// F4（终审）：索引不可信这条威胁模型此前只落在**还原写入**上，本地**读取**侧没有落地。
/// <c>/import</c> 让任何人都能把一个自造的 container 变成本地索引数据（设计 §5），于是
/// <c>Path.Combine(localRoot, &lt;索引里的路径&gt;)</c> 这种拼接就成了越出 <c>Backup__Root</c> 的入口：
/// 多数点有 hash 门，只是「某文件存在且内容等于 X」的确认预言机；死重压实里那两处更糟——
/// 一处是无 hash 门的纯存在性探测，另一处 <c>CopyInto</c> 是把 pack 里的内容写到 compose
/// 目录之外的**任意写**。
/// <para>这些用例全部用假件，不需要 Azurite / 7z：断言的是「一步都没走出去」，
/// 真去压缩上传反而会把判定点淹掉。</para>
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

    /// <summary>记录每一次被要求 hash 的路径——用来证明根外的文件连读都没读过。</summary>
    private sealed class RecordingHasher : IFileHasher
    {
        public List<string> Hashed { get; } = [];

        public Task<string> HeadHashAsync(string path, int headBytes, CancellationToken ct = default)
            => Record(path);

        public Task<string> TailHashAsync(string path, int tailBytes, CancellationToken ct = default)
            => Record(path);

        public Task<string> FullHashAsync(string path, CancellationToken ct = default)
            => Record(path);

        private Task<string> Record(string path)
        {
            lock (Hashed) Hashed.Add(path);
            // 返回一个不可能匹配的值：万一守卫失效，也不该因为「hash 恰好对不上」而蒙混过关。
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
    /// 死重压实：一个成员名越出 compose 目录 → 整包放弃压实。
    /// <para>
    /// 断言分三层，对应被堵住的三个洞：
    /// 一、根外文件**一次都没被读过**（<c>LocalPath</c> 的存在性探测 + hash 确认预言机）；
    /// 二、压缩器从未被调用，也就是 <c>CopyInto</c> 从未执行 → compose 目录外没有发生任何写；
    /// 三、pack 本身原封不动，只记下死重——放弃是安全空操作，不是数据损失。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Compaction_Is_Abandoned_When_A_Members_Entry_Name_Escapes_The_Compose_Directory()
    {
        // 根外的「秘密」：只要它被 stat 或 hash 过一次，预言机就成立了。
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

        // 存活成员 4000 字节 / 原始 6000 → 死重 1/3 > 阈值 0.3，必定进入重压路径。
        var live = new Dictionary<string, Dictionary<string, LivePackMember>>
        {
            ["p0001"] = new(StringComparer.Ordinal)
            {
                ["b.txt"] = new LivePackMember("b.txt", 2000, "hash-b"),
                // `../secret.txt`：相对 localRoot 指向 _base/secret.txt（读），
                // 相对 composeDir 指向 compose 的上一级（写）。
                ["../secret.txt"] = new LivePackMember("../secret.txt", 2000, "hash-escape"),
            },
        };

        var hasher = new RecordingHasher();
        var compressor = new RecordingCompressor();
        var compactor = new DeadWeightCompactor(
            new ThrowingUploader(), compressor, hasher, Path.Combine(_temp, "compact"));

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
    /// 检查器的本地轴：索引条目的路径越出本地根 → 判 Missing，且**不读**那个文件。
    /// 越界条目原本会变成「某路径存在且内容 hash 等于 X」的确认预言机（Content 级）
    /// 或「存在 + 尺寸 + 权限」（Attributes 级）。
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

    /// <summary>对照组：同一条路径落在根内时，本地轴照常工作（读文件、比 hash）——
    /// 证明上面那条 Missing 来自边界判定，而不是本地轴整个失灵。</summary>
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

    /// <summary>
    /// 修复单文件 blob：索引条目的路径越出本地根 → 该候选来源直接跳过，本地无可用来源，
    /// 条目标记不可恢复。原本它是「本地某处存在内容 hash 等于 X 的文件」的确认预言机，
    /// 而且探中之后还会把根外文件的内容压缩上传到云端。
    /// </summary>
    [Fact]
    public async Task Repairing_A_Blob_Skips_A_Source_Path_That_Escapes_The_Local_Root()
    {
        // hash 故意与 RecordingHasher 的返回值一致：守卫失效时这条路径就会被当成可用来源，
        // 于是断言失败的原因是「越界被采用了」，而不是「hash 恰好没对上」。
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
            new List<string>(), unrecoverable, new HashSet<int>(), CancellationToken.None,
        ]);

        Assert.Empty(hasher.Hashed);
        Assert.Null(compressor.LastRequest);
        Assert.Equal(["../secret.txt"], unrecoverable);
        Assert.Contains("../secret.txt", index.UnrecoverablePaths);
    }

    /// <summary>
    /// 修复 pack：成员名越出本地根 → 该成员按「本地取不到」处置（标记不可恢复），既不读根外文件，
    /// 也不会把它 <c>File.Copy</c> 到 compose 目录之外（<c>dest</c> 与 <c>local</c> 拼的是同一段字符串）。
    /// 全部成员都取不到 → 整个 pack 从信息文件里移除，与既有语义一致。
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
            new HashSet<int>(), CancellationToken.None,
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
            new ThrowingUploader(), Path.Combine(_temp, "repair")), hasher, compressor);
    }

    /// <summary>
    /// 本地轴与两个修复分支都是 private（对外入口分别需要真实 container 和一次完整检查）。
    /// 用反射直接驱动，是在不拉起 Azurite / 7z 的前提下把这三个判定点单独钉住的最短路径；
    /// 方法改名会得到一条明确的失败信息，而不是静默失效。
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

    private static async Task InvokeAsync(object target, string name, object?[] args)
    {
        var method = target.GetType().GetMethod(
            name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{target.GetType().Name}.{name} not found");

        await (Task)method.Invoke(target, args)!;
    }
}
