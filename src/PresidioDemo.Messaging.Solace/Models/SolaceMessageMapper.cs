using SolaceSystems.Solclient.Messaging;
using SolaceSystems.Solclient.Messaging.SDT;

namespace PresidioDemo.Messaging.Solace;

/// <summary>
/// Converts between native Solace messages and the broker-neutral message records.
/// </summary>
internal static class SolaceMessageMapper
{

    #region Methods

    /// <summary>
    /// Fills a native message for persistent delivery to a topic or queue.
    /// </summary>
    /// <param name="outgoing">The native message created by the context factory.</param>
    /// <param name="message">The broker-neutral message.</param>
    public static void Populate(IMessage outgoing, OutboundMessage message)
    {
        outgoing.Destination = message.Destination.Kind == DestinationKind.Queue
            ? ContextFactory.Instance.CreateQueue(message.Destination.Name)
            : ContextFactory.Instance.CreateTopic(message.Destination.Name);
        outgoing.DeliveryMode = MessageDeliveryMode.Persistent;
        outgoing.BinaryAttachment = message.Payload.ToArray();
        outgoing.ApplicationMessageId = message.MessageId;
        if (message.CorrelationId is { } correlation)
        {
            outgoing.CorrelationId = correlation;
        }
        if (message.Headers.Count > 0)
        {
            var map = outgoing.CreateUserPropertyMap();
            foreach (var (name, value) in message.Headers)
            {
                map.AddString(name, value);
            }
        }
    }

    /// <summary>
    /// Copies a received message out of native memory; the caller disposes the native message.
    /// </summary>
    /// <param name="received">The native message delivered on a flow.</param>
    /// <param name="source">The queue the flow is bound to.</param>
    /// <param name="complete">Acknowledges the message on its flow.</param>
    /// <returns>The managed copy.</returns>
    public static InboundMessage ToInbound(IMessage received, string source, Func<CancellationToken, ValueTask> complete)
    {
        string stableId;
        try { stableId = received.ReplicationGroupMessageId?.ToString() ?? ""; }
        catch (Exception) { stableId = ""; /* Older brokers do not supply replication-group IDs. */ }
        if (stableId.Length == 0)
        {
            stableId = received.ApplicationMessageId is { Length: > 0 } producerId ? "app:" + producerId : "ad:" + received.ADMessageId;
        }
        int deliveries;
        try { deliveries = received.DeliveryCount; }
        catch (Exception) { deliveries = received.Redelivered ? 2 : 1; /* Delivery count is a broker feature that may be disabled. */ }

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (received.UserPropertyMap is { } map)
        {
            // Only string properties are copied; other types are not used by the pipeline.
            map.Rewind();
            while (map.HasNext())
            {
                var (name, field) = map.GetNext();
                if (field?.Type == SDTFieldType.STRING && field.Value is string value)
                {
                    headers[name] = value;
                }
            }
        }
        return new(source, stableId, received.BinaryAttachment ?? [], received.CorrelationId, headers, Math.Max(1, deliveries), complete);
    }

    #endregion
}
