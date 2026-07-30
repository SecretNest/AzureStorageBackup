using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 在途那几行显示的名字。blob 是内容寻址的——加密时还是 HMAC 后的乱码——
/// <c>data/9f2a3b7c…001</c> 对着屏幕的人毫无意义。上传侧早已换成源路径，
/// 下载侧（还原/校验）要给出**同样的形状**，否则同一个界面上两边读起来不是一回事。
/// </summary>
public class TransferLabelTests
{
    private static IndexEntry Entry(string path, StorageRef storage) =>
        new() { Path = path, Kind = "file", Permissions = "0644", Length = 100, Storage = storage };

    private static BackupInfoFile Info() =>
        new() { Backup = new BackupMeta { Name = "t", CreatedAt = DateTimeOffset.UtcNow } };

    [Fact]
    public void A_Single_File_Blob_Shows_Its_Source_Path()
    {
        var storage = new StorageRef { Kind = "blob", Ref = "data/9f2a3b7c1e4d8a0b" };
        var label = TransferLabel.For(storage, [Entry("photos/2024/IMG_0042.mov", storage)]);

        Assert.Equal("photos/2024/IMG_0042.mov", label);
    }

    /// <summary>一箱装着几百个文件，列不下——报包号与成员数。</summary>
    [Fact]
    public void A_Pack_Shows_Its_Id_And_Member_Count()
    {
        var storage = new StorageRef { Kind = "pack", Ref = "p3f2a9c1e0007" };
        var members = Enumerable.Range(0, 318).Select(i => Entry($"docs/f{i}.txt", storage)).ToList();

        Assert.Equal("pack p3f2a9c1e0007 (318 files)", TransferLabel.For(storage, members));
    }

    [Fact]
    public void One_Member_Is_Not_Pluralised()
    {
        var storage = new StorageRef { Kind = "pack", Ref = "p0001" };
        Assert.Equal("pack p0001 (1 file)", TransferLabel.For(storage, [Entry("a.txt", storage)]));
    }

    /// <summary>
    /// 报的是**这一组**的成员数，不是包里的全部。选择性还原只取其中几个，
    /// 报全量会和界面上别处的数字对不上。
    /// </summary>
    [Fact]
    public void A_Pack_Counts_Only_The_Members_Being_Handled()
    {
        var storage = new StorageRef { Kind = "pack", Ref = "p0002" };
        var selected = new[] { Entry("a.txt", storage), Entry("b.txt", storage) };

        Assert.Equal("pack p0002 (2 files)", TransferLabel.For(storage, selected));
    }

    /// <summary>拿不到条目时退回 ref——不该为了一个名字把整次传输弄崩。</summary>
    [Fact]
    public void With_No_Members_It_Falls_Back_To_The_Ref()
    {
        var storage = new StorageRef { Kind = "blob", Ref = "data/abc" };
        Assert.Equal("data/abc", TransferLabel.For(storage, []));
    }

    /// <summary>单文件 blob 的卷尺寸就记在条目上。</summary>
    [Fact]
    public void Download_Size_Of_A_Blob_Sums_Its_Volumes()
    {
        var storage = new StorageRef { Kind = "blob", Ref = "data/x", VolumeSizes = [100, 200, 50] };
        Assert.Equal(350, TransferLabel.DownloadBytesOf(storage, Info()));
    }

    /// <summary>
    /// pack 的卷尺寸记在**信息文件**里而不是条目上：死重压实会重写整个包、改变卷数与尺寸，
    /// 记在条目上就会随着每次压实全部过期。
    /// </summary>
    [Fact]
    public void Download_Size_Of_A_Pack_Comes_From_The_Info_File()
    {
        var info = Info();
        info.Packs["p0001"] = new PackInfo { Blob = "packs/p0001.7z", VolumeSizes = [1000, 300] };

        var storage = new StorageRef { Kind = "pack", Ref = "p0001", VolumeSizes = [999999] };
        Assert.Equal(1300, TransferLabel.DownloadBytesOf(storage, info));
    }

    /// <summary>老索引没记卷尺寸时报 0＝未知，界面据此不显示尺寸——绝不能拿一个偏小的数当分母。</summary>
    [Fact]
    public void An_Unknown_Size_Is_Reported_As_Zero()
    {
        Assert.Equal(0, TransferLabel.DownloadBytesOf(
            new StorageRef { Kind = "pack", Ref = "missing" }, Info()));
        Assert.Equal(0, TransferLabel.DownloadBytesOf(
            new StorageRef { Kind = "blob", Ref = "data/old" }, Info()));
    }
}
