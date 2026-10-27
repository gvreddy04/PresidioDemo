namespace PresidioDemo.Feeds.ACI;

/// <summary>
/// Rebuilds the complete payload in declaration order; no offset patching.
/// </summary>
public sealed class AciEncoder
{

    #region Methods

    /// <summary>
    /// Rebuilds the complete feed payload from its decoded structure and selected text replacements.
    /// </summary>
    /// <param name="document">The decoded ACI document containing original values and wire locations.</param>
    /// <param name="textValues">The selected text replacements indexed by decoded scalar path.</param>
    /// <returns>The complete rebuilt payload bytes, excluding any MQ envelope.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    public byte[] Encode(AciDocument document, IReadOnlyDictionary<string, string>? textValues = null)
    {
        using (var writer = new AciXdrWriter())
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            writer.WriteInt32(document.Utype);
            switch (document.Utype)
            {
                case 1:
                    writer.WriteString(document.ErrorText ?? throw new InvalidDataException("Missing ACI error text."));
                    break;
                case 2:
                    WritePointer(document.FlightList, "flight_list", 0);
                    WritePointer(document.PassengerList, "pax_list", 0);
                    break;
                default:
                    throw new InvalidDataException("Unknown ACI union discriminator.");
            }
            if (textValues is not null && !used.SetEquals(textValues.Keys))
            {
                throw new InvalidDataException("ACI replacement names do not match encoded strings.");
            }
            return writer.ToArray();

            void WritePointer(AciNode? node, string expectedType, int depth)
            {
                if (depth > 128)
                {
                    throw new InvalidDataException("ACI encode nesting exceeds its limit.");
                }
                writer.WriteInt32(node is null ? 0 : 1);
                if (node is null)
                {
                    return;
                }
                var definitions = AciStructureDefinitions.All[expectedType].Fields;
                if (node.Type != expectedType || node.Members.Count != definitions.Count)
                {
                    throw new InvalidDataException("ACI node does not match its structure definition.");
                }
                for (int index = 0; index < definitions.Count; index++)
                {
                    var definition = definitions[index];
                    var member = node.Members[index];
                    if (member.Definition != definition)
                    {
                        throw new InvalidDataException("ACI field order changed.");
                    }
                    if (definition.Kind == AciWireKind.Pointer)
                    {
                        WritePointer(member.Child, definition.Target, depth + 1);
                    }
                    else if (definition.Kind == AciWireKind.String)
                    {
                        var scalar = member.Scalar!;
                        string value = scalar.Text!;
                        if (textValues is not null && textValues.TryGetValue(scalar.Path, out var replacement))
                        {
                            value = replacement;
                            used.Add(scalar.Path);
                        }
                        writer.WriteString(value);
                    }
                    else
                    {
                        writer.WriteInt32(member.Scalar!.Number!.Value);
                    }
                }
            }
        }
    }

    #endregion
}
