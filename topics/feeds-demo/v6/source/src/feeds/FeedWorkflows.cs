using System.Diagnostics;

namespace PresidioDemo.Feeds;

// Each strategy owns its confirmed codec and its feed-specific field policy.
public abstract class ProtectedFeedWorkflow(IFeedProtector protector, IFeedCodec codec, FeedPolicy policy) : IFeedWorkflow
{
    public abstract FeedType Feed { get; }
    public async ValueTask<FeedWorkflowResult> ProcessAsync(FeedMessage message, CancellationToken cancellationToken)
    {
        if (message.Delivery.Feed != Feed || policy.Feed != Feed)
            throw new InvalidDataException("Feed strategy, delivery and policy do not match.");
        cancellationToken.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        var parsed = codec.Parse(message);
        Console.WriteLine($"{FeedCatalog.Get(Feed).FolderName,-12} PARSED     Entire feed parsed | {timer.ElapsedMilliseconds} ms");
        Console.WriteLine($"{FeedCatalog.Get(Feed).FolderName,-12} IDENTIFY   Resolve exact configured PII fields and local detection rules");
        var protectedFields = await protector.ProtectAsync(parsed.Fields, policy, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        timer.Restart();
        byte[] content = codec.Serialize(parsed, protectedFields);
        codec.Validate(parsed, content);
        Console.WriteLine($"{FeedCatalog.Get(Feed).FolderName,-12} SERIALIZED Original format rebuilt and validated | {timer.ElapsedMilliseconds} ms");
        cancellationToken.ThrowIfCancellationRequested();
        return new FeedWorkflowResult(content);
    }
}

public sealed class EtktWorkflow(IFeedProtector protector, IFeedCodec codec, FeedPolicy policy) : ProtectedFeedWorkflow(protector, codec, policy) { public override FeedType Feed => FeedType.ETKT; }
public sealed class PnrWorkflow(IFeedProtector protector, IFeedCodec codec, FeedPolicy policy) : ProtectedFeedWorkflow(protector, codec, policy) { public override FeedType Feed => FeedType.PNR; }
public sealed class PnrLinkingWorkflow(IFeedProtector protector, IFeedCodec codec, FeedPolicy policy) : ProtectedFeedWorkflow(protector, codec, policy) { public override FeedType Feed => FeedType.PnrLinking; }
public sealed class SeatsWorkflow(IFeedProtector protector, IFeedCodec codec, FeedPolicy policy) : ProtectedFeedWorkflow(protector, codec, policy) { public override FeedType Feed => FeedType.Seats; }
public sealed class AciWorkflow(IFeedProtector protector, IFeedCodec codec, FeedPolicy policy) : ProtectedFeedWorkflow(protector, codec, policy) { public override FeedType Feed => FeedType.ACI; }

public sealed class FeedWorkflowRouter
{
    private readonly IReadOnlyDictionary<FeedType, IFeedWorkflow> _workflows;
    public FeedWorkflowRouter(IEnumerable<IFeedWorkflow> workflows)
    {
        _workflows = workflows.ToDictionary(workflow => workflow.Feed);
        foreach (var feed in FeedCatalog.All)
            if (!_workflows.ContainsKey(feed.Type)) throw new ArgumentException($"No workflow registered for {feed.FolderName}.", nameof(workflows));
    }
    public IFeedWorkflow Get(FeedType type) => _workflows[type];
    public static FeedWorkflowRouter Create(IFeedProtector protector, IReadOnlyDictionary<FeedType, FeedPolicy> policies, Func<FeedType, IFeedCodec> codecFactory) => new([
        new EtktWorkflow(protector, codecFactory(FeedType.ETKT), policies[FeedType.ETKT]),
        new PnrWorkflow(protector, codecFactory(FeedType.PNR), policies[FeedType.PNR]),
        new PnrLinkingWorkflow(protector, codecFactory(FeedType.PnrLinking), policies[FeedType.PnrLinking]),
        new SeatsWorkflow(protector, codecFactory(FeedType.Seats), policies[FeedType.Seats]),
        new AciWorkflow(protector, codecFactory(FeedType.ACI), policies[FeedType.ACI])
    ]);
}