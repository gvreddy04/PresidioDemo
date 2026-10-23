using PresidioDemo.Feeds.ACI;
using PresidioDemo.Feeds.ETKT;
using PresidioDemo.Feeds.PNRLinking;
using PresidioDemo.Feeds.Seats;
using PresidioDemo.Feeds.Shared;

namespace PresidioDemo.Pipeline;

public static class FeedCodecRegistry
{
    public static IFeedCodec Create(FeedType feed) => Create(feed, new Dictionary<FeedType, XdrFeedSchema>());

    public static IFeedCodec Create(FeedType feed, IReadOnlyDictionary<FeedType, XdrFeedSchema> schemas) =>
        Create(feed, schemas, new Dictionary<FeedType, ReferenceFeedLayout>());

    public static IFeedCodec Create(FeedType feed, IReadOnlyDictionary<FeedType, XdrFeedSchema> schemas,
        IReadOnlyDictionary<FeedType, ReferenceFeedLayout> references)
    {
        if (feed == FeedType.Seats)
        {
            return new SeatsFeedCodec();
        }
        if (feed == FeedType.PnrLinking)
        {
            return new PnrLinkingFeedCodec();
        }
        if (references.TryGetValue(feed, out var dynamicReference) && dynamicReference.Schema is not null)
        {
            if (feed == FeedType.ACI && dynamicReference.UseAciStructures)
            {
                return new AciFeedCodec(dynamicReference);
            }
            if (feed == FeedType.ETKT && dynamicReference.UseEtktStructures)
            {
                return new EtktFeedCodec(dynamicReference);
            }
            return new ReferenceLayoutFeedCodec(dynamicReference);
        }
        if (schemas.TryGetValue(feed, out var schema))
        {
            return new XdrFeedCodec(feed, schema);
        }
        if (references.TryGetValue(feed, out var reference))
        {
            return new ReferenceLayoutFeedCodec(reference);
        }
        return new XdrFeedCodec(feed, null);
    }
}
