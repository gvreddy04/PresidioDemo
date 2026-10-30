namespace PresidioDemo.Messaging;

#region Enumerations

/// <summary>
/// Identifies whether a destination fans out to subscribers or delivers to one durable endpoint.
/// </summary>
public enum DestinationKind
{
    /// <summary>
    /// A publish-subscribe topic, such as a Solace topic or a Kafka topic.
    /// </summary>
    Topic,
    /// <summary>
    /// A point-to-point durable queue, such as a Solace queue; brokers without queues map it to a topic.
    /// </summary>
    Queue
}

#endregion

#region Models

/// <summary>
/// Names a broker destination and its kind.
/// </summary>
/// <param name="Name">The topic or queue name.</param>
/// <param name="Kind">Whether the destination is a topic or a queue.</param>
public sealed record MessageDestination(string Name, DestinationKind Kind)
{

    #region Methods

    /// <summary>
    /// Returns the destination for logs.
    /// </summary>
    /// <returns>The kind and name, such as topic:airline/pnr/protected.</returns>
    public override string ToString() => $"{Kind.ToString().ToLowerInvariant()}:{Name}";

    #endregion
}

/// <summary>
/// One message received from a durable source, copied out of the broker client.
/// </summary>
/// <param name="Source">The queue or topic the message was read from.</param>
/// <param name="StableId">A broker ID that is identical for every redelivery of this message.</param>
/// <param name="Payload">The complete message body.</param>
/// <param name="CorrelationId">The producer's correlation ID, or null.</param>
/// <param name="Headers">The message's string headers or user properties.</param>
/// <param name="DeliveryCount">How many times the broker has delivered this message, starting at 1.</param>
/// <param name="CompleteAsync">Acknowledges the message; call only after its outcome is confirmed by the broker.</param>
public sealed record InboundMessage(string Source, string StableId, ReadOnlyMemory<byte> Payload, string? CorrelationId,
    IReadOnlyDictionary<string, string> Headers, int DeliveryCount, Func<CancellationToken, ValueTask> CompleteAsync);

/// <summary>
/// One message to publish durably.
/// </summary>
/// <param name="Destination">The topic or queue receiving the message.</param>
/// <param name="Payload">The complete message body.</param>
/// <param name="MessageId">A stable transaction ID, identical when the same input is republished, so consumers can drop duplicates.</param>
/// <param name="CorrelationId">The correlation ID copied from the input message, or null.</param>
/// <param name="Headers">The string headers or user properties to send.</param>
public sealed record OutboundMessage(MessageDestination Destination, ReadOnlyMemory<byte> Payload, string MessageId,
    string? CorrelationId, IReadOnlyDictionary<string, string> Headers);

#endregion

#region Constants

/// <summary>
/// Header names the pipeline reads or writes on every broker.
/// </summary>
public static class MessageHeaders
{

    #region Fields and Constants

    /// <summary>
    /// The input payload format: raw (default) or mq-export, an IBM MQ export with A and X lines.
    /// </summary>
    public const string PayloadFormat = "presidio-payload-format";

    /// <summary>
    /// The feed route name added to protected and dead-letter messages.
    /// </summary>
    public const string Feed = "presidio-feed";

    /// <summary>
    /// The rejection reason added to dead-letter messages.
    /// </summary>
    public const string RejectReason = "presidio-reject-reason";

    #endregion
}

#endregion

#region Exceptions

/// <summary>
/// Signals that a message could not be published or acknowledged yet; the caller retries without acknowledging the input.
/// </summary>
/// <param name="message">The reason the operation did not complete.</param>
/// <param name="inner">The underlying broker failure, if any.</param>
public sealed class MessagingUnavailableException(string message, Exception? inner = null) : IOException(message, inner);

#endregion
