using System.Buffers.Binary;
using System.IO.Compression;

namespace Factory;

internal static class RemoteGitBundle
{
    private const int MaxExpandedBytes = 128 * 1024 * 1024;
    private const int MaxObjects = 100_000;

    internal static void Validate(byte[] bundle, CancellationToken ct, int maxExpandedBytes = MaxExpandedBytes)
    {
        // The blank line terminates the bundle header. Git validates refs and the pack checksum on import.
        var header = bundle.AsSpan(0, Math.Min(bundle.Length, 65536));
        var end = header.IndexOf("\n\n"u8);
        if ((!header.StartsWith("# v2 git bundle\n"u8) &&
             !header.StartsWith("# v3 git bundle\n@object-format=sha1\n"u8)) || end < 0)
            throw InvalidBundle();
        using var input = new PackStream(bundle) { Position = end + 2 };
        Span<byte> pack = stackalloc byte[12];
        input.ReadExactly(pack);
        if (!pack[..4].SequenceEqual("PACK"u8) || BinaryPrimitives.ReadUInt32BigEndian(pack[4..]) is not (2 or 3))
            throw InvalidBundle();
        var objects = BinaryPrimitives.ReadUInt32BigEndian(pack[8..]);
        if (objects is 0 or > MaxObjects) throw InvalidBundle();
        long inflated = 0, expanded = 0;
        var buffer = new byte[8192];
        for (var i = 0; i < objects; i++)
        {
            ct.ThrowIfCancellationRequested();
            var first = ReadByte(input);
            var type = (first >> 4) & 7;
            var size = ReadSize(input, first, first & 15, 4);
            if (size > maxExpandedBytes - inflated) throw InvalidBundle();
            inflated += size;
            if (type == 6)
            {
                var offsetLength = 0;
                while ((ReadByte(input) & 128) != 0)
                    if (++offsetLength >= 8) throw InvalidBundle();
            }
            else if (type == 7) input.Position += 20;
            else if (type is < 1 or > 4) throw InvalidBundle();

            using var objectStream = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true);
            var remaining = size;
            var expandedSize = size;
            if (type is 6 or 7)
            {
                var baseSize = ReadDeltaSize(objectStream, ref remaining);
                expandedSize = ReadDeltaSize(objectStream, ref remaining);
                if (baseSize > maxExpandedBytes) throw InvalidBundle();
            }
            if (expandedSize > maxExpandedBytes - expanded) throw InvalidBundle();
            expanded += expandedSize;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                var length = (int)Math.Min(remaining, buffer.Length);
                objectStream.ReadExactly(buffer.AsSpan(0, length));
                remaining -= length;
            }
            if (objectStream.ReadByte() != -1) throw InvalidBundle();
        }
        if (input.Length - input.Position != 20) throw InvalidBundle();
    }

    private static long ReadDeltaSize(Stream stream, ref long remaining)
    {
        long size = 0;
        for (var shift = 0; shift <= 28 && remaining > 0; shift += 7)
        {
            var value = ReadByte(stream);
            remaining--;
            size |= (long)(value & 127) << shift;
            if ((value & 128) == 0) return size;
        }
        throw InvalidBundle();
    }

    private static long ReadSize(Stream stream, int current, long size, int shift)
    {
        while ((current & 128) != 0)
        {
            if (shift > 32) throw InvalidBundle();
            current = ReadByte(stream);
            size |= (long)(current & 127) << shift;
            shift += 7;
        }
        return size;
    }

    private static int ReadByte(Stream stream)
    {
        var value = stream.ReadByte();
        return value >= 0 ? value : throw InvalidBundle();
    }

    private static InvalidDataException InvalidBundle() =>
        new("Executor Git bundle is malformed or exceeds the expanded object limit");

    // Pack entries use separate zlib streams. Limit read-ahead so each decoder leaves the next entry unread.
    private sealed class PackStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 1)]);
    }
}
