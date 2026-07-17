using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// data blob 的存储寻址方案。加密备份用**密钥化**地址防止指纹识别：
/// 未授权者即使能列出 container，也无法用公开 hash 反推「是否备份过某已知文件」。
/// <para>
/// 加密（有密码 + kdfSalt）：key = HKDF(password, kdfSalt)；地址 = data/{HMAC(key, fullHash)[:16]}；
/// 碰撞检测元数据为不透明 v = HMAC(key, fullHash|len|head)，不泄露长度/头部指纹。
/// 非加密：明文 data/{fullHash} + 元数据 len/head（本就不隐藏内容）。
/// </para>
/// 还原/检查/清理只用索引里已存的实际地址，无需密钥；仅备份创建 blob 时用到本方案。
/// </summary>
public sealed class BlobAddressScheme
{
    private readonly byte[]? _key;

    public BlobAddressScheme(string? password, byte[]? kdfSalt)
    {
        if (!string.IsNullOrEmpty(password) && kdfSalt is { Length: > 0 })
            _key = HKDF.DeriveKey(
                HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(password), outputLength: 32,
                salt: kdfSalt, info: "asb-blob-address"u8.ToArray());
    }

    /// <summary>是否密钥化（加密备份且有 salt）。</summary>
    public bool Keyed => _key is not null;

    /// <summary>data blob 基名（不含 ~N 碰撞后缀 / 分卷后缀）。</summary>
    public string DataAddress(string fullHash) => _key is null
        ? "data/" + fullHash
        : "data/" + Hex(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(fullHash)).AsSpan(0, 16));

    /// <summary>上传时写入的碰撞检测元数据。</summary>
    public IReadOnlyDictionary<string, string> Metadata(string fullHash, long length, string headHash) => _key is null
        ? new Dictionary<string, string>
        {
            ["len"] = length.ToString(CultureInfo.InvariantCulture),
            ["head"] = headHash,
        }
        : new Dictionary<string, string> { ["v"] = Verifier(fullHash, length, headHash) };

    /// <summary>去重时判断既有 blob 的元数据是否代表同内容（老 blob 无元数据 → 视为同内容，向后兼容）。</summary>
    public bool MetadataMatches(IDictionary<string, string> meta, string fullHash, long length, string headHash)
    {
        if (_key is null)
        {
            if (!meta.TryGetValue("len", out var l))
                return true; // 老 blob 无元数据
            return l == length.ToString(CultureInfo.InvariantCulture)
                && meta.TryGetValue("head", out var h) && h == headHash;
        }
        if (!meta.TryGetValue("v", out var v))
            return true;
        return v == Verifier(fullHash, length, headHash);
    }

    private string Verifier(string fullHash, long length, string headHash) =>
        Hex(HMACSHA256.HashData(_key!, Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{fullHash}|{length}|{headHash}"))));

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
