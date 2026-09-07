using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// <see cref="RangeStream"/> is what lets one volume of a split index be uploaded straight off the encoded file
/// without slicing it into a byte array first. The Azure SDK treats it as an ordinary seekable stream — it reads
/// <c>Length</c>, and on a retry it seeks back to where it started — so the window has to look like a whole stream
/// from the outside, which is exactly what these pin.
/// </summary>
public sealed class RangeStreamTests
{
    private static byte[] Bytes(int n) => [.. Enumerable.Range(0, n).Select(i => (byte)i)];

    private static RangeStream Over(byte[] data, long offset, long length)
        => new(new MemoryStream(data, writable: false), offset, length);

    [Fact]
    public void Reads_exactly_the_range_and_stops()
    {
        using var s = Over(Bytes(100), 10, 20);
        var buffer = new MemoryStream();
        s.CopyTo(buffer);

        Assert.Equal(Bytes(100)[10..30], buffer.ToArray());
        // A read past the end returns 0 rather than spilling into the bytes that follow the window.
        Assert.Equal(0, s.Read(new byte[8], 0, 8));
    }

    [Fact]
    public void Length_and_Position_are_relative_to_the_range()
    {
        using var s = Over(Bytes(100), 40, 25);

        Assert.Equal(25, s.Length);
        Assert.Equal(0, s.Position);

        Assert.Equal(5, s.Read(new byte[5], 0, 5));
        Assert.Equal(5, s.Position);

        // Seeking is in the window's own coordinates: 0 is the first byte of the window, not of the file.
        s.Seek(0, SeekOrigin.Begin);
        Assert.Equal(0, s.Position);
        Assert.Equal(40, s.ReadByte());

        s.Seek(-1, SeekOrigin.End);
        Assert.Equal(24, s.Position);
        Assert.Equal(64, s.ReadByte());
    }

    /// <summary>
    /// Seeking past the end clamps to the end (the next read returns 0), the same as landing there by reading;
    /// seeking before the start throws, because a negative position is a caller bug and silently reading someone
    /// else's volume would be worse than an exception.
    /// </summary>
    [Fact]
    public void Seeking_outside_the_range_clamps_at_the_end_and_throws_at_the_start()
    {
        using var s = Over(Bytes(100), 10, 20);

        s.Seek(1000, SeekOrigin.Begin);
        Assert.Equal(20, s.Position);
        Assert.Equal(0, s.Read(new byte[4], 0, 4));

        Assert.Throws<IOException>(() => s.Seek(-1, SeekOrigin.Begin));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Position = -1);
    }
}
