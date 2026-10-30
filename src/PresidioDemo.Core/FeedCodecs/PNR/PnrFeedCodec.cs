using PresidioDemo.Feeds.Shared;
namespace PresidioDemo.Feeds.PNR;
/// <summary>PNR route combining semantic decoding and the existing protection projection.</summary>
public sealed class PnrFeedCodec : IFeedCodec
{
    private readonly ReferenceLayoutFeedCodec _projection;
    private readonly PnrParser _parser;
    private readonly PnrSerializer _serializer;
    /// <summary>Initializes the reviewed sample-family PNR route.</summary>
    public PnrFeedCodec(ReferenceFeedLayout configuration)
    {
        if (configuration.Feed != FeedType.PNR || configuration.Schema is null)
        {
            throw new InvalidDataException("PNR requires its reviewed schema-driven configuration.");
        }
        _projection = new(configuration);
        _parser = new(new PnrDecoder(new PnrXdrReader(configuration.Schema)));
        _serializer = new(_projection);
    }
    /// <summary>Decodes the message and extracts protection fields.</summary>
    public ParsedFeed Parse(FeedMessage message)
    {
        var document = _parser.Parse(message);
        var projection = _projection.Parse(message);
        return new(projection.Fields, new PnrParsedLayout(projection, document));
    }
    /// <summary>Gets the complete diagnostic document from a parsed message.</summary>
    public static PnrDocument Document(ParsedFeed parsed) => ((PnrParsedLayout)parsed.Layout).Document;
    /// <summary>Reconstructs the protected message.</summary>
    public byte[] Serialize(ParsedFeed original, IReadOnlyDictionary<string, string> anonymizedFields) => _serializer.Serialize((PnrParsedLayout)original.Layout, anonymizedFields);
    /// <summary>Validates framing, values and the PNR locator.</summary>
    public void Validate(ParsedFeed original, ReadOnlyMemory<byte> serialized) => _serializer.Validate((PnrParsedLayout)original.Layout, serialized);
}
