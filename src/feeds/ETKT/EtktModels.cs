namespace PresidioDemo.Feeds.ETKT;

public enum EtktWireKind { Int32, Character, String }

public sealed record EtktFieldDefinition(string Path, string SourceName, string Group, EtktWireKind Kind);

/// <summary>Locations describe this message's wire layout, never passenger identity.</summary>
public sealed record EtktScalar(EtktFieldDefinition Definition, int Offset, int Width,
    int? Number = null, string? Text = null)
{
    public string Path => Definition.Path;
    public EtktWireKind Kind => Definition.Kind;
    public int ValueOffset => Kind == EtktWireKind.String ? Offset + 4 : Offset;
    public int ValueLength => Kind == EtktWireKind.String ? Text!.Length : 4;
}

/// <summary>Observed type-5 framing with a body aligned to the documented history order.</summary>
public sealed record EtktDocument(IReadOnlyList<EtktScalar> Scalars, int PayloadLength)
{
    public int SampleType => Scalars[0].Number!.Value;
    public EtktScalar Field(string path) => Scalars.Single(scalar => scalar.Path == path);
}

internal sealed record EtktTextBinding(FeedField Field, EtktScalar Scalar, int Start);
internal sealed record EtktParsedLayout(FeedMessage Message, FeedEnvelope Envelope,
    EtktDocument Document, IReadOnlyList<EtktTextBinding> Bindings);
