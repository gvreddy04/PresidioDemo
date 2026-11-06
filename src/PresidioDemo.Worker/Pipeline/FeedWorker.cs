using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using PresidioDemo.Feeds.Shared;
using PresidioDemo.Messaging;
using PresidioDemo.Pipeline;
using PresidioDemo.Transport;

namespace PresidioDemo.Worker;

#region Models

/// <summary>
/// What happened to one input message, reported after the broker confirmed its outcome.
/// </summary>
/// <param name="Input">The received message.</param>
/// <param name="Result">The protected output or rejection reason.</param>
/// <param name="Published">The confirmed output or dead-letter message.</param>
public sealed record FeedWorkerOutcome(InboundMessage Input, FeedProcessingResult Result, OutboundMessage Published);

#endregion

/// <summary>
/// Processes one feed's queue: receive, protect and validate, publish to the output topic or the dead-letter queue,
/// then acknowledge. The input is acknowledged only after the broker confirms its outcome, so a crash at any step
/// leads to redelivery instead of loss.
/// </summary>
/// <param name="feed">The supported feed route.</param>
/// <param name="route">The feed's input, output and dead-letter destinations.</param>
/// <param name="options">The broker-neutral delivery settings.</param>
/// <param name="consumer">Reads the input queue.</param>
/// <param name="publisher">Publishes output and dead-letter messages.</param>
/// <param name="processor">Runs the feed workflow.</param>
/// <param name="logger">Writes summaries without personal data.</param>
/// <param name="onOutcome">Optional observer, used by tests.</param>
/// <param name="detectionLog">Whether to write the value-free detection log (<c>Presidio:DetectionLog</c>, WR-018).</param>
public sealed class FeedWorker(FeedType feed, MessageRoute route, MessagingOptions options, IMessageConsumer consumer,
    IMessagePublisher publisher, FeedMessageProcessor processor, ILogger logger, Func<FeedWorkerOutcome, ValueTask>? onOutcome = null,
    bool detectionLog = false)
{

    #region Fields and Constants

    /// <summary>
    /// The payload-format header value for an IBM MQ export with A and X lines.
    /// </summary>
    public const string MqExportFormat = "mq-export";

    /// <summary>
    /// The payload-format header value for a raw payload; the default.
    /// </summary>
    public const string RawFormat = "raw";

    #endregion

    #region Methods

    /// <summary>
    /// Returns a transaction ID that is identical for every redelivery of the same broker message.
    /// </summary>
    /// <param name="source">The input queue.</param>
    /// <param name="stableId">The broker's stable message ID.</param>
    /// <returns>A deterministic GUID.</returns>
    public static Guid TransactionId(string source, string stableId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(source + "\n" + stableId)).AsSpan(0, 16));

    /// <summary>
    /// Processes messages until cancellation; an unacknowledged message is redelivered after a restart.
    /// </summary>
    /// <param name="cancellationToken">The token that stops the worker.</param>
    /// <returns>A task that completes when the worker stops.</returns>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        string name = FeedCatalog.Get(feed).FolderName;
        logger.LogInformation("{Feed} worker reading {Input}; output {Output}; dead letter {DeadLetter}.", name, route.Input, route.Output, route.DeadLetter);
        try
        {
            await foreach (var input in consumer.ReadAsync(route.Input, cancellationToken))
            {
                await HandleAsync(input, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        logger.LogInformation("{Feed} worker stopped.", name);
    }

    /// <summary>
    /// Copies the allowed input headers and adds the pipeline's own headers.
    /// </summary>
    /// <param name="input">The received message.</param>
    /// <param name="extra">Pipeline headers to add.</param>
    /// <returns>The outbound headers.</returns>
    private Dictionary<string, string> Headers(InboundMessage input, params (string Name, string Value)[] extra)
    {
        var headers = input.Headers.Where(h => options.CopyHeaders.Contains(h.Key, StringComparer.Ordinal))
            .ToDictionary(h => h.Key, h => h.Value, StringComparer.Ordinal);
        headers[MessageHeaders.Feed] = FeedCatalog.Get(feed).FolderName;
        foreach (var (key, value) in extra)
        {
            headers[key] = value;
        }
        return headers;
    }

    /// <summary>
    /// Processes one message and settles it.
    /// </summary>
    /// <param name="input">The received message.</param>
    /// <param name="cancellationToken">The token that stops the worker.</param>
    /// <returns>A task that completes once the message is acknowledged.</returns>
    private async Task HandleAsync(InboundMessage input, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        Guid id = TransactionId(input.Source, input.StableId);
        string format = input.Headers.GetValueOrDefault(MessageHeaders.PayloadFormat) ?? RawFormat;
        string name = id.ToString("N") + (format == MqExportFormat ? ".txt" : ".bin");
        FeedProcessingResult result;
        if (input.DeliveryCount > options.MaxDeliveries)
        {
            // A message that keeps failing before acknowledgment is set aside instead of blocking the queue.
            result = new(new(new(feed, id, name, ""), input.Payload), null, $"Exceeded {options.MaxDeliveries} deliveries without completing.");
        }
        else if (format is not (RawFormat or MqExportFormat))
        {
            result = new(new(new(feed, id, name, ""), input.Payload), null, $"Unsupported {MessageHeaders.PayloadFormat} '{format}'; use raw or mq-export.");
        }
        else
        {
            result = await processor.ProcessAsync(feed, id, name, input.Payload, cancellationToken);
        }

        // Rejected input contains personal data and goes only to the route's restricted dead-letter queue.
        var outbound = result.Published
            ? new OutboundMessage(new(route.Output, DestinationKind.Topic), result.Workflow!.Content, id.ToString("N"), input.CorrelationId, Headers(input))
            : new OutboundMessage(new(route.DeadLetter, DestinationKind.Queue), input.Payload, id.ToString("N"), input.CorrelationId,
                Headers(input, (MessageHeaders.RejectReason, result.Reason!), (MessageHeaders.PayloadFormat, format)));
        await PublishAsync(outbound, cancellationToken);
        try
        {
            await input.CompleteAsync(cancellationToken);
        }
        catch (MessagingUnavailableException ex)
        {
            // The outcome is already confirmed; a redelivery republishes it under the same transaction ID.
            logger.LogWarning("{Feed} {Transaction} acknowledgment failed and will be redelivered: {Reason}", FeedCatalog.Get(feed).FolderName, id.ToString("N"), ex.Message);
        }
        LogSummary(id, input, result, outbound, timer.ElapsedMilliseconds);
        if (detectionLog && result.Workflow?.Events is { } events)
        {
            foreach (string line in DetectionLog.Format(feed, id, events))
            {
                logger.LogInformation("{DetectionLine}", line);
            }
        }
        if (onOutcome is not null)
        {
            await onOutcome(new(input, result, outbound));
        }
    }

    /// <summary>
    /// Writes one summary line without field values or payload content.
    /// </summary>
    /// <param name="id">The transaction ID.</param>
    /// <param name="input">The received message.</param>
    /// <param name="result">The processing result.</param>
    /// <param name="outbound">The confirmed outcome message.</param>
    /// <param name="elapsedMs">The processing time.</param>
    private void LogSummary(Guid id, InboundMessage input, FeedProcessingResult result, OutboundMessage outbound, long elapsedMs)
    {
        string name = FeedCatalog.Get(feed).FolderName;
        if (result.Published)
        {
            var original = result.Workflow!.OriginalFields ?? [];
            var protectedFields = result.Workflow.ProtectedFields ?? [];
            int changed = original.Zip(protectedFields).Count(pair => pair.First.Value != pair.Second.Value);
            logger.LogInformation("{Feed} {Transaction} PUBLISHED to {Destination} | {Bytes} bytes | {Changed} of {Fields} fields protected | delivery {Delivery} | {Elapsed} ms",
                name, id.ToString("N"), outbound.Destination, result.Workflow.Content.Length, changed, original.Count, input.DeliveryCount, elapsedMs);
        }
        else
        {
            logger.LogWarning("{Feed} {Transaction} DEAD-LETTERED to {Destination} | {Reason} | delivery {Delivery} | {Elapsed} ms",
                name, id.ToString("N"), outbound.Destination, result.Reason, input.DeliveryCount, elapsedMs);
        }
    }

    /// <summary>
    /// Publishes until the broker confirms; while it is unavailable the input stays unacknowledged.
    /// </summary>
    /// <param name="message">The output or dead-letter message.</param>
    /// <param name="cancellationToken">The token that stops retrying.</param>
    /// <returns>A task that completes once the broker confirms.</returns>
    private async Task PublishAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await publisher.PublishAsync(message, cancellationToken);
                return;
            }
            catch (MessagingUnavailableException ex)
            {
                logger.LogWarning("{Feed} {Transaction} waiting for the broker: {Reason}", FeedCatalog.Get(feed).FolderName, message.MessageId, ex.Message);
                await Task.Delay(options.PublishRetryDelayMs, cancellationToken);
            }
        }
    }

    #endregion
}
