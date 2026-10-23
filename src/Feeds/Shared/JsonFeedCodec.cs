using System.Text.Json;
using System.Text;
using PresidioDemo.Protection;

namespace PresidioDemo.Feeds.Shared;

/// <summary>Shared System.Text.Json processing for PNR-Linking and Seats, with exact unselected bytes.</summary>
public abstract class JsonFeedCodec(FeedType feed, JsonTokenType rootKind) : IFeedCodec
{
    private sealed record Scalar(FeedField Field, int Offset, int Length, JsonTokenType Kind);
    private sealed record JsonLayout(FeedMessage Message, FeedEnvelope Envelope, IReadOnlyList<Scalar> Scalars);

    public ParsedFeed Parse(FeedMessage message)
    {
        if (feed is not (FeedType.PnrLinking or FeedType.Seats) || message.Delivery.Feed != feed)
        {
            throw new InvalidDataException("JSON codec route does not match delivery.");
        }
        var envelope = FeedPayloadReader.Read(message);
        if (envelope.Ccsid is not null && envelope.Ccsid != "1208")
        {
            throw new InvalidDataException("JSON MQ exports require UTF-8 CCSID 1208; no implicit code-page conversion is performed.");
        }
        var scalars = new List<Scalar>();
        try
        {
            var reader = new Utf8JsonReader(envelope.Payload, new JsonReaderOptions { MaxDepth = 64 });
            if (!reader.Read() || reader.TokenType != rootKind)
            {
                throw new InvalidDataException("Unexpected JSON root for this feed.");
            }
            ReadValue(ref reader, "", "", scalars);
            if (reader.Read())
            {
                throw new InvalidDataException("JSON feed has trailing data.");
            }
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"Incomplete or malformed JSON payload at line {error.LineNumber}, byte {error.BytePositionInLine}; the complete message is retained without output.");
        }
        if (scalars.Count == 0)
        {
            throw new InvalidDataException("JSON feed contains no configured scalar fields.");
        }
        return new(Array.AsReadOnly(scalars.Select(scalar => scalar.Field).ToArray()),
            new JsonLayout(message, envelope, scalars.AsReadOnly()));
    }

    // JSON supplies the structure. Instance paths locate fields; wildcard paths select XML rules.
    private static void ReadValue(ref Utf8JsonReader reader, string id, string rule, List<Scalar> scalars)
    {
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new InvalidDataException("Invalid JSON object.");
                }
                string name = reader.GetString()!;
                if (!names.Add(name) || name == "*")
                {
                    throw new InvalidDataException("Duplicate or ambiguous JSON property.");
                }
                if (!reader.Read())
                {
                    throw new InvalidDataException("Incomplete JSON object.");
                }
                string escaped = name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
                ReadValue(ref reader, id + "/" + escaped, rule + "/" + escaped, scalars);
            }
            if (reader.TokenType != JsonTokenType.EndObject)
            {
                throw new InvalidDataException("Incomplete JSON object.");
            }
        }
        else if (reader.TokenType == JsonTokenType.StartArray)
        {
            int index = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                ReadValue(ref reader, id + "/" + index++, rule + "/*", scalars);
            }
            if (reader.TokenType != JsonTokenType.EndArray)
            {
                throw new InvalidDataException("Incomplete JSON array.");
            }
        }
        else if (reader.TokenType is JsonTokenType.String or JsonTokenType.Number or
            JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null)
        {
            string value = reader.TokenType == JsonTokenType.String ? reader.GetString()! :
                reader.TokenType == JsonTokenType.Null ? "" : Encoding.UTF8.GetString(reader.ValueSpan);
            scalars.Add(new(new(id, rule, value), checked((int)reader.TokenStartIndex),
                checked((int)(reader.BytesConsumed - reader.TokenStartIndex)), reader.TokenType));
        }
        else
        {
            throw new InvalidDataException("Unsupported JSON token.");
        }
    }

    public byte[] Serialize(ParsedFeed original, IReadOnlyDictionary<string, string> anonymizedFields)
    {
        var layout = Layout(original);
        if (anonymizedFields.Count != layout.Scalars.Count)
        {
            throw new InvalidDataException("Protected field set does not match parsed fields.");
        }
        using var output = new MemoryStream();
        int cursor = 0;
        foreach (var scalar in layout.Scalars)
        {
            if (!anonymizedFields.TryGetValue(scalar.Field.Id, out string? value) || value is null)
            {
                throw new InvalidDataException("Protected field is missing or null.");
            }
            output.Write(layout.Envelope.Payload.AsSpan(cursor, scalar.Offset - cursor));
            if (value == scalar.Field.Value)
            {
                output.Write(layout.Envelope.Payload.AsSpan(scalar.Offset, scalar.Length));
            }
            else
            {
                if (scalar.Kind != JsonTokenType.String)
                {
                    throw new InvalidDataException("Non-string JSON fields must remain unchanged.");
                }
                if (FieldPolicy.IsPnr(scalar.Field.Name))
                {
                    throw new InvalidDataException("JSON PNRs must remain unchanged.");
                }
                output.Write(JsonSerializer.SerializeToUtf8Bytes(value));
            }
            cursor = scalar.Offset + scalar.Length;
        }
        output.Write(layout.Envelope.Payload.AsSpan(cursor));
        return layout.Envelope.Serialize(output.ToArray());
    }

    public void Validate(ParsedFeed original, ReadOnlyMemory<byte> serialized)
    {
        var before = Layout(original);
        var after = Layout(Parse(before.Message with { Content = serialized }));
        if (before.Envelope.Format != after.Envelope.Format || before.Envelope.Header != after.Envelope.Header ||
            before.Envelope.Newline != after.Envelope.Newline || before.Envelope.FinalNewline != after.Envelope.FinalNewline ||
            before.Envelope.HexRowBytes != after.Envelope.HexRowBytes || before.Scalars.Count != after.Scalars.Count)
        {
            throw new InvalidDataException("Serialized JSON structure or MQ metadata changed.");
        }
        int leftCursor = 0, rightCursor = 0;
        for (int index = 0; index < before.Scalars.Count; index++)
        {
            var left = before.Scalars[index];
            var right = after.Scalars[index];
            if (left.Field.Id != right.Field.Id || left.Field.Name != right.Field.Name || left.Kind != right.Kind ||
                (FieldPolicy.IsPnr(left.Field.Name) && left.Field.Value != right.Field.Value))
            {
                throw new InvalidDataException("Serialized field locations, types, or PNR preservation failed validation.");
            }
            // Comparing the gaps also protects property names, separators and empty containers.
            if (!before.Envelope.Payload.AsSpan(leftCursor, left.Offset - leftCursor).SequenceEqual(
                after.Envelope.Payload.AsSpan(rightCursor, right.Offset - rightCursor)))
            {
                throw new InvalidDataException("Unselected JSON bytes or empty-container membership changed.");
            }
            bool mustPreserve = left.Kind != JsonTokenType.String || left.Field.Value == right.Field.Value;
            if (mustPreserve && !before.Envelope.Payload.AsSpan(left.Offset, left.Length).SequenceEqual(
                after.Envelope.Payload.AsSpan(right.Offset, right.Length)))
            {
                throw new InvalidDataException("Unselected JSON scalar bytes or non-string values changed.");
            }
            leftCursor = left.Offset + left.Length;
            rightCursor = right.Offset + right.Length;
        }
        if (!before.Envelope.Payload.AsSpan(leftCursor).SequenceEqual(after.Envelope.Payload.AsSpan(rightCursor)))
        {
            throw new InvalidDataException("Unselected JSON trailing bytes changed.");
        }
    }

    private JsonLayout Layout(ParsedFeed parsed)
    {
        if (parsed.Layout is not JsonLayout layout || layout.Message.Delivery.Feed != feed)
        {
            throw new InvalidDataException("JSON serializer requires a parsed layout from the same feed route.");
        }
        return layout;
    }
}
