using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PresidioDemo.Feeds;

public sealed record ParsedFeed(JsonObject Document, bool HasUtf8Bom);

/// <summary>JSON is the current demo feed contract. Airline binary schemas must be supplied.</summary>
public static class FeedFileParser
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    public static ParsedFeed Parse(FeedMessage message)
    {
        string extension = Path.GetExtension(message.Delivery.OriginalName);
        if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)) ValidateMqDump(message.Content.Span);
            throw new InvalidDataException("Binary feed layout is not configured. Supply the producer schema before parsing or publishing.");
        }
        var bytes = message.Content.Span;
        bool bom = bytes.StartsWith(Bom);
        if (bom) bytes = bytes[3..];
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 256 });
        CheckDuplicateProperties(document.RootElement);
        var root = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 256 });
        return new(root as JsonObject ?? throw new InvalidDataException("Feed JSON must be an object."), bom);
    }

    public static byte[] Serialize(JsonObject document, bool bom)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions { WriteIndented = true, MaxDepth = 256 });
        return bom ? [.. Bom, .. body] : body;
    }

    private static void CheckDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new InvalidDataException("Duplicate JSON properties are not supported.");
                CheckDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckDuplicateProperties(item);
    }

    private static void ValidateMqDump(ReadOnlySpan<byte> bytes)
    {
        string text = new UTF8Encoding(false, true).GetString(bytes);
        if (!text.Contains("AMQSBCG0", StringComparison.Ordinal))
            throw new InvalidDataException("Only JSON and IBM MQ diagnostic dump inputs are recognized by this demo.");
        var declared = Regex.Match(text, @"length\s*-\s*(\d+)\s+of\s+(\d+)\s+bytes");
        if (!declared.Success) throw new InvalidDataException("MQ dump has no declared payload length.");
        int expected = int.Parse(declared.Groups[2].Value);
        int captured = 0;
        foreach (var line in text.Split('\n'))
        {
            var row = Regex.Match(line, @"^([0-9A-Fa-f]{8}):\s+((?:[0-9A-Fa-f]{4}\s*)+)");
            if (!row.Success) continue;
            int offset = Convert.ToInt32(row.Groups[1].Value, 16);
            if (offset != captured) throw new InvalidDataException("MQ dump rows are missing, duplicated or out of order.");
            captured = checked(captured + Regex.Replace(row.Groups[2].Value, @"\s", "").Length / 2);
        }
        if (captured != expected)
            throw new InvalidDataException($"Incomplete MQ dump: {captured} of {expected} payload bytes are present. No output was published.");
        throw new InvalidDataException("MQ dump bytes are complete, but its binary schema is not configured. No output was published.");
    }
}