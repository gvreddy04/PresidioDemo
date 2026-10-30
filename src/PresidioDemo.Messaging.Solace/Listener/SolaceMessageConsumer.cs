using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SolaceSystems.Solclient.Messaging;

namespace PresidioDemo.Messaging.Solace;

/// <summary>
/// Reads a Solace queue through a client-acknowledged flow with a window of one message,
/// so the broker sends the next message only after the previous one is acknowledged.
/// </summary>
/// <param name="connection">The shared Solace connection.</param>
public sealed class SolaceMessageConsumer(SolaceConnection connection) : IMessageConsumer
{

    #region Methods

    /// <summary>
    /// Streams the queue's messages until cancellation; unacknowledged messages are redelivered when the flow closes.
    /// </summary>
    /// <param name="source">The durable queue name.</param>
    /// <param name="cancellationToken">The token that stops reading and closes the flow.</param>
    /// <returns>The received messages in queue order.</returns>
    /// <exception cref="InvalidOperationException">The queue cannot be bound.</exception>
    public async IAsyncEnumerable<InboundMessage> ReadAsync(string source, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var received = Channel.CreateUnbounded<InboundMessage>(new UnboundedChannelOptions { SingleReader = true });
        IFlow? flow = null;
        try
        {
            // Start stopped so no message arrives before the flow reference used for acknowledgment is assigned.
            flow = connection.Session().CreateFlow(
                new FlowProperties { AckMode = MessageAckMode.ClientAck, WindowSize = 1, FlowStartState = false },
                ContextFactory.Instance.CreateQueue(source), null, (_, e) => received.Writer.TryWrite(Copy(e.Message, source, flow!)), (_, _) => { });
            flow.Start();
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            flow?.Dispose();
            throw new InvalidOperationException($"Cannot bind Solace queue {source}: {ex.Message}", ex);
        }
        using (flow)
        {
            await foreach (var message in received.Reader.ReadAllAsync(cancellationToken))
            {
                yield return message;
            }
        }
    }

    /// <summary>
    /// Copies a native message and releases it; the copy acknowledges it on its flow.
    /// </summary>
    /// <param name="message">The native message.</param>
    /// <param name="source">The queue name.</param>
    /// <param name="flow">The flow that delivered the message.</param>
    /// <returns>The managed copy.</returns>
    private static InboundMessage Copy(IMessage message, string source, IFlow flow)
    {
        using (message)
        {
            long id = message.ADMessageId;
            return SolaceMessageMapper.ToInbound(message, source, _ =>
            {
                var result = flow.Ack(id);
                return result == ReturnCode.SOLCLIENT_OK ? ValueTask.CompletedTask
                    : throw new MessagingUnavailableException("Solace acknowledgment returned " + result + ".");
            });
        }
    }

    #endregion
}
