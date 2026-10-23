namespace PresidioDemo.Feeds.ETKT;

/// <summary>
/// Decodes the supplied type-5 family; does not reinterpret 5 as the documented enum 3.
/// </summary>
public sealed class EtktDecoder
{

    #region Methods

    /// <summary>
    /// Decodes a complete payload into the feed&apos;s reviewed structural representation.
    /// </summary>
    /// <param name="payload">The complete decoded payload bytes without transport metadata.</param>
    /// <returns>The complete decoded document with original values and wire locations.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    public EtktDocument Decode(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length == 0 || payload.Length > 4 * 1024 * 1024 || payload.Length % 4 != 0)
        {
            throw new InvalidDataException("ETKT payload must be complete, four-byte aligned and within 4 MiB.");
        }
        var reader = new EtktXdrReader(payload);
        var scalars = new List<EtktScalar>();
        foreach (var definition in EtktStructureDefinitions.SampleHistory)
        {
            int start = reader.Offset;
            string? text = definition.Kind == EtktWireKind.String ? reader.ReadString() : null;
            int? number = definition.Kind == EtktWireKind.String ? null : reader.ReadInt32();
            if (definition.Kind == EtktWireKind.Character && number is < -128 or > 127)
            {
                throw new InvalidDataException($"ETKT character slot is outside its signed-byte range at {start}.");
            }
            scalars.Add(new(definition, start, reader.Offset - start, number, text));
            int? expected = scalars.Count switch { 1 => 5, 2 => 1, 3 => 0, _ => null };
            if (expected.HasValue && number != expected)
            {
                throw new InvalidDataException($"ETKT sample profile expected constant {expected} at byte {start}; unsupported structure.");
            }
        }
        if (reader.Offset != reader.Length)
        {
            throw new InvalidDataException($"ETKT structure leaves unconsumed bytes at {reader.Offset}.");
        }
        return new(scalars.AsReadOnly(), payload.Length);
    }

    #endregion
}
