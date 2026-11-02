using System.Text.Json;
using PresidioDemo.Feeds.Shared;

namespace PresidioDemo.Feeds.Seats;

/// <summary>Parses Seats messages using their required JSON root.</summary>
public sealed class SeatsParser
{
    private readonly JsonFeedProcessor _processor = new(FeedType.Seats, JsonTokenType.StartObject);

    /// <summary>Parses one complete feed message.</summary>
    public ParsedFeed Parse(FeedMessage message) => _processor.Parse(message);
}
