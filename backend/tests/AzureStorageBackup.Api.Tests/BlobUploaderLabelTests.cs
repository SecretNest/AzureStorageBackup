using System.IO.Hashing;
using System.Net.Sockets;
using System.Text;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The volume identity label (volume-identity.md): every blob the uploader writes carries its own xxh128 in
/// metadata, written **with** the upload (atomic with the commit), because equality of that label is the only
/// thing that ever justifies not uploading a volume later. Azure stores labels but computes nothing, so a blob
/// that misses its moment is unlabelled forever — and "unlabelled" means "different" to every skip decision.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BlobUploaderLabelTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "asb-label-" + Guid.NewGuid().ToString("N"));

    public BlobUploaderLabelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static Account AzuriteAccount() => new()
    {
        Name = "azurite",
        BlobEndpoint = "http://127.0.0.1:10000/devstoreaccount1",
        AccountKeyProtected = TestSecrets.Protect(AzuriteKey),
        Region = AzureRegion.Global,
    };

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static string RandomName(string p) => p + Guid.NewGuid().ToString("N")[..8];

    [SkippableFact]
    public async Task An_Uploaded_Blob_Carries_Its_Own_Hash_Alongside_The_Callers_Metadata()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var account = AzuriteAccount();
        var name = RandomName("label-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            var content = Encoding.UTF8.GetBytes("the volume's bytes");
            var file = Path.Combine(_dir, "v.001");
            await File.WriteAllBytesAsync(file, content);

            await new BlobUploader(factory).UploadIfMissingAsync(
                account, name, "data/x.001", file, AccessTier.Hot,
                metadata: new Dictionary<string, string> { ["collision"] = "meta" });

            var props = (await container.GetBlobClient("data/x.001").GetPropertiesAsync()).Value;
            Assert.Equal("xxh128:" + Convert.ToHexString(XxHash128.Hash(content)).ToLowerInvariant(),
                props.Metadata[VolumeIdentity.MetaKey]);
            Assert.Equal("meta", props.Metadata["collision"]); // the label joins the caller's metadata, never replaces it
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>An uploader wrapper that counts what actually went over the wire — the unit the skip machinery
    /// is priced in.</summary>
    private sealed class CountingUploader(IBlobUploader inner) : IBlobUploader
    {
        public List<string> Uploaded { get; } = [];

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            Azure.Storage.Blobs.Models.AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            lock (Uploaded) Uploaded.Add(blobName);
            return inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            Azure.Storage.Blobs.Models.AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            lock (Uploaded) Uploaded.Add(blobName);
            return inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }
    }

    /// <summary>The incident, replayed under the new rules: a labelled thousand-volume-style family loses its
    /// tail; the replacement re-uploads **only** what cannot prove itself — the missing volumes — and the
    /// label-matching survivors are verified in place and, crucially, survive the trim (the trim criterion is
    /// the name, never "was not uploaded this run"). ~97 GB of re-upload becomes the size of the hole.</summary>
    [SkippableFact]
    public async Task Replace_Uploads_Only_The_Volumes_That_Cannot_Prove_Themselves()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var account = AzuriteAccount();
        var name = RandomName("skip-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            // Six volume files of distinct content, uploaded labelled — the "old era done right".
            var files = new List<string>();
            for (var i = 1; i <= 6; i++)
            {
                var f = Path.Combine(_dir, $"v{i:D3}");
                await File.WriteAllTextAsync(f, $"volume {i} content");
                files.Add(f);
            }
            var real = new BlobUploader(factory);
            await VolumeBlobIO.ReplaceAsync(real, account, container, "data/fam", files, Azure.Storage.Blobs.Models.AccessTier.Hot);

            // The incident: the tail is deleted; .002 is additionally rewritten in place (same name, wrong bytes,
            // a stale label from someone else's era).
            await container.GetBlobClient("data/fam.005").DeleteIfExistsAsync();
            await container.GetBlobClient("data/fam.006").DeleteIfExistsAsync();
            await container.GetBlobClient("data/fam.002").UploadAsync(BinaryData.FromString("impostor"), overwrite: true);

            var counting = new CountingUploader(real);
            await VolumeBlobIO.ReplaceAsync(counting, account, container, "data/fam", files, Azure.Storage.Blobs.Models.AccessTier.Hot);

            Assert.Equal(["data/fam.002", "data/fam.005", "data/fam.006"], counting.Uploaded.OrderBy(x => x, StringComparer.Ordinal));
            // The verified-in-place survivors are intact after the trim — the family is whole.
            for (var i = 1; i <= 6; i++)
            {
                var body = (await container.GetBlobClient($"data/fam.{i:D3}").DownloadContentAsync()).Value.Content.ToString();
                Assert.Equal($"volume {i} content", body);
            }
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>The backup path's version of the same rule: a stopped run's landed volumes are verified in
    /// place, only holes and impostors upload — and what exists gets **overwritten** (an if-missing upload would
    /// keep the impostor on the grounds that something is there).</summary>
    [SkippableFact]
    public async Task UploadAsync_Skips_Label_Matches_And_Overwrites_The_Rest()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var account = AzuriteAccount();
        var name = RandomName("upskip-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            var files = new List<string>();
            for (var i = 1; i <= 4; i++)
            {
                var f = Path.Combine(_dir, $"u{i:D3}");
                await File.WriteAllTextAsync(f, $"upload volume {i}");
                files.Add(f);
            }
            var real = new BlobUploader(factory);
            // The stopped run: volumes 1..3 landed labelled; 4 never made it; 2 is an impostor at its name.
            await real.UploadOverwriteAsync(account, name, "data/u.001", files[0], AccessTier.Hot);
            await real.UploadOverwriteAsync(account, name, "data/u.002", files[1], AccessTier.Hot);
            await real.UploadOverwriteAsync(account, name, "data/u.003", files[2], AccessTier.Hot);
            await container.GetBlobClient("data/u.002").UploadAsync(BinaryData.FromString("impostor"), overwrite: true);

            var released = new List<string>();
            var counting = new CountingUploader(real);
            var existing = await VolumeBlobIO.ListFamilyLabelsAsync(container, "data/u", default);
            await VolumeBlobIO.UploadAsync(
                counting, account, name, "data/u", files, AccessTier.Hot, ct: default,
                onVolumeUploaded: released.Add, existingVolumes: existing);

            Assert.Equal(["data/u.002", "data/u.004"], counting.Uploaded.OrderBy(x => x, StringComparer.Ordinal));
            // Skipped volumes still release their staging seats — the temp disk must not keep them hostage.
            Assert.Equal(files.OrderBy(x => x, StringComparer.Ordinal), released.OrderBy(x => x, StringComparer.Ordinal));
            for (var i = 1; i <= 4; i++)
            {
                var body = (await container.GetBlobClient($"data/u.{i:D3}").DownloadContentAsync()).Value.Content.ToString();
                Assert.Equal($"upload volume {i}", body);
            }
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>The raw route uploads the user's own file, whose full hash the backup has already computed —
    /// re-reading it for the label would be a second pass over a possibly enormous file for a value already in
    /// hand. A caller-supplied label (the metadata already carrying the key) is used verbatim: no buffering, no
    /// recompute, any size. Consistency between the label and the bytes rides the raw route's existing
    /// stat-bracket (the item is thrown away if the file moved under it).</summary>
    [SkippableFact]
    public async Task A_Caller_Supplied_Label_Is_Used_Verbatim_Without_Rehashing()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var account = AzuriteAccount();
        var name = RandomName("labelpre-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            var file = Path.Combine(_dir, "raw.bin");
            await File.WriteAllBytesAsync(file, new byte[64]);

            // A sentinel value, deliberately not these bytes' hash: stored verbatim proves nothing rehashed.
            await new BlobUploader(factory) { DefaultInMemoryLimitBytes = 16 }.UploadIfMissingAsync(
                account, name, "data/raw", file, AccessTier.Hot,
                metadata: new Dictionary<string, string> { [VolumeIdentity.MetaKey] = "xxh128:precomputed" });

            var props = (await container.GetBlobClient("data/raw").GetPropertiesAsync()).Value;
            Assert.Equal("xxh128:precomputed", props.Metadata[VolumeIdentity.MetaKey]);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>The volume does not fit its stream's share of the upload memory limit, so it goes two-pass: hashed
    /// from disk, then re-read from disk for the send. The label is still there and still describes the bytes —
    /// the cut-off costs a second read, never the label (volume-identity.md).</summary>
    [SkippableFact]
    public async Task A_Volume_Past_Its_Streams_Share_Is_Labelled_Without_Being_Held_In_Memory()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var account = AzuriteAccount();
        var name = RandomName("label-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            var content = new byte[300 * 1024];
            Random.Shared.NextBytes(content);
            var file = Path.Combine(_dir, "big.001");
            await File.WriteAllBytesAsync(file, content);

            await new BlobUploader(factory).UploadIfMissingAsync(
                account, name, "data/big.001", file, AccessTier.Hot, retry: null, ct: default,
                metadata: null, progress: null, inMemoryLimitBytes: 100 * 1024);

            var blob = container.GetBlobClient("data/big.001");
            var props = (await blob.GetPropertiesAsync()).Value;
            Assert.Equal("xxh128:" + Convert.ToHexString(XxHash128.Hash(content)).ToLowerInvariant(),
                props.Metadata[VolumeIdentity.MetaKey]);
            Assert.Equal(content, (await blob.DownloadContentAsync()).Value.Content.ToArray());
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>A limit of 0 is "never hold a volume in memory": every volume goes two-pass, and every volume is
    /// still labelled — turning the memory off must not turn the skip machinery off with it.</summary>
    [SkippableFact]
    public async Task A_Zero_Limit_Still_Labels_Every_Volume()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var account = AzuriteAccount();
        var name = RandomName("label-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            var content = Encoding.UTF8.GetBytes("small, but not held in memory either");
            var file = Path.Combine(_dir, "z.001");
            await File.WriteAllBytesAsync(file, content);

            await new BlobUploader(factory).UploadOverwriteAsync(
                account, name, "data/z.001", file, AccessTier.Hot, retry: null, ct: default,
                metadata: null, progress: null, inMemoryLimitBytes: 0);

            var props = (await container.GetBlobClient("data/z.001").GetPropertiesAsync()).Value;
            Assert.Equal("xxh128:" + Convert.ToHexString(XxHash128.Hash(content)).ToLowerInvariant(),
                props.Metadata[VolumeIdentity.MetaKey]);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>The in-memory route is taken exactly when the file fits the share; the uploader's own fallback
    /// applies when a caller names no share (index/info blobs, and the 8/9-argument overloads).</summary>
    [Fact]
    public void Holds_In_Memory_Exactly_When_The_File_Fits_The_Share()
    {
        Assert.True(BlobUploader.HoldsInMemory(fileLength: 100, inMemoryLimitBytes: 100));
        Assert.False(BlobUploader.HoldsInMemory(fileLength: 101, inMemoryLimitBytes: 100));
        Assert.False(BlobUploader.HoldsInMemory(fileLength: 1, inMemoryLimitBytes: 0));
        Assert.True(BlobUploader.HoldsInMemory(fileLength: 0, inMemoryLimitBytes: 0)); // nothing to hold
        var fallback = new BlobUploader(new BlobClientFactory(TestSecrets.Reader)).DefaultInMemoryLimitBytes;
        Assert.Equal(256L * 1024 * 1024, fallback);
    }
}
