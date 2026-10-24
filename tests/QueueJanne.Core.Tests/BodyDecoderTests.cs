using System.IO.Compression;
using System.Text;
using QueueJanne.Core.Bodies;

namespace QueueJanne.Core.Tests;

public class BodyDecoderTests
{
    private static readonly byte[] Json = Encoding.UTF8.GetBytes("""{"orderId":"ORD-1","lines":[1,2,3]}""");

    [Fact]
    public void Decompresses_gzip()
    {
        var decoded = BodyDecoder.Decode(Compress(Json, s => new GZipStream(s, CompressionLevel.Optimal)));

        Assert.Equal("gzip", decoded.Encoding);
        Assert.Equal(Json, decoded.Bytes);
        Assert.Equal(BodyFormat.Json, BodyFormatter.Detect(decoded.Bytes, null));
    }

    [Fact]
    public void Decompresses_zlib_deflate()
    {
        var decoded = BodyDecoder.Decode(Compress(Json, s => new ZLibStream(s, CompressionLevel.Optimal)));

        Assert.Equal("deflate", decoded.Encoding);
        Assert.Equal(Json, decoded.Bytes);
    }

    [Fact]
    public void Leaves_plain_and_broken_bodies_alone()
    {
        Assert.Same(Json, BodyDecoder.Decode(Json).Bytes);

        // A gzip header followed by garbage stays as received.
        byte[] broken = [0x1F, 0x8B, 0x08, 0x00, 0x00, 0x01, 0x02, 0xFF, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
        var decoded = BodyDecoder.Decode(broken);
        Assert.Null(decoded.Encoding);
        Assert.Same(broken, decoded.Bytes);
    }

    [Fact]
    public void Caps_the_decompressed_size()
    {
        var huge = new byte[BodyDecoder.MaxDecodedBytes + 1_000_000];

        var decoded = BodyDecoder.Decode(Compress(huge, s => new GZipStream(s, CompressionLevel.Fastest)));

        Assert.Equal(BodyDecoder.MaxDecodedBytes, decoded.Bytes.Length);
    }

    private static byte[] Compress(byte[] data, Func<Stream, Stream> open)
    {
        using var output = new MemoryStream();
        using (var stream = open(output))
        {
            stream.Write(data);
        }

        return output.ToArray();
    }
}
