namespace PresidioDemo.Feeds;

public enum FeedType { ETKT, PNR, PnrLinking, Seats, ACI }

public sealed record FeedDefinition(FeedType Type, string FolderName);

public static class FeedCatalog
{
    public static IReadOnlyList<FeedDefinition> All { get; } = Array.AsReadOnly(new[]
    {
        new FeedDefinition(FeedType.ETKT, "ETKT"),
        new FeedDefinition(FeedType.PNR, "PNR"),
        new FeedDefinition(FeedType.PnrLinking, "PNR-Linking"),
        new FeedDefinition(FeedType.Seats, "Seats"),
        new FeedDefinition(FeedType.ACI, "ACI")
    });

    public static FeedDefinition Get(FeedType type) => All.Single(feed => feed.Type == type);
}

public sealed record FeedDelivery(FeedType Feed, Guid MessageId, string OriginalName, string WorkingPath);
public sealed record FeedMessage(FeedDelivery Delivery, ReadOnlyMemory<byte> Content);
public sealed record FeedWorkflowResult(ReadOnlyMemory<byte> Content);

public interface IFeedTransport : IDisposable
{
    string InputRoot { get; }
    string OutputRoot { get; }
    string FailedRoot { get; }
    void Initialize();
    IReadOnlyList<FeedDelivery> ClaimReadyDeliveries();
    Task<byte[]> ReadAsync(FeedDelivery delivery, CancellationToken cancellationToken);
    Task<string> PublishAsync(FeedMessage message, CancellationToken cancellationToken);
    Task<string> RejectAsync(FeedMessage message, string reason, CancellationToken cancellationToken);
    void Acknowledge(FeedDelivery delivery);
}

public interface IFeedWorkflow
{
    FeedType Feed { get; }
    ValueTask<FeedWorkflowResult> ProcessAsync(FeedMessage message, CancellationToken cancellationToken);
}
