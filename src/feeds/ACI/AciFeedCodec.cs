using PresidioDemo.Feeds.Shared;

namespace PresidioDemo.Feeds.ACI;

/// <summary>ACI route: semantic decoding and full encoding, with existing XML policy projection.</summary>
public sealed class AciFeedCodec : IFeedCodec
{
    private readonly ReferenceLayoutFeedCodec _projection;
    private readonly AciParser _parser = new();
    private readonly AciSerializer _serializer = new();

    public AciFeedCodec(ReferenceFeedLayout configuration)
    {
        if (configuration.Feed != FeedType.ACI || configuration.Schema is null)
        {
            throw new InvalidDataException("ACI requires its reviewed schema-driven XML configuration.");
        }
        _projection = new(configuration);
    }

    public ParsedFeed Parse(FeedMessage message)
    {
        if (message.Delivery.Feed != FeedType.ACI)
        {
            throw new InvalidDataException("ACI codec and delivery route differ.");
        }
        var envelope = FeedPayloadReader.Read(message);
        var document = _parser.Parse(envelope.Payload);
        var fields = _projection.Parse(message);
        var bindings = AciParser.Bind(document, fields, ReferenceLayoutFeedCodec.TextLocations(fields));
        return new(fields.Fields, new AciParsedLayout(message, envelope, document, bindings));
    }

    public byte[] Serialize(ParsedFeed original, IReadOnlyDictionary<string, string> anonymizedFields) =>
        _serializer.Serialize((AciParsedLayout)original.Layout, anonymizedFields);

    public void Validate(ParsedFeed original, ReadOnlyMemory<byte> serialized)
    {
        var before = (AciParsedLayout)original.Layout;
        var after = (AciParsedLayout)Parse(before.Message with { Content = serialized }).Layout;
        _serializer.Validate(before, after);
    }
}
