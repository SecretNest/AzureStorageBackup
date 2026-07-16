using System.Text;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

[Trait("Category", "Integration")]
public sealed class SevenZipArchiveCodecTests
{
    private static readonly string? Exe = SevenZipArchiveCodec.TryResolveExecutable();

    private static SevenZipArchiveCodec Codec()
    {
        Skip.If(Exe is null, "7z executable not found on PATH.");
        return new SevenZipArchiveCodec(Exe);
    }

    private static byte[] Payload() =>
        Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"secret\":\"TOP-SECRET-MARKER\"}");

    [SkippableFact]
    public async Task Plain_RoundTrips_Without_Password()
    {
        var codec = Codec();
        var content = Payload();

        var archive = await codec.EncodeAsync(content, password: null);
        var back = await codec.DecodeAsync(archive, password: null);

        Assert.Equal(content, back);
    }

    [SkippableFact]
    public async Task Encrypted_RoundTrips_With_Password()
    {
        var codec = Codec();
        var content = Payload();

        var archive = await codec.EncodeAsync(content, password: "corr3ct h0rse!");
        var back = await codec.DecodeAsync(archive, password: "corr3ct h0rse!");

        Assert.Equal(content, back);
    }

    [SkippableFact]
    public async Task Encoded_Output_Differs_From_Raw_Content()
    {
        var codec = Codec();
        var content = Payload();

        var archive = await codec.EncodeAsync(content, password: null);

        Assert.NotEqual(content, archive);
    }

    [SkippableFact]
    public async Task Encrypted_Archive_Does_Not_Leak_Plaintext()
    {
        var codec = Codec();

        var archive = await codec.EncodeAsync(Payload(), password: "pw");

        var asText = Encoding.Latin1.GetString(archive);
        Assert.DoesNotContain("TOP-SECRET-MARKER", asText);
    }

    [SkippableFact]
    public async Task Decode_With_Wrong_Password_Fails()
    {
        var codec = Codec();

        var archive = await codec.EncodeAsync(Payload(), password: "right");

        await Assert.ThrowsAnyAsync<Exception>(() => codec.DecodeAsync(archive, password: "wrong"));
    }

    [SkippableFact]
    public async Task Decode_Encrypted_Without_Password_Fails()
    {
        var codec = Codec();

        var archive = await codec.EncodeAsync(Payload(), password: "pw");

        await Assert.ThrowsAnyAsync<Exception>(() => codec.DecodeAsync(archive, password: null));
    }
}
