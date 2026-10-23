using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using PresidioDemo.Protection;

namespace PresidioDemo.Feeds.Shared;

#region Models

/// <summary>
/// Describes an ordered binary field; offsets are computed while parsing each message.
/// </summary>
/// <param name="Name">The declared field name or reviewed layout identifier.</param>
/// <param name="Kind">The declared primitive, collection or optional-structure representation.</param>
/// <param name="Limit">The maximum variable-length size permitted by the reviewed schema.</param>
/// <param name="Count">The fixed element count declared by the reviewed layout.</param>
/// <param name="Expected">The optional constant numeric value required by the schema.</param>
/// <param name="Members">The ordered member definitions or decoded values within this structure.</param>
/// <param name="Cases">The reviewed union arms indexed by their discriminator values.</param>
/// <param name="DefaultCase">The optional union arm used when no explicit discriminator case matches.</param>
/// <param name="Pattern">The optional reviewed validation or capture pattern for a text field.</param>
/// <param name="Captures">The reviewed named text captures inside this string field.</param>
/// <param name="AllowEmpty">Whether an empty capture string is permitted by the reviewed schema.</param>
public sealed record XdrFieldDefinition(string Name, string Kind, int Limit = 0, int Count = 0,
    long? Expected = null, IReadOnlyList<XdrFieldDefinition>? Members = null,
    IReadOnlyDictionary<int, XdrFieldDefinition>? Cases = null, XdrFieldDefinition? DefaultCase = null,
    Regex? Pattern = null,
    IReadOnlyList<XdrCaptureDefinition>? Captures = null, bool AllowEmpty = false);

/// <summary>
/// Describes a named reference-text capture and optional PNR equality check.
/// </summary>
/// <param name="Name">The declared field name or reviewed layout identifier.</param>
/// <param name="EqualTo">The optional field path that must contain the same captured value.</param>
public sealed record XdrCaptureDefinition(string Name, string EqualTo = "");

/// <summary>
/// Defines a sequential binary layout and its serialization-width contract.
/// </summary>
/// <param name="Feed">The supported feed route.</param>
/// <param name="Fields">The ordered decoded fields or reviewed field definitions.</param>
/// <param name="PreserveWidths">Whether reconstructed scalar values must retain their original wire widths.</param>
public sealed record XdrFeedSchema(FeedType Feed, IReadOnlyList<XdrFieldDefinition> Fields, bool PreserveWidths = false);

#endregion

#region Classes

/// <summary>
/// Explicit producer layouts only; no inference from strings, names, or offsets.
/// </summary>
public static class XdrLayoutConfiguration
{

    #region Methods

    /// <summary>
    /// Loads explicit ordered XDR declarations and rejects unknown or unsupported settings.
    /// </summary>
    /// <param name="filename">The reviewed XML configuration filename.</param>
    /// <returns>The complete validated configuration indexed by feed route, or its verification bundle.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    public static IReadOnlyDictionary<FeedType, XdrFeedSchema> Load(string filename)
    {
        using (var reader = XmlReader.Create(filename, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
        {
            var root = XDocument.Load(reader).Root ?? throw new InvalidDataException("Missing XDR layout root.");
            Check(root, "xdrLayouts", []);
            var schemas = new Dictionary<FeedType, XdrFeedSchema>();
            var seen = new HashSet<FeedType>();
            foreach (var feed in root.Elements())
            {
                Check(feed, "feed", ["type", "configured", "preserveWidths"]);
                var type = FeedCatalog.All.SingleOrDefault(f => f.FolderName == Required(feed, "type"))?.Type
                    ?? throw new InvalidDataException("Unknown XDR feed route.");
                if (type is FeedType.Seats or FeedType.PnrLinking || !seen.Add(type))
                {
                    throw new InvalidDataException("Duplicate or non-binary XDR feed route.");
                }

                string configured = Required(feed, "configured");
                if (configured == "false")
                {
                    if (feed.Elements().Any())
                    {
                        throw new InvalidDataException("Unconfigured XDR feed cannot declare fields.");
                    }

                    continue;
                }
                if (configured != "true")
                {
                    throw new InvalidDataException("XDR configured must be true or false.");
                }

                var fields = ParseMembers(feed, 0);
                bool preserveWidths = (string?)feed.Attribute("preserveWidths") switch
                {
                    null or "false" => false,
                    "true" => true,
                    _ => throw new InvalidDataException("preserveWidths must be true or false.")
                };
                schemas.Add(type, new(type, fields, preserveWidths));
            }
            foreach (var type in new[] { FeedType.ACI, FeedType.PNR, FeedType.ETKT })
            {
                if (!seen.Contains(type))
                {
                    throw new InvalidDataException("An XDR feed declaration is missing.");
                }
            }

            return schemas;
        }
    }

    /// <summary>
    /// Rejects unexpected XML element names, attributes or text content.
    /// </summary>
    /// <param name="element">The XML declaration or JSON node being processed.</param>
    /// <param name="tag">The expected XML element name or XDR union discriminator.</param>
    /// <param name="attributes">The permitted XML attribute names for this element.</param>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    private static void Check(XElement element, string tag, string[] attributes)
    {
        if (element.Name != tag || element.Attributes().Any(a => !attributes.Contains(a.Name.ToString())) || element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
        {
            throw new InvalidDataException("Unknown XDR schema element, attribute or text.");
        }
    }

    /// <summary>
    /// Parses the complete input, validates its supported shape and retains the layout required for reconstruction.
    /// </summary>
    /// <param name="element">The XML declaration or JSON node being processed.</param>
    /// <param name="depth">The current nesting depth used to enforce structural limits.</param>
    /// <returns>The decoded document or parsed fields and retained reconstruction layout.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    private static XdrFieldDefinition Parse(XElement element, int depth)
    {
        if (depth > 32)
        {
            throw new InvalidDataException("XDR layout nesting exceeds its limit.");
        }

        string kind = element.Name.ToString();
        string[] allowed = kind switch
        {
            "string" or "opaque" or "array" => ["name", "max"],
            "fixedOpaque" or "fixedArray" or "words" => ["name", "count"],
            "int32" or "uint32" => ["name", "expected"],
            "int64" or "uint64" or "float32" or "float64" or "boolean" or "struct" or "optional" or "union" => ["name"],
            _ => throw new InvalidDataException("Unsupported XDR schema type.")
        };
        Check(element, kind, allowed);
        string name = Required(element, "name");
        if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            throw new InvalidDataException("XDR field names must use ASCII letters, digits or underscores.");
        }

        int Positive(string key) => int.TryParse(Required(element, key), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is > 0 and <= 1048576
                    ? n : throw new InvalidDataException("XDR length/count limit must be between 1 and 1048576.");
        int limit = kind is "string" or "opaque" or "array" ? Positive("max") : 0;
        int count = kind is "fixedOpaque" or "fixedArray" or "words" ? Positive("count") : 0;
        // Normalize the shorthand to the original definition. No count prefix is added,
        // and the Value member preserves existing scalar paths and codec behavior.
        if (kind == "words")
        {
            if (element.Elements().Any())
            {
                throw new InvalidDataException("XDR words cannot contain schema children.");
            }

            return new(name, "fixedArray", Count: count, Members: [new("Value", "uint32")]);
        }
        long? expected = null;
        if (element.Attribute("expected") is not null)
        {
            if (!long.TryParse(Required(element, "expected"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value) ||
                kind == "int32" && (value < int.MinValue || value > int.MaxValue) || kind == "uint32" && (value < 0 || value > uint.MaxValue))
            {
                throw new InvalidDataException("Invalid XDR constant.");
            }

            expected = value;
        }
        IReadOnlyList<XdrFieldDefinition>? members = null;
        IReadOnlyDictionary<int, XdrFieldDefinition>? cases = null;
        XdrFieldDefinition? defaultCase = null;
        if (kind == "struct")
        {
            members = ParseMembers(element, depth);
        }
        else if (kind is "array" or "fixedArray" or "optional")
        {
            members = ParseMembers(element, depth);
            if (members.Count != 1)
            {
                throw new InvalidDataException("XDR collection/optional needs exactly one element type.");
            }
        }
        else if (kind == "union")
        {
            var arms = new Dictionary<int, XdrFieldDefinition>();
            foreach (var arm in element.Elements())
            {
                bool isDefault = arm.Name == "default";
                Check(arm, isDefault ? "default" : "case", isDefault ? [] : ["value"]);
                var children = ParseMembers(arm, depth);
                if (children.Count != 1)
                {
                    throw new InvalidDataException("XDR union arm needs exactly one type.");
                }

                if (isDefault)
                {
                    if (defaultCase is not null)
                    {
                        throw new InvalidDataException("Duplicate XDR union default.");
                    }

                    defaultCase = children[0];
                }
                else if (!int.TryParse(Required(arm, "value"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int tag) || !arms.TryAdd(tag, children[0]))
                {
                    throw new InvalidDataException("Invalid/duplicate XDR union case.");
                }
            }
            if (arms.Count == 0 && defaultCase is null)
            {
                throw new InvalidDataException("XDR union needs cases.");
            }

            cases = arms;
        }
        else if (element.Elements().Any())
        {
            throw new InvalidDataException("Primitive XDR field cannot contain schema children.");
        }

        return new(name, kind, limit, count, expected, members, cases, defaultCase);
    }

    /// <summary>
    /// Reads ordered XDR child declarations and rejects empty or duplicate field names.
    /// </summary>
    /// <param name="parent">The XML element containing the ordered child declarations.</param>
    /// <param name="depth">The current nesting depth used to enforce structural limits.</param>
    /// <returns>The nonempty ordered collection of distinct reviewed XDR field definitions.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    private static IReadOnlyList<XdrFieldDefinition> ParseMembers(XElement parent, int depth)
    {
        var fields = parent.Elements().Select(e => Parse(e, depth + 1)).ToArray();
        if (fields.Length == 0 || fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length)
        {
            throw new InvalidDataException("XDR structures need distinct named fields.");
        }

        return fields;
    }

    /// <summary>
    /// Reads a nonempty mandatory XML attribute and rejects a missing setting.
    /// </summary>
    /// <param name="element">The XML declaration or JSON node being processed.</param>
    /// <param name="attribute">The required XML attribute name.</param>
    /// <returns>The validated nonempty configuration value.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    private static string Required(XElement element, string attribute) => !string.IsNullOrWhiteSpace((string?)element.Attribute(attribute))
            ? element.Attribute(attribute)!.Value : throw new InvalidDataException("Required XDR layout setting is missing.");

    #endregion
}

/// <summary>
/// Strict, schema-driven XDR; consumes the whole payload and rebuilds counted strings and padding.
/// </summary>
/// <param name="feed">The supported feed route.</param>
/// <param name="schema">The reviewed ordered XDR declarations for this feed.</param>
public sealed class XdrFeedCodec(FeedType feed, XdrFeedSchema? schema) : IFeedCodec
{

    #region Methods

    /// <summary>
    /// Parses the complete input, validates its supported shape and retains the layout required for reconstruction.
    /// </summary>
    /// <param name="message">The complete input delivery and its retained message bytes.</param>
    /// <returns>The decoded document or parsed fields and retained reconstruction layout.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    public ParsedFeed Parse(FeedMessage message)
    {
        if (message.Delivery.Feed != feed || schema is not null && schema.Feed != feed)
        {
            throw new InvalidDataException("XDR codec, schema and delivery routes do not match.");
        }

        var envelope = FeedPayloadReader.Read(message);
        if (envelope.Payload.Length % 4 != 0)
        {
            throw new InvalidDataException($"XDR payload has {envelope.Payload.Length} bytes, not a multiple of four. Incomplete input or custom framing requires producer confirmation; no output was published.");
        }

        if (schema is null)
        {
            throw new InvalidDataException("The binary producer schema is not configured. XDR encoding alone does not identify airline fields; input retained and no output was published.");
        }

        var parser = new Parser(envelope.Payload, schema.PreserveWidths);
        foreach (var field in schema.Fields)
        {
            parser.Read(field, "/" + field.Name, "/" + field.Name, 0);
        }

        parser.ValidateReferences();
        if (parser.Offset != envelope.Payload.Length)
        {
            throw new InvalidDataException("XDR schema did not consume the entire payload; trailing bytes are rejected.");
        }

        if (parser.Scalars.Count == 0)
        {
            throw new InvalidDataException("XDR message contains no configured scalar fields.");
        }

        return new(parser.Scalars.Select(s => s.Field).ToArray(), new Layout(message, envelope, parser.Scalars, parser.Structure));
    }

    /// <summary>
    /// Reconstructs the original message representation using the complete protected field set.
    /// </summary>
    /// <param name="original">The complete extracted field set and its retained codec-specific serialization layout.</param>
    /// <param name="anonymizedFields">The complete protected field values indexed by original instance identifier.</param>
    /// <returns>The complete reconstructed message in its original transport representation.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    public byte[] Serialize(ParsedFeed original, IReadOnlyDictionary<string, string> anonymizedFields)
    {
        var layout = (Layout)original.Layout;
        if (anonymizedFields.Count != layout.Scalars.Count)
        {
            throw new InvalidDataException("Protected XDR field set does not match parsed fields.");
        }

        using (var output = new MemoryStream())
        {
            int cursor = 0;
            foreach (var scalar in layout.Scalars)
            {
                if (!anonymizedFields.TryGetValue(scalar.Field.Id, out string? value))
                {
                    throw new InvalidDataException("Protected XDR field is missing.");
                }

                output.Write(layout.Envelope.Payload.AsSpan(cursor, scalar.Offset - cursor));
                if (value == scalar.Field.Value)
                {
                    output.Write(layout.Envelope.Payload.AsSpan(scalar.Offset, scalar.Length));
                }
                else
                {
                    if (scalar.Kind is not ("string" or "capture"))
                    {
                        throw new InvalidDataException("Non-string XDR changes require a producer-specific protection adapter.");
                    }

                    if (value.Any(c => c > 127))
                    {
                        throw new InvalidDataException("XDR replacement is not valid ASCII.");
                    }

                    byte[] encoded = Encoding.ASCII.GetBytes(value);
                    if ((schema!.PreserveWidths || scalar.Kind == "capture") && encoded.Length != scalar.ValueLength)
                    {
                        throw new InvalidDataException("Binary protection must preserve every configured field's byte width.");
                    }

                    if (FieldPolicy.IsPnr(scalar.Field.Name))
                    {
                        throw new InvalidDataException("XDR serialization cannot change a PNR.");
                    }

                    if (encoded.Length > scalar.Maximum)
                    {
                        throw new InvalidDataException("Protected XDR string exceeds its schema maximum.");
                    }

                    if (scalar.Kind == "capture")
                    {
                        output.Write(encoded);
                        cursor = scalar.Offset + scalar.Length;
                        continue;
                    }
                    byte[] length = new byte[4];
                    BinaryPrimitives.WriteUInt32BigEndian(length, (uint)encoded.Length);
                    output.Write(length);
                    output.Write(encoded);
                    for (int i = 0; i < ((-encoded.Length) & 3); i++)
                    {
                        output.WriteByte(0);
                    }
                }
                cursor = scalar.Offset + scalar.Length;
            }
            output.Write(layout.Envelope.Payload.AsSpan(cursor));
            return layout.Envelope.Serialize(output.ToArray());
        }
    }

    /// <summary>
    /// Rejects reconstructed data or settings that violate the preserved structure and protection contract.
    /// </summary>
    /// <param name="original">The complete extracted field set and its retained codec-specific serialization layout.</param>
    /// <param name="serialized">The complete reconstructed output bytes being validated.</param>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    public void Validate(ParsedFeed original, ReadOnlyMemory<byte> serialized)
    {
        var before = (Layout)original.Layout;
        var after = (Layout)Parse(before.Message with { Content = serialized }).Layout;
        if (before.Envelope.Header != after.Envelope.Header || before.Envelope.Format != after.Envelope.Format ||
            before.Envelope.Newline != after.Envelope.Newline || before.Envelope.FinalNewline != after.Envelope.FinalNewline ||
            before.Scalars.Count != after.Scalars.Count ||
            before.Structure.Count != after.Structure.Count || before.Structure.Any(kv => !after.Structure.TryGetValue(kv.Key, out var v) || v != kv.Value))
        {
            throw new InvalidDataException("XDR message structure or MQ metadata changed.");
        }

        for (int i = 0; i < before.Scalars.Count; i++)
        {
            var left = before.Scalars[i];
            var right = after.Scalars[i];
            if (left.Field.Id != right.Field.Id || left.Field.Name != right.Field.Name || left.Kind != right.Kind ||
                (left.Kind is not ("string" or "capture") || FieldPolicy.IsPnr(left.Field.Name)) && left.Field.Value != right.Field.Value ||
                schema!.PreserveWidths && (left.Offset != right.Offset || left.Length != right.Length))
            {
                throw new InvalidDataException("XDR field types, binary values or PNR preservation failed validation.");
            }
        }
        if (schema!.PreserveWidths)
        {
            if (before.Envelope.Payload.Length != after.Envelope.Payload.Length)
            {
                throw new InvalidDataException("Binary framing length changed.");
            }

            var mutable = new bool[before.Envelope.Payload.Length];
            foreach (var scalar in before.Scalars)
            {
                if (scalar.Kind == "string")
                {
                    Array.Fill(mutable, true, scalar.Offset + 4, scalar.ValueLength);
                }
                else if (scalar.Kind == "capture")
                {
                    Array.Fill(mutable, true, scalar.Offset, scalar.Length);
                }
            }
            for (int offset = 0; offset < mutable.Length; offset++)
            {
                if (!mutable[offset] && before.Envelope.Payload[offset] != after.Envelope.Payload[offset])
                {
                    throw new InvalidDataException("Binary framing, structural values or record separators changed.");
                }
            }
        }
    }

    /// <summary>
    /// Returns exact payload locations for the codec&apos;s reviewed text-protection fields.
    /// </summary>
    /// <param name="parsed">The complete extracted field set and its retained codec-specific serialization layout.</param>
    /// <returns>The exact payload offset and length associated with each reviewed text field.</returns>
    internal static IReadOnlyDictionary<string, (int Offset, int Length)> TextLocations(ParsedFeed parsed)
    {
        var layout = (Layout)parsed.Layout;
        return layout.Scalars.Where(scalar => scalar.Kind is "string" or "capture")
            .ToDictionary(scalar => scalar.Field.Id, scalar => scalar.Kind == "string"
                ? (scalar.Offset + 4, scalar.ValueLength) : (scalar.Offset, scalar.Length));
    }

    #endregion

    #region Nested Types

    /// <summary>
    /// Retains an XDR message, its transport envelope, scalar locations and structural markers.
    /// </summary>
    /// <param name="Message">The complete delivery metadata and original or protected message bytes.</param>
    /// <param name="Envelope">The original payload and transport framing.</param>
    /// <param name="Scalars">The ordered decoded scalars and their original wire spans.</param>
    /// <param name="Structure">The original collection, presence and union markers retained for validation.</param>
    private sealed record Layout(FeedMessage Message, FeedEnvelope Envelope, IReadOnlyList<Scalar> Scalars, IReadOnlyDictionary<string, string> Structure);

    /// <summary>
    /// Reads one XDR payload using configured declarations, bounds and cross-field constraints.
    /// </summary>
    /// <param name="bytes">The complete payload or fixture bytes to read, encode or compare.</param>
    /// <param name="printableText">Whether decoded XDR strings must contain only printable ASCII characters.</param>
    private sealed class Parser(byte[] bytes, bool printableText)
    {

        #region Fields and Constants

        /// <summary>
        /// The pending capture-equality checks required by the XDR schema.
        /// </summary>
        private readonly List<(string FieldId, string TargetPath)> _references = [];

        #endregion

        #region Methods

        /// <summary>
        /// Reads one XDR declaration and records its scalar spans and structural markers.
        /// </summary>
        /// <param name="definition">The reviewed field declaration to decode.</param>
        /// <param name="id">The instance path locating one value within this message.</param>
        /// <param name="path">The exact structural field path within the decoded message.</param>
        /// <param name="depth">The current nesting depth used to enforce structural limits.</param>
        /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
        public void Read(XdrFieldDefinition definition, string id, string path, int depth)
        {
            if (depth > 64 || Scalars.Count >= 1000000)
            {
                throw new InvalidDataException("XDR message exceeds structural limits.");
            }

            int start = Offset;
            string value;
            switch (definition.Kind)
            {
                case "struct":
                    Structure.Add(id, "struct");
                    foreach (var member in definition.Members!)
                    {
                        Read(member, id + "/" + member.Name, path + "/" + member.Name, depth + 1);
                    }

                    return;
                case "array":
                case "fixedArray":
                    int count = definition.Kind == "array" ? Count(definition.Limit) : definition.Count;
                    Structure.Add(id, "array:" + count);
                    for (int i = 0; i < count; i++)
                    {
                        Read(definition.Members![0], id + "/" + i, path + "/*", depth + 1);
                    }

                    return;
                case "optional":
                    uint present = Unsigned();
                    if (present > 1)
                    {
                        throw new InvalidDataException("Invalid XDR optional presence value.");
                    }

                    Structure.Add(id, "optional:" + present);
                    if (present == 1)
                    {
                        Read(definition.Members![0], id + "/value", path + "/value", depth + 1);
                    }

                    return;
                case "union":
                    int tag = BinaryPrimitives.ReadInt32BigEndian(Take(4));
                    Structure.Add(id, "union:" + tag);
                    var arm = definition.Cases!.GetValueOrDefault(tag) ?? definition.DefaultCase
                        ?? throw new InvalidDataException("Unknown XDR union discriminator.");
                    Read(arm, id + "/" + arm.Name, path + "/" + arm.Name, depth + 1);
                    return;
                case "string":
                    int length = Count(definition.Limit);
                    var encoded = Take(length);
                    foreach (byte b in encoded)
                    {
                        if (b > 127 || printableText && b is < 32 or > 126)
                        {
                            throw new InvalidDataException("This XDR string schema requires ASCII text in its configured character range.");
                        }
                    }

                    value = Encoding.ASCII.GetString(encoded);
                    Padding(length);
                    if (definition.Pattern is not null)
                    {
                        if (definition.AllowEmpty && length == 0)
                        {
                            Structure.Add(id, "empty-capture-string");
                            return;
                        }
                        Match match = definition.Pattern.Match(value);
                        if (!match.Success)
                        {
                            throw new InvalidDataException($"XDR text at {path} does not match its configured record pattern.");
                        }

                        if (definition.Captures is not null)
                        {
                            Structure.Add(id, "capture-string");
                            var groups = definition.Captures.Select(capture => (Definition: capture, Group: match.Groups[capture.Name]))
                                .Where(item => item.Group.Success).OrderBy(item => item.Group.Index).ToArray();
                            int previousEnd = 0;
                            foreach (var (capture, group) in groups)
                            {
                                if (group.Captures.Count != 1 || group.Index < previousEnd)
                                {
                                    throw new InvalidDataException("Text captures overlap or repeat; exact protection spans are required.");
                                }

                                string captureId = id + "/" + capture.Name;
                                Scalars.Add(new(new(captureId, path + "/" + capture.Name, group.Value),
                                    "capture", start + 4 + group.Index, group.Length, group.Length, group.Length));
                                if (capture.EqualTo.Length > 0)
                                {
                                    _references.Add((captureId, capture.EqualTo));
                                }

                                previousEnd = group.Index + group.Length;
                            }
                            return;
                        }
                    }
                    break;
                case "opaque":
                case "fixedOpaque":
                    int size = definition.Kind == "opaque" ? Count(definition.Limit) : definition.Count;
                    value = Convert.ToHexString(Take(size));
                    Padding(size);
                    break;
                case "uint32": value = Unsigned().ToString(CultureInfo.InvariantCulture); break;
                case "int32": value = BinaryPrimitives.ReadInt32BigEndian(Take(4)).ToString(CultureInfo.InvariantCulture); break;
                case "uint64": value = BinaryPrimitives.ReadUInt64BigEndian(Take(8)).ToString(CultureInfo.InvariantCulture); break;
                case "int64": value = BinaryPrimitives.ReadInt64BigEndian(Take(8)).ToString(CultureInfo.InvariantCulture); break;
                case "float32": value = Convert.ToHexString(Take(4)); break;
                case "float64": value = Convert.ToHexString(Take(8)); break;
                case "boolean":
                    uint boolean = Unsigned();
                    if (boolean > 1)
                    {
                        throw new InvalidDataException("Invalid XDR Boolean value.");
                    }

                    value = boolean == 1 ? "true" : "false";
                    break;
                default: throw new InvalidDataException("Unsupported XDR field type.");
            }
            if (definition.Expected is not null && value != definition.Expected.Value.ToString(CultureInfo.InvariantCulture))
            {
                throw new InvalidDataException("XDR message version or expected constant does not match its schema.");
            }

            Scalars.Add(new(new(id, path, value), definition.Kind, start, Offset - start, definition.Limit,
                            definition.Kind == "string" ? value.Length : 0));
        }

        /// <summary>
        /// Verifies that every configured capture-equality constraint resolves to one matching value.
        /// </summary>
        /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
        public void ValidateReferences()
        {
            foreach (var (fieldId, targetPath) in _references)
            {
                var value = Scalars.Single(s => s.Field.Id == fieldId).Field.Value;
                var targets = Scalars.Where(s => s.Field.Name == targetPath).ToArray();
                if (targets.Length != 1 || targets[0].Field.Value != value)
                {
                    throw new InvalidDataException("Configured cross-field reference values do not agree.");
                }
            }
        }

        /// <summary>
        /// Reads a bounded unsigned XDR count and rejects values beyond the configured maximum.
        /// </summary>
        /// <param name="maximum">The maximum supported length or count for the declared value.</param>
        /// <returns>The validated count, no greater than the configured maximum.</returns>
        /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
        private int Count(int maximum)
        {
            uint count = Unsigned();
            if (count > maximum)
            {
                throw new InvalidDataException("XDR field exceeds its configured maximum.");
            }

            return (int)count;
        }

        /// <summary>
        /// Consumes XDR alignment bytes and rejects nonzero padding.
        /// </summary>
        /// <param name="length">The original value width in bytes.</param>
        /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
        private void Padding(int length)
        {
            foreach (byte value in Take((-length) & 3))
            {
                if (value != 0)
                {
                    throw new InvalidDataException("XDR padding must contain zero bytes.");
                }
            }
        }

        /// <summary>
        /// Consumes an exact payload slice and rejects truncated or out-of-range reads.
        /// </summary>
        /// <param name="count">The exact number of bytes or elements to consume.</param>
        /// <returns>The requested payload slice; the reader offset advances by its length.</returns>
        /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > bytes.Length - Offset)
            {
                throw new InvalidDataException("Incomplete XDR field; complete message retained.");
            }

            var result = bytes.AsSpan(Offset, count);
            Offset += count;
            return result;
        }

        /// <summary>
        /// Reads one unsigned 32-bit integer in big-endian wire order.
        /// </summary>
        /// <returns>The original unsigned 32-bit word.</returns>
        private uint Unsigned() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));

        #endregion

        #region Properties, Indexers

        /// <summary>
        /// Gets the current byte offset within the decoded payload.
        /// </summary>
        /// <value>The current byte offset within the decoded payload.</value>
        public int Offset { get; private set; }

        /// <summary>
        /// Gets the decoded scalar values and their original payload locations.
        /// </summary>
        /// <value>The decoded scalar values and their original payload locations.</value>
        public List<Scalar> Scalars { get; } = [];

        /// <summary>
        /// Gets the parsed collection, presence and union markers used to validate reconstruction.
        /// </summary>
        /// <value>The parsed collection, presence and union markers used to validate reconstruction.</value>
        public Dictionary<string, string> Structure { get; } = new(StringComparer.Ordinal);

        #endregion
    }

    /// <summary>
    /// Retains one parsed scalar and the wire span needed to rebuild its value.
    /// </summary>
    /// <param name="Field">The parsed protection field associated with this binding.</param>
    /// <param name="Kind">The declared primitive, collection or optional-structure representation.</param>
    /// <param name="Offset">The zero-based payload byte offset of this value or span.</param>
    /// <param name="Length">The wire span or observed file length in bytes.</param>
    /// <param name="Maximum">The configured maximum width or count for this scalar.</param>
    /// <param name="ValueLength">The counted-string value length excluding framing and padding.</param>
    private sealed record Scalar(FeedField Field, string Kind, int Offset, int Length, int Maximum, int ValueLength = 0);

    #endregion
}

#endregion
