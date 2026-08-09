using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Storage addressing scheme for data blobs. Encrypted backups use **keyed** addresses to defeat fingerprinting:
/// even someone able to list the container cannot use a public hash to work out "was this known file ever backed up".
/// <para>
/// Encrypted (password + kdfSalt present): key = HKDF(password, kdfSalt); address = data/{HMAC(key, fullHash)[:16]};
/// the collision-detection metadata is an opaque v = HMAC(key, fullHash|len|head|tail), leaking neither the length nor a head fingerprint;
/// when head/tail are unknown (repairing an old index entry) it falls back to the narrow verifier v1 = HMAC(key, "v1"|fullHash|len), which likewise leaks no length.
/// Unencrypted: plaintext data/{fullHash} plus len/head metadata (nothing is being hidden there anyway).
/// </para>
/// Restore/check/cleanup only use the actual addresses already stored in the index and need no key; this scheme is only used when a backup creates a blob.
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

    /// <summary>Whether addressing is keyed (an encrypted backup that has a salt).</summary>
    public bool Keyed => _key is not null;

    /// <summary>
    /// Identity fingerprint of this addressing scheme, used on resume to decide "was this journal written with the same key".
    /// Change the password or the KDF salt and the address space changes: every ref in the old journal misses, so the whole volume has to be voided.
    /// It is one more HMAC over the already-derived key, so it leaks no more than the addressing scheme itself already does.
    /// </summary>
    public string Identity => _key is null
        ? "plain"
        : Convert.ToHexString(HMACSHA256.HashData(
            _key, "asb-journal-identity"u8.ToArray()))[..16].ToLowerInvariant();

    /// <summary>Base name of a data blob (without the ~N collision suffix or the volume suffix).</summary>
    public string DataAddress(string fullHash) => _key is null
        ? "data/" + fullHash
        : "data/" + Hex(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(fullHash)).AsSpan(0, 16));

    /// <summary>
    /// Collision-detection metadata written on upload (head/tail segment hashes included, to catch the residual collisions where the content differs but fullHash+length agree).
    /// <para>
    /// A null <paramref name="headHash"/>/<paramref name="tailHash"/> means "that item is unknown" — an old index entry
    /// may be missing those two fields (a fresh backup's BuildEntries always fills them in, so this only shows up when repairing an old backup). The affected key must then be **omitted**
    /// rather than written as an empty string: <see cref="MetadataMatches"/> treats "key absent" as not taking part in the decision and "key present
    /// but different" as a collision, so an empty string would make identical content look like a collision, divert it to a ~N fallback address and falsely report "collision avoided".
    /// </para>
    /// <para>
    /// Omitting is painless when unkeyed: the remaining keys (len above all) still take part in the decision. When keyed a single item cannot be omitted (v fuses all four),
    /// so we send the narrow verifier v1 covering only the known items instead, rather than letting the protection drop to zero.
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
        // When keyed, v is one opaque value fusing fullHash|len|head|tail, so no single item can be dropped from it. But omitting v altogether would leave
        // the object with **not one bit** of protection (MetadataMatches lets anything through when it sees no v), weaker than the unkeyed branch —
        // that one at least keeps len. So when any item is unknown we send the narrow verifier v1 = HMAC(key, "v1"|fullHash|len) instead:
        // it covers only the two items we really know, keeps the length in the decision, and unlike a plaintext len it leaks no length fingerprint
        // (the whole point of keying is to stop a bystander fingerprinting content by length, see the class comment).
        // A fresh backup always has all four items, so it sends v and never v1 — the metadata it writes is unchanged to the letter.
        return headHash is null || tailHash is null
            ? new Dictionary<string, string> { ["v1"] = NarrowVerifier(fullHash, length) }
            : new Dictionary<string, string> { ["v"] = Verifier(fullHash, length, headHash, tailHash) };
    }

    /// <summary>
    /// Decides whether a blob's metadata stands for this content (an old blob missing an item → that item does not take part in the decision, for backward compatibility).
    /// <para>
    /// **Nothing on the backup path calls this** — dedup does not look at cloud metadata. It is kept because those keys are genuine
    /// persistent data really written out to the cloud, and deleting the reading side would leave the writing side's (<see cref="Metadata"/>) care over "omit, never blank"
    /// with nothing to explain it; manual investigation, and any future work that has to derive content identity from the cloud, find their criteria here.
    /// </para>
    /// </summary>
    public bool MetadataMatches(IDictionary<string, string> meta, string fullHash, long length, string headHash, string tailHash)
    {
        if (_key is null)
        {
            if (!meta.TryGetValue("len", out var l))
                return true; // an old blob with no metadata
            if (l != length.ToString(CultureInfo.InvariantCulture))
                return false;
            // head/tail are both handled as "absent means it does not take part in the decision", symmetric with Metadata's omission semantics:
            // there is nothing to compare a missing item against, and rejecting on that basis would only misjudge identical content as a collision.
            //
            // The real risk surface of this relaxation (do not re-derive the blast radius next time):
            // · a missing tail can only come from a format 1 index (in IndexSerializer, tail is a later addition read only when `format >= 2`) —
            //   and format 1 **never shipped**, so it is not encountered in the field;
            // · a missing head is no longer something only a hand-crafted/corrupted index produces — repair (BackupRepairer) also publishes
            //   objects without a head key for old entries whose HeadHash is null. But the blast radius has narrowed to nothing: dedup **no longer reads cloud metadata**,
            //   it always goes through the local-authority LocalDedupResolver.ResolveAsync and decides by ContentKey (exact string comparison of
            //   fullHash+len+head+tail). An entry with missing fields naturally matches no key there, and it is not merely "slipping past the check":
            //   that old entry's ContentKey looks like `hash\nlen\n\n` (LocalDedupResolver.ContentKey), so it still occupies the base address in
            //   _priorRefs, pushing a new ref for the same content out to …~1 and flagging it collision:true.
            if (meta.TryGetValue("head", out var h) && h != headHash)
                return false;
            return !meta.TryGetValue("tail", out var t) || t == tailHash;
        }
        // The order is the priority, and it has to stay backward compatible:
        // · objects carrying v (which is every historical object) are still decided by v alone, byte for byte the same verdict as before this change;
        // · objects carrying only v1 (written when repairing an old index entry) fall back to deciding on "fullHash+length" — better than letting anything through;
        // · only objects with neither are genuinely old blobs, still let through as "no metadata → does not take part", adding no new rejection surface.
        if (meta.TryGetValue("v", out var v))
            return v == Verifier(fullHash, length, headHash, tailHash);
        if (meta.TryGetValue("v1", out var v1))
            return v1 == NarrowVerifier(fullHash, length);
        return true; // an old blob with no metadata
    }

    private string Verifier(string fullHash, long length, string headHash, string tailHash) =>
        Hex(HMACSHA256.HashData(_key!, Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{fullHash}|{length}|{headHash}|{tailHash}"))));

    /// <summary>
    /// Narrow verifier: covers only fullHash + length, for when head/tail are unknown (an old index entry).
    /// The "v1|" prefix is domain separation, guaranteeing its input string can never equal that of the four-item <see cref="Verifier"/> —
    /// otherwise a value from one domain could in theory pass for the other (fullHash comes from FileHasher, shaped like xxh128:…, and never starts with "v1|",
    /// but domain separation should not lean on the caller's habits about values).
    /// </summary>
    private string NarrowVerifier(string fullHash, long length) =>
        Hex(HMACSHA256.HashData(_key!, Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"v1|{fullHash}|{length}"))));

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
