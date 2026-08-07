using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// data blob 的存储寻址方案。加密备份用**密钥化**地址防止指纹识别：
/// 未授权者即使能列出 container，也无法用公开 hash 反推「是否备份过某已知文件」。
/// <para>
/// 加密（有密码 + kdfSalt）：key = HKDF(password, kdfSalt)；地址 = data/{HMAC(key, fullHash)[:16]}；
/// 碰撞检测元数据为不透明 v = HMAC(key, fullHash|len|head|tail)，不泄露长度/头部指纹；
/// head/tail 未知（修复老索引条目）时退为窄校验值 v1 = HMAC(key, "v1"|fullHash|len)，同样不泄露长度。
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

    /// <summary>
    /// 这套寻址方案的身份指纹，用来在恢复时判定"journal 是不是同一把钥匙写的"。
    /// 换了密码 / 换了 KDF 盐，地址空间就变了，旧 journal 里的引用全都对不上，必须整卷作废。
    /// 从已派生的密钥再 HMAC 一次，泄露的信息不比现有寻址方案更多。
    /// </summary>
    public string Identity => _key is null
        ? "plain"
        : Convert.ToHexString(HMACSHA256.HashData(
            _key, "asb-journal-identity"u8.ToArray()))[..16].ToLowerInvariant();

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
    /// <para>
    /// 非密钥化下省略是无痛的：剩下的键（尤其 len）照样参与判定。密钥化下省不掉单项（v 四项合一），
    /// 故改发只覆盖已知项的窄校验值 v1，避免防护整体归零。
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
        // 密钥化时 v 是 fullHash|len|head|tail 合一的不透明值，无法只省其中一项。但整体省略 v 会让
        // 该对象**一点**防护都不剩（MetadataMatches 见不到 v 就无条件放行），比非密钥化分支还弱——
        // 那边至少还留着 len。故任一项未知时改发窄校验值 v1 = HMAC(key, "v1"|fullHash|len)：
        // 只覆盖确实已知的两项，让长度继续参与判定，又不像明文 len 那样泄露长度指纹
        // （密钥化的初衷就是防止旁观者按长度做内容指纹，见类注释）。
        // 全新备份四项永远齐全，只发 v、不发 v1——写出的元数据一字不变。
        return headHash is null || tailHash is null
            ? new Dictionary<string, string> { ["v1"] = NarrowVerifier(fullHash, length) }
            : new Dictionary<string, string> { ["v"] = Verifier(fullHash, length, headHash, tailHash) };
    }

    /// <summary>
    /// 判断某个 blob 的元数据是否代表这份内容（老 blob 缺某项 → 该项不参与判定，向后兼容）。
    /// <para>
    /// **备份路径上没有调用者**，去重不看云端元数据。留着它是因为那些键是真真切切写在云上的
    /// 持久数据，删掉读取端会让写入端（<see cref="Metadata"/>）那一套「省略而非空串」的讲究
    /// 失去解释的落点；人工排查、以及将来任何要从云端反推内容身份的活，判据都在这里。
    /// </para>
    /// </summary>
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
            // · head 缺失不再只是人为构造/损坏索引才会出现——修复(BackupRepairer)对 HeadHash 为 null 的
            //   老条目也会发布不带 head 键的对象。但爆炸半径已经窄到没有了：去重**不再读云端元数据**，
            //   一律走本地权威的 LocalDedupResolver.ResolveAsync，按 ContentKey（fullHash+len+head+tail
            //   精确字符串比对）判定。缺字段的条目在那里天然配不上任何键，而且并非只是「绕过了检查」：
            //   该老条目的 ContentKey 形如 `hash\nlen\n\n`（LocalDedupResolver.ContentKey），仍会占住
            //   _priorRefs 里的基址，把同内容的新引用挤到 …~1 并标 collision:true。
            if (meta.TryGetValue("head", out var h) && h != headHash)
                return false;
            return !meta.TryGetValue("tail", out var t) || t == tailHash;
        }
        // 顺序即优先级，且必须保持向后兼容：
        // · 带 v 的对象（含全部历史对象）照旧只按 v 判定，判定结果与本改动前逐字节相同；
        // · 只带 v1 的对象（修复老索引条目时写出）退而按「fullHash+长度」判定——比无条件放行强；
        // · 两个都没有的才是真正的老 blob，仍按「无元数据 → 不参与判定」放行，不新增拒绝面。
        if (meta.TryGetValue("v", out var v))
            return v == Verifier(fullHash, length, headHash, tailHash);
        if (meta.TryGetValue("v1", out var v1))
            return v1 == NarrowVerifier(fullHash, length);
        return true; // 老 blob 无元数据
    }

    private string Verifier(string fullHash, long length, string headHash, string tailHash) =>
        Hex(HMACSHA256.HashData(_key!, Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{fullHash}|{length}|{headHash}|{tailHash}"))));

    /// <summary>
    /// 窄校验值：只覆盖 fullHash + 长度，用于 head/tail 未知（老索引条目）时。
    /// 前缀 "v1|" 做域分隔，保证它与四项版 <see cref="Verifier"/> 的输入串不可能相等——
    /// 否则理论上可拿一个域的值冒充另一个域（fullHash 由 FileHasher 产出，形如 xxh128:…，永不以 "v1|" 开头，
    /// 但域分隔不该依赖调用方的取值习惯）。
    /// </summary>
    private string NarrowVerifier(string fullHash, long length) =>
        Hex(HMACSHA256.HashData(_key!, Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"v1|{fullHash}|{length}"))));

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
