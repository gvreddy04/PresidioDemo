using System.Text;
using System.Text.RegularExpressions;

namespace PresidioDemo.Feeds;

// Field IDs address locations within one parsed file; they are not passenger identity tokens.
public sealed record FeedField(string Id, string Name, string Value);
public sealed record ParsedFeed(IReadOnlyList<FeedField> Fields, object Layout);

/// <summary>A producer-specific codec must consume the entire payload and retain its binary layout.</summary>
public interface IBinaryFeedCodec
{
    ParsedFeed Parse(FeedMessage message);
    byte[] Rebuild(ParsedFeed original, IReadOnlyDictionary<string, string> anonymizedFields);
    void Validate(ParsedFeed original, ReadOnlyMemory<byte> rebuilt);
}

/// <summary>Read raw binary or recover bytes from a single IBM MQ diagnostic dump.</summary>
public static class BinaryFeedInput
{
    public static byte[] ReadPayload(FeedMessage message)
    {
        string extension = Path.GetExtension(message.Delivery.OriginalName);
        if (extension.Equals(".bin", StringComparison.OrdinalIgnoreCase)) return message.Content.ToArray();
        if (!extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsupported feed format. Only IBM MQ dumps (.txt) and binary payloads (.bin) are accepted.");
        string text = new UTF8Encoding(false, true).GetString(message.Content.Span);
        if (!text.Contains("AMQSBCG0", StringComparison.Ordinal))
            throw new InvalidDataException("The TXT input must be an IBM MQ diagnostic dump.");
        var declared = Regex.Match(text, @"length\s*-\s*(\d+)(?:\s+of\s+(\d+))?\s+bytes");
        if (!declared.Success) throw new InvalidDataException("MQ dump has no declared payload length.");
        int expected = int.Parse(declared.Groups[2].Success ? declared.Groups[2].Value : declared.Groups[1].Value);
        using var payload = new MemoryStream();
        foreach (string line in text.Split('\n'))
        {
            var row = Regex.Match(line, @"^([0-9A-Fa-f]{8}):\s+(.*)$");
            if (!row.Success) continue;
            int offset = Convert.ToInt32(row.Groups[1].Value, 16);
            if (offset != payload.Length) throw new InvalidDataException("MQ dump rows are missing, duplicated or out of order.");
            string hex = row.Groups[2].Value.Split('\'')[0];
            string[] groups = hex.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (groups.Length == 0 || groups.Any(group => !Regex.IsMatch(group, @"^(?:[0-9A-Fa-f]{2}|[0-9A-Fa-f]{4})$")))
                throw new InvalidDataException("MQ dump contains an invalid hexadecimal row.");
            byte[] bytes = Convert.FromHexString(string.Concat(groups));
            if (bytes.Length > 16) throw new InvalidDataException("MQ dump contains an oversized hexadecimal row.");
            payload.Write(bytes);
        }
        if (payload.Length != expected)
            throw new InvalidDataException($"Incomplete MQ dump: {payload.Length} of {expected} payload bytes are present. No output was published.");
        return payload.ToArray();
    }
}

/// <summary>Fail closed until the producer's complete binary schema and codec are supplied.</summary>
public sealed class UnconfiguredBinaryFeedCodec : IBinaryFeedCodec
{
    public ParsedFeed Parse(FeedMessage message)
    {
        _ = BinaryFeedInput.ReadPayload(message);
        throw new InvalidDataException("The binary producer schema is not configured. Input retained; no output was published.");
    }
    public byte[] Rebuild(ParsedFeed original, IReadOnlyDictionary<string, string> anonymizedFields) =>
        throw new InvalidDataException("The binary serializer is not configured.");
    public void Validate(ParsedFeed original, ReadOnlyMemory<byte> rebuilt) =>
        throw new InvalidDataException("The binary validator is not configured.");
}