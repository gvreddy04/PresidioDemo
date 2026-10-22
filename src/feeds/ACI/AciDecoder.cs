namespace PresidioDemo.Feeds.ACI;

/// <summary>Decodes declared field order, recursive list pointers and the reviewed extension.</summary>
public sealed class AciDecoder
{
    public AciDocument Decode(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length == 0 || payload.Length > 4 * 1024 * 1024 || payload.Length % 4 != 0)
        {
            throw new InvalidDataException("ACI payload must be complete, four-byte aligned and within 4 MiB.");
        }
        var reader = new AciXdrReader(payload);
        var scalars = new List<AciScalar>();
        int utype = reader.ReadInt32();
        scalars.Add(new("/utype", AciWireKind.Int32, 0, 4, utype));
        AciNode? flights = null, passengers = null;
        string? error = null;
        switch (utype)
        {
            case 1:
                error = ReadScalar("/error_text", AciWireKind.String).Text;
                break;
            case 2:
                flights = ReadPointer("flight_list", "/flight_list", 0).Node;
                passengers = ReadPointer("pax_list", "/pax_list", 0).Node;
                break;
            default:
                throw new InvalidDataException("ACI utype expected constant ERROR=1 or DATA=2.");
        }
        if (reader.Offset != reader.Length)
        {
            throw new InvalidDataException($"ACI structure leaves unconsumed bytes at {reader.Offset}.");
        }
        return new(utype, flights, passengers, error, scalars.AsReadOnly(), payload.Length);

        AciScalar ReadScalar(string path, AciWireKind kind)
        {
            if (scalars.Count >= 100000)
            {
                throw new InvalidDataException("ACI scalar limit exceeded.");
            }
            int start = reader.Offset;
            string? text = kind == AciWireKind.String ? reader.ReadString() : null;
            int? number = kind == AciWireKind.String ? null : reader.ReadInt32();
            var scalar = new AciScalar(path, kind, start, reader.Offset - start, number, text);
            scalars.Add(scalar);
            return scalar;
        }

        (AciScalar Presence, AciNode? Node) ReadPointer(string type, string path, int depth)
        {
            if (depth > 128 || scalars.Count >= 100000)
            {
                throw new InvalidDataException("ACI structure nesting or scalar limit exceeded.");
            }
            int start = reader.Offset;
            int present = reader.ReadPresence();
            var presence = new AciScalar(path + "/$present", AciWireKind.Int32, start, 4, present);
            scalars.Add(presence);
            if (present == 0)
            {
                return (presence, null);
            }
            var members = new List<AciMember>();
            foreach (var definition in AciStructureDefinitions.All[type].Fields)
            {
                string fieldPath = path + "/" + definition.Name;
                if (definition.Kind == AciWireKind.Pointer)
                {
                    var child = ReadPointer(definition.Target, fieldPath, depth + 1);
                    members.Add(new(definition, Presence: child.Presence, Child: child.Node));
                }
                else
                {
                    members.Add(new(definition, Scalar: ReadScalar(fieldPath, definition.Kind)));
                }
            }
            return (presence, new(type, path, members.AsReadOnly()));
        }
    }
}
