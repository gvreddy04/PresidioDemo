using System.Text.Json;
using PresidioDemo.Feeds.Shared;

namespace PresidioDemo.Feeds.Shared;

/// <summary>Reconstructs and validates JSON using the common feed codec.</summary>
internal sealed class JsonFeedSerializer(FeedType feed, JsonTokenType rootKind)
{
    private readonly JsonFeedProcessor _processor = new(feed, rootKind);

    internal byte[] Serialize(ParsedFeed original, IReadOnlyDictionary<string, string> fields) => _processor.Serialize(original, fields);

    internal void Validate(ParsedFeed original, ReadOnlyMemory<byte> output) => _processor.Validate(original, output);
}
