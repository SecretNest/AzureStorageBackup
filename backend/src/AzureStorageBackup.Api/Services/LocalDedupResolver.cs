using System.Collections.Concurrent;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>已存在的单文件 data blob：实际存储名 + 是否原始字节 + 分卷数。</summary>
public sealed record ResolvedBlob(string Ref, bool Raw, int Volumes);

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

    private LocalDedupResolver(
        BlobAddressScheme addressing,
        IReadOnlyDictionary<string, ResolvedBlob> priorByContent,
        IReadOnlyDictionary<string, string> priorRefs)
    {
        _addressing = addressing;
        _priorByContent = priorByContent;
        _priorRefs = priorRefs;
    }

    /// <summary>从保留版本的第二级索引构建映射（仅单文件 blob 条目参与内容寻址去重）。</summary>
    public static LocalDedupResolver Build(BlobAddressScheme addressing, IEnumerable<VersionIndex> indexes)
    {
        var byContent = new Dictionary<string, ResolvedBlob>(StringComparer.Ordinal);
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var index in indexes)
        {
            foreach (var e in index.Entries)
            {
                if (e.Storage is not { Kind: "blob" } s || e.FullHash is null)
                    continue;
                var ck = ContentKey(e.FullHash, e.Length, e.HeadHash, e.TailHash);
                byContent[ck] = new ResolvedBlob(s.Ref, s.Raw, Math.Max(1, s.Volumes));
                refs[s.Ref] = ck;
            }
        }
        return new LocalDedupResolver(addressing, byContent, refs);
    }

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
                    new ResolvedBlob(refName, false, 1), collision);
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
        public void Complete(string refName, bool raw, int volumes) => _tcs.TrySetResult(new ResolvedBlob(refName, raw, volumes));
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
        public void Complete(bool raw, int volumes) => _reservation?.Complete(Ref, raw, volumes);

        /// <summary>上传失败时调用，令等待的后到者一并失败（不会错误去重到不存在的 blob）。</summary>
        public void Fail(Exception ex) => _reservation?.Fail(ex);
    }
}
