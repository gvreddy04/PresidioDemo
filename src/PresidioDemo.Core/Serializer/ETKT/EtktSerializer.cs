using PresidioDemo.Protection;

namespace PresidioDemo.Feeds.ETKT;

/// <summary>
/// Applies reviewed spans and rebuilds XDR while preserving the MQ envelope.
/// </summary>
public sealed class EtktSerializer
{

    #region Methods

    /// <summary>
    /// Reconstructs the original message representation using the complete protected field set.
    /// </summary>
    /// <param name="layout">The retained codec layout used for reconstruction or validation.</param>
    /// <param name="protectedFields">The complete protected values indexed by original field identifier.</param>
    /// <returns>The complete reconstructed message in its original transport representation.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    internal byte[] Serialize(EtktParsedLayout layout, IReadOnlyDictionary<string, string> protectedFields)
    {
        if (protectedFields.Count != layout.Bindings.Count)
        {
            throw new InvalidDataException("ETKT serialization requires exactly the parsed protection fields.");
        }
        var textValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in layout.Bindings.GroupBy(binding => binding.Scalar.Path))
        {
            char[] value = group.First().Scalar.Text!.ToCharArray();
            foreach (var binding in group)
            {
                if (!protectedFields.TryGetValue(binding.Field.Id, out var replacement) || replacement is null ||
                    replacement.Length != binding.Field.Value.Length || replacement.Any(character => character is < ' ' or > '~') ||
                    MustPreserve(binding) && replacement != binding.Field.Value)
                {
                    throw new InvalidDataException("ETKT protection must preserve field widths, printable ASCII, PNRs and the agent terminal address.");
                }
                replacement.CopyTo(0, value, binding.Start, replacement.Length);
            }
            textValues.Add(group.Key, new string(value));
        }
        byte[] payload = new EtktEncoder().Encode(layout.Document, textValues);
        return layout.Envelope.Serialize(payload);
    }

    /// <summary>
    /// Rejects reconstructed data or settings that violate the preserved structure and protection contract.
    /// </summary>
    /// <param name="before">The original message or decoded layout retained before protection.</param>
    /// <param name="after">The reconstructed message or decoded layout being validated.</param>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    internal void Validate(EtktParsedLayout before, EtktParsedLayout after)
    {
        if (before.Envelope.Format != after.Envelope.Format || before.Envelope.Header != after.Envelope.Header ||
            before.Envelope.Newline != after.Envelope.Newline || before.Envelope.FinalNewline != after.Envelope.FinalNewline ||
            before.Envelope.HexRowBytes != after.Envelope.HexRowBytes ||
            before.Document.PayloadLength != after.Document.PayloadLength || before.Document.Scalars.Count != after.Document.Scalars.Count)
        {
            throw new InvalidDataException("ETKT serialization changed framing, metadata or structure.");
        }
        var mutable = new bool[before.Document.PayloadLength];
        foreach (var binding in before.Bindings)
        {
            if (!MustPreserve(binding))
            {
                Array.Fill(mutable, true, binding.Scalar.ValueOffset + binding.Start, binding.Field.Value.Length);
            }
        }
        for (int index = 0; index < before.Document.Scalars.Count; index++)
        {
            var left = before.Document.Scalars[index];
            var right = after.Document.Scalars[index];
            if (left.Definition != right.Definition || left.Offset != right.Offset || left.Width != right.Width)
            {
                throw new InvalidDataException("ETKT field order or widths changed during serialization.");
            }
        }
        for (int offset = 0; offset < mutable.Length; offset++)
        {
            if (!mutable[offset] && before.Envelope.Payload[offset] != after.Envelope.Payload[offset])
            {
                throw new InvalidDataException("ETKT framing, numeric values, kept fields or unselected bytes changed.");
            }
        }
    }

    /// <summary>
    /// Determines whether an ETKT binding represents a PNR or the agent terminal address.
    /// </summary>
    /// <param name="binding">The reviewed association between a field and its containing scalar.</param>
    /// <returns>True for a PNR or the agent terminal address; otherwise, false.</returns>
    private static bool MustPreserve(EtktTextBinding binding) => FieldPolicy.IsPnr(binding.Field.Name) ||
            binding.Scalar.Path is "/history/h_new_recloc" or "/history/h_old_recloc" or "/history/h_aaa_lniata";

    #endregion
}
