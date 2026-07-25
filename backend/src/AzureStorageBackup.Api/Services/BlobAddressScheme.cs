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

    /// <summary>
    /// 上传时写入的碰撞检测元数据（含头/尾段 hash，抓住内容不同却 fullHash+长度 相同的残余碰撞）。
    /// <para>
    /// <paramref name="headHash"/>/<paramref name="tailHash"/> 为 null 表示「该项未知」——老索引条目
    /// 可能缺这两个字段（全新备份的 BuildEntries 总会填齐，故这只在修复老备份时出现）。此时必须**省略**
    /// 受影响的键，而不是写空串：<see cref="MetadataMatches"/> 把「键缺失」当作不参与判定、把「键存在
    /// 但值不同」当作碰撞，写空串会让同内容被判成碰撞，改用 ~N 备用地址并误报「已避让碰撞」。
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata(string fullHash, long length, string? headHash, string? tailHash)
    {
        if (_key is null)
        {
            var meta = new Dictionary<string, string>
            {
                ["len"] = length.ToString(CultureInfo.InvariantCulture),
            };
            if (headHash is not null)
                meta["head"] = headHash;
            if (tailHash is not null)
                meta["tail"] = tailHash;
            return meta;
        }
        // 密钥化时 v 是 fullHash|len|head|tail 合一的不透明值，无法只省其中一项：
        // 任一项未知就整体省略 v，退化成「老 blob 无元数据」——不参与判定，而不是给出错误的判定依据。
        return headHash is null || tailHash is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["v"] = Verifier(fullHash, length, headHash, tailHash) };
    }

    /// <summary>去重时判断既有 blob 的元数据是否代表同内容（老 blob 缺某项 → 该项不参与判定，向后兼容）。</summary>
    public bool MetadataMatches(IDictionary<string, string> meta, string fullHash, long length, string headHash, string tailHash)
    {
        if (_key is null)
        {
            if (!meta.TryGetValue("len", out var l))
                return true; // 老 blob 无元数据
            if (l != length.ToString(CultureInfo.InvariantCulture))
                return false;
            // head/tail 均按「缺失即不参与判定」处理，与 Metadata 的省略语义对称：
            // 缺的那项本就无从比对，据此否决只会把同内容误判成碰撞。
            //
            // 这次放宽的实际风险面（判断爆炸半径时别再重新推一遍）：
            // · tail 缺失只可能来自 format 1 的索引（IndexSerializer 里 tail 是 `format >= 2` 才读的后加项）——
            //   而 format 1 **未投产**，现网不会遇到；
            // · head 在**每个** format 下都是无条件读的，故 head 缺失（→ 上面这条 TryGetValue 落空）
            //   在现网几乎不可达，只有人为构造/损坏的索引才到得了。
            // 也就是说：放宽的是一条现实中打不到的分支，换来的是「Metadata 省略键」与此处判定的语义对称。
            if (meta.TryGetValue("head", out var h) && h != headHash)
                return false;
            return !meta.TryGetValue("tail", out var t) || t == tailHash;
        }
        if (!meta.TryGetValue("v", out var v))
            return true;
        return v == Verifier(fullHash, length, headHash, tailHash);
    }

    private string Verifier(string fullHash, long length, string headHash, string tailHash) =>
        Hex(HMACSHA256.HashData(_key!, Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{fullHash}|{length}|{headHash}|{tailHash}"))));

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
