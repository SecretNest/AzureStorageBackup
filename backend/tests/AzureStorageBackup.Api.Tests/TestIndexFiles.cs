using System.Buffers.Binary;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// A throwaway <see cref="VersionIndexFileStore"/> per call site, plus the writer that store no longer has. A
/// shared directory would let one test's leftover <c>.idx</c> file answer another test's migration probe.
/// <para>
/// All of them sit under one per-process directory that is removed on exit. Unlike the in-memory SQLite connections
/// <c>TestLocalAuthority</c> leaves to the process (see the note there), directories outlive the process, so leaving
/// one behind per test would quietly fill the machine's temp space over a few hundred runs.
/// </para>
/// </summary>
internal static class TestIndexFiles
{
    private static readonly string Root =
        Path.Combine(Path.GetTempPath(), "asb-idxcache-tests-" + Environment.ProcessId);

    static TestIndexFiles() =>
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort; it is temp space */ }
        };

    internal static VersionIndexFileStore New() => new(Path.Combine(Root, Guid.NewGuid().ToString("N")));

    /// <summary>
    /// Lay down one <c>.idx</c> file in the format <see cref="VersionIndexFileStore.OpenBodyAsync"/> reads: a
    /// 24-byte header (magic, format, identity, body length) followed by the body verbatim.
    /// <para>
    /// The product stopped writing these when version indexes moved into the SQLite catalog — the file store is a
    /// reader now, kept only so that a file an older build left behind still migrates. That is precisely what has
    /// to stay tested, and it cannot be tested without something able to produce the old build's output; so the
    /// writer lives here, beside <see cref="LegacyIndexSerializer"/>, for the same reason. Deliberately not atomic
    /// (no temp-then-rename): the production concern that motivated that went away with the production writer.
    /// </para>
    /// </summary>
    internal static async Task WriteAsync(
        VersionIndexFileStore files, int accountId, string container, int version, long identityTicks, byte[] body,
        CancellationToken ct = default)
    {
        var path = files.PathFor(accountId, container, version);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var header = new byte[24];
        "ASBI"u8.CopyTo(header.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8, 8), identityTicks);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16, 8), body.LongLength);

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
    }
}
