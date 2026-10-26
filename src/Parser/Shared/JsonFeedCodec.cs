using System.Text;
using System.Text.Json;
using PresidioDemo.Protection;

namespace PresidioDemo.Feeds.Shared;

/// <summary>
/// Shared System.Text.Json processing for PNR-Linking and Seats, with exact unselected bytes.
/// </summary>
/// <param name="feed">The supported feed route.</param>
/// <param name="rootKind">The required JSON array or object root for this feed.</param>
public abstract class JsonFeedCodec(FeedType feed, JsonTokenType rootKind) : IFeedCodec
{

    #region Methods

    /// <summary>
    /// Parses the complete input, validates its supported shape and retains the layout required for reconstruction.
    /// </summary>
    /// <param name="message">The complete input delivery and its retained message bytes.</param>
    /// <returns>The decoded document or parsed fields and retained reconstruction layout.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    public virtual ParsedFeed Parse(FeedMessage message)
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

    /// <summary>
    /// Reconstructs the original message representation using the complete protected field set.
    /// </summary>
    /// <param name="original">The complete extracted field set and its retained codec-specific serialization layout.</param>
    /// <param name="anonymizedFields">The complete protected field values indexed by original instance identifier.</param>
    /// <returns>The complete reconstructed message in its original transport representation.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    public virtual byte[] Serialize(ParsedFeed original, IReadOnlyDictionary<string, string> anonymizedFields)
    {
        var layout = Layout(original);
        if (anonymizedFields.Count != layout.Scalars.Count)
        {
            throw new InvalidDataException("Protected field set does not match parsed fields.");
        }
        using (var output = new MemoryStream())
        {
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
    }

    /// <summary>
    /// Verifies JSON paths, scalar types, PNRs, MQ metadata and every unselected payload byte.
    /// </summary>
    /// <param name="original">The complete extracted field set and its retained codec-specific serialization layout.</param>
    /// <param name="serialized">The complete reconstructed output bytes being validated.</param>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    public virtual void Validate(ParsedFeed original, ReadOnlyMemory<byte> serialized)
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

    /// <summary>
    /// Obtains the retained parsed layout and verifies that it belongs to this feed route.
    /// </summary>
    /// <param name="parsed">The complete extracted field set and its retained codec-specific serialization layout.</param>
    /// <returns>The retained parsed layout belonging to this JSON feed route.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    private JsonLayout Layout(ParsedFeed parsed)
    {
        if (parsed.Layout is not JsonLayout layout || layout.Message.Delivery.Feed != feed)
        {
            throw new InvalidDataException("JSON serializer requires a parsed layout from the same feed route.");
        }
        return layout;
    }

    // JSON supplies the structure. Instance paths locate fields; wildcard paths select XML rules.
    /// <summary>
    /// Traverses JSON objects and arrays using System.Text.Json and records exact scalar spans.
    /// </summary>
    /// <param name="reader">The JSON token reader positioned at the value being traversed.</param>
    /// <param name="id">The instance path locating one value within this message.</param>
    /// <param name="rule">The validated field-protection rule being exercised.</param>
    /// <param name="scalars">The ordered scalar locations collected during complete-message parsing.</param>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
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

    #endregion

    #region Nested Types

    /// <summary>
    /// Retains JSON delivery metadata, transport framing and ordered scalar locations.
    /// </summary>
    /// <param name="Message">The complete delivery metadata and original or protected message bytes.</param>
    /// <param name="Envelope">The original payload and transport framing.</param>
    /// <param name="Scalars">The ordered decoded scalars and their original wire spans.</param>
    private sealed record JsonLayout(FeedMessage Message, FeedEnvelope Envelope, IReadOnlyList<Scalar> Scalars);

    /// <summary>
    /// Retains one parsed scalar and the wire span needed to rebuild its value.
    /// </summary>
    /// <param name="Field">The parsed protection field associated with this binding.</param>
    /// <param name="Offset">The zero-based payload byte offset of this value or span.</param>
    /// <param name="Length">The wire span or observed file length in bytes.</param>
    /// <param name="Kind">The declared primitive, collection or optional-structure representation.</param>
    private sealed record Scalar(FeedField Field, int Offset, int Length, JsonTokenType Kind);

    #endregion
}
