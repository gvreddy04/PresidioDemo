namespace PresidioDemo.Feeds.Shared;

#region Enumerations

/// <summary>
/// Identifies a supported feed route; declaration order preserves its existing numeric values.
/// </summary>
public enum FeedType
{
    /// <summary>
    /// The ETKT binary feed route.
    /// </summary>
    ETKT,
    /// <summary>
    /// The reviewed PNR binary feed route.
    /// </summary>
    PNR,
    /// <summary>
    /// The PNR-Linking JSON array feed route.
    /// </summary>
    PnrLinking,
    /// <summary>
    /// The Seats JSON object feed route.
    /// </summary>
    Seats,
    /// <summary>
    /// The ACI binary feed route.
    /// </summary>
    ACI
}

#endregion

#region Models

/// <summary>
/// Associates a feed route with its exchange-folder name.
/// </summary>
/// <param name="Type">The supported feed route represented by this definition.</param>
/// <param name="FolderName">The exchange-folder name used for this feed route.</param>
public sealed record FeedDefinition(FeedType Type, string FolderName);

#endregion

/// <summary>
/// Provides the supported feed routes and their exchange-folder names.
/// </summary>
public static class FeedCatalog
{

    #region Methods

    /// <summary>
    /// Returns the supported definition for the requested feed route.
    /// </summary>
    /// <param name="type">The supported feed route or requested definition category.</param>
    /// <returns>The supported feed definition matching the requested route.</returns>
    public static FeedDefinition Get(FeedType type) => All.Single(feed => feed.Type == type);

    #endregion

    #region Properties, Indexers

    /// <summary>
    /// Gets all supported feed definitions in the existing catalog order.
    /// </summary>
    /// <value>The reviewed definitions in their required declaration order.</value>
    public static IReadOnlyList<FeedDefinition> All { get; } = Array.AsReadOnly(new[]
        {
        new FeedDefinition(FeedType.ETKT, "ETKT"),
        new FeedDefinition(FeedType.PNR, "PNR"),
        new FeedDefinition(FeedType.PnrLinking, "PNR-Linking"),
        new FeedDefinition(FeedType.Seats, "Seats"),
        new FeedDefinition(FeedType.ACI, "ACI")
    });

    #endregion
}

#region Models

/// <summary>
/// Identifies one input message with its feed route and stable transaction identifier.
/// </summary>
/// <param name="Feed">The supported feed route.</param>
/// <param name="MessageId">The stable transaction identifier retained across retries.</param>
/// <param name="OriginalName">The message name; a .txt extension marks an IBM MQ export, .bin a raw payload.</param>
/// <param name="WorkingPath">An optional source path used only by diagnostics; empty for broker messages.</param>
public sealed record FeedDelivery(FeedType Feed, Guid MessageId, string OriginalName, string WorkingPath);

/// <summary>
/// Combines delivery metadata with the complete original or protected message bytes.
/// </summary>
/// <param name="Delivery">The feed route, transaction identifier and claimed-input metadata.</param>
/// <param name="Content">The complete original or protected message bytes.</param>
public sealed record FeedMessage(FeedDelivery Delivery, ReadOnlyMemory<byte> Content);

/// <summary>
/// Contains validated output and the original and protected fields used for diagnostics.
/// </summary>
/// <param name="Content">The complete original or protected message bytes.</param>
/// <param name="OriginalFields">The original parsed fields included in local diagnostic reports.</param>
/// <param name="ProtectedFields">The protected parsed fields included in local diagnostic reports.</param>
public sealed record FeedWorkflowResult(ReadOnlyMemory<byte> Content,
    IReadOnlyList<FeedField>? OriginalFields = null, IReadOnlyList<FeedField>? ProtectedFields = null);

#endregion

#region Interfaces

/// <summary>
/// Defines the processing strategy for one feed route.
/// </summary>
public interface IFeedWorkflow
{

    #region Methods

    /// <summary>
    /// Parses, protects, reconstructs and validates one complete delivery before a durable outcome.
    /// </summary>
    /// <param name="message">The complete input delivery and its retained message bytes.</param>
    /// <param name="cancellationToken">The token used to interrupt processing while retaining recoverable input.</param>
    /// <returns>A task containing the validated protected output and its diagnostic field sets.</returns>
    ValueTask<FeedWorkflowResult> ProcessAsync(FeedMessage message, CancellationToken cancellationToken);

    #endregion

    #region Properties, Indexers

    /// <summary>
    /// Gets the feed route handled by this definition, policy or workflow.
    /// </summary>
    /// <value>The feed route handled by this definition, policy or workflow.</value>
    FeedType Feed { get; }

    #endregion
}

#endregion
