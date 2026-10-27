using System.Text.Json;
using PresidioDemo.Feeds.Shared;

namespace PresidioDemo.Feeds.PNRLinking;

/// <summary>Reconstructs and validates PNRLinking messages using shared JSON processing.</summary>
public sealed class PnrLinkingSerializer
{
    private readonly JsonFeedSerializer _processor = new(FeedType.PnrLinking, JsonTokenType.StartArray);

    /// <summary>Reconstructs one message with its protected values.</summary>
    public byte[] Serialize(ParsedFeed original, IReadOnlyDictionary<string, string> fields) => _processor.Serialize(original, fields);

    /// <summary>Validates the reconstructed message and preserved fields.</summary>
    public void Validate(ParsedFeed original, ReadOnlyMemory<byte> output) => _processor.Validate(original, output);
}
