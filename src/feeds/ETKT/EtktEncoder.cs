namespace PresidioDemo.Feeds.ETKT;

/// <summary>
/// Rebuilds every declared field; never patches byte offsets in the original payload.
/// </summary>
public sealed class EtktEncoder
{

    #region Methods

    /// <summary>
    /// Rebuilds the complete feed payload from its decoded structure and selected text replacements.
    /// </summary>
    /// <param name="document">The decoded ETKT document containing original values and wire locations.</param>
    /// <param name="textValues">The selected text replacements indexed by decoded scalar path.</param>
    /// <returns>The complete rebuilt payload bytes, excluding any MQ envelope.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    public byte[] Encode(EtktDocument document, IReadOnlyDictionary<string, string>? textValues = null)
    {
        var definitions = EtktStructureDefinitions.SampleHistory;
        if (document.Scalars.Count != definitions.Count)
        {
            throw new InvalidDataException("ETKT document does not match the sample profile.");
        }
        using (var writer = new EtktXdrWriter())
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < definitions.Count; index++)
            {
                var scalar = document.Scalars[index];
                if (scalar.Definition != definitions[index])
                {
                    throw new InvalidDataException("ETKT field order or declaration changed.");
                }
                if (scalar.Kind == EtktWireKind.String)
                {
                    string value = scalar.Text ?? throw new InvalidDataException("Missing ETKT text value.");
                    if (textValues is not null && textValues.TryGetValue(scalar.Path, out var replacement))
                    {
                        value = replacement;
                        used.Add(scalar.Path);
                    }
                    writer.WriteString(value);
                }
                else
                {
                    int value = scalar.Number ?? throw new InvalidDataException("Missing ETKT numeric value.");
                    int? expected = index switch { 0 => 5, 1 => 1, 2 => 0, _ => null };
                    if (expected.HasValue && value != expected || scalar.Kind == EtktWireKind.Character && value is < -128 or > 127)
                    {
                        throw new InvalidDataException("ETKT scalar violates the sample profile.");
                    }
                    writer.WriteInt32(value);
                }
            }
            if (textValues is not null && !used.SetEquals(textValues.Keys))
            {
                throw new InvalidDataException("ETKT replacement paths do not match encoded strings.");
            }
            return writer.ToArray();
        }
    }

    #endregion
}
