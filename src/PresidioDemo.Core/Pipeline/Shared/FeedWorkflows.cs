using System.Diagnostics;
using PresidioDemo.Feeds.Shared;
using PresidioDemo.Protection;

namespace PresidioDemo.Pipeline;

#region Classes

/// <summary>
/// Coordinates decoding, detection and protection, reconstruction and validation for one feed.
/// </summary>
/// <param name="protector">The local detection and protection service.</param>
/// <param name="codec">The decoder, serializer and validator selected for this feed.</param>
public abstract class ProtectedFeedWorkflow(IFeedProtector protector, IFeedCodec codec) : IFeedWorkflow
{

    #region Methods

    /// <summary>
    /// Decodes, protects, reconstructs and validates one complete delivery before a durable outcome.
    /// </summary>
    /// <param name="message">The complete input delivery and its retained message bytes.</param>
    /// <param name="cancellationToken">The token used to interrupt processing while retaining recoverable input.</param>
    /// <returns>A task containing the validated protected output, its diagnostic field sets and detection events.</returns>
    /// <exception cref="InvalidDataException">A structural failure (WR-016); the message is dead-lettered.</exception>
    public async ValueTask<FeedWorkflowResult> ProcessAsync(FeedMessage message, CancellationToken cancellationToken)
    {
        if (message.Delivery.Feed != Feed)
        {
            throw new InvalidDataException("Feed strategy and delivery do not match.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        var parsed = codec.Parse(message);
        Console.WriteLine($"{FeedCatalog.Get(Feed).FolderName,-12} DECODED    {parsed.Fields.Count} fields | {timer.ElapsedMilliseconds} ms");
        timer.Restart();
        var outcome = await protector.ProtectAsync(Feed, parsed, cancellationToken);
        Console.WriteLine($"{FeedCatalog.Get(Feed).FolderName,-12} DETECTED   {outcome.Events.Count} strings | {timer.ElapsedMilliseconds} ms (includes engine wait)");
        cancellationToken.ThrowIfCancellationRequested();
        timer.Restart();
        byte[] content = codec.Serialize(parsed, outcome.Values);
        codec.Validate(parsed, content);
        Console.WriteLine($"{FeedCatalog.Get(Feed).FolderName,-12} SERIALIZED Original format rebuilt and validated | {timer.ElapsedMilliseconds} ms");
        cancellationToken.ThrowIfCancellationRequested();
        var loggedFields = parsed.Fields.Select(field => field with { Value = outcome.Values[field.Id] }).ToArray();
        return new FeedWorkflowResult(content, parsed.Fields, loggedFields, outcome.Events);
    }

    #endregion

    #region Properties, Indexers

    /// <summary>
    /// Gets the feed route handled by this workflow.
    /// </summary>
    /// <value>The feed route handled by this workflow.</value>
    public abstract FeedType Feed { get; }

    #endregion
}

/// <summary>
/// Selects the ETKT route for the shared protection workflow.
/// </summary>
/// <param name="protector">The local detection and protection service.</param>
/// <param name="codec">The decoder, serializer and validator selected for this feed.</param>
public sealed class EtktWorkflow(IFeedProtector protector, IFeedCodec codec) : ProtectedFeedWorkflow(protector, codec)
{
    #region Properties, Indexers

    /// <summary>
    /// Gets the feed route handled by this workflow.
    /// </summary>
    /// <value>The ETKT route.</value>
    public override FeedType Feed => FeedType.ETKT;

    #endregion
}

/// <summary>
/// Selects the PNR route for the shared protection workflow.
/// </summary>
/// <param name="protector">The local detection and protection service.</param>
/// <param name="codec">The decoder, serializer and validator selected for this feed.</param>
public sealed class PnrWorkflow(IFeedProtector protector, IFeedCodec codec) : ProtectedFeedWorkflow(protector, codec)
{
    #region Properties, Indexers

    /// <summary>
    /// Gets the feed route handled by this workflow.
    /// </summary>
    /// <value>The PNR route.</value>
    public override FeedType Feed => FeedType.PNR;

    #endregion
}

/// <summary>
/// Selects the PNR-Linking route for the shared protection workflow.
/// </summary>
/// <param name="protector">The local detection and protection service.</param>
/// <param name="codec">The decoder, serializer and validator selected for this feed.</param>
public sealed class PnrLinkingWorkflow(IFeedProtector protector, IFeedCodec codec) : ProtectedFeedWorkflow(protector, codec)
{
    #region Properties, Indexers

    /// <summary>
    /// Gets the feed route handled by this workflow.
    /// </summary>
    /// <value>The PNR-Linking route.</value>
    public override FeedType Feed => FeedType.PnrLinking;

    #endregion
}

/// <summary>
/// Selects the Seats route for the shared protection workflow.
/// </summary>
/// <param name="protector">The local detection and protection service.</param>
/// <param name="codec">The decoder, serializer and validator selected for this feed.</param>
public sealed class SeatsWorkflow(IFeedProtector protector, IFeedCodec codec) : ProtectedFeedWorkflow(protector, codec)
{
    #region Properties, Indexers

    /// <summary>
    /// Gets the feed route handled by this workflow.
    /// </summary>
    /// <value>The Seats route.</value>
    public override FeedType Feed => FeedType.Seats;

    #endregion
}

/// <summary>
/// Selects the ACI route for the shared protection workflow.
/// </summary>
/// <param name="protector">The local detection and protection service.</param>
/// <param name="codec">The decoder, serializer and validator selected for this feed.</param>
public sealed class AciWorkflow(IFeedProtector protector, IFeedCodec codec) : ProtectedFeedWorkflow(protector, codec)
{
    #region Properties, Indexers

    /// <summary>
    /// Gets the feed route handled by this workflow.
    /// </summary>
    /// <value>The ACI route.</value>
    public override FeedType Feed => FeedType.ACI;

    #endregion
}

/// <summary>
/// Resolves exactly one registered workflow for every supported feed route.
/// </summary>
public sealed class FeedWorkflowRouter
{

    #region Fields and Constants

    /// <summary>
    /// Maps each supported feed route to its single registered workflow.
    /// </summary>
    private readonly IReadOnlyDictionary<FeedType, IFeedWorkflow> _workflows;

    #endregion

    #region Constructors

    /// <summary>
    /// Registers and validates exactly one processing workflow for every supported route.
    /// </summary>
    /// <param name="workflows">The feed-specific processing strategies to register.</param>
    /// <exception cref="ArgumentException">A feed has no workflow.</exception>
    public FeedWorkflowRouter(IEnumerable<IFeedWorkflow> workflows)
    {
        _workflows = workflows.ToDictionary(workflow => workflow.Feed);
        foreach (var feed in FeedCatalog.All)
        {
            if (!_workflows.ContainsKey(feed.Type))
            {
                throw new ArgumentException($"No workflow registered for {feed.FolderName}.", nameof(workflows));
            }
        }
    }

    #endregion

    #region Methods

    /// <summary>
    /// Registers one protection workflow for every supported feed route.
    /// </summary>
    /// <param name="protector">The local detection and protection service.</param>
    /// <param name="codecFactory">The factory that selects the codec for each feed route.</param>
    /// <returns>A router with one registered workflow for every supported feed route.</returns>
    public static FeedWorkflowRouter Create(IFeedProtector protector, Func<FeedType, IFeedCodec> codecFactory) => new([
        new EtktWorkflow(protector, codecFactory(FeedType.ETKT)),
        new PnrWorkflow(protector, codecFactory(FeedType.PNR)),
        new PnrLinkingWorkflow(protector, codecFactory(FeedType.PnrLinking)),
        new SeatsWorkflow(protector, codecFactory(FeedType.Seats)),
        new AciWorkflow(protector, codecFactory(FeedType.ACI))
    ]);

    /// <summary>
    /// Returns the workflow registered for the requested feed route.
    /// </summary>
    /// <param name="type">The feed route.</param>
    /// <returns>The registered processing workflow.</returns>
    public IFeedWorkflow Get(FeedType type) => _workflows[type];

    #endregion
}

#endregion
