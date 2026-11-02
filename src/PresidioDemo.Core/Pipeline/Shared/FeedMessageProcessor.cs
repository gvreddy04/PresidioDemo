using PresidioDemo.Feeds.Shared;

namespace PresidioDemo.Pipeline;

#region Models

/// <summary>
/// The outcome of processing one message: validated protected content, or the reason it was rejected.
/// </summary>
/// <param name="Message">The original input delivery and bytes.</param>
/// <param name="Workflow">The validated output and diagnostic field sets, or null when rejected.</param>
/// <param name="Reason">The rejection reason, or null when published.</param>
public sealed record FeedProcessingResult(FeedMessage Message, FeedWorkflowResult? Workflow, string? Reason)
{

    #region Properties, Indexers

    /// <summary>
    /// Gets a value indicating whether the message produced validated protected output.
    /// </summary>
    /// <value>Whether the output can be published.</value>
    public bool Published => Workflow is not null;

    #endregion
}

#endregion

/// <summary>
/// Runs the feed's workflow for one message without any transport concerns.
/// </summary>
/// <param name="router">The registry resolving a workflow for each supported feed route.</param>
public sealed class FeedMessageProcessor(FeedWorkflowRouter router)
{

    #region Methods

    /// <summary>
    /// Parses, protects, reconstructs and validates one complete message.
    /// </summary>
    /// <param name="feed">The supported feed route.</param>
    /// <param name="messageId">The stable transaction ID.</param>
    /// <param name="name">The message name; a .txt extension selects an IBM MQ export, .bin a raw payload.</param>
    /// <param name="content">The complete input bytes.</param>
    /// <param name="cancellationToken">The token used to stop processing during shutdown.</param>
    /// <returns>The validated output, or the reason the message was rejected.</returns>
    public async Task<FeedProcessingResult> ProcessAsync(FeedType feed, Guid messageId, string name, ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var message = new FeedMessage(new FeedDelivery(feed, messageId, name, ""), content);
        try
        {
            return new(message, await router.Get(feed).ProcessAsync(message, cancellationToken), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Codec and policy contracts explain themselves; anything else is reported without internal detail.
            return new(message, null, ex is InvalidDataException ? ex.Message : "Feed processing failed.");
        }
    }

    #endregion
}
