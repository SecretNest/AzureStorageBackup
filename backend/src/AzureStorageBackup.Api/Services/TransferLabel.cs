using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 一条传输流在界面上的名字与大小。
/// <para>
/// blob 是内容寻址的——加密时还是 HMAC 后的乱码——<c>data/9f2a3b7c…001</c> 对着屏幕的人毫无意义。
/// 上传侧早已换成源文件路径，下载侧（还原/校验）这里给出**同样的形状**，两边读起来才是一回事。
/// </para>
/// </summary>
public static class TransferLabel
{
    /// <param name="members">
    /// 这一组当前要处理的条目。pack 报的是**这一组**的成员数而不是包里的全部：选择性还原只取
    /// 其中几个，报全量会和界面上别处的数字对不上。一箱装着几百个文件，列不下，所以只报个数。
    /// </param>
    public static string For(StorageRef storage, IReadOnlyList<IndexEntry> members) =>
        storage.Kind == "pack"
            ? $"pack {storage.Ref} ({members.Count} file{(members.Count == 1 ? "" : "s")})"
            : members.Count > 0 ? members[0].Path : storage.Ref;

    /// <summary>
    /// 这个存储对象要拉下来多少字节（压缩后，含全部分卷）。索引在备份时就把各卷尺寸记下来了，
    /// 所以不必先去问云端。
    /// <para>
    /// pack 的卷尺寸记在信息文件里而不是条目里——死重压实会重写整个包、改变卷数与尺寸，
    /// 记在条目上就会随着每次压实全部过期（<c>StorageRef.VolumeSizes</c> 上有同样的说明）。
    /// </para>
    /// <para>0 = 问不出来（老索引没有这一项）。调用方据此决定报不报尺寸。</para>
    /// </summary>
    public static long DownloadBytesOf(StorageRef storage, BackupInfoFile info) =>
        storage.Kind == "pack"
            ? info.Packs.TryGetValue(storage.Ref, out var pack) ? pack.VolumeSizes.Sum() : 0
            : storage.VolumeSizes.Sum();
}
