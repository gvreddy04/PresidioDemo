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

#region Classes

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

#endregion

#region Models

/// <summary>
/// Identifies an input file claimed for processing with a stable transaction identifier.
/// </summary>
/// <param name="Feed">The supported feed route.</param>
/// <param name="MessageId">The stable transaction identifier retained across retries.</param>
/// <param name="OriginalName">The filename supplied before the input was claimed.</param>
/// <param name="WorkingPath">The claimed-input path retained until a durable terminal outcome is saved.</param>
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
/// Defines input claiming, publication, quarantine, transaction logging and acknowledgment.
/// </summary>
public interface IFeedTransport : IDisposable
{

    #region Methods

    /// <summary>
    /// Removes a completed processing file after its durable terminal outcome is saved.
    /// </summary>
    /// <param name="delivery">The claimed input and its stable feed-route and transaction metadata.</param>
    void Acknowledge(FeedDelivery delivery);

    /// <summary>
    /// Claims stable or recoverable inputs while excluding feed routes that are already busy.
    /// </summary>
    /// <param name="busyFeeds">The feed routes that already have active processing work.</param>
    /// <returns>The stable or recovered deliveries claimed for currently idle feed routes.</returns>
    IReadOnlyList<FeedDelivery> ClaimReadyDeliveries(IReadOnlySet<FeedType>? busyFeeds = null);

    /// <summary>
    /// Creates the exchange folders, acquires the workspace lock and recovers legacy pending input.
    /// </summary>
    void Initialize();

    /// <summary>
    /// Durably saves the validated protected message under its stable transaction identifier.
    /// </summary>
    /// <param name="message">The complete input delivery and its retained message bytes.</param>
    /// <param name="cancellationToken">The token used to interrupt processing while retaining recoverable input.</param>
    /// <returns>A task containing the durable artifact path saved for this transaction.</returns>
    Task<string> PublishAsync(FeedMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the complete claimed input without publishing or acknowledging it.
    /// </summary>
    /// <param name="delivery">The claimed input and its stable feed-route and transaction metadata.</param>
    /// <param name="cancellationToken">The token used to interrupt processing while retaining recoverable input.</param>
    /// <returns>A task containing all bytes from the claimed input file.</returns>
    Task<byte[]> ReadAsync(FeedDelivery delivery, CancellationToken cancellationToken);

    /// <summary>
    /// Saves the complete rejected input and its error report before acknowledgment.
    /// </summary>
    /// <param name="message">The complete input delivery and its retained message bytes.</param>
    /// <param name="reason">The validated explanation for retaining or rejecting the complete input.</param>
    /// <param name="cancellationToken">The token used to interrupt processing while retaining recoverable input.</param>
    /// <returns>A task containing the durable artifact path saved for this transaction.</returns>
    Task<string> RejectAsync(FeedMessage message, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Persists complete before-and-after transaction evidence before publication or rejection.
    /// </summary>
    /// <param name="original">The complete delivery metadata and original or protected message bytes.</param>
    /// <param name="result">The validated protected output and its optional diagnostic field sets.</param>
    /// <param name="reason">The validated explanation for retaining or rejecting the complete input.</param>
    /// <param name="cancellationToken">The token used to interrupt processing while retaining recoverable input.</param>
    /// <returns>A task containing the durable artifact path saved for this transaction.</returns>
    Task<string> WriteTransactionLogAsync(FeedMessage original, FeedWorkflowResult? result, string? reason, CancellationToken cancellationToken);

    #endregion

    #region Properties, Indexers

    /// <summary>
    /// Gets the directory containing complete rejected inputs and their error reports.
    /// </summary>
    /// <value>The directory containing complete rejected inputs and their error reports.</value>
    string FailedRoot { get; }

    /// <summary>
    /// Gets the directory containing files waiting to be claimed.
    /// </summary>
    /// <value>The directory containing files waiting to be claimed.</value>
    string InputRoot { get; }

    /// <summary>
    /// Gets the directory containing transaction-specific diagnostic reports.
    /// </summary>
    /// <value>The directory containing transaction-specific diagnostic reports.</value>
    string LogsRoot { get; }

    /// <summary>
    /// Gets the directory containing validated protected output.
    /// </summary>
    /// <value>The directory containing validated protected output.</value>
    string OutputRoot { get; }

    /// <summary>
    /// Gets the directory containing claimed or recoverable input files.
    /// </summary>
    /// <value>The directory containing claimed or recoverable input files.</value>
    string ProcessingRoot { get; }

    #endregion
}

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
