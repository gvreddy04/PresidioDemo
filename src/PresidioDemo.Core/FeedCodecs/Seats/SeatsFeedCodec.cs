using System.Text.Json;
using PresidioDemo.Feeds.Shared;

namespace PresidioDemo.Feeds.Seats;

/// <summary>Coordinates the Seats parser and serializer.</summary>
public sealed class SeatsFeedCodec() : JsonFeedCodec(FeedType.Seats, JsonTokenType.StartObject)
{
    private readonly SeatsParser _parser = new();
    private readonly SeatsSerializer _serializer = new();

    /// <inheritdoc />
    public override ParsedFeed Parse(FeedMessage message) => _parser.Parse(message);

    /// <inheritdoc />
    public override byte[] Serialize(ParsedFeed original, IReadOnlyDictionary<string, string> anonymizedFields) => _serializer.Serialize(original, anonymizedFields);

    /// <inheritdoc />
    public override void Validate(ParsedFeed original, ReadOnlyMemory<byte> serialized) => _serializer.Validate(original, serialized);
}
