namespace PresidioDemo.Feeds.ETKT;

/// <summary>Complete type-5 reconstruction using the existing XML protection projection.</summary>
public sealed class EtktFeedCodec : IFeedCodec
{
    private readonly ReferenceLayoutFeedCodec _projection;
    private readonly EtktParser _parser = new();
    private readonly EtktSerializer _serializer = new();

    public EtktFeedCodec(ReferenceFeedLayout configuration)
    {
        if (configuration.Feed != FeedType.ETKT || configuration.Schema is null)
        {
            throw new InvalidDataException("ETKT requires its reviewed schema-driven XML configuration.");
        }
        _projection = new(configuration);
    }

    public ParsedFeed Parse(FeedMessage message)
    {
        if (message.Delivery.Feed != FeedType.ETKT)
        {
            throw new InvalidDataException("ETKT codec and delivery route differ.");
        }
        var envelope = FeedPayloadReader.Read(message);
        var document = _parser.Parse(envelope.Payload);
        var fields = _projection.Parse(message);
        var bindings = EtktParser.Bind(document, fields, ReferenceLayoutFeedCodec.TextLocations(fields));
        return new(fields.Fields, new EtktParsedLayout(message, envelope, document, bindings));
    }

    public byte[] Serialize(ParsedFeed original, IReadOnlyDictionary<string, string> anonymizedFields) =>
        _serializer.Serialize((EtktParsedLayout)original.Layout, anonymizedFields);

    public void Validate(ParsedFeed original, ReadOnlyMemory<byte> serialized)
    {
        var before = (EtktParsedLayout)original.Layout;
        var after = (EtktParsedLayout)Parse(before.Message with { Content = serialized }).Layout;
        _serializer.Validate(before, after);
    }
}
