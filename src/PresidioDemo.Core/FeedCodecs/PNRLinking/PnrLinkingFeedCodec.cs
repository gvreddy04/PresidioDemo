using System.Text.Json;
using PresidioDemo.Feeds.Shared;

namespace PresidioDemo.Feeds.PNRLinking;

/// <summary>Coordinates the PNRLinking parser and serializer.</summary>
public sealed class PnrLinkingFeedCodec() : JsonFeedCodec(FeedType.PnrLinking, JsonTokenType.StartArray)
{
    private readonly PnrLinkingParser _parser = new();
    private readonly PnrLinkingSerializer _serializer = new();

    /// <inheritdoc />
    public override ParsedFeed Parse(FeedMessage message) => _parser.Parse(message);

    /// <inheritdoc />
    public override byte[] Serialize(ParsedFeed original, IReadOnlyDictionary<string, string> anonymizedFields) => _serializer.Serialize(original, anonymizedFields);

    /// <inheritdoc />
    public override void Validate(ParsedFeed original, ReadOnlyMemory<byte> serialized) => _serializer.Validate(original, serialized);
}
