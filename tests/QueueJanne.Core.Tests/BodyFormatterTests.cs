using System.Text;
using QueueJanne.Core.Bodies;

namespace QueueJanne.Core.Tests;

public class BodyFormatterTests
{
    [Fact]
    public void Detects_json_from_content_even_without_content_type()
    {
        var body = Encoding.UTF8.GetBytes("""{"orderId":"ORD-1","amount":129.50}""");

        Assert.Equal(BodyFormat.Json, BodyFormatter.Detect(body, contentType: null));
    }

    [Fact]
    public void Pretty_prints_json_and_keeps_numbers_as_sent()
    {
        var body = Encoding.UTF8.GetBytes("""{"amount":129.50,"name":"Åsa"}""");

        var formatted = BodyFormatter.Format(body, BodyFormat.Json);

        Assert.Contains("\"amount\": 129.50", formatted);
        Assert.Contains("\"name\": \"Åsa\"", formatted);
        Assert.Contains(Environment.NewLine, formatted);
    }

    [Fact]
    public void Falls_back_to_text_when_json_content_type_lies()
    {
        var body = Encoding.UTF8.GetBytes("not json at all");

        Assert.Equal(BodyFormat.Text, BodyFormatter.Detect(body, "application/json"));
    }

    [Fact]
    public void Pretty_prints_xml()
    {
        var body = Encoding.UTF8.GetBytes("<invoice id=\"1\"><amount>10</amount></invoice>");

        Assert.Equal(BodyFormat.Xml, BodyFormatter.Detect(body, null));
        Assert.Contains("  <amount>10</amount>", BodyFormatter.Format(body, BodyFormat.Xml));
    }

    [Fact]
    public void Decodes_utf16_with_byte_order_mark()
    {
        var body = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("hej Janne")).ToArray();

        Assert.True(BodyFormatter.LooksLikeText(body));
        Assert.Equal("hej Janne", BodyFormatter.DecodeText(body));
    }

    [Fact]
    public void Treats_binary_as_hex()
    {
        byte[] body = [0x1F, 0x8B, 0x08, 0x00, 0x00, 0x01, 0x02, 0xFF];

        Assert.Equal(BodyFormat.Hex, BodyFormatter.Detect(body, null));
        Assert.StartsWith("00000000  1F 8B 08 00 00 01 02 FF", BodyFormatter.HexDump(body));
        Assert.Equal("[8 bytes of binary data]", BodyFormatter.Preview(body));
    }

    [Fact]
    public void Preview_collapses_whitespace_and_truncates()
    {
        var body = Encoding.UTF8.GetBytes("{\n  \"a\": 1,\n  \"b\": \"" + new string('x', 400) + "\"\n}");

        var preview = BodyFormatter.Preview(body, maxChars: 40);

        Assert.Equal(41, preview.Length);
        Assert.StartsWith("{ \"a\": 1, \"b\":", preview);
        Assert.EndsWith("…", preview);
    }
}
