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
}
