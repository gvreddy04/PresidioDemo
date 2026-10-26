using PresidioDemo.Feeds.Shared;
using PresidioDemo.Logging.ACI;
using PresidioDemo.Logging.ETKT;
using PresidioDemo.Logging.PNR;
using PresidioDemo.Logging.PNRLinking;
using PresidioDemo.Logging.Seats;

namespace PresidioDemo.Transport;

/// <summary>Formats transaction evidence for one feed.</summary>
public interface IFeedLogger
{
    /// <summary>Gets the feed route handled by this logger.</summary>
    FeedType Feed { get; }

    /// <summary>Formats a transaction using its original and protected message data.</summary>
    string Format(FeedMessage message, FeedWorkflowResult? result, string? reason);
}

/// <summary>Resolves the feed-specific transaction logger.</summary>
public static class FeedLoggers
{
    private static readonly IReadOnlyDictionary<FeedType, IFeedLogger> Loggers = new IFeedLogger[]
    {
        new AciFeedLogger(), new EtktFeedLogger(), new PnrFeedLogger(), new PnrLinkingFeedLogger(), new SeatsFeedLogger()
    }.ToDictionary(logger => logger.Feed);

    /// <summary>Formats a transaction using the logger for the supplied feed.</summary>
    public static string Format(FeedMessage message, FeedWorkflowResult? result, string? reason)
    {
        if (!Loggers.TryGetValue(message.Delivery.Feed, out var logger))
        {
            throw new InvalidDataException("No transaction logger is configured for this feed.");
        }
        return logger.Format(message, result, reason);
    }
}
