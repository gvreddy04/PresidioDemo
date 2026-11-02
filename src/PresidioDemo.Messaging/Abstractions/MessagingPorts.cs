namespace PresidioDemo.Messaging;

#region Interfaces

/// <summary>
/// Opens and closes the broker connection shared by the consumer and publisher.
/// </summary>
public interface IMessagingConnection : IAsyncDisposable
{

    #region Methods

    /// <summary>
    /// Connects to the broker and prepares the configured endpoints.
    /// </summary>
    /// <param name="cancellationToken">The token used to abandon connecting during shutdown.</param>
    /// <returns>A task that completes once the connection is ready.</returns>
    /// <exception cref="InvalidOperationException">The broker connection or an endpoint could not be established.</exception>
    Task ConnectAsync(CancellationToken cancellationToken);

    #endregion

    #region Properties, Indexers

    /// <summary>
    /// Gets the transport name shown in startup logs, such as Solace.
    /// </summary>
    /// <value>The transport name.</value>
    string Transport { get; }

    #endregion
}

/// <summary>
/// Reads durable messages one at a time; the next message arrives only after the previous one is completed.
/// </summary>
public interface IMessageConsumer
{

    #region Methods

    /// <summary>
    /// Streams messages from a queue until cancellation; uncompleted messages are redelivered by the broker.
    /// </summary>
    /// <param name="source">The queue, or for log-based brokers the topic, to read.</param>
    /// <param name="cancellationToken">The token that stops reading.</param>
    /// <returns>The received messages in broker order.</returns>
    IAsyncEnumerable<InboundMessage> ReadAsync(string source, CancellationToken cancellationToken);

    #endregion
}

/// <summary>
/// Publishes durable messages and completes only after the broker confirms them.
/// </summary>
public interface IMessagePublisher
{

    #region Methods

    /// <summary>
    /// Publishes a persistent message and waits for the broker's confirmation.
    /// </summary>
    /// <param name="message">The message and its destination.</param>
    /// <param name="cancellationToken">The token used to stop waiting during shutdown.</param>
    /// <returns>A task that completes once the broker has accepted the message.</returns>
    /// <exception cref="MessagingUnavailableException">The broker is unavailable, refused the message or did not confirm it in time.</exception>
    Task PublishAsync(OutboundMessage message, CancellationToken cancellationToken);

    #endregion
}

#endregion
