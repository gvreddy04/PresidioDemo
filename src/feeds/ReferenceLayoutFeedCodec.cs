using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PresidioDemo.Feeds;

public sealed record ReferenceFieldSpan(string Name, int Offset, int Length);

/// <summary>A pinned reference frame with explicitly configured, fixed-width ASCII fields.</summary>
public sealed class ReferenceFeedLayout
{
    private readonly byte[] _template;
    private readonly bool[] _fieldBytes;

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
    public static IReadOnlyDictionary<FeedType, ReferenceFeedLayout> Load(string filename)
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
            Check(element, "feed", ["type", "name", "referenceFile", "referenceSha256"]);
            var feed = FeedCatalog.All.SingleOrDefault(item => item.FolderName == Required(element, "type"))
                ?? throw new InvalidDataException("Unknown reference-layout feed route.");
            if (feed.Type is FeedType.Seats or FeedType.PnrLinking || layouts.ContainsKey(feed.Type))
            {
                throw new InvalidDataException("Duplicate or non-binary reference-layout route.");
            }
            string name = Identifier(Required(element, "name"));
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
            foreach (var part in text.Elements())
            {
                Check(part, "part", ["name", "start", "length"]);
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
                Add("/" + name + "/" + Identifier(Required(part, "name")), offset + 4 + start, partLength);
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
    private sealed record ParsedLayout(FeedMessage Message, FeedEnvelope Envelope);

    public ParsedFeed Parse(FeedMessage message)
    {
        if (message.Delivery.Feed != layout.Feed)
        {
            throw new InvalidDataException("Reference layout and delivery routes do not match.");
        }
        var envelope = FeedPayloadReader.Read(message);
        layout.ValidatePayload(envelope.Payload);
        var fields = layout.Fields.Select(field => new FeedField(field.Name, field.Name,
            Encoding.ASCII.GetString(envelope.Payload, field.Offset, field.Length))).ToArray();
        return new(fields, new ParsedLayout(message, envelope));
    }

    public byte[] Serialize(ParsedFeed original, IReadOnlyDictionary<string, string> anonymizedFields)
    {
        var parsed = (ParsedLayout)original.Layout;
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
