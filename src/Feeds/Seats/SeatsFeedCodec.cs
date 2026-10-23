using System.Text.Json;
using PresidioDemo.Feeds.Shared;

namespace PresidioDemo.Feeds.Seats;

#region Classes

/// <summary>
/// Seats owns its object-root contract and existing Seats XML policy route.
/// </summary>
public sealed class SeatsFeedCodec() : JsonFeedCodec(FeedType.Seats, JsonTokenType.StartObject)
{
}

#endregion
