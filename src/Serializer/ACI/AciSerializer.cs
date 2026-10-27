using PresidioDemo.Protection;

namespace PresidioDemo.Feeds.ACI;

/// <summary>
/// Applies reviewed text spans, rebuilds XDR and preserves the original MQ envelope.
/// </summary>
public sealed class AciSerializer
{

    #region Methods

    /// <summary>
    /// Reconstructs the original message representation using the complete protected field set.
    /// </summary>
    /// <param name="layout">The retained codec layout used for reconstruction or validation.</param>
    /// <param name="protectedFields">The complete protected values indexed by original field identifier.</param>
    /// <returns>The complete reconstructed message in its original transport representation.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    internal byte[] Serialize(AciParsedLayout layout, IReadOnlyDictionary<string, string> protectedFields)
    {
        if (protectedFields.Count != layout.Bindings.Count)
        {
            throw new InvalidDataException("ACI serialization requires exactly the parsed protection fields.");
        }
        var textValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in layout.Bindings.GroupBy(binding => binding.Scalar.Path))
        {
            char[] value = group.First().Scalar.Text!.ToCharArray();
            foreach (var binding in group)
            {
                if (!protectedFields.TryGetValue(binding.Field.Id, out var replacement) || replacement is null ||
                    replacement.Length != binding.Field.Value.Length || replacement.Any(character => character is < ' ' or > '~') ||
                    FieldPolicy.IsPnr(binding.Field.Name) && replacement != binding.Field.Value)
                {
                    throw new InvalidDataException("ACI protection must preserve field widths, printable ASCII and PNRs.");
                }
                replacement.CopyTo(0, value, binding.Start, replacement.Length);
            }
            textValues.Add(group.Key, new string(value));
        }
        byte[] payload = new AciEncoder().Encode(layout.Document, textValues);
        return layout.Envelope.Serialize(payload);
    }

    /// <summary>
    /// Rejects reconstructed data or settings that violate the preserved structure and protection contract.
    /// </summary>
    /// <param name="before">The original message or decoded layout retained before protection.</param>
    /// <param name="after">The reconstructed message or decoded layout being validated.</param>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    internal void Validate(AciParsedLayout before, AciParsedLayout after)
    {
        if (before.Envelope.Format != after.Envelope.Format || before.Envelope.Header != after.Envelope.Header ||
            before.Envelope.Newline != after.Envelope.Newline || before.Envelope.FinalNewline != after.Envelope.FinalNewline ||
            before.Document.PayloadLength != after.Document.PayloadLength || before.Document.Scalars.Count != after.Document.Scalars.Count)
        {
            throw new InvalidDataException("ACI serialization changed framing, metadata or structure.");
        }
        var mutable = new bool[before.Document.PayloadLength];
        foreach (var binding in before.Bindings)
        {
            if (!FieldPolicy.IsPnr(binding.Field.Name))
            {
                Array.Fill(mutable, true, binding.Scalar.ValueOffset + binding.Start, binding.Field.Value.Length);
            }
        }
        for (int index = 0; index < before.Document.Scalars.Count; index++)
        {
            var left = before.Document.Scalars[index];
            var right = after.Document.Scalars[index];
            if (left.Path != right.Path || left.Kind != right.Kind || left.Offset != right.Offset || left.Width != right.Width)
            {
                throw new InvalidDataException("ACI field order or widths changed during serialization.");
            }
        }
        for (int offset = 0; offset < mutable.Length; offset++)
        {
            if (!mutable[offset] && before.Envelope.Payload[offset] != after.Envelope.Payload[offset])
            {
                throw new InvalidDataException("ACI framing, numeric values, PNR or unselected bytes changed.");
            }
        }
    }

    #endregion
}
