using System.IO.Compression;

namespace QueueJanne.Core.Bodies;

/// <summary>A body as shown: decompressed when it was GZip or zlib/Deflate compressed, else the bytes as received.</summary>
/// <param name="Encoding">"gzip" or "deflate" when the body was decompressed, else null.</param>
public sealed record DecodedBody(byte[] Bytes, string? Encoding)
{
    public bool IsDecompressed => Encoding is not null;
}

/// <summary>Recognises compressed bodies by their header bytes and decompresses them. Never throws on malformed input.</summary>
public static class BodyDecoder
{
    /// <summary>Decompressed bodies larger than this are cut off, so a hostile message can't exhaust memory.</summary>
    public const int MaxDecodedBytes = 16 * 1024 * 1024;

    public static DecodedBody Decode(byte[] body)
    {
        if (IsGzip(body) && TryDecompress(body, static s => new GZipStream(s, CompressionMode.Decompress), out var gzip))
        {
            return new DecodedBody(gzip, "gzip");
        }

        if (IsZlib(body) && TryDecompress(body, static s => new ZLibStream(s, CompressionMode.Decompress), out var zlib))
        {
            return new DecodedBody(zlib, "deflate");
        }

        return new DecodedBody(body, null);
    }

    private static bool IsGzip(ReadOnlySpan<byte> body) => body.Length > 18 && body[0] == 0x1F && body[1] == 0x8B && body[2] == 8;

    // A zlib header is CM=8 (deflate) with a check value that makes the first two bytes a multiple of 31.
    private static bool IsZlib(ReadOnlySpan<byte> body) =>
        body.Length > 6 && (body[0] & 0x0F) == 8 && (body[0] >> 4) <= 7 && ((body[0] << 8) | body[1]) % 31 == 0;

    private static bool TryDecompress(byte[] body, Func<Stream, Stream> open, out byte[] result)
    {
        try
        {
            using var input = open(new MemoryStream(body, writable: false));
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, (int)Math.Min(read, MaxDecodedBytes - output.Length));
                if (output.Length >= MaxDecodedBytes)
                {
                    break;
                }
            }

            result = output.ToArray();
            return true;
        }
        catch (InvalidDataException)
        {
            result = body;
            return false;
        }
    }
}
