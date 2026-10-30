using SolaceSystems.Solclient.Messaging;

namespace PresidioDemo.Messaging.Solace;

/// <summary>
/// Publishes persistent messages to Solace topics or queues and waits for the broker's confirmation.
/// </summary>
/// <param name="connection">The shared Solace connection.</param>
/// <param name="messaging">The delivery settings, including the confirmation timeout.</param>
public sealed class SolaceMessagePublisher(SolaceConnection connection, MessagingOptions messaging) : IMessagePublisher
{

    #region Methods

    /// <summary>
    /// Publishes a persistent message and waits for the broker's confirmation.
    /// </summary>
    /// <param name="message">The message and its destination.</param>
    /// <param name="cancellationToken">The token used to stop waiting during shutdown.</param>
    /// <returns>A task that completes once the broker has accepted the message.</returns>
    /// <exception cref="MessagingUnavailableException">The broker is unavailable, refused the message or did not confirm it in time.</exception>
    public async Task PublishAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        using (var outgoing = ContextFactory.Instance.CreateMessage())
        {
            SolaceMessageMapper.Populate(outgoing, message);
            await connection.SendConfirmedAsync(outgoing, TimeSpan.FromMilliseconds(messaging.PublishAckTimeoutMs), cancellationToken);
        }
    }

    #endregion
}
