using System.Security.Cryptography;
using System.Text;
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
    /// 密钥化时 v 是四项合一的不透明值，省不掉其中一项：任一项未知就不写 v，改写只覆盖
    /// fullHash+长度 的窄校验值 v1。绝不写出 head/tail/len 明文（那正是密钥化要防的长度指纹）。
    /// <para>修复前：这里返回空字典，该对象一点碰撞防护都不剩。</para>
    /// </summary>
    [Fact]
    public void Keyed_Falls_Back_To_A_Narrow_Verifier_When_Head_Or_Tail_Unknown()
    {
        var s = new BlobAddressScheme("pw", Salt);

        foreach (var meta in new[] { s.Metadata(Hash, 42, "xxh128:aa", null), s.Metadata(Hash, 42, null, "xxh128:zz") })
        {
            Assert.Equal(new[] { "v1" }, meta.Keys.Order());  // 只有窄校验值，没有 v，也没有任何明文项
            Assert.NotEmpty(meta["v1"]);
        }
        // 两项都未知时同样发 v1（缺的是哪一项不影响它覆盖的范围）。
        Assert.Equal(new[] { "v1" }, s.Metadata(Hash, 42, null, null).Keys.Order());
    }

    /// <summary>
    /// 全新备份（四项齐全）写出的密钥化元数据必须一字不变：只有 v，且值与本改动前逐字节相同。
    /// 期望值是按 v 的定义 HMAC(key, "fullHash|len|head|tail") 独立算出来的——不是从被测代码里抄的，
    /// 故 <see cref="BlobAddressScheme.Metadata"/> 一旦改了 v 的输入串（会使既有对象全部失配）这里就红。
    /// </summary>
    [Fact]
    public void Keyed_Fresh_Backup_Metadata_Is_Byte_For_Byte_Unchanged()
    {
        var salt = new byte[16]; // 固定盐 → 固定 key → 期望值可复算
        var s = new BlobAddressScheme("pw", salt);
        var key = HKDF.DeriveKey(
            HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes("pw"), outputLength: 32,
            salt: salt, info: "asb-blob-address"u8.ToArray());
        var expected = Convert.ToHexString(HMACSHA256.HashData(
            key, Encoding.UTF8.GetBytes($"{Hash}|42|xxh128:aa|xxh128:zz"))).ToLowerInvariant();

        var meta = s.Metadata(Hash, 42, "xxh128:aa", "xxh128:zz");

        Assert.Equal(new[] { "v" }, meta.Keys.Order()); // 一个键都没多（尤其没有 v1）
        Assert.Equal(expected, meta["v"]);
    }

    /// <summary>
    /// 向后兼容：已带 v 的既有对象仍**只**按 v 判定——v1 的引入不改变它们的任何判定结果。
    /// 手工塞一个 v1（既有对象不会有，但要证明 v 存在时它一眼都不看）。
    /// </summary>
    [Fact]
    public void Keyed_Existing_Verifier_Still_Wins_Over_The_Narrow_One()
    {
        var s = new BlobAddressScheme("pw", Salt);
        var meta = new Dictionary<string, string>(s.Metadata(Hash, 100, "xxh128:bb", "xxh128:dd"));
        meta["v1"] = "deadbeef"; // 一个必然对不上的窄校验值

        Assert.True(s.MetadataMatches(meta, Hash, 100, "xxh128:bb", "xxh128:dd"));  // v 说同内容 → 放行
        Assert.False(s.MetadataMatches(meta, Hash, 100, "xxh128:bb", "xxh128:ee")); // v 说碰撞 → 否决

        // 反向：v 对不上但 v1 对得上，也必须以 v 为准（否则 v1 会成为绕过四项校验的后门）。
        var forged = new Dictionary<string, string>
        {
            ["v"] = "deadbeef",
            ["v1"] = new Dictionary<string, string>(s.Metadata(Hash, 100, null, null))["v1"],
        };
        Assert.False(s.MetadataMatches(forged, Hash, 100, "xxh128:bb", "xxh128:dd"));
    }

    /// <summary>
    /// 向后兼容：既无 v 也无 v1 的真·老 blob 仍按「无元数据 → 不参与判定」放行，不新增拒绝面。
    /// </summary>
    [Fact]
    public void Keyed_Legacy_Object_With_Neither_Verifier_Still_Participates_As_Before()
    {
        var s = new BlobAddressScheme("pw", Salt);

        Assert.True(s.MetadataMatches(new Dictionary<string, string>(), Hash, 1, "xxh128:aa", "xxh128:bb"));
        // 只带无关键（如 raw 标记）的老对象同样放行。
        Assert.True(s.MetadataMatches(
            new Dictionary<string, string> { ["raw"] = "1" }, Hash, 1, "xxh128:aa", "xxh128:bb"));
    }

    /// <summary>
    /// v1 的实际作用：以缺 head/tail 的元数据写出的对象，长度仍参与判定。
    /// <para>修复前这里写的是空字典，长度不同也一律返回 true——碰撞防护塌成只剩 fullHash。</para>
    /// </summary>
    [Fact]
    public void Keyed_Narrow_Verifier_Keeps_Length_In_The_Decision()
    {
        var s = new BlobAddressScheme("pw", Salt);
        var written = new Dictionary<string, string>(s.Metadata(Hash, 42, "xxh128:aa", null));

        // head/tail 不参与（本就未知），同内容照旧放行——不会把同内容误判成碰撞。
        Assert.True(s.MetadataMatches(written, Hash, 42, "xxh128:aa", "xxh128:real-tail"));
        Assert.True(s.MetadataMatches(written, Hash, 42, "xxh128:whatever", "xxh128:anything"));
        // 长度不同 = 内容必然不同 → 碰撞，必须否决。
        Assert.False(s.MetadataMatches(written, Hash, 43, "xxh128:aa", "xxh128:real-tail"));
        // fullHash 不同也否决（v1 覆盖 fullHash+长度两项）。
        Assert.False(s.MetadataMatches(written, "xxh128:ffffffffffffffffffffffffffffffff", 42, "xxh128:aa", "xxh128:zz"));
    }

    /// <summary>
    /// v1 是密钥化的：换密码/换盐算出的 v1 对不上，与 v 的性质一致（不是可被旁观者复算的明文长度）。
    /// </summary>
    [Fact]
    public void Keyed_Narrow_Verifier_Is_Key_Bound_And_Not_A_Plaintext_Length()
    {
        var written = new Dictionary<string, string>(new BlobAddressScheme("pw", Salt).Metadata(Hash, 42, null, null));

        Assert.DoesNotContain("42", written["v1"], StringComparison.Ordinal); // 长度不以明文形式出现
        Assert.DoesNotContain(Hash, written["v1"], StringComparison.Ordinal);
        Assert.NotEqual(
            written["v1"],
            new Dictionary<string, string>(new BlobAddressScheme("pw2", Salt).Metadata(Hash, 42, null, null))["v1"]);
        Assert.NotEqual(
            written["v1"],
            new Dictionary<string, string>(
                new BlobAddressScheme("pw", RandomNumberGenerator.GetBytes(16)).Metadata(Hash, 42, null, null))["v1"]);
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
