using PresidioDemo.Feeds.Shared;
using PresidioDemo.Transport;

namespace PresidioDemo.Logging.PNR;

/// <summary>Formats transaction logs for the PNR feed.</summary>
public sealed class PnrFeedLogger : IFeedLogger
{
    /// <inheritdoc />
    public FeedType Feed => FeedType.PNR;

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
