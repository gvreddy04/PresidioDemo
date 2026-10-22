namespace PresidioDemo.Feeds.ACI;

public enum AciWireKind { Int32, String, Pointer }

public sealed record AciFieldDefinition(string Name, AciWireKind Kind, string Target = "");
public sealed record AciStructureDefinition(string Name,
    IReadOnlyList<AciFieldDefinition> Fields, bool ObservedOnly = false);

/// <summary>Offsets identify wire locations, never passenger identity.</summary>
public sealed record AciScalar(string Path, AciWireKind Kind, int Offset, int Width,
    int? Number = null, string? Text = null)
{
    public int ValueOffset => Kind == AciWireKind.String ? Offset + 4 : Offset;
    public int ValueLength => Kind == AciWireKind.String ? Text!.Length : 4;
}

public sealed record AciMember(AciFieldDefinition Definition, AciScalar? Scalar = null,
    AciScalar? Presence = null, AciNode? Child = null);
public sealed record AciNode(string Type, string Path, IReadOnlyList<AciMember> Members)
{
    public AciScalar Scalar(string name) => Members.Single(member => member.Definition.Name == name).Scalar
        ?? throw new InvalidDataException("ACI member is not a scalar.");
    public AciNode? Child(string name) => Members.Single(member => member.Definition.Name == name).Child;
}

public sealed record AciDocument(int Utype, AciNode? FlightList, AciNode? PassengerList,
    string? ErrorText, IReadOnlyList<AciScalar> Scalars, int PayloadLength);

internal sealed record AciTextBinding(FeedField Field, AciScalar Scalar, int Start);
internal sealed record AciParsedLayout(FeedMessage Message, FeedEnvelope Envelope,
    AciDocument Document, IReadOnlyList<AciTextBinding> Bindings);
