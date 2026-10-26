using PresidioDemo.Feeds.Shared;
using PresidioDemo.Transport;

namespace PresidioDemo.Logging.ETKT;

/// <summary>Formats transaction logs for the ETKT feed.</summary>
public sealed class EtktFeedLogger : IFeedLogger
{
    /// <inheritdoc />
    public FeedType Feed => FeedType.ETKT;

    /// <inheritdoc />
    public string Format(FeedMessage message, FeedWorkflowResult? result, string? reason)
    {
        if (message.Delivery.Feed != Feed)
        {
            throw new InvalidDataException("Feed logger and delivery route differ.");
        }
        return FeedTransactionLog.Format(message, result, reason);
    }
}
