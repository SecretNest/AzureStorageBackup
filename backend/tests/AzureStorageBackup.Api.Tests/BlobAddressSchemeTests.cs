using System.Security.Cryptography;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class BlobAddressSchemeTests
{
    private const string Hash = "xxh128:0123456789abcdef0123456789abcdef";
    private static readonly byte[] Salt = RandomNumberGenerator.GetBytes(16);

    [Fact]
    public void Plain_When_No_Password()
    {
        var s = new BlobAddressScheme(null, null);

        Assert.False(s.Keyed);
        Assert.Equal("data/" + Hash, s.DataAddress(Hash)); // 明文寻址
        var meta = s.Metadata(Hash, 42, "xxh128:aa", "xxh128:zz");
        Assert.Equal("42", meta["len"]);
        Assert.Equal("xxh128:aa", meta["head"]);
        Assert.Equal("xxh128:zz", meta["tail"]);
    }

    [Fact]
    public void Keyed_When_Password_And_Salt()
    {
        var s = new BlobAddressScheme("pw", Salt);

        Assert.True(s.Keyed);
        var addr = s.DataAddress(Hash);
        Assert.StartsWith("data/", addr);
        Assert.NotEqual("data/" + Hash, addr);              // 不是明文 hash
        Assert.DoesNotContain(Hash, addr);                  // 公开 hash 不出现在地址里
        var meta = s.Metadata(Hash, 42, "xxh128:aa", "xxh128:zz");
        Assert.False(meta.ContainsKey("len"));              // 不泄露长度
        Assert.False(meta.ContainsKey("head"));             // 不泄露头部指纹
        Assert.False(meta.ContainsKey("tail"));             // 不泄露尾部指纹
        Assert.True(meta.ContainsKey("v"));                 // 只有不透明校验值
    }

    [Fact]
    public void Keyed_Address_Is_Deterministic_And_Salt_Sensitive()
    {
        var a = new BlobAddressScheme("pw", Salt).DataAddress(Hash);
        var again = new BlobAddressScheme("pw", Salt).DataAddress(Hash);
        var otherSalt = new BlobAddressScheme("pw", RandomNumberGenerator.GetBytes(16)).DataAddress(Hash);
        var otherPw = new BlobAddressScheme("pw2", Salt).DataAddress(Hash);

        Assert.Equal(a, again);        // 同 (密码,盐,hash) → 同地址（去重可用）
        Assert.NotEqual(a, otherSalt); // 盐不同 → 地址不同
        Assert.NotEqual(a, otherPw);   // 密码不同 → 地址不同
    }

    [Fact]
    public void Metadata_Matches_Only_Same_Content()
    {
        foreach (var s in new[] { new BlobAddressScheme(null, null), new BlobAddressScheme("pw", Salt) })
        {
            var meta = new Dictionary<string, string>(s.Metadata(Hash, 100, "xxh128:bb", "xxh128:dd"));
            Assert.True(s.MetadataMatches(meta, Hash, 100, "xxh128:bb", "xxh128:dd"));   // 同内容
            Assert.False(s.MetadataMatches(meta, Hash, 101, "xxh128:bb", "xxh128:dd"));  // 长度不同 → 碰撞
            Assert.False(s.MetadataMatches(meta, Hash, 100, "xxh128:cc", "xxh128:dd"));  // 头部不同 → 碰撞
            Assert.False(s.MetadataMatches(meta, Hash, 100, "xxh128:bb", "xxh128:ee"));  // 尾部不同 → 碰撞
        }
    }

    [Fact]
    public void Missing_Metadata_Treated_As_Match()
    {
        var s = new BlobAddressScheme("pw", Salt);
        Assert.True(s.MetadataMatches(new Dictionary<string, string>(), Hash, 1, "xxh128:aa", "xxh128:bb"));
    }

    /// <summary>
    /// F4：老索引条目缺 head/tail 时（修复器会把 null 原样传进来），必须**省略**对应的键，
    /// 而不是写空串。<see cref="BlobAddressScheme.MetadataMatches"/> 把「键缺失」当不参与判定、
    /// 把「键存在但值不同」当碰撞——写空串会让同内容被判成碰撞。
    /// </summary>
    [Fact]
    public void Unknown_Head_Or_Tail_Is_Omitted_Not_Blanked()
    {
        var s = new BlobAddressScheme(null, null);

        var noTail = s.Metadata(Hash, 42, "xxh128:aa", null);
        Assert.Equal("42", noTail["len"]);
        Assert.Equal("xxh128:aa", noTail["head"]);
        Assert.False(noTail.ContainsKey("tail")); // 空串会让下面的判定变成「碰撞」

        var noHead = s.Metadata(Hash, 42, null, "xxh128:zz");
        Assert.False(noHead.ContainsKey("head"));
        Assert.Equal("xxh128:zz", noHead["tail"]);
    }

    /// <summary>
    /// F4 的实际后果：以缺 tail 的元数据写出的 blob，后续以真实 tail 去重时必须判为同内容。
    /// 若 Metadata 写的是 tail=""，这里会返回 false，同内容被改写到 ~N 备用地址并误报「已避让碰撞」。
    /// </summary>
    [Fact]
    public void Omitted_Tail_Does_Not_Look_Like_A_Collision()
    {
        var s = new BlobAddressScheme(null, null);
        var written = new Dictionary<string, string>(s.Metadata(Hash, 42, "xxh128:aa", null));

        Assert.True(s.MetadataMatches(written, Hash, 42, "xxh128:aa", "xxh128:real-tail")); // 同内容
        Assert.False(s.MetadataMatches(written, Hash, 43, "xxh128:aa", "xxh128:real-tail")); // 长度仍然管用

        var noHead = new Dictionary<string, string>(s.Metadata(Hash, 42, null, "xxh128:zz"));
        Assert.True(s.MetadataMatches(noHead, Hash, 42, "xxh128:real-head", "xxh128:zz"));
        Assert.False(s.MetadataMatches(noHead, Hash, 42, "xxh128:real-head", "xxh128:other")); // tail 仍然管用
    }

    /// <summary>
    /// 密钥化时 v 是四项合一的不透明值，省不掉其中一项：任一项未知就整体不写 v，
    /// 退化成「老 blob 无元数据」（不参与判定），而不是给出一个用空串算出来的错误判定依据。
    /// </summary>
    [Fact]
    public void Keyed_Omits_Verifier_Entirely_When_Head_Or_Tail_Unknown()
    {
        var s = new BlobAddressScheme("pw", Salt);

        Assert.Empty(s.Metadata(Hash, 42, "xxh128:aa", null));
        Assert.Empty(s.Metadata(Hash, 42, null, "xxh128:zz"));
        Assert.True(s.Metadata(Hash, 42, "xxh128:aa", "xxh128:zz").ContainsKey("v")); // 齐全时照旧
    }

    /// <summary>全新备份写出的元数据不受 F4 影响：head/tail 齐全时三个键一个不少。</summary>
    [Fact]
    public void Fresh_Backup_Metadata_Is_Unchanged()
    {
        var meta = new BlobAddressScheme(null, null).Metadata(Hash, 42, "xxh128:aa", "xxh128:zz");

        Assert.Equal(3, meta.Count);
        Assert.Equal("42", meta["len"]);
        Assert.Equal("xxh128:aa", meta["head"]);
        Assert.Equal("xxh128:zz", meta["tail"]);
    }
}
