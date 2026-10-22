namespace PresidioDemo.Feeds.ETKT;

/// <summary>Separates structural decoding from reviewed protection-field bindings.</summary>
public sealed class EtktParser
{
    public EtktDocument Parse(ReadOnlyMemory<byte> payload)
    {
        var document = new EtktDecoder().Decode(payload);
        foreach (string path in new[] { "/history/h_new_recloc", "/history/h_old_recloc" })
        {
            string value = document.Field(path).Text!;
            if (value.Length != 0 && (value.Length != 6 || value.Any(character => character is not (>= 'A' and <= 'Z' or >= '0' and <= '9'))))
            {
                throw new InvalidDataException("ETKT divide-event PNRs must be empty or six uppercase letters/digits.");
            }
        }
        return document;
    }

    internal static IReadOnlyList<EtktTextBinding> Bind(EtktDocument document, ParsedFeed fields,
        IReadOnlyDictionary<string, (int Offset, int Length)> locations)
    {
        var bindings = new List<EtktTextBinding>();
        var coverage = new bool[document.PayloadLength];
        foreach (var field in fields.Fields)
        {
            if (!locations.TryGetValue(field.Id, out var span))
            {
                throw new InvalidDataException("ETKT protection field has no XDR string location.");
            }
            var candidates = document.Scalars.Where(scalar => scalar.Kind == EtktWireKind.String &&
                span.Offset >= scalar.ValueOffset && span.Offset + span.Length <= scalar.ValueOffset + scalar.ValueLength).ToArray();
            if (candidates.Length != 1 || span.Length != field.Value.Length)
            {
                throw new InvalidDataException("ETKT configured field does not match exactly one decoded string.");
            }
            var scalar = candidates[0];
            int start = span.Offset - scalar.ValueOffset;
            if (scalar.Text!.Substring(start, span.Length) != field.Value)
            {
                throw new InvalidDataException("ETKT configured field and semantic decoder disagree.");
            }
            for (int offset = span.Offset; offset < span.Offset + span.Length; offset++)
            {
                if (coverage[offset])
                {
                    throw new InvalidDataException("ETKT protection fields overlap.");
                }
                coverage[offset] = true;
            }
            bindings.Add(new(field, scalar, start));
        }
        foreach (var scalar in document.Scalars.Where(scalar => scalar.Kind == EtktWireKind.String))
        {
            for (int offset = scalar.ValueOffset; offset < scalar.ValueOffset + scalar.ValueLength; offset++)
            {
                if (!coverage[offset])
                {
                    throw new InvalidDataException($"Nonempty ETKT text at {scalar.Path} lacks a reviewed field policy.");
                }
            }
        }
        return bindings.AsReadOnly();
    }
}
