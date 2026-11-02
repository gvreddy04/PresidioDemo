using PresidioDemo.Feeds.Shared;

namespace PresidioDemo.Feeds.ETKT;

/// <summary>
/// Separates structural decoding from reviewed protection-field bindings.
/// </summary>
public sealed class EtktParser
{

    #region Methods

    /// <summary>
    /// Parses the complete input, validates its supported shape and retains the layout required for reconstruction.
    /// </summary>
    /// <param name="payload">The complete decoded payload bytes without transport metadata.</param>
    /// <returns>The validated ETKT document with its complete original scalar and structure values.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
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

    /// <summary>
    /// Matches every reviewed protection field to exactly one decoded text span and verifies coverage.
    /// </summary>
    /// <param name="document">The decoded ETKT document containing original values and wire locations.</param>
    /// <param name="fields">The complete extracted field set and its retained codec-specific serialization layout.</param>
    /// <param name="locations">The exact reviewed payload offsets and lengths indexed by protection-field identifier.</param>
    /// <returns>The validated bindings between reviewed protection fields and decoded text spans.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
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

    #endregion
}
