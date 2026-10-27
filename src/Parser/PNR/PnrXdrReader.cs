using PresidioDemo.Feeds.Shared;
namespace PresidioDemo.Feeds.PNR;
/// <summary>Reads the observed PNR XDR sequence with shared checked primitives.</summary>
public sealed class PnrXdrReader
{
    private readonly XdrFeedCodec _codec;
    /// <summary>Initializes the reader with the observed ordered PNR schema.</summary>
    public PnrXdrReader(XdrFeedSchema schema)
    {
        if (schema.Feed != FeedType.PNR)
        {
            throw new InvalidDataException("PNR reader requires a PNR schema.");
        }
        _codec = new(FeedType.PNR, schema with { Fields = schema.Fields.Select(WithoutCaptures).ToArray() });
    }
    private static XdrFieldDefinition WithoutCaptures(XdrFieldDefinition field) => field with
    {
        Pattern = null, Captures = null, Variants = null,
        Members = field.Members?.Select(WithoutCaptures).ToArray(),
        Cases = field.Cases?.ToDictionary(pair => pair.Key, pair => WithoutCaptures(pair.Value)),
        DefaultCase = field.DefaultCase is null ? null : WithoutCaptures(field.DefaultCase)
    };
    /// <summary>Reads the whole message and rejects malformed or trailing data.</summary>
    public ParsedFeed Read(FeedMessage message) => _codec.Parse(message);
    internal XdrFeedCodec Codec => _codec;
}
