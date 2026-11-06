using PresidioDemo.Feeds.PNRLinking;
using PresidioDemo.Feeds.Seats;
using PresidioDemo.Feeds.Shared;

namespace PresidioDemo.Pipeline;

/// <summary>
/// Selects the decoded-text codec for a delivery route: the generic XDR codec for binary feeds (no per-feed schema),
/// the shared JSON codec for JSON feeds.
/// </summary>
public static class FeedCodecRegistry
{

    #region Methods

    /// <summary>
    /// Returns the codec for a feed route.
    /// </summary>
    /// <param name="feed">The supported feed route.</param>
    /// <returns>The codec.</returns>
    public static IFeedCodec Create(FeedType feed) => feed switch
    {
        FeedType.Seats => new SeatsFeedCodec(),
        FeedType.PnrLinking => new PnrLinkingFeedCodec(),
        FeedType.ACI or FeedType.ETKT or FeedType.PNR => new GenericXdrFeedCodec(feed),
        _ => throw new InvalidDataException("No codec is registered for this feed.")
    };

    #endregion
}
