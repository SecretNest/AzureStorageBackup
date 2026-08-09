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
        Assert.Equal("data/" + Hash, s.DataAddress(Hash)); // plaintext addressing
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
        Assert.NotEqual("data/" + Hash, addr);              // not the plaintext hash
        Assert.DoesNotContain(Hash, addr);                  // the public hash does not appear in the address
        var meta = s.Metadata(Hash, 42, "xxh128:aa", "xxh128:zz");
        Assert.False(meta.ContainsKey("len"));              // no length leaked
        Assert.False(meta.ContainsKey("head"));             // no head fingerprint leaked
        Assert.False(meta.ContainsKey("tail"));             // no tail fingerprint leaked
        Assert.True(meta.ContainsKey("v"));                 // only the opaque verifier
    }

    [Fact]
    public void Keyed_Address_Is_Deterministic_And_Salt_Sensitive()
    {
        var a = new BlobAddressScheme("pw", Salt).DataAddress(Hash);
        var again = new BlobAddressScheme("pw", Salt).DataAddress(Hash);
        var otherSalt = new BlobAddressScheme("pw", RandomNumberGenerator.GetBytes(16)).DataAddress(Hash);
        var otherPw = new BlobAddressScheme("pw2", Salt).DataAddress(Hash);

        Assert.Equal(a, again);        // same (password, salt, hash) → same address (usable for dedup)
        Assert.NotEqual(a, otherSalt); // different salt → different address
        Assert.NotEqual(a, otherPw);   // different password → different address
    }

    [Fact]
    public void Metadata_Matches_Only_Same_Content()
    {
        foreach (var s in new[] { new BlobAddressScheme(null, null), new BlobAddressScheme("pw", Salt) })
        {
            var meta = new Dictionary<string, string>(s.Metadata(Hash, 100, "xxh128:bb", "xxh128:dd"));
            Assert.True(s.MetadataMatches(meta, Hash, 100, "xxh128:bb", "xxh128:dd"));   // same content
            Assert.False(s.MetadataMatches(meta, Hash, 101, "xxh128:bb", "xxh128:dd"));  // different length → collision
            Assert.False(s.MetadataMatches(meta, Hash, 100, "xxh128:cc", "xxh128:dd"));  // different head → collision
            Assert.False(s.MetadataMatches(meta, Hash, 100, "xxh128:bb", "xxh128:ee"));  // different tail → collision
        }
    }

    [Fact]
    public void Missing_Metadata_Treated_As_Match()
    {
        var s = new BlobAddressScheme("pw", Salt);
        Assert.True(s.MetadataMatches(new Dictionary<string, string>(), Hash, 1, "xxh128:aa", "xxh128:bb"));
    }

    /// <summary>
    /// F4: when an old index entry is missing head/tail (the repairer passes the nulls straight through), the corresponding key must be **omitted**
    /// rather than written as an empty string. <see cref="BlobAddressScheme.MetadataMatches"/> treats "key absent" as not taking part in the decision and
    /// "key present but different" as a collision — an empty string would make identical content look like a collision.
    /// </summary>
    [Fact]
    public void Unknown_Head_Or_Tail_Is_Omitted_Not_Blanked()
    {
        var s = new BlobAddressScheme(null, null);

        var noTail = s.Metadata(Hash, 42, "xxh128:aa", null);
        Assert.Equal("42", noTail["len"]);
        Assert.Equal("xxh128:aa", noTail["head"]);
        Assert.False(noTail.ContainsKey("tail")); // an empty string would turn the check below into a "collision"

        var noHead = s.Metadata(Hash, 42, null, "xxh128:zz");
        Assert.False(noHead.ContainsKey("head"));
        Assert.Equal("xxh128:zz", noHead["tail"]);
    }

    /// <summary>
    /// The practical consequence of F4: a blob written with metadata missing tail must later be judged identical content when dedup comes back with the real tail.
    /// Had Metadata written tail="", this would return false, and the identical content would be rewritten to a ~N fallback address with a false report of "collision avoided".
    /// </summary>
    [Fact]
    public void Omitted_Tail_Does_Not_Look_Like_A_Collision()
    {
        var s = new BlobAddressScheme(null, null);
        var written = new Dictionary<string, string>(s.Metadata(Hash, 42, "xxh128:aa", null));

        Assert.True(s.MetadataMatches(written, Hash, 42, "xxh128:aa", "xxh128:real-tail")); // same content
        Assert.False(s.MetadataMatches(written, Hash, 43, "xxh128:aa", "xxh128:real-tail")); // length still works

        var noHead = new Dictionary<string, string>(s.Metadata(Hash, 42, null, "xxh128:zz"));
        Assert.True(s.MetadataMatches(noHead, Hash, 42, "xxh128:real-head", "xxh128:zz"));
        Assert.False(s.MetadataMatches(noHead, Hash, 42, "xxh128:real-head", "xxh128:other")); // tail still works
    }

    /// <summary>
    /// When keyed, v is one opaque value fusing all four items and none of them can be dropped: if any item is unknown, no v is written, and instead
    /// the narrow verifier v1 covering only fullHash+length goes out. Plaintext head/tail/len are never written (that length fingerprint is exactly what keying defends against).
    /// <para>Before the fix: this returned an empty dictionary, leaving the object with not one bit of collision protection.</para>
    /// </summary>
    [Fact]
    public void Keyed_Falls_Back_To_A_Narrow_Verifier_When_Head_Or_Tail_Unknown()
    {
        var s = new BlobAddressScheme("pw", Salt);

        foreach (var meta in new[] { s.Metadata(Hash, 42, "xxh128:aa", null), s.Metadata(Hash, 42, null, "xxh128:zz") })
        {
            Assert.Equal(new[] { "v1" }, meta.Keys.Order());  // only the narrow verifier: no v, and no plaintext item either
            Assert.NotEmpty(meta["v1"]);
        }
        // With both items unknown it still sends v1 (which item is missing does not change what it covers).
        Assert.Equal(new[] { "v1" }, s.Metadata(Hash, 42, null, null).Keys.Order());
    }

    /// <summary>
    /// The keyed metadata a fresh backup writes (all four items present) must be unchanged to the letter: v only, with a value byte for byte identical to before this change.
    /// The expected value is computed independently from v's definition, HMAC(key, "fullHash|len|head|tail") — it is not copied out of the code under test,
    /// so the moment <see cref="BlobAddressScheme.Metadata"/> changes v's input string (which would make every existing object mismatch) this goes red.
    /// </summary>
    [Fact]
    public void Keyed_Fresh_Backup_Metadata_Is_Byte_For_Byte_Unchanged()
    {
        var salt = new byte[16]; // fixed salt → fixed key → the expected value can be recomputed
        var s = new BlobAddressScheme("pw", salt);
        var key = HKDF.DeriveKey(
            HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes("pw"), outputLength: 32,
            salt: salt, info: "asb-blob-address"u8.ToArray());
        var expected = Convert.ToHexString(HMACSHA256.HashData(
            key, Encoding.UTF8.GetBytes($"{Hash}|42|xxh128:aa|xxh128:zz"))).ToLowerInvariant();

        var meta = s.Metadata(Hash, 42, "xxh128:aa", "xxh128:zz");

        Assert.Equal(new[] { "v" }, meta.Keys.Order()); // not one extra key (no v1 above all)
        Assert.Equal(expected, meta["v"]);
    }

    /// <summary>
    /// Backward compatibility: existing objects that already carry v are still decided by v **alone** — introducing v1 changes none of their verdicts.
    /// A v1 is stuffed in by hand (existing objects will not have one, but this proves that when v is present it is not even glanced at).
    /// </summary>
    [Fact]
    public void Keyed_Existing_Verifier_Still_Wins_Over_The_Narrow_One()
    {
        var s = new BlobAddressScheme("pw", Salt);
        var meta = new Dictionary<string, string>(s.Metadata(Hash, 100, "xxh128:bb", "xxh128:dd"));
        meta["v1"] = "deadbeef"; // a narrow verifier that is bound to mismatch

        Assert.True(s.MetadataMatches(meta, Hash, 100, "xxh128:bb", "xxh128:dd"));  // v says same content → let through
        Assert.False(s.MetadataMatches(meta, Hash, 100, "xxh128:bb", "xxh128:ee")); // v says collision → reject

        // The reverse: v mismatching while v1 matches must still be decided by v (otherwise v1 becomes a back door around the four-item check).
        var forged = new Dictionary<string, string>
        {
            ["v"] = "deadbeef",
            ["v1"] = new Dictionary<string, string>(s.Metadata(Hash, 100, null, null))["v1"],
        };
        Assert.False(s.MetadataMatches(forged, Hash, 100, "xxh128:bb", "xxh128:dd"));
    }

    /// <summary>
    /// Backward compatibility: a genuinely old blob with neither v nor v1 is still let through as "no metadata → does not take part in the decision", adding no new rejection surface.
    /// </summary>
    [Fact]
    public void Keyed_Legacy_Object_With_Neither_Verifier_Still_Participates_As_Before()
    {
        var s = new BlobAddressScheme("pw", Salt);

        Assert.True(s.MetadataMatches(new Dictionary<string, string>(), Hash, 1, "xxh128:aa", "xxh128:bb"));
        // An old object carrying only irrelevant keys (such as the raw marker) is let through as well.
        Assert.True(s.MetadataMatches(
            new Dictionary<string, string> { ["raw"] = "1" }, Hash, 1, "xxh128:aa", "xxh128:bb"));
    }

    /// <summary>
    /// What v1 actually buys: for an object written with metadata missing head/tail, the length still takes part in the decision.
    /// <para>Before the fix this wrote an empty dictionary and returned true even for a different length — collision protection collapsed to fullHash alone.</para>
    /// </summary>
    [Fact]
    public void Keyed_Narrow_Verifier_Keeps_Length_In_The_Decision()
    {
        var s = new BlobAddressScheme("pw", Salt);
        var written = new Dictionary<string, string>(s.Metadata(Hash, 42, "xxh128:aa", null));

        // head/tail do not take part (they were unknown anyway), so identical content is let through as before — identical content is never misjudged as a collision.
        Assert.True(s.MetadataMatches(written, Hash, 42, "xxh128:aa", "xxh128:real-tail"));
        Assert.True(s.MetadataMatches(written, Hash, 42, "xxh128:whatever", "xxh128:anything"));
        // A different length means the content is necessarily different → a collision, and must be rejected.
        Assert.False(s.MetadataMatches(written, Hash, 43, "xxh128:aa", "xxh128:real-tail"));
        // A different fullHash is rejected too (v1 covers the two items fullHash+length).
        Assert.False(s.MetadataMatches(written, "xxh128:ffffffffffffffffffffffffffffffff", 42, "xxh128:aa", "xxh128:zz"));
    }

    /// <summary>
    /// v1 is keyed: a v1 computed with a different password or a different salt does not match, exactly like v (it is not a plaintext length a bystander can recompute).
    /// </summary>
    [Fact]
    public void Keyed_Narrow_Verifier_Is_Key_Bound_And_Not_A_Plaintext_Length()
    {
        var written = new Dictionary<string, string>(new BlobAddressScheme("pw", Salt).Metadata(Hash, 42, null, null));

        // It is a fixed-length HMAC-SHA256 digest, not a length/hash a bystander can read straight off.
        // (Deliberately no DoesNotContain("42"): the digest is random hex and will occasionally really contain the substring "42", so that assertion would go red at random.)
        Assert.Equal(64, written["v1"].Length);
        Assert.True(written["v1"].All(char.IsAsciiHexDigitLower));
        Assert.DoesNotContain(Hash, written["v1"], StringComparison.Ordinal);
        Assert.NotEqual(
            written["v1"],
            new Dictionary<string, string>(new BlobAddressScheme("pw2", Salt).Metadata(Hash, 42, null, null))["v1"]);
        Assert.NotEqual(
            written["v1"],
            new Dictionary<string, string>(
                new BlobAddressScheme("pw", RandomNumberGenerator.GetBytes(16)).Metadata(Hash, 42, null, null))["v1"]);
    }

    /// <summary>The metadata a fresh backup writes is untouched by F4: with head/tail present, all three keys are there.</summary>
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
