using System.Collections.Concurrent;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>已存在的单文件 data blob：实际存储名 + 是否原始字节 + 分卷数 + 各分卷尺寸。</summary>
public sealed record ResolvedBlob(string Ref, bool Raw, int Volumes, IReadOnlyList<long> VolumeSizes);

/// <summary>
/// 某个既有 pack 里的一个成员。新条目指向它即完成去重——不压、不传、不装箱。
/// <para>
/// <paramref name="EntryName"/> 是**最初存进去时**那个路径（归档内的成员名），与现在引用它的
/// 路径可以不同：内容一样、路径不同，正是要去重的那种情形。还原按这个名字从归档里取成员，
/// 写到索引条目自己的 Path 上。
/// </para>
/// </summary>
public sealed record PackMemberRef(string PackId, string EntryName, string? TailHash);

/// <summary>
/// 纯本地的单文件 blob 去重/碰撞解析（不读云端）。自建备份的本地缓存索引已含每个 blob 的
/// 内容身份（fullHash+长度+head+tail）与存储信息，故去重、碰撞避让、分卷数、raw 均可从本地判定。
/// <para>
/// 跨版本：查保留版本索引建的「内容身份 → 既有 blob」映射。
/// 同一次备份内：用运行内预约表（每个 ref 一个 <see cref="TaskCompletionSource{T}"/>）协调——
/// 同内容的后到者等首个上传者完成后拿到同一 (ref, raw, 分卷数)，不同内容撞同址则避让到 …~N。
/// </para>
/// 信任本地索引＝云端真相（与"尽量不读云端"设计一致）；云端被外部改动由检查(Check)负责发现。
/// </summary>
public sealed class LocalDedupResolver
{
    private readonly BlobAddressScheme _addressing;
    private readonly IReadOnlyDictionary<string, ResolvedBlob> _priorByContent; // 内容身份 → 既有 blob（跨版本）
    private readonly IReadOnlyDictionary<string, string> _priorRefs;            // 已占用 ref → 其内容身份（碰撞避让）
    private readonly ConcurrentDictionary<string, Reservation> _run = new(StringComparer.Ordinal);
    private readonly IReadOnlySet<string> _priorHeads;                                  // 预筛：既有内容的 "长度\nhead"
    private readonly ConcurrentDictionary<string, byte> _runHeads = new(StringComparer.Ordinal); // 预筛：本轮已开工的
    // 打包成员的内容身份（三项）→ 它躺在哪个包的哪个成员上。见 TryFindPackMember 上的说明。
    private readonly IReadOnlyDictionary<string, PackMemberRef> _packMembers;

    private LocalDedupResolver(
        BlobAddressScheme addressing,
        IReadOnlyDictionary<string, ResolvedBlob> priorByContent,
        IReadOnlyDictionary<string, string> priorRefs,
        IReadOnlySet<string> priorHeads,
        IReadOnlyDictionary<string, PackMemberRef> packMembers)
    {
        _addressing = addressing;
        _priorByContent = priorByContent;
        _priorRefs = priorRefs;
        _priorHeads = priorHeads;
        _packMembers = packMembers;
    }

    /// <summary>
    /// 本轮**可能**存在同内容的既有 blob 吗？只看长度 + head hash，因此不必读完整个文件就能回答。
    /// <para>
    /// 流式压缩要压完才知道全文 hash，"先算全文 hash 再判去重"等于把文件多读一遍。这个预筛让
    /// 首次备份（一个候选都没有）直接走一遍读的快路径，只有真有候选时才付那一遍。
    /// 宁可误报也绝不漏报：误报只是多读一遍，漏报会让一份本可以整个跳过的内容被白压一遍。
    /// </para>
    /// </summary>
    public bool MayDeduplicate(long length, string headHash)
    {
        var key = HeadKey(length, headHash);
        return _priorHeads.Contains(key) || _runHeads.ContainsKey(key);
    }

    /// <summary>登记本轮已开工的内容（长度 + head）：后到的同内容文件据此走预筛慢路径，
    /// 先查一次而不是白压一遍。</summary>
    public void NoteInFlight(long length, string headHash) => _runHeads.TryAdd(HeadKey(length, headHash), 0);

    /// <summary>只查跨版本映射，**不**占用 ref、不产生预约。给"先探一次、命中就完全不压"的预筛路径用；
    /// 真要上传时仍须走 <see cref="ResolveAsync"/> 拿预约。</summary>
    public ResolvedBlob? TryFindExisting(string fullHash, long length, string headHash, string tailHash) =>
        _priorByContent.GetValueOrDefault(ContentKey(fullHash, length, headHash, tailHash));

    private static string HeadKey(long length, string headHash) => $"{length}\n{headHash}";

    /// <summary>从保留版本的第二级索引构建映射（单文件 blob 走内容寻址；pack 成员另建一张表，见下）。</summary>
    public static LocalDedupResolver Build(BlobAddressScheme addressing, IEnumerable<VersionIndex> indexes)
    {
        var byContent = new Dictionary<string, ResolvedBlob>(StringComparer.Ordinal);
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        var heads = new HashSet<string>(StringComparer.Ordinal);
        var packMembers = new Dictionary<string, PackMemberRef>(StringComparer.Ordinal);
        foreach (var index in indexes)
        {
            foreach (var e in index.Entries)
            {
                if (e.FullHash is null)
                    continue;

                // 打包成员：内容已经躺在某个既有 pack 里，新文件同内容时直接指过去，不必再装一箱。
                // 同一箱内的重复本来就被 7z 的 solid 归档消掉了（字典跨成员匹配），真正省下来的是
                // **跨箱、跨版本**那部分——不同箱之间压缩不共享字典，同一份内容会实打实地存两遍。
                if (e.Storage is { Kind: "pack" } p)
                {
                    if (e.HeadHash is not null)
                    {
                        // 多个保留版本可能各有一条同内容成员。**指向**取最先遇到的（版本从旧到新
                        // 传入）：引用聚到老包上，它就更不容易在死重压实里被重写。
                        // 取最先遇到的（版本从旧到新传入）：引用聚到老包上，它就更不容易在
                        // 死重压实里被重写。同内容的更新版本条目指向的是同一份内容，谁都行。
                        packMembers.TryAdd(
                            PackMemberKey(e.FullHash, e.Length, e.HeadHash),
                            new PackMemberRef(p.Ref, p.EntryName ?? e.Path, e.TailHash));
                    }
                    continue;
                }

                if (e.Storage is not { Kind: "blob" } s)
                    continue;
                var ck = ContentKey(e.FullHash, e.Length, e.HeadHash, e.TailHash);
                byContent[ck] = new ResolvedBlob(s.Ref, s.Raw, Math.Max(1, s.Volumes), s.VolumeSizes);
                refs[s.Ref] = ck;
                // HeadHash 为 null 的老条目不进预筛集：它们的内容身份里 head 也是 null，
                // 和任何一个算得出 head 的新文件都对不上，本来就不可能命中去重。
                if (e.HeadHash is not null)
                    heads.Add(HeadKey(e.Length, e.HeadHash));
            }
        }
        return new LocalDedupResolver(addressing, byContent, refs, heads, packMembers);
    }

    /// <summary>
    /// 这份内容是不是已经在某个既有 pack 里。命中即可让新条目直接指过去——不压、不传、不装箱。
    /// <para>
    /// 判据与单文件 blob 那条路**一致**：fullHash + 长度 + head + tail 四项全等。两条路各有一套
    /// 标准是说不通的——同样是"这份内容已经有了"的判断，同样是判错就让索引指向别人的内容、
    /// 还原时出来错误数据。
    /// </para>
    /// <para>
    /// 四项**严格**相等，缺失也算不等。曾经放宽成"两边都有才比"，为的是让老索引里那些没有尾部
    /// 的打包成员也能参与去重；那个放宽已经撤掉——判据要么是四项要么不是，为兼容开个口子，
    /// 等于在最不该含糊的地方（"这份内容是不是同一份"）留了一档说不清的语义。
    /// 新写的条目都带着尾部，老条目不参与去重而已，代价只是它们那份内容会被再存一次。
    /// </para>
    /// </summary>
    public PackMemberRef? TryFindPackMember(string fullHash, long length, string headHash, string? tailHash) =>
        _packMembers.GetValueOrDefault(PackMemberKey(fullHash, length, headHash)) is { } member
        && member.TailHash == tailHash
            ? member
            : null;

    private static string PackMemberKey(string fullHash, long length, string head) =>
        $"{fullHash}\n{length}\n{head}";

    /// <summary>解析某内容：命中既有 → 去重；否则占一个空 ref 由调用方上传，完成后回填 (raw, 分卷数)。</summary>
    public async Task<Resolution> ResolveAsync(string fullHash, long length, string headHash, string tailHash)
    {
        var ck = ContentKey(fullHash, length, headHash, tailHash);
        if (_priorByContent.TryGetValue(ck, out var prior))
            return Resolution.ForExisting(prior, collision: false); // 跨版本去重

        var baseAddr = _addressing.DataAddress(fullHash);
        for (var n = 0; ; n++)
        {
            var refName = n == 0 ? baseAddr : $"{baseAddr}~{n}";
            var collision = n > 0;

            if (_priorRefs.TryGetValue(refName, out var priorCk))
            {
                if (priorCk != ck)
                    continue;                                     // 旧版本不同内容占此址 → 避让
                return Resolution.ForExisting(                     // 理论上已被 _priorByContent 命中，稳妥兜底
                    new ResolvedBlob(refName, false, 1, []), collision);
            }

            var mine = new Reservation(ck);
            if (_run.TryAdd(refName, mine))
                return Resolution.ForClaim(refName, collision, mine); // 由我上传

            var held = _run[refName];
            if (held.ContentKey == ck)
                return Resolution.ForExisting(await held.Completion, collision); // 同批同内容 → 等首个上传者
            // 同批不同内容占此址 → 避让到下一个
        }
    }

    private static string ContentKey(string fullHash, long length, string? head, string? tail) =>
        $"{fullHash}\n{length}\n{head}\n{tail}";

    /// <summary>运行内某 ref 的预约：内容身份 + 上传完成信号。</summary>
    internal sealed class Reservation(string contentKey)
    {
        private readonly TaskCompletionSource<ResolvedBlob> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ContentKey => contentKey;
        public Task<ResolvedBlob> Completion => _tcs.Task;
        public void Complete(string refName, bool raw, int volumes, IReadOnlyList<long> volumeSizes) =>
            _tcs.TrySetResult(new ResolvedBlob(refName, raw, volumes, volumeSizes));
        public void Fail(Exception ex) => _tcs.TrySetException(ex);
    }

    /// <summary>解析结果：去重命中(Exists) 或 需上传的占位(Claim)。</summary>
    public sealed class Resolution
    {
        private readonly Reservation? _reservation;

        private Resolution(string @ref, bool collision, bool exists, ResolvedBlob? existing, Reservation? reservation)
        {
            Ref = @ref;
            Collision = collision;
            Exists = exists;
            Existing = existing;
            _reservation = reservation;
        }

        public string Ref { get; }
        public bool Collision { get; }
        public bool Exists { get; }
        public ResolvedBlob? Existing { get; }

        internal static Resolution ForExisting(ResolvedBlob blob, bool collision) =>
            new(blob.Ref, collision, exists: true, blob, null);

        internal static Resolution ForClaim(string @ref, bool collision, Reservation reservation) =>
            new(@ref, collision, exists: false, null, reservation);

        /// <summary>上传成功后调用，供同批同内容的后到者拿到相同存储信息。</summary>
        public void Complete(bool raw, int volumes, IReadOnlyList<long> volumeSizes) =>
            _reservation?.Complete(Ref, raw, volumes, volumeSizes);

        /// <summary>上传失败时调用，令等待的后到者一并失败（不会错误去重到不存在的 blob）。</summary>
        public void Fail(Exception ex) => _reservation?.Fail(ex);
    }
}
