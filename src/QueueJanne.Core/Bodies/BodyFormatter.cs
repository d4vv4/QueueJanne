using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Xml;
using System.Xml.Linq;

namespace QueueJanne.Core.Bodies;

public enum BodyFormat
{
    Text,
    Json,
    Xml,
    Hex,
}

/// <summary>Detects and formats message bodies for display. Never throws on malformed input.</summary>
public static class BodyFormatter
{
    private const int MaxHexBytes = 256 * 1024;

    private static readonly JsonWriterOptions IndentedJson = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private static ReadOnlySpan<byte> Utf16LeBom => [0xFF, 0xFE];

    private static ReadOnlySpan<byte> Utf16BeBom => [0xFE, 0xFF];

    public static BodyFormat Detect(ReadOnlySpan<byte> body, string? contentType)
    {
        if (body.IsEmpty)
        {
            return BodyFormat.Text;
        }

        if (!LooksLikeText(body))
        {
            return BodyFormat.Hex;
        }

        var type = contentType?.ToLowerInvariant() ?? string.Empty;
        var text = DecodeText(body);
        var start = text.AsSpan().TrimStart();
        if (type.Contains("json", StringComparison.Ordinal) || (!start.IsEmpty && start[0] is '{' or '['))
        {
            return TryFormatJson(text, out _) ? BodyFormat.Json : BodyFormat.Text;
        }

        if (type.Contains("xml", StringComparison.Ordinal) || (!start.IsEmpty && start[0] == '<'))
        {
            return TryFormatXml(text, out _) ? BodyFormat.Xml : BodyFormat.Text;
        }

        return BodyFormat.Text;
    }

    public static string Format(ReadOnlySpan<byte> body, BodyFormat format)
    {
        if (format == BodyFormat.Hex)
        {
            return HexDump(body);
        }

        var text = DecodeText(body);
        return format switch
        {
            BodyFormat.Json when TryFormatJson(text, out var json) => json,
            BodyFormat.Xml when TryFormatXml(text, out var xml) => xml,
            _ => text,
        };
    }

    public static string DecodeText(ReadOnlySpan<byte> body)
    {
        if (body.StartsWith(Utf8Bom))
        {
            return Encoding.UTF8.GetString(body[3..]);
        }

        if (body.StartsWith(Utf16LeBom))
        {
            return Encoding.Unicode.GetString(body[2..]);
        }

        if (body.StartsWith(Utf16BeBom))
        {
            return Encoding.BigEndianUnicode.GetString(body[2..]);
        }

        return Encoding.UTF8.GetString(body);
    }

    public static bool LooksLikeText(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty || body.StartsWith(Utf8Bom) || body.StartsWith(Utf16LeBom) || body.StartsWith(Utf16BeBom))
        {
            return true;
        }

        if (!Utf8.IsValid(body))
        {
            return false;
        }

        var sample = body[..Math.Min(body.Length, 8192)];
        var controls = 0;
        foreach (var b in sample)
        {
            if (b == 0)
            {
                return false;
            }

            if (b < 0x20 && b is not (byte)'\t' and not (byte)'\n' and not (byte)'\r')
            {
                controls++;
            }
        }

        return controls * 50 < sample.Length;
    }

    public static string HexDump(ReadOnlySpan<byte> body)
    {
        var length = Math.Min(body.Length, MaxHexBytes);
        var builder = new StringBuilder((length * 4) + 64);
        for (var offset = 0; offset < length; offset += 16)
        {
            var line = body.Slice(offset, Math.Min(16, length - offset));
            builder.Append(offset.ToString("X8")).Append("  ");
            for (var i = 0; i < 16; i++)
            {
                builder.Append(i < line.Length ? line[i].ToString("X2") + " " : "   ");
                if (i == 7)
                {
                    builder.Append(' ');
                }
            }

            builder.Append(" |");
            foreach (var b in line)
            {
                builder.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }

            builder.Append('|').AppendLine();
        }

        if (body.Length > length)
        {
            builder.Append($"… {body.Length - length:N0} more bytes not shown");
        }

        return builder.ToString();
    }

    /// <summary>One line of text for list views.</summary>
    public static string Preview(ReadOnlySpan<byte> body, int maxChars = 160)
    {
        if (body.IsEmpty)
        {
            return string.Empty;
        }

        if (!LooksLikeText(body))
        {
            return $"[{body.Length:N0} bytes of binary data]";
        }

        var text = DecodeText(body.Length > maxChars * 4 ? body[..(maxChars * 4)] : body);
        var builder = new StringBuilder(Math.Min(text.Length, maxChars + 1));
        var lastWasSpace = false;
        foreach (var c in text)
        {
            if (builder.Length >= maxChars)
            {
                return builder.Append('…').ToString();
            }

            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
            }
            else
            {
                builder.Append(c);
                lastWasSpace = false;
            }
        }

        return builder.ToString().TrimEnd();
    }

    public static bool TryFormatJson(string text, out string formatted)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, IndentedJson))
            {
                document.WriteTo(writer);
            }

            formatted = Encoding.UTF8.GetString(stream.ToArray());
            return true;
        }
        catch (JsonException)
        {
            formatted = text;
            return false;
        }
    }

    public static bool TryFormatXml(string text, out string formatted)
    {
        try
        {
            var document = XDocument.Parse(text, LoadOptions.None);
            formatted = document.Declaration is null
                ? document.ToString()
                : document.Declaration + System.Environment.NewLine + document;
            return true;
        }
        catch (XmlException)
        {
            formatted = text;
            return false;
        }
    }
}
