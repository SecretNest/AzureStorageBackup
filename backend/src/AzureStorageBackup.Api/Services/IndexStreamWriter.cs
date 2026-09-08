using System.Text;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Writes a second-level version index (§3.2) to a <see cref="Stream"/> one entry at a time, never building the
/// whole <see cref="VersionIndex"/> in memory: the SQLite catalog feeds entries in from a query one row at a time,
/// so the index writer needs the same shape. The byte layout is the cloud format, which is a frozen contract, so
/// this and <see cref="IndexSerializer"/> (still the info file's) both call the shared
/// <see cref="IndexEncoding"/> primitives rather than keeping two copies that could drift apart.
/// </summary>
public sealed class IndexStreamWriter(Stream output) : IDisposable
{
    public const byte IndexFormat = 4;

    private readonly BinaryWriter _w = new(output, Encoding.UTF8, leaveOpen: true);
    private int _remaining = -1;

    public void WriteHeader(int version, int entryCount)
    {
        _w.Write(IndexFormat);
        _w.Write(version);
        _w.Write(entryCount);
        _remaining = entryCount;
    }

    public void WriteEntry(IndexEntry e)
    {
        if (_remaining <= 0) throw new InvalidOperationException("More entries than the header announced.");
        _remaining--;

        _w.Write(e.Path);
        _w.Write((byte)(e.Kind == "symlink" ? 1 : 0));
        _w.Write(e.Length);
        IndexEncoding.WriteDto(_w, e.Mtime);
        _w.Write(e.Permissions);
        IndexEncoding.WriteHash(_w, e.HeadHash);
        IndexEncoding.WriteHash(_w, e.TailHash);
        IndexEncoding.WriteHash(_w, e.FullHash);
        IndexEncoding.WriteNullableString(_w, e.Target);
        IndexEncoding.WriteNullableDto(_w, e.UnreadableAt);

        if (e.Storage is { } s)
        {
            _w.Write(true);
            _w.Write((byte)(s.Kind == "pack" ? 1 : 0));
            _w.Write(s.Ref);
            IndexEncoding.WriteNullableString(_w, s.EntryName);
            _w.Write(s.Volumes);
            _w.Write(s.Raw);
            IndexEncoding.WriteLongs(_w, s.VolumeSizes);
        }
        else
        {
            _w.Write(false);
        }
    }

    public void WriteEmptyDirs(IReadOnlyList<string> dirs)
    {
        if (_remaining != 0) throw new InvalidOperationException($"{_remaining} entries still owed.");
        _w.Write(dirs.Count);
        foreach (var d in dirs) _w.Write(d);
    }

    public void WriteUnrecoverable(IReadOnlyList<string> paths)
    {
        _w.Write(paths.Count);
        foreach (var p in paths) _w.Write(p);
    }

    /// <summary>Flushes the underlying <see cref="BinaryWriter"/>; the wrapped <paramref name="output"/> stream is left open.</summary>
    public void Dispose() => _w.Dispose();
}

/// <summary>
/// The wire-encoding primitives shared by <see cref="IndexSerializer"/> and the streaming
/// <see cref="IndexStreamWriter"/>/<see cref="IndexStreamReader"/>, so there is exactly one copy of each rule
/// (nullable string presence bytes, hash compaction, the DateTimeOffset tick+offset layout, ...) instead of two
/// that could quietly diverge.
/// </summary>
internal static class IndexEncoding
{
    public static void WriteNullableString(BinaryWriter w, string? value)
    {
        w.Write(value is not null);
        if (value is not null)
            w.Write(value);
    }

    public static string? ReadNullableString(BinaryReader r) => r.ReadBoolean() ? r.ReadString() : null;

    public static void WriteLongs(BinaryWriter w, IReadOnlyList<long> values)
    {
        w.Write(values.Count);
        foreach (var v in values)
            w.Write(v);
    }

    public static List<long> ReadLongs(BinaryReader r)
    {
        var count = r.ReadInt32();
        var list = new List<long>(count);
        for (var i = 0; i < count; i++)
            list.Add(r.ReadInt64());
        return list;
    }

    public static void WriteDto(BinaryWriter w, DateTimeOffset value)
    {
        w.Write(value.UtcTicks);
        w.Write((short)value.Offset.TotalMinutes);
    }

    public static DateTimeOffset ReadDto(BinaryReader r)
    {
        var utcTicks = r.ReadInt64();
        var offset = TimeSpan.FromMinutes(r.ReadInt16());
        return new DateTimeOffset(utcTicks + offset.Ticks, offset);
    }

    public static void WriteNullableDto(BinaryWriter w, DateTimeOffset? value)
    {
        w.Write(value.HasValue);
        if (value.HasValue)
            WriteDto(w, value.Value);
    }

    public static DateTimeOffset? ReadNullableDto(BinaryReader r) => r.ReadBoolean() ? ReadDto(r) : null;

    // Hash encoding: 0 = null; 1 = the 16 raw bytes of an xxh128; 2 = an arbitrary string (fallback).
    private const string HashPrefix = "xxh128:";

    public static void WriteHash(BinaryWriter w, string? hash)
    {
        if (hash is null)
        {
            w.Write((byte)0);
            return;
        }

        byte[]? raw = null;
        if (hash.StartsWith(HashPrefix, StringComparison.Ordinal))
        {
            var hex = hash[HashPrefix.Length..];
            if (hex.Length == 32)
            {
                try { raw = Convert.FromHexString(hex); }
                catch (FormatException) { /* Not hex, take the fallback */ }
            }
        }

        if (raw is { Length: 16 })
        {
            w.Write((byte)1);
            w.Write(raw);
        }
        else
        {
            w.Write((byte)2);
            w.Write(hash);
        }
    }

    public static string? ReadHash(BinaryReader r) => r.ReadByte() switch
    {
        0 => null,
        1 => HashPrefix + Convert.ToHexString(r.ReadBytes(16)).ToLowerInvariant(),
        _ => r.ReadString(),
    };
}
