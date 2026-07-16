using System.Security.Cryptography;
using System.Text;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class FileHasherTests : IDisposable
{
    private readonly string _dir;

    public FileHasherTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "asb-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Write(string name, byte[] content)
    {
        var full = Path.Combine(_dir, name);
        File.WriteAllBytes(full, content);
        return full;
    }

    private static string Sha256Hex(byte[] data) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    [Fact]
    public async Task FullHash_Is_Sha256_Of_Whole_File()
    {
        var content = Encoding.UTF8.GetBytes("some content to hash");
        var path = Write("a.bin", content);

        var hash = await new FileHasher().FullHashAsync(path);

        Assert.Equal(Sha256Hex(content), hash);
    }

    [Fact]
    public async Task HeadHash_Covers_Only_First_N_Bytes()
    {
        var head = new byte[8];
        for (var i = 0; i < head.Length; i++) head[i] = (byte)i;
        var a = Write("a.bin", head.Concat(new byte[] { 1, 1, 1 }).ToArray());
        var b = Write("b.bin", head.Concat(new byte[] { 2, 2, 2 }).ToArray());

        var hasher = new FileHasher();
        var headA = await hasher.HeadHashAsync(a, 8);
        var headB = await hasher.HeadHashAsync(b, 8);

        Assert.Equal(headA, headB);
        Assert.Equal(Sha256Hex(head), headA);
        Assert.NotEqual(await hasher.FullHashAsync(a), await hasher.FullHashAsync(b));
    }

    [Fact]
    public async Task HeadHash_Equals_FullHash_When_File_Smaller_Than_Window()
    {
        var path = Write("tiny.bin", Encoding.UTF8.GetBytes("tiny"));
        var hasher = new FileHasher();

        Assert.Equal(await hasher.FullHashAsync(path), await hasher.HeadHashAsync(path, 4096));
    }
}
