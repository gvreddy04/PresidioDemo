using System.Text.Json;
using PresidioDemo.Feeds.Shared;

namespace PresidioDemo.Feeds.Shared;

/// <summary>Uses the common JSON implementation for a particular feed and root type.</summary>
internal sealed class JsonFeedProcessor(FeedType feed, JsonTokenType rootKind) : JsonFeedCodec(feed, rootKind)
{
}
