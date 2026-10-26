using PresidioDemo.Feeds.Shared;
using PresidioDemo.Transport;

namespace PresidioDemo.Logging.PNRLinking;

/// <summary>Formats transaction logs for the PNRLinking feed.</summary>
public sealed class PnrLinkingFeedLogger : IFeedLogger
{
    /// <inheritdoc />
    public FeedType Feed => FeedType.PnrLinking;

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
