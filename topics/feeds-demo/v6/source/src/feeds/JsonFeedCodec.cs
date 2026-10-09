using System.Text;
using System.Text.Json;

namespace PresidioDemo.Feeds;

/// <summary>Edits JSON scalar spans, preserving original bytes outside changed values.</summary>
public sealed class JsonFeedCodec(FeedType feed) : IFeedCodec
{
    private sealed record Scalar(FeedField Field, int Offset, int Length, JsonTokenType Kind);
    private sealed record JsonLayout(FeedMessage Message, FeedEnvelope Envelope, IReadOnlyList<Scalar> Scalars);
    public ParsedFeed Parse(FeedMessage message)
    {
        if (message.Delivery.Feed != feed) throw new InvalidDataException("JSON codec route does not match delivery.");
        var envelope = FeedPayloadReader.Read(message);
        if (envelope.Ccsid is not null && envelope.Ccsid != "1208")
            throw new InvalidDataException("JSON MQ exports require UTF-8 CCSID 1208; no implicit code-page conversion is performed.");
        var scalars = new List<Scalar>();
        try
        {
            var reader = new Utf8JsonReader(envelope.Payload, new JsonReaderOptions { MaxDepth = 64 });
            if (!reader.Read() || (feed == FeedType.Seats ? reader.TokenType != JsonTokenType.StartObject : reader.TokenType != JsonTokenType.StartArray))
                throw new InvalidDataException("Unexpected JSON root for this feed.");
            ReadValue(ref reader, "", "", scalars);
            if (reader.Read()) throw new InvalidDataException("JSON feed has trailing data.");
        }
        catch (JsonException) { throw new InvalidDataException("Incomplete or malformed JSON payload; the complete message is retained without output."); }
        if (scalars.Count == 0) throw new InvalidDataException("JSON feed contains no configured scalar fields.");
        return new(scalars.Select(s => s.Field).ToArray(), new JsonLayout(message, envelope, scalars));
    }
    private static string Escape(string name) => name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    private static void ReadValue(ref Utf8JsonReader reader, string id, string rule, List<Scalar> fields)
    {
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw new InvalidDataException("Invalid JSON object.");
                string name = reader.GetString()!;
                if (!names.Add(name) || name == "*") throw new InvalidDataException("Duplicate or ambiguous JSON property.");
                if (!reader.Read()) throw new InvalidDataException("Incomplete JSON object.");
                ReadValue(ref reader, id + "/" + Escape(name), rule + "/" + Escape(name), fields);
            }
            if (reader.TokenType != JsonTokenType.EndObject) throw new InvalidDataException("Incomplete JSON object.");
        }
        else if (reader.TokenType == JsonTokenType.StartArray)
        {
            int index = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                ReadValue(ref reader, id + "/" + index++, rule + "/*", fields);
            if (reader.TokenType != JsonTokenType.EndArray) throw new InvalidDataException("Incomplete JSON array.");
        }
        else if (reader.TokenType is JsonTokenType.String or JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null)
        {
            string value = reader.TokenType == JsonTokenType.String ? reader.GetString()! : reader.TokenType == JsonTokenType.Null ? "" : Encoding.UTF8.GetString(reader.ValueSpan);
            fields.Add(new(new(id, rule, value), checked((int)reader.TokenStartIndex), checked((int)(reader.BytesConsumed - reader.TokenStartIndex)), reader.TokenType));
        }
        else throw new InvalidDataException("Unsupported JSON token.");
    }
    public byte[] Serialize(ParsedFeed original, IReadOnlyDictionary<string, string> anonymizedFields)
    {
        var layout = (JsonLayout)original.Layout;
        if (anonymizedFields.Count != layout.Scalars.Count) throw new InvalidDataException("Protected field set does not match parsed fields.");
        using var output = new MemoryStream();
        int cursor = 0;
        foreach (var scalar in layout.Scalars)
        {
            if (!anonymizedFields.TryGetValue(scalar.Field.Id, out string? value)) throw new InvalidDataException("Protected field is missing.");
            output.Write(layout.Envelope.Payload.AsSpan(cursor, scalar.Offset - cursor));
            if (value == scalar.Field.Value) output.Write(layout.Envelope.Payload.AsSpan(scalar.Offset, scalar.Length));
            else
            {
                if (scalar.Kind != JsonTokenType.String) throw new InvalidDataException("Non-string JSON fields must remain unchanged.");
                output.Write(JsonSerializer.SerializeToUtf8Bytes(value));
            }
            cursor = scalar.Offset + scalar.Length;
        }
        output.Write(layout.Envelope.Payload.AsSpan(cursor));
        return layout.Envelope.Serialize(output.ToArray());
    }
    public void Validate(ParsedFeed original, ReadOnlyMemory<byte> serialized)
    {
        var layout = (JsonLayout)original.Layout;
        var check = Parse(layout.Message with { Content = serialized });
        var after = (JsonLayout)check.Layout;
        if (after.Scalars.Count != layout.Scalars.Count || after.Envelope.Header != layout.Envelope.Header)
            throw new InvalidDataException("Serialized JSON structure or MQ metadata changed.");
        for (int i = 0; i < layout.Scalars.Count; i++)
        {
            var before = layout.Scalars[i];
            var next = after.Scalars[i];
            if (before.Field.Id != next.Field.Id || before.Field.Name != next.Field.Name || before.Kind != next.Kind ||
                (FieldPolicy.IsPnr(before.Field.Name) && before.Field.Value != next.Field.Value))
                throw new InvalidDataException("Serialized field locations, types, or PNR preservation failed validation.");
        }
    }
}

public static class FeedCodecRegistry
{
    public static IFeedCodec Create(FeedType feed) => feed is FeedType.Seats or FeedType.PnrLinking
        ? new JsonFeedCodec(feed) : new UnconfiguredBinaryFeedCodec();
}
