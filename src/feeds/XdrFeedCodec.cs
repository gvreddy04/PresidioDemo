using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PresidioDemo.Feeds;

public sealed record XdrFieldDefinition(string Name, string Kind, int Limit = 0, int Count = 0,
    long? Expected = null, IReadOnlyList<XdrFieldDefinition>? Members = null,
    IReadOnlyDictionary<int, XdrFieldDefinition>? Cases = null, XdrFieldDefinition? DefaultCase = null);
public sealed record XdrFeedSchema(FeedType Feed, IReadOnlyList<XdrFieldDefinition> Fields);

/// <summary>Explicit producer layouts only; no inference from strings, names, or offsets.</summary>
public static class XdrLayoutConfiguration
{
    public static IReadOnlyDictionary<FeedType, XdrFeedSchema> Load(string filename)
    {
        using var reader = XmlReader.Create(filename, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var root = XDocument.Load(reader).Root ?? throw new InvalidDataException("Missing XDR layout root.");
        Check(root, "xdrLayouts", []);
        var schemas = new Dictionary<FeedType, XdrFeedSchema>();
        var seen = new HashSet<FeedType>();
        foreach (var feed in root.Elements())
        {
            Check(feed, "feed", ["type", "configured"]);
            var type = FeedCatalog.All.SingleOrDefault(f => f.FolderName == Required(feed, "type"))?.Type
                ?? throw new InvalidDataException("Unknown XDR feed route.");
            if (type is FeedType.Seats or FeedType.PnrLinking || !seen.Add(type)) throw new InvalidDataException("Duplicate or non-binary XDR feed route.");
            string configured = Required(feed, "configured");
            if (configured == "false")
            {
                if (feed.Elements().Any()) throw new InvalidDataException("Unconfigured XDR feed cannot declare fields.");
                continue;
            }
            if (configured != "true") throw new InvalidDataException("XDR configured must be true or false.");
            var fields = ParseMembers(feed, 0);
            schemas.Add(type, new(type, fields));
        }
        foreach (var type in new[] { FeedType.ACI, FeedType.PNR, FeedType.ETKT })
            if (!seen.Contains(type)) throw new InvalidDataException("An XDR feed declaration is missing.");
        return schemas;
    }
    private static IReadOnlyList<XdrFieldDefinition> ParseMembers(XElement parent, int depth)
    {
        var fields = parent.Elements().Select(e => Parse(e, depth + 1)).ToArray();
        if (fields.Length == 0 || fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length)
            throw new InvalidDataException("XDR structures need distinct named fields.");
        return fields;
    }
    private static XdrFieldDefinition Parse(XElement element, int depth)
    {
        if (depth > 32) throw new InvalidDataException("XDR layout nesting exceeds its limit.");
        string kind = element.Name.ToString();
        string[] allowed = kind switch
        {
            "string" or "opaque" or "array" => ["name", "max"],
            "fixedOpaque" or "fixedArray" => ["name", "count"],
            "int32" or "uint32" => ["name", "expected"],
            "int64" or "uint64" or "float32" or "float64" or "boolean" or "struct" or "optional" or "union" => ["name"],
            _ => throw new InvalidDataException("Unsupported XDR schema type.")
        };
        Check(element, kind, allowed);
        string name = Required(element, "name");
        if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) throw new InvalidDataException("XDR field names must use ASCII letters, digits or underscores.");
        int Positive(string key) => int.TryParse(Required(element, key), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is > 0 and <= 1048576
            ? n : throw new InvalidDataException("XDR length/count limit must be between 1 and 1048576.");
        int limit = kind is "string" or "opaque" or "array" ? Positive("max") : 0;
        int count = kind is "fixedOpaque" or "fixedArray" ? Positive("count") : 0;
        long? expected = null;
        if (element.Attribute("expected") is not null)
        {
            if (!long.TryParse(Required(element, "expected"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value) ||
                kind == "int32" && (value < int.MinValue || value > int.MaxValue) || kind == "uint32" && (value < 0 || value > uint.MaxValue))
                throw new InvalidDataException("Invalid XDR constant.");
            expected = value;
        }
        IReadOnlyList<XdrFieldDefinition>? members = null;
        IReadOnlyDictionary<int, XdrFieldDefinition>? cases = null;
        XdrFieldDefinition? defaultCase = null;
        if (kind == "struct") members = ParseMembers(element, depth);
        else if (kind is "array" or "fixedArray" or "optional")
        {
            members = ParseMembers(element, depth);
            if (members.Count != 1) throw new InvalidDataException("XDR collection/optional needs exactly one element type.");
        }
        else if (kind == "union")
        {
            var arms = new Dictionary<int, XdrFieldDefinition>();
            foreach (var arm in element.Elements())
            {
                bool isDefault = arm.Name == "default";
                Check(arm, isDefault ? "default" : "case", isDefault ? [] : ["value"]);
                var children = ParseMembers(arm, depth);
                if (children.Count != 1) throw new InvalidDataException("XDR union arm needs exactly one type.");
                if (isDefault)
                {
                    if (defaultCase is not null) throw new InvalidDataException("Duplicate XDR union default.");
                    defaultCase = children[0];
                }
                else if (!int.TryParse(Required(arm, "value"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int tag) || !arms.TryAdd(tag, children[0]))
                    throw new InvalidDataException("Invalid/duplicate XDR union case.");
            }
            if (arms.Count == 0 && defaultCase is null) throw new InvalidDataException("XDR union needs cases.");
            cases = arms;
        }
        else if (element.Elements().Any()) throw new InvalidDataException("Primitive XDR field cannot contain schema children.");
        return new(name, kind, limit, count, expected, members, cases, defaultCase);
    }
    private static string Required(XElement element, string attribute) => !string.IsNullOrWhiteSpace((string?)element.Attribute(attribute))
        ? element.Attribute(attribute)!.Value : throw new InvalidDataException("Required XDR layout setting is missing.");
    private static void Check(XElement element, string tag, string[] attributes)
    {
        if (element.Name != tag || element.Attributes().Any(a => !attributes.Contains(a.Name.ToString())) || element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
            throw new InvalidDataException("Unknown XDR schema element, attribute or text.");
    }
}

/// <summary>Strict, schema-driven XDR; consumes the whole payload and rebuilds counted strings and padding.</summary>
public sealed class XdrFeedCodec(FeedType feed, XdrFeedSchema? schema) : IFeedCodec
{
    private sealed record Scalar(FeedField Field, string Kind, int Offset, int Length, int Maximum);
    private sealed record Layout(FeedMessage Message, FeedEnvelope Envelope, IReadOnlyList<Scalar> Scalars, IReadOnlyDictionary<string, string> Structure);
    public ParsedFeed Parse(FeedMessage message)
    {
        if (message.Delivery.Feed != feed || schema is not null && schema.Feed != feed) throw new InvalidDataException("XDR codec, schema and delivery routes do not match.");
        var envelope = FeedPayloadReader.Read(message);
        if (envelope.Payload.Length % 4 != 0)
            throw new InvalidDataException($"XDR payload has {envelope.Payload.Length} bytes, not a multiple of four. Incomplete input or custom framing requires producer confirmation; no output was published.");
        if (schema is null) throw new InvalidDataException("The binary producer schema is not configured. XDR encoding alone does not identify airline fields; input retained and no output was published.");
        var parser = new Parser(envelope.Payload);
        foreach (var field in schema.Fields) parser.Read(field, "/" + field.Name, "/" + field.Name, 0);
        if (parser.Offset != envelope.Payload.Length) throw new InvalidDataException("XDR schema did not consume the entire payload; trailing bytes are rejected.");
        if (parser.Scalars.Count == 0) throw new InvalidDataException("XDR message contains no configured scalar fields.");
        return new(parser.Scalars.Select(s => s.Field).ToArray(), new Layout(message, envelope, parser.Scalars, parser.Structure));
    }
    private sealed class Parser(byte[] bytes)
    {
        public int Offset { get; private set; }
        public List<Scalar> Scalars { get; } = [];
        public Dictionary<string, string> Structure { get; } = new(StringComparer.Ordinal);
        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > bytes.Length - Offset) throw new InvalidDataException("Incomplete XDR field; complete message retained.");
            var result = bytes.AsSpan(Offset, count);
            Offset += count;
            return result;
        }
        private uint Unsigned() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
        private int Count(int maximum)
        {
            uint count = Unsigned();
            if (count > maximum) throw new InvalidDataException("XDR field exceeds its configured maximum.");
            return (int)count;
        }
        private void Padding(int length)
        {
            foreach (byte value in Take((-length) & 3))
                if (value != 0) throw new InvalidDataException("XDR padding must contain zero bytes.");
        }
        public void Read(XdrFieldDefinition definition, string id, string path, int depth)
        {
            if (depth > 64 || Scalars.Count >= 1000000) throw new InvalidDataException("XDR message exceeds structural limits.");
            int start = Offset;
            string value;
            switch (definition.Kind)
            {
                case "struct":
                    Structure.Add(id, "struct");
                    foreach (var member in definition.Members!) Read(member, id + "/" + member.Name, path + "/" + member.Name, depth + 1);
                    return;
                case "array": case "fixedArray":
                    int count = definition.Kind == "array" ? Count(definition.Limit) : definition.Count;
                    Structure.Add(id, "array:" + count);
                    for (int i = 0; i < count; i++) Read(definition.Members![0], id + "/" + i, path + "/*", depth + 1);
                    return;
                case "optional":
                    uint present = Unsigned();
                    if (present > 1) throw new InvalidDataException("Invalid XDR optional presence value.");
                    Structure.Add(id, "optional:" + present);
                    if (present == 1) Read(definition.Members![0], id + "/value", path + "/value", depth + 1);
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
                    foreach (byte b in encoded) if (b > 127) throw new InvalidDataException("This XDR string schema supports ASCII only; producer text encoding needs an explicit adapter.");
                    value = Encoding.ASCII.GetString(encoded);
                    Padding(length);
                    break;
                case "opaque": case "fixedOpaque":
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
                    if (boolean > 1) throw new InvalidDataException("Invalid XDR Boolean value.");
                    value = boolean == 1 ? "true" : "false";
                    break;
                default: throw new InvalidDataException("Unsupported XDR field type.");
            }
            if (definition.Expected is not null && value != definition.Expected.Value.ToString(CultureInfo.InvariantCulture))
                throw new InvalidDataException("XDR message version or expected constant does not match its schema.");
            Scalars.Add(new(new(id, path, value), definition.Kind, start, Offset - start, definition.Limit));
        }
    }
    public byte[] Serialize(ParsedFeed original, IReadOnlyDictionary<string, string> anonymizedFields)
    {
        var layout = (Layout)original.Layout;
        if (anonymizedFields.Count != layout.Scalars.Count) throw new InvalidDataException("Protected XDR field set does not match parsed fields.");
        using var output = new MemoryStream();
        int cursor = 0;
        foreach (var scalar in layout.Scalars)
        {
            if (!anonymizedFields.TryGetValue(scalar.Field.Id, out string? value)) throw new InvalidDataException("Protected XDR field is missing.");
            output.Write(layout.Envelope.Payload.AsSpan(cursor, scalar.Offset - cursor));
            if (value == scalar.Field.Value) output.Write(layout.Envelope.Payload.AsSpan(scalar.Offset, scalar.Length));
            else
            {
                if (scalar.Kind != "string") throw new InvalidDataException("Non-string XDR changes require a producer-specific protection adapter.");
                if (value.Any(c => c > 127)) throw new InvalidDataException("XDR replacement is not valid ASCII.");
                byte[] encoded = Encoding.ASCII.GetBytes(value);
                if (encoded.Length > scalar.Maximum) throw new InvalidDataException("Protected XDR string exceeds its schema maximum.");
                byte[] length = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(length, (uint)encoded.Length);
                output.Write(length);
                output.Write(encoded);
                for (int i = 0; i < ((-encoded.Length) & 3); i++) output.WriteByte(0);
            }
            cursor = scalar.Offset + scalar.Length;
        }
        output.Write(layout.Envelope.Payload.AsSpan(cursor));
        return layout.Envelope.Serialize(output.ToArray());
    }
    public void Validate(ParsedFeed original, ReadOnlyMemory<byte> serialized)
    {
        var before = (Layout)original.Layout;
        var after = (Layout)Parse(before.Message with { Content = serialized }).Layout;
        if (before.Envelope.Header != after.Envelope.Header || before.Scalars.Count != after.Scalars.Count ||
            before.Structure.Count != after.Structure.Count || before.Structure.Any(kv => !after.Structure.TryGetValue(kv.Key, out var v) || v != kv.Value))
            throw new InvalidDataException("XDR message structure or MQ metadata changed.");
        for (int i = 0; i < before.Scalars.Count; i++)
        {
            var left = before.Scalars[i];
            var right = after.Scalars[i];
            if (left.Field.Id != right.Field.Id || left.Field.Name != right.Field.Name || left.Kind != right.Kind ||
                (left.Kind != "string" || FieldPolicy.IsPnr(left.Field.Name)) && left.Field.Value != right.Field.Value)
                throw new InvalidDataException("XDR field types, binary values or PNR preservation failed validation.");
        }
    }
}
