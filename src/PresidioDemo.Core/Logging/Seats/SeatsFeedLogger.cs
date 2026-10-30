using PresidioDemo.Feeds.Shared;
using PresidioDemo.Transport;

namespace PresidioDemo.Logging.Seats;

/// <summary>Formats transaction logs for the Seats feed.</summary>
public sealed class SeatsFeedLogger : IFeedLogger
{
    /// <inheritdoc />
    public FeedType Feed => FeedType.Seats;

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
