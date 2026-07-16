using Azure.Storage.Blobs;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

public sealed class BackupInfoStore(IBlobClientFactory factory, IArchiveCodec codec) : IBackupInfoStore
{
    public async Task<BackupInfoFile?> ReadInfoAsync(
        Account account, string container, string? password, CancellationToken ct = default)
    {
        var cc = Container(account, container);

        // 优先非加密（PRD 1.6）。非加密仅压缩（空密码），加密用备份密码。
        var plain = cc.GetBlobClient(BackupDiscovery.IndexBlobName);
        if ((await plain.ExistsAsync(ct)).Value)
            return IndexSerializer.DeserializeInfoFile(await DownloadDecodeAsync(plain, password: null, ct));

        var enc = cc.GetBlobClient(BackupDiscovery.EncryptedIndexBlobName);
        if ((await enc.ExistsAsync(ct)).Value)
            return IndexSerializer.DeserializeInfoFile(await DownloadDecodeAsync(enc, password, ct));

        return null;
    }

    public async Task WriteInfoAsync(
        Account account, string container, BackupInfoFile info, string? password, CancellationToken ct = default)
    {
        var cc = Container(account, container);
        var name = string.IsNullOrEmpty(password)
            ? BackupDiscovery.IndexBlobName
            : BackupDiscovery.EncryptedIndexBlobName;

        var json = IndexSerializer.SerializeInfoFile(info);
        await WriteAtomicAsync(cc, name, json, password, b => IndexSerializer.DeserializeInfoFile(b), ct);
    }

    public async Task<VersionIndex> ReadIndexAsync(
        Account account, string container, string indexBlob, string? password, CancellationToken ct = default)
    {
        var cc = Container(account, container);
        return IndexSerializer.DeserializeIndex(
            await DownloadDecodeAsync(cc.GetBlobClient(indexBlob), password, ct));
    }

    public async Task<string> WriteIndexAsync(
        Account account, string container, int version, VersionIndex index, string? password, CancellationToken ct = default)
    {
        var cc = Container(account, container);
        var name = $"indexes/v{version}.json" + (string.IsNullOrEmpty(password) ? "" : ".enc");

        var json = IndexSerializer.SerializeIndex(index);
        await WriteAtomicAsync(cc, name, json, password, b => IndexSerializer.DeserializeIndex(b), ct);
        return name;
    }

    /// <summary>
    /// 原子写：编码→写临时 blob→下载校验（解码+反序列化）→写正式名→删临时。
    /// 校验失败不触碰正式名；正式名单 blob 提交是原子的，失败时旧文件完好（§8）。
    /// </summary>
    private async Task WriteAtomicAsync(
        BlobContainerClient cc, string finalName, byte[] json, string? password, Action<byte[]> verify, CancellationToken ct)
    {
        var encoded = await codec.EncodeAsync(json, password, ct);
        var temp = cc.GetBlobClient(finalName + ".writing." + Guid.NewGuid().ToString("N"));
        try
        {
            await temp.UploadAsync(BinaryData.FromBytes(encoded), overwrite: true, ct);

            verify(await DownloadDecodeAsync(temp, password, ct));

            await cc.GetBlobClient(finalName).UploadAsync(BinaryData.FromBytes(encoded), overwrite: true, ct);
        }
        finally
        {
            await temp.DeleteIfExistsAsync(cancellationToken: ct);
        }
    }

    private async Task<byte[]> DownloadDecodeAsync(BlobClient blob, string? password, CancellationToken ct)
    {
        var content = (await blob.DownloadContentAsync(ct)).Value.Content.ToArray();
        return await codec.DecodeAsync(content, password, ct);
    }

    private BlobContainerClient Container(Account account, string container) =>
        factory.CreateServiceClient(account).GetBlobContainerClient(container);
}
