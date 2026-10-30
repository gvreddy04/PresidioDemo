using System.Text.Json;
using PresidioDemo.Feeds.Shared;

namespace PresidioDemo.Feeds.PNRLinking;

/// <summary>Parses PNRLinking messages using their required JSON root.</summary>
public sealed class PnrLinkingParser
{
    private readonly JsonFeedProcessor _processor = new(FeedType.PnrLinking, JsonTokenType.StartArray);

    /// <summary>Parses one complete feed message.</summary>
    public ParsedFeed Parse(FeedMessage message) => _processor.Parse(message);
}
