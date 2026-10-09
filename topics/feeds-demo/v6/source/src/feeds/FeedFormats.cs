using System.Text;
using System.Text.RegularExpressions;

namespace PresidioDemo.Feeds;

// Locations address fields within one message; they are never passenger identities.
public sealed record FeedField(string Id, string Name, string Value);
public sealed record ParsedFeed(IReadOnlyList<FeedField> Fields, object Layout);

public interface IFeedCodec
{
    ParsedFeed Parse(FeedMessage message);
    byte[] Serialize(ParsedFeed original, IReadOnlyDictionary<string, string> anonymizedFields);
    void Validate(ParsedFeed original, ReadOnlyMemory<byte> serialized);
}

public sealed record FeedEnvelope(byte[] Payload, byte[] Original, string Format,
    string Header = "", string Newline = "\n", bool FinalNewline = true, int HexRowBytes = 25,
    string? Ccsid = null)
{
    public byte[] Serialize(byte[] payload)
    {
        if (payload.AsSpan().SequenceEqual(Payload)) return Original.ToArray();
        if (Format == "Raw") return payload;
        if (Format != "MqExport")
            throw new InvalidDataException("Rewriting this diagnostic dump format is not supported.");
        var text = new StringBuilder(Header);
        for (int offset = 0; offset < payload.Length; offset += HexRowBytes)
        {
            text.Append("X ").Append(Convert.ToHexString(payload.AsSpan(offset, Math.Min(HexRowBytes, payload.Length - offset))));
            if (offset + HexRowBytes < payload.Length || FinalNewline) text.Append(Newline);
        }
        return Encoding.UTF8.GetBytes(text.ToString());
    }
}

/// <summary>Separates transport metadata from payload without interpreting proprietary binary records.</summary>
public static class FeedPayloadReader
{
    public static byte[] ReadPayload(FeedMessage message) => Read(message).Payload;
    public static FeedEnvelope Read(FeedMessage message)
    {
        byte[] original = message.Content.ToArray();
        string extension = Path.GetExtension(message.Delivery.OriginalName);
        if (extension.Equals(".bin", StringComparison.OrdinalIgnoreCase)) return new(original, original, "Raw");
        if (!extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsupported feed format. Use an IBM MQ export (.txt) or raw payload (.bin).");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(original); }
        catch (DecoderFallbackException) { throw new InvalidDataException("MQ export text must be UTF-8."); }
        if (text.StartsWith("A VER ", StringComparison.Ordinal)) return ReadExport(text, original);
        if (!text.Contains("AMQSBCG0", StringComparison.Ordinal))
            throw new InvalidDataException("The TXT input must be an IBM MQ diagnostic dump or A/X MQ export.");
        var declared = Regex.Match(text, @"length\s*-\s*(\d+)(?:\s+of\s+(\d+))?\s+bytes");
        if (!declared.Success || !int.TryParse(declared.Groups[2].Success ? declared.Groups[2].Value : declared.Groups[1].Value, out int expected))
            throw new InvalidDataException("MQ dump has no valid declared payload length.");
        using var payload = new MemoryStream();
        foreach (string line in text.Split('\n'))
        {
            var row = Regex.Match(line, @"^([0-9A-Fa-f]{8}):\s+(.*)$");
            if (!row.Success) continue;
            if (Convert.ToUInt32(row.Groups[1].Value, 16) != payload.Length)
                throw new InvalidDataException("MQ dump rows are missing, duplicated or out of order.");
            string[] groups = row.Groups[2].Value.Split('\'')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (groups.Length == 0 || groups.Any(group => !Regex.IsMatch(group, @"^(?:[0-9A-Fa-f]{2}|[0-9A-Fa-f]{4})$")))
                throw new InvalidDataException("MQ dump contains an invalid hexadecimal row.");
            byte[] bytes = Convert.FromHexString(string.Concat(groups));
            if (bytes.Length > 16) throw new InvalidDataException("MQ dump contains an oversized hexadecimal row.");
            payload.Write(bytes);
        }
        if (payload.Length != expected)
            throw new InvalidDataException($"Incomplete MQ dump: {payload.Length} of {expected} payload bytes are present. No output was published.");
        return new(payload.ToArray(), original, "MqDiagnostic");
    }
    private static FeedEnvelope ReadExport(string text, byte[] original)
    {
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (normalized.Contains('\r')) throw new InvalidDataException("Invalid MQ export line endings.");
        string[] lines = normalized.Split('\n');
        bool finalNewline = lines[^1] == "";
        if (finalNewline) lines = lines[..^1];
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        var header = new StringBuilder();
        using var payload = new MemoryStream();
        int rowBytes = 0;
        bool body = false;
        foreach (string line in lines)
        {
            if (line.StartsWith("A ", StringComparison.Ordinal) && !body)
            {
                var match = Regex.Match(line, @"^A ([A-Z0-9]{3}) (.*)$");
                if (!match.Success || !attributes.TryAdd(match.Groups[1].Value, match.Groups[2].Value))
                    throw new InvalidDataException("Duplicate or invalid MQ export metadata; one message per file is required.");
                header.Append(line).Append(newline);
            }
            else if (line.StartsWith("X ", StringComparison.Ordinal))
            {
                body = true;
                string hex = line[2..];
                if (!Regex.IsMatch(hex, @"^(?:[0-9A-Fa-f]{2})+$"))
                    throw new InvalidDataException("Invalid hexadecimal MQ export payload row.");
                byte[] bytes = Convert.FromHexString(hex);
                if (rowBytes == 0) rowBytes = bytes.Length;
                payload.Write(bytes);
            }
            else throw new InvalidDataException("Unexpected MQ export record; one complete message per file is required.");
        }
        if (!body || !attributes.ContainsKey("VER") || !attributes.ContainsKey("MSI") ||
            !attributes.ContainsKey("ENC") || !attributes.ContainsKey("CCS"))
            throw new InvalidDataException("MQ export metadata or payload is missing.");
        return new(payload.ToArray(), original, "MqExport", header.ToString(), newline, finalNewline, rowBytes, attributes["CCS"].Trim());
    }
}

/// <summary>Proprietary records require the producer's schema and serializer.</summary>
public sealed class UnconfiguredBinaryFeedCodec : IFeedCodec
{
    public ParsedFeed Parse(FeedMessage message)
    {
        _ = FeedPayloadReader.ReadPayload(message);
        throw new InvalidDataException("The binary producer schema is not configured. Input retained; no output was published.");
    }
    public byte[] Serialize(ParsedFeed original, IReadOnlyDictionary<string, string> anonymizedFields) =>
        throw new InvalidDataException("The binary serializer is not configured.");
    public void Validate(ParsedFeed original, ReadOnlyMemory<byte> serialized) =>
        throw new InvalidDataException("The binary validator is not configured.");
}
