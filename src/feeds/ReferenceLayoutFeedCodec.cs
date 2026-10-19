using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace PresidioDemo.Feeds;

public sealed record ReferenceFieldSpan(string Name, int Offset, int Length);

/// <summary>A pinned reference frame with explicitly configured, fixed-width ASCII fields.</summary>
public sealed class ReferenceFeedLayout
{
    private readonly byte[] _template;
    private readonly bool[] _fieldBytes;

    internal XdrFeedSchema? Schema { get; }
    internal IReadOnlyDictionary<string, string> Aliases { get; } = new Dictionary<string, string>();
    internal IReadOnlySet<string> RequiredPaths { get; } = new HashSet<string>();

    internal ReferenceFeedLayout(FeedType feed, string name, XdrFeedSchema schema,
        IReadOnlyList<ReferenceFieldSpan> fields, IReadOnlyDictionary<string, string> aliases,
        IReadOnlySet<string> requiredPaths)
    {
        Feed = feed;
        Name = name;
        Schema = schema;
        Fields = fields;
        Aliases = aliases;
        RequiredPaths = requiredPaths;
        _template = [];
        _fieldBytes = [];
    }

    internal ReferenceFeedLayout(FeedType feed, string name, byte[] template, IReadOnlyList<ReferenceFieldSpan> fields)
    {
        Feed = feed;
        Name = name;
        _template = template.ToArray();
        Fields = Array.AsReadOnly(fields.ToArray());
        _fieldBytes = new bool[template.Length];
        foreach (var field in Fields)
        {
            Array.Fill(_fieldBytes, true, field.Offset, field.Length);
        }
    }

    public FeedType Feed { get; }
    public string Name { get; }
    public IReadOnlyList<ReferenceFieldSpan> Fields { get; }

    internal void ValidatePayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != _template.Length)
        {
            throw new InvalidDataException($"Reference layout {Name} requires {_template.Length} payload bytes. A different layout needs an explicitly reviewed profile; no output was published.");
        }
        for (int offset = 0; offset < payload.Length; offset++)
        {
            if (!_fieldBytes[offset] && payload[offset] != _template[offset])
            {
                throw new InvalidDataException($"Reference layout {Name} differs in an unclassified binary region at byte {offset}; the message is retained without output.");
            }
        }
        string? pnr = null;
        foreach (var field in Fields)
        {
            var value = payload.Slice(field.Offset, field.Length);
            foreach (byte character in value)
            {
                if (character is < 32 or > 126)
                {
                    throw new InvalidDataException("Reference layout fields require printable ASCII; the message is retained without output.");
                }
            }
            if (FieldPolicy.IsPnr(field.Name))
            {
                string current = Encoding.ASCII.GetString(value);
                if (pnr is not null && current != pnr)
                {
                    throw new InvalidDataException("Reference layout PNR copies do not agree; the message is retained without output.");
                }
                pnr = current;
            }
        }
    }
}

/// <summary>Loads observed reference layouts without claiming an airline-wide producer schema.</summary>
public static class ReferenceLayoutConfiguration
{
    /// <summary>Loads original fixed layouts or optional schema-driven mappings without sample-file dependencies.</summary>
    public static IReadOnlyDictionary<FeedType, ReferenceFeedLayout> Load(string filename,
        IReadOnlyDictionary<FeedType, XdrFeedSchema>? schemas = null)
    {
        using var reader = XmlReader.Create(filename, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        var root = XDocument.Load(reader).Root ?? throw new InvalidDataException("Missing reference-layout root.");
        Check(root, "referenceLayouts", []);
        var layouts = new Dictionary<FeedType, ReferenceFeedLayout>();
        foreach (var element in root.Elements())
        {
            Check(element, "feed", ["type", "name", "referenceFile", "referenceSha256", "schemaDriven"]);
            var feed = FeedCatalog.All.SingleOrDefault(item => item.FolderName == Required(element, "type"))
                ?? throw new InvalidDataException("Unknown reference-layout feed route.");
            if (feed.Type is FeedType.Seats or FeedType.PnrLinking || layouts.ContainsKey(feed.Type))
            {
                throw new InvalidDataException("Duplicate or non-binary reference-layout route.");
            }
            string name = Identifier(Required(element, "name"));
            bool schemaDriven = Flag(element, "schemaDriven");
            if (schemaDriven)
            {
                if (schemas is null || !schemas.TryGetValue(feed.Type, out var schema))
                    throw new InvalidDataException("Schema-driven reference layout requires a configured XDR feed.");
                if (element.Attribute("referenceSha256") is { } hash &&
                    (hash.Value.Length != 64 || !hash.Value.All(Uri.IsHexDigit)))
                    throw new InvalidDataException("Invalid reference provenance hash.");
                layouts.Add(feed.Type, ReadSchemaDriven(element, name, schema));
                continue;
            }
            string reference = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(filename))!, Required(element, "referenceFile")));
            byte[] fileBytes = File.ReadAllBytes(reference);
            string expectedHash = Required(element, "referenceSha256");
            string actualHash = Convert.ToHexString(SHA256.HashData(fileBytes));
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Reference file checksum differs from the reviewed layout; input processing was not started.");
            }
            var delivery = new FeedDelivery(feed.Type, Guid.Empty, Path.GetFileName(reference), reference);
            byte[] payload = FeedPayloadReader.ReadPayload(new(delivery, fileBytes));
            if (payload.Length == 0 || payload.Length % 4 != 0)
            {
                throw new InvalidDataException("Reference XDR candidate must have a complete, four-byte-aligned payload.");
            }
            var fields = ReadFields(element, payload);
            var layout = new ReferenceFeedLayout(feed.Type, name, payload, fields);
            layout.ValidatePayload(payload);
            layouts.Add(feed.Type, layout);
        }
        return new ReadOnlyDictionary<FeedType, ReferenceFeedLayout>(layouts);
    }

    private static bool Flag(XElement element, string name) => (string?)element.Attribute(name) switch
    {
        null or "false" => false,
        "true" => true,
        _ => throw new InvalidDataException("Reference boolean settings must be true or false.")
    };

    private static ReferenceFeedLayout ReadSchemaDriven(XElement element, string name, XdrFeedSchema schema)
    {
        var definitions = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var fields = new List<ReferenceFieldSpan>();
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var required = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in element.Elements())
        {
            Check(text, "text", ["name", "offset", "length", "path", "pattern", "allowEmpty", "optional"]);
            string alias = Identifier(Required(text, "name"));
            _ = Number(text, "offset", true);
            _ = Number(text, "length", true);
            if (!names.Add(alias) || !definitions.TryAdd((string?)text.Attribute("path") ?? "/" + alias, text))
                throw new InvalidDataException("Duplicate reference text name or schema path.");
            _ = Flag(text, "optional");
            _ = Flag(text, "allowEmpty");
        }
        var used = new HashSet<string>(StringComparer.Ordinal);
        var mapped = schema.Fields.Select(field => Map(field, "/" + field.Name, false)).ToArray();
        if (!used.SetEquals(definitions.Keys) || fields.Count == 0)
            throw new InvalidDataException("Reference mappings must match the complete XDR text structure.");
        var paths = new HashSet<string>(aliases.Keys, StringComparer.Ordinal);
        foreach (var text in definitions.Values)
        foreach (var part in text.Elements("part"))
            if (part.Attribute("equalTo") is { } target && !paths.Contains(target.Value))
                throw new InvalidDataException("Reference PNR target is not a mapped scalar path.");
        return new(schema.Feed, name, schema with { Fields = mapped, PreserveWidths = true },
            fields.AsReadOnly(), new ReadOnlyDictionary<string, string>(aliases), required);

        XdrFieldDefinition Map(XdrFieldDefinition field, string path, bool conditional)
        {
            if (field.Kind == "struct")
                return field with { Members = field.Members!.Select(child => Map(child, path + "/" + child.Name, conditional)).ToArray() };
            if (field.Kind is "array" or "fixedArray" or "optional")
                return field with { Members = [Map(field.Members![0], path + (field.Kind == "optional" ? "/value" : "/*"), true)] };
            if (field.Kind == "union")
                return field with
                {
                    Cases = field.Cases!.ToDictionary(pair => pair.Key, pair => Map(pair.Value, path + "/" + pair.Value.Name, true)),
                    DefaultCase = field.DefaultCase is null ? null : Map(field.DefaultCase, path + "/" + field.DefaultCase.Name, true)
                };
            if (field.Kind != "string") return field;
            if (!definitions.TryGetValue(path, out var text))
                throw new InvalidDataException("An XDR string is missing its reviewed reference mapping.");
            used.Add(path);
            string alias = "/" + Required(text, "name");
            int offset = Number(text, "offset", true), length = Number(text, "length", true);
            if (length > field.Limit)
                throw new InvalidDataException("Reference baseline length exceeds the XDR string bounds.");
            bool optional = conditional || Flag(text, "optional"), allowEmpty = Flag(text, "allowEmpty");
            Regex? pattern = null;
            if (text.Attribute("pattern") is { } expression)
            {
                try { pattern = new Regex(@"\A(?:" + expression.Value + @")\z", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1)); }
                catch (Exception error) when (error is ArgumentException or NotSupportedException)
                { throw new InvalidDataException("Invalid reference text pattern.", error); }
            }
            var captures = new List<XdrCaptureDefinition>();
            var parts = text.Elements().ToArray();
            int consumed = 0;
            var partNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in parts)
            {
                Check(part, "part", ["name", "start", "length", "fixed", "equalTo"]);
                if (part.Elements().Any()) throw new InvalidDataException("Reference parts cannot contain children.");
                string partName = Identifier(Required(part, "name"));
                int start = Number(part, "start", true), width = Number(part, "length", true);
                if (!partNames.Add(partName) || start != consumed || width > length - consumed)
                    throw new InvalidDataException("Reference parts must cover their reviewed baseline without gaps or overlaps.");
                consumed += width;
                if (Flag(part, "fixed")) continue;
                captures.Add(new(partName, (string?)part.Attribute("equalTo") ?? ""));
                Add(path + "/" + partName, alias + "/" + partName, offset + 4 + start, width);
            }
            if (parts.Length > 0 && consumed != length) throw new InvalidDataException("Incomplete reference text parts.");
            if (captures.Count > 0 && (pattern is null || !pattern.GetGroupNames().Where(group => group != "0").ToHashSet(StringComparer.Ordinal).SetEquals(captures.Select(c => c.Name))))
                throw new InvalidDataException("Dynamic reference parts require an exact named-capture pattern.");
            if (parts.Length == 0)
            {
                if (pattern is not null && pattern.GetGroupNames().Length != 1)
                    throw new InvalidDataException("Reference patterns with captures require mapped parts.");
                Add(path, alias, offset + 4, length);
            }
            if (allowEmpty && captures.Count == 0)
                throw new InvalidDataException("allowEmpty requires a capture string.");
            return field with { Pattern = pattern, Captures = captures.Count == 0 ? null : captures.AsReadOnly(), AllowEmpty = allowEmpty };

            void Add(string sourcePath, string policyPath, int start, int width)
            {
                if (!aliases.TryAdd(sourcePath, policyPath)) throw new InvalidDataException("Duplicate reference part path.");
                fields.Add(new(policyPath, start, width));
                if (!optional && !allowEmpty) required.Add(sourcePath);
            }
        }
    }

    private static IReadOnlyList<ReferenceFieldSpan> ReadFields(XElement parent, byte[] payload)
    {
        var fields = new List<ReferenceFieldSpan>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int previousEnd = 0;
        foreach (var text in parent.Elements())
        {
            Check(text, "text", ["name", "offset", "length"]);
            string name = Identifier(Required(text, "name"));
            int offset = Number(text, "offset", allowZero: true);
            int length = Number(text, "length", allowZero: false);
            if (offset % 4 != 0 || offset < previousEnd || offset > payload.Length - 4 || length > payload.Length - offset - 4)
            {
                throw new InvalidDataException("Reference text spans are overlapping, unordered or outside the payload.");
            }
            int padding = (-length) & 3;
            int end = offset + 4 + length + padding;
            if (end > payload.Length || BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(offset, 4)) != length)
            {
                throw new InvalidDataException("Configured reference text length does not match its counted-string prefix.");
            }
            foreach (byte value in payload.AsSpan(offset + 4 + length, padding))
            {
                if (value != 0)
                {
                    throw new InvalidDataException("Reference text padding must be zero.");
                }
            }
            previousEnd = end;
            if (!text.Elements().Any())
            {
                Add("/" + name, offset + 4, length);
                continue;
            }
            int consumed = 0;
            var partNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in text.Elements())
            {
                Check(part, "part", ["name", "start", "length", "fixed"]);
                if (part.Elements().Any())
                {
                    throw new InvalidDataException("Reference text parts cannot contain children.");
                }
                int start = Number(part, "start", allowZero: true);
                int partLength = Number(part, "length", allowZero: false);
                if (start != consumed || partLength > length - consumed)
                {
                    throw new InvalidDataException("Reference text parts must cover their string without gaps or overlaps.");
                }
                string partName = Identifier(Required(part, "name"));
                if (!partNames.Add(partName))
                {
                    throw new InvalidDataException("Duplicate reference text part name.");
                }
                bool fixedPart = (string?)part.Attribute("fixed") switch
                {
                    null or "false" => false,
                    "true" => true,
                    _ => throw new InvalidDataException("Reference part fixed must be true or false.")
                };
                if (!fixedPart)
                {
                    Add("/" + name + "/" + partName, offset + 4 + start, partLength);
                }
                consumed += partLength;
            }
            if (consumed != length)
            {
                throw new InvalidDataException("Reference text parts did not cover the complete string.");
            }
        }
        if (fields.Count == 0)
        {
            throw new InvalidDataException("Reference layout has no configured text fields.");
        }
        return fields;

        void Add(string name, int offset, int length)
        {
            if (!names.Add(name))
            {
                throw new InvalidDataException("Duplicate reference field path.");
            }
            fields.Add(new(name, offset, length));
        }
    }

    private static int Number(XElement element, string attribute, bool allowZero)
    {
        if (!int.TryParse(Required(element, attribute), NumberStyles.None, CultureInfo.InvariantCulture, out int value) ||
            value < (allowZero ? 0 : 1) || value > 1048576)
        {
            throw new InvalidDataException("Invalid reference-layout offset or length.");
        }
        return value;
    }

    private static string Identifier(string name)
    {
        if (name.Length == 0 || !name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
        {
            throw new InvalidDataException("Reference-layout names require ASCII letters, digits or underscores.");
        }
        return name;
    }

    private static string Required(XElement element, string name) =>
        !string.IsNullOrWhiteSpace((string?)element.Attribute(name))
            ? element.Attribute(name)!.Value
            : throw new InvalidDataException("A required reference-layout setting is missing.");

    private static void Check(XElement element, string name, string[] attributes)
    {
        if (element.Name != name || element.Attributes().Any(attribute => !attributes.Contains(attribute.Name.ToString())) ||
            element.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value)))
        {
            throw new InvalidDataException("Unknown reference-layout element, attribute or text.");
        }
    }
}

/// <summary>Protects configured text in the approved reference frame while retaining every other byte.</summary>
public sealed class ReferenceLayoutFeedCodec(ReferenceFeedLayout layout) : IFeedCodec
{
    private readonly XdrFeedCodec? _schemaCodec = layout.Schema is null ? null : new(layout.Feed, layout.Schema);
    private sealed record ParsedLayout(FeedMessage Message, FeedEnvelope Envelope, ParsedFeed? SchemaFields = null);

    public ParsedFeed Parse(FeedMessage message)
    {
        if (message.Delivery.Feed != layout.Feed)
        {
            throw new InvalidDataException("Reference layout and delivery routes do not match.");
        }
        var envelope = FeedPayloadReader.Read(message);
        if (_schemaCodec is not null)
        {
            var decoded = _schemaCodec.Parse(message);
            var paths = decoded.Fields.Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
            if (layout.RequiredPaths.Any(path => !paths.Contains(path)))
                throw new InvalidDataException("A required reference field is missing from this XDR message.");
            var selected = decoded.Fields.Where(field => layout.Aliases.ContainsKey(field.Name))
                .Select(field => new FeedField(field.Id, layout.Aliases[field.Name], field.Value)).ToArray();
            return new(selected, new ParsedLayout(message, envelope, decoded));
        }
        layout.ValidatePayload(envelope.Payload);
        var fields = layout.Fields.Select(field => new FeedField(field.Name, field.Name,
            Encoding.ASCII.GetString(envelope.Payload, field.Offset, field.Length))).ToArray();
        return new(fields, new ParsedLayout(message, envelope));
    }

    public byte[] Serialize(ParsedFeed original, IReadOnlyDictionary<string, string> anonymizedFields)
    {
        var parsed = (ParsedLayout)original.Layout;
        if (parsed.SchemaFields is not null)
        {
            if (anonymizedFields.Count != original.Fields.Count)
                throw new InvalidDataException("Reference serialization requires exactly the parsed fields.");
            var values = parsed.SchemaFields.Fields.ToDictionary(field => field.Id, field => field.Value);
            foreach (var field in original.Fields)
            {
                if (!anonymizedFields.TryGetValue(field.Id, out string? value) || value is null ||
                    value.Length != field.Value.Length || value.Any(c => c is < ' ' or > '~') ||
                    FieldPolicy.IsPnr(field.Name) && value != field.Value)
                    throw new InvalidDataException("Reference protection must preserve field widths and PNRs.");
                values[field.Id] = value;
            }
            return _schemaCodec!.Serialize(parsed.SchemaFields, values);
        }
        if (anonymizedFields.Count != layout.Fields.Count)
        {
            throw new InvalidDataException("Reference serialization requires exactly the configured fields.");
        }
        byte[] output = parsed.Envelope.Payload.ToArray();
        foreach (var field in layout.Fields)
        {
            if (!anonymizedFields.TryGetValue(field.Name, out string? value) || value is null ||
                value.Length != field.Length || value.Any(character => character is < ' ' or > '~'))
            {
                throw new InvalidDataException("Reference protection must preserve each field's ASCII byte length. Configure same-length Mask or Replace actions.");
            }
            string before = original.Fields.Single(item => item.Id == field.Name).Value;
            if (FieldPolicy.IsPnr(field.Name) && value != before)
            {
                throw new InvalidDataException("Reference serialization cannot change a PNR.");
            }
            Encoding.ASCII.GetBytes(value).CopyTo(output, field.Offset);
        }
        layout.ValidatePayload(output);
        return parsed.Envelope.Serialize(output);
    }

    public void Validate(ParsedFeed original, ReadOnlyMemory<byte> serialized)
    {
        var before = (ParsedLayout)original.Layout;
        if (before.SchemaFields is not null) _schemaCodec!.Validate(before.SchemaFields, serialized);
        var rebuilt = Parse(new(before.Message.Delivery, serialized));
        var after = (ParsedLayout)rebuilt.Layout;
        if (after.Envelope.Format != before.Envelope.Format || after.Envelope.Header != before.Envelope.Header ||
            after.Envelope.Newline != before.Envelope.Newline || after.Envelope.FinalNewline != before.Envelope.FinalNewline)
        {
            throw new InvalidDataException("Reference serialization changed MQ metadata or the original container format.");
        }
        foreach (var field in original.Fields)
        {
            if (FieldPolicy.IsPnr(field.Name) && rebuilt.Fields.Single(item => item.Id == field.Id).Value != field.Value)
            {
                throw new InvalidDataException("Reference validation detected a changed PNR.");
            }
        }
    }
}
