using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

public sealed class BackupInfoStore(IBlobClientFactory factory, IArchiveCodec codec) : IBackupInfoStore
{
    public async Task<BackupInfoFile?> ReadInfoAsync(
        Account account, string container, string? password, CancellationToken ct = default)
        => (await ReadInfoWithETagAsync(account, container, password, ct))?.Info;

    public async Task<(BackupInfoFile Info, string ETag)?> ReadInfoWithETagAsync(
        Account account, string container, string? password, CancellationToken ct = default)
    {
        var cc = Container(account, container);

        // 优先非加密（PRD 1.6）。非加密仅压缩（空密码），加密用备份密码。
        var plain = cc.GetBlobClient(BackupDiscovery.IndexBlobName);
        if ((await plain.ExistsAsync(ct)).Value)
            return await ReadWithETagAsync(plain, password: null, ct);

        var enc = cc.GetBlobClient(BackupDiscovery.EncryptedIndexBlobName);
        if ((await enc.ExistsAsync(ct)).Value)
            return await ReadWithETagAsync(enc, password, ct);

        return null;
    }

    /// <summary>
    /// 无条件写（无 ETag 前提）。**必须保持为对 <see cref="WriteInfoConditionalAsync"/> 的纯委托**：
    /// 「信息文件的 blob 名只由 WriteInfoConditionalAsync 按 password 是否为空决定」是 reset-password
    /// 端点据以只查 <c>Backup.Encrypted</c> 标志位、而不必以解密结果为准的不变量
    /// （见 BackupConfigEndpoints 的 reset-password 注释）。在此自行拼 blob 名会静默打破它。
    /// </summary>
    public Task WriteInfoAsync(
        Account account, string container, BackupInfoFile info, string? password, AccessTier? tier = null, CancellationToken ct = default)
        => WriteInfoConditionalAsync(account, container, info, password, tier, ifMatch: null, ct);

    public async Task<string> WriteInfoConditionalAsync(
        Account account, string container, BackupInfoFile info, string? password, AccessTier? tier, string? ifMatch, CancellationToken ct = default)
    {
        var cc = Container(account, container);
        var name = string.IsNullOrEmpty(password)
            ? BackupDiscovery.IndexBlobName
            : BackupDiscovery.EncryptedIndexBlobName;

        var json = IndexSerializer.SerializeInfoFile(info);
        return await WriteAtomicAsync(cc, name, json, password, b => IndexSerializer.DeserializeInfoFile(b), tier, ifMatch, ct);
    }

    public async Task<VersionIndex> ReadIndexAsync(
        Account account, string container, string indexBlob, string? password, CancellationToken ct = default)
    {
        var cc = Container(account, container);
        return IndexSerializer.DeserializeIndex(
            await DownloadDecodeAsync(cc.GetBlobClient(indexBlob), password, ct));
    }

    public async Task<string> WriteIndexAsync(
        Account account, string container, int version, VersionIndex index, string? password, AccessTier? tier = null, CancellationToken ct = default)
    {
        var cc = Container(account, container);
        var name = $"indexes/v{version}.json" + (string.IsNullOrEmpty(password) ? "" : ".enc");

        var json = IndexSerializer.SerializeIndex(index);
        await WriteAtomicAsync(cc, name, json, password, b => IndexSerializer.DeserializeIndex(b), tier, ifMatch: null, ct);
        return name;
    }

    /// <summary>
    /// 原子写：编码→写临时 blob→下载校验（解码+反序列化）→写正式名（带 tier，可选 If-Match）→删临时。返回正式名的新 ETag。
    /// ifMatch 非空且外部已改动 → 正式写抛 RequestFailedException(412)，不触碰旧内容（§8）。
    /// </summary>
    private async Task<string> WriteAtomicAsync(
        BlobContainerClient cc, string finalName, byte[] json, string? password, Action<byte[]> verify,
        AccessTier? tier, string? ifMatch, CancellationToken ct)
    {
        var encoded = await codec.EncodeAsync(json, password, ct);
        var temp = cc.GetBlobClient(finalName + ".writing." + Guid.NewGuid().ToString("N"));
        try
        {
            await temp.UploadAsync(BinaryData.FromBytes(encoded), overwrite: true, ct);

            verify(await DownloadDecodeAsync(temp, password, ct));

            var options = new BlobUploadOptions();
            if (tier is { } t)
                options.AccessTier = t;
            if (ifMatch is not null)
                options.Conditions = new BlobRequestConditions { IfMatch = new ETag(ifMatch) };

            var resp = await cc.GetBlobClient(finalName).UploadAsync(BinaryData.FromBytes(encoded).ToStream(), options, ct);
            return resp.Value.ETag.ToString();
        }
        finally
        {
            await temp.DeleteIfExistsAsync(cancellationToken: ct);
        }
    }

    private async Task<(BackupInfoFile Info, string ETag)> ReadWithETagAsync(BlobClient blob, string? password, CancellationToken ct)
    {
        var resp = (await blob.DownloadContentAsync(ct)).Value;
        var decoded = await codec.DecodeAsync(resp.Content.ToArray(), password, ct);
        return (IndexSerializer.DeserializeInfoFile(decoded), resp.Details.ETag.ToString());
    }

    private async Task<byte[]> DownloadDecodeAsync(BlobClient blob, string? password, CancellationToken ct)
    {
        var content = (await blob.DownloadContentAsync(ct)).Value.Content.ToArray();
        return await codec.DecodeAsync(content, password, ct);
    }

    private BlobContainerClient Container(Account account, string container) =>
        factory.CreateServiceClient(account).GetBlobContainerClient(container);
}
