using System.Text.Json;

namespace PresidioDemo.Feeds;

// Strategy implementations have separate extension points for later feed parsing.
// This phase deliberately shares syntax validation; no airline schema is assumed.
public abstract class JsonValidationWorkflow : IFeedWorkflow
{
    public abstract FeedType Feed { get; }

    public virtual ValueTask<FeedWorkflowResult> ProcessAsync(FeedMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var content = message.Content;
        // Windows producers may include a UTF-8 BOM. Preserve it in output.
        if (content.Span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) content = content[3..];
        using var json = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 256 });
        return ValueTask.FromResult(new FeedWorkflowResult(message.Content));
    }
}

public sealed class EtktWorkflow : JsonValidationWorkflow { public override FeedType Feed => FeedType.ETKT; }
public sealed class PnrWorkflow : JsonValidationWorkflow { public override FeedType Feed => FeedType.PNR; }
public sealed class PnrLinkingWorkflow : JsonValidationWorkflow { public override FeedType Feed => FeedType.PnrLinking; }
public sealed class SeatsWorkflow : JsonValidationWorkflow { public override FeedType Feed => FeedType.Seats; }
public sealed class AciWorkflow : JsonValidationWorkflow { public override FeedType Feed => FeedType.ACI; }

public sealed class FeedWorkflowRouter
{
    private readonly IReadOnlyDictionary<FeedType, IFeedWorkflow> _workflows;

    public FeedWorkflowRouter(IEnumerable<IFeedWorkflow> workflows)
    {
        _workflows = workflows.ToDictionary(workflow => workflow.Feed);
        foreach (var feed in FeedCatalog.All)
            if (!_workflows.ContainsKey(feed.Type))
                throw new ArgumentException($"No workflow registered for {feed.FolderName}.", nameof(workflows));
    }

    public IFeedWorkflow Get(FeedType type) => _workflows[type];
}
