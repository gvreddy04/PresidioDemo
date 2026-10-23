using System.Diagnostics;
using PresidioDemo.Feeds.Shared;
using PresidioDemo.Protection;

namespace PresidioDemo.Pipeline;

#region Classes

// Each strategy owns its confirmed codec and its feed-specific field policy.
/// <summary>
/// Coordinates parsing, configured local protection, reconstruction and validation.
/// </summary>
/// <param name="protector">The local configured field-protection service.</param>
/// <param name="codec">The parser, serializer and validator selected for this feed.</param>
/// <param name="policy">The reviewed field rules for this feed route.</param>
public abstract class ProtectedFeedWorkflow(IFeedProtector protector, IFeedCodec codec, FeedPolicy policy) : IFeedWorkflow
{

    #region Methods

    /// <summary>
    /// Parses, protects, reconstructs and validates one complete delivery before a durable outcome.
    /// </summary>
    /// <param name="message">The complete input delivery and its retained message bytes.</param>
    /// <param name="cancellationToken">The token used to interrupt processing while retaining recoverable input.</param>
    /// <returns>A task containing the validated protected output and its diagnostic field sets.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    public async ValueTask<FeedWorkflowResult> ProcessAsync(FeedMessage message, CancellationToken cancellationToken)
    {
        if (message.Delivery.Feed != Feed || policy.Feed != Feed)
        {
            throw new InvalidDataException("Feed strategy, delivery and policy do not match.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        var parsed = codec.Parse(message);
        Console.WriteLine($"{FeedCatalog.Get(Feed).FolderName,-12} PARSED     Entire feed parsed | {timer.ElapsedMilliseconds} ms");
        Console.WriteLine($"{FeedCatalog.Get(Feed).FolderName,-12} POLICY     Resolve configured field actions; NLP only for Analyze rules");
        var protectedFields = await protector.ProtectAsync(parsed.Fields, policy, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        timer.Restart();
        byte[] content = codec.Serialize(parsed, protectedFields);
        codec.Validate(parsed, content);
        Console.WriteLine($"{FeedCatalog.Get(Feed).FolderName,-12} SERIALIZED Original format rebuilt and validated | {timer.ElapsedMilliseconds} ms");
        cancellationToken.ThrowIfCancellationRequested();
        var loggedFields = parsed.Fields.Select(field => field with { Value = protectedFields[field.Id] }).ToArray();
        return new FeedWorkflowResult(content, parsed.Fields, loggedFields);
    }

    #endregion

    #region Properties, Indexers

    /// <summary>
    /// Gets the feed route handled by this definition, policy or workflow.
    /// </summary>
    /// <value>The feed route handled by this definition, policy or workflow.</value>
    public abstract FeedType Feed { get; }

    #endregion
}

/// <summary>
/// Selects the ETKT route for the shared protection workflow.
/// </summary>
/// <param name="protector">The local configured field-protection service.</param>
/// <param name="codec">The parser, serializer and validator selected for this feed.</param>
/// <param name="policy">The reviewed field rules for this feed route.</param>
public sealed class EtktWorkflow(IFeedProtector protector, IFeedCodec codec, FeedPolicy policy) : ProtectedFeedWorkflow(protector, codec, policy)
{
    #region Properties, Indexers

    /// <summary>
    /// Gets the feed route handled by this definition, policy or workflow.
    /// </summary>
    /// <value>The feed route handled by this definition, policy or workflow.</value>
    public override FeedType Feed => FeedType.ETKT;

    #endregion
}

/// <summary>
/// Selects the PNR route for the shared protection workflow.
/// </summary>
/// <param name="protector">The local configured field-protection service.</param>
/// <param name="codec">The parser, serializer and validator selected for this feed.</param>
/// <param name="policy">The reviewed field rules for this feed route.</param>
public sealed class PnrWorkflow(IFeedProtector protector, IFeedCodec codec, FeedPolicy policy) : ProtectedFeedWorkflow(protector, codec, policy)
{
    #region Properties, Indexers

    /// <summary>
    /// Gets the feed route handled by this definition, policy or workflow.
    /// </summary>
    /// <value>The feed route handled by this definition, policy or workflow.</value>
    public override FeedType Feed => FeedType.PNR;

    #endregion
}

/// <summary>
/// Selects the PNR-Linking route for the shared protection workflow.
/// </summary>
/// <param name="protector">The local configured field-protection service.</param>
/// <param name="codec">The parser, serializer and validator selected for this feed.</param>
/// <param name="policy">The reviewed field rules for this feed route.</param>
public sealed class PnrLinkingWorkflow(IFeedProtector protector, IFeedCodec codec, FeedPolicy policy) : ProtectedFeedWorkflow(protector, codec, policy)
{
    #region Properties, Indexers

    /// <summary>
    /// Gets the feed route handled by this definition, policy or workflow.
    /// </summary>
    /// <value>The feed route handled by this definition, policy or workflow.</value>
    public override FeedType Feed => FeedType.PnrLinking;

    #endregion
}

/// <summary>
/// Selects the Seats route for the shared protection workflow.
/// </summary>
/// <param name="protector">The local configured field-protection service.</param>
/// <param name="codec">The parser, serializer and validator selected for this feed.</param>
/// <param name="policy">The reviewed field rules for this feed route.</param>
public sealed class SeatsWorkflow(IFeedProtector protector, IFeedCodec codec, FeedPolicy policy) : ProtectedFeedWorkflow(protector, codec, policy)
{
    #region Properties, Indexers

    /// <summary>
    /// Gets the feed route handled by this definition, policy or workflow.
    /// </summary>
    /// <value>The feed route handled by this definition, policy or workflow.</value>
    public override FeedType Feed => FeedType.Seats;

    #endregion
}

/// <summary>
/// Selects the ACI route for the shared protection workflow.
/// </summary>
/// <param name="protector">The local configured field-protection service.</param>
/// <param name="codec">The parser, serializer and validator selected for this feed.</param>
/// <param name="policy">The reviewed field rules for this feed route.</param>
public sealed class AciWorkflow(IFeedProtector protector, IFeedCodec codec, FeedPolicy policy) : ProtectedFeedWorkflow(protector, codec, policy)
{
    #region Properties, Indexers

    /// <summary>
    /// Gets the feed route handled by this definition, policy or workflow.
    /// </summary>
    /// <value>The feed route handled by this definition, policy or workflow.</value>
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
    /// <exception cref="ArgumentException">A supplied argument does not satisfy the supported range or configuration contract.</exception>
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
    /// Registers one configured protection workflow for every supported feed route.
    /// </summary>
    /// <param name="protector">The local configured field-protection service.</param>
    /// <param name="policies">The reviewed protection policies indexed by feed route.</param>
    /// <param name="codecFactory">The factory that selects the reviewed codec for each feed route.</param>
    /// <returns>A router with one registered workflow for every supported feed route.</returns>
    public static FeedWorkflowRouter Create(IFeedProtector protector, IReadOnlyDictionary<FeedType, FeedPolicy> policies, Func<FeedType, IFeedCodec> codecFactory) => new([
            new EtktWorkflow(protector, codecFactory(FeedType.ETKT), policies[FeedType.ETKT]),
        new PnrWorkflow(protector, codecFactory(FeedType.PNR), policies[FeedType.PNR]),
        new PnrLinkingWorkflow(protector, codecFactory(FeedType.PnrLinking), policies[FeedType.PnrLinking]),
        new SeatsWorkflow(protector, codecFactory(FeedType.Seats), policies[FeedType.Seats]),
        new AciWorkflow(protector, codecFactory(FeedType.ACI), policies[FeedType.ACI])
        ]);

    /// <summary>
    /// Returns the workflow registered for the requested feed route.
    /// </summary>
    /// <param name="type">The supported feed route or requested definition category.</param>
    /// <returns>The registered processing workflow for the requested feed route.</returns>
    public IFeedWorkflow Get(FeedType type) => _workflows[type];

    #endregion
}

#endregion
