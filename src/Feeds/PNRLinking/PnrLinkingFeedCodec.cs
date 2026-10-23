using System.Text.Json;
using PresidioDemo.Feeds.Shared;

namespace PresidioDemo.Feeds.PNRLinking;

#region Classes

/// <summary>
/// PNR-Linking owns its array-root contract and existing PNR-Linking XML policy route.
/// </summary>
public sealed class PnrLinkingFeedCodec() : JsonFeedCodec(FeedType.PnrLinking, JsonTokenType.StartArray)
{
}

#endregion
