using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SolaceSystems.Solclient.Messaging;

namespace PresidioDemo.Messaging.Solace;

/// <summary>
/// Owns the native Solace context and session shared by the listener and publisher, and tracks publish confirmations.
/// </summary>
/// <param name="options">The validated Solace settings.</param>
/// <param name="messaging">The validated routes; used to provision queues on a local broker.</param>
/// <param name="logger">The connection event log.</param>
/// <param name="environment">Reads the password environment variable; defaults to the process environment.</param>
public sealed class SolaceConnection(SolaceOptions options, MessagingOptions messaging, ILogger<SolaceConnection> logger,
    Func<string, string?>? environment = null) : IMessagingConnection
{

    #region Fields and Constants

    /// <summary>
    /// Guards one-time initialization of the native client library.
    /// </summary>
    private static readonly Lock InitializationLock = new();

    /// <summary>
    /// Whether the native client library has been initialized in this process.
    /// </summary>
    private static bool _initialized;

    /// <summary>
    /// Publications waiting for a broker confirmation, keyed by correlation key.
    /// </summary>
    private readonly ConcurrentDictionary<long, TaskCompletionSource> _pending = new();

    /// <summary>
    /// The native client context owning the session's dispatch thread.
    /// </summary>
    private IContext? _context;

    /// <summary>
    /// The next correlation key assigned to a publication.
    /// </summary>
    private long _nextKey;

    /// <summary>
    /// The connected broker session.
    /// </summary>
    private ISession? _session;

    /// <summary>
    /// Whether the session is connected rather than down or reconnecting.
    /// </summary>
    private volatile bool _up;

    #endregion

    #region Methods

    /// <summary>
    /// Connects the session and, when enabled for a local broker, creates the input and dead-letter queues.
    /// </summary>
    /// <param name="cancellationToken">Unused; the native connect call blocks for its configured retries.</param>
    /// <returns>A task that completes once the session is connected.</returns>
    /// <exception cref="InvalidOperationException">The connection or a queue could not be established.</exception>
    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        lock (InitializationLock)
        {
            if (!_initialized)
            {
                var factory = new ContextFactoryProperties { SolClientLogLevel = SolLogLevel.Warning };
                factory.LogToConsoleError();
                ContextFactory.Instance.Init(factory);
                _initialized = true;
            }
        }
        var properties = new SessionProperties
        {
            Host = options.Host,
            VPNName = options.Vpn,
            UserName = options.Username,
            Password = (environment ?? Environment.GetEnvironmentVariable)(options.PasswordEnvironmentVariable)
                ?? throw new InvalidOperationException($"Solace password environment variable {options.PasswordEnvironmentVariable} is not set."),
            ClientName = options.ClientName,
            ConnectRetries = options.ConnectRetries,
            ReconnectRetries = options.ReconnectRetries,
            ReconnectRetriesWaitInMsecs = options.ReconnectRetryWaitMs,
            KeepAliveIntervalInMsecs = options.KeepAliveMs,
            SSLValidateCertificate = options.ValidateCertificate,
            SSLValidateCertificateDate = options.ValidateCertificate,
            SSLValidateCertificateHost = options.ValidateCertificate,
            SendBlocking = true
        };
        if (options.TrustStoreFolder is { } store)
        {
            properties.SSLTrustStoreDir = store;
        }
        try
        {
            _context = ContextFactory.Instance.CreateContext(new ContextProperties(), null);
            _session = _context.CreateSession(properties, (_, e) => e.Message.Dispose(), OnSessionEvent);
            var result = _session.Connect();
            if (result != ReturnCode.SOLCLIENT_OK)
            {
                throw new InvalidOperationException("Solace connection failed: " + result);
            }
            _up = true;
            if (options.ProvisionQueues)
            {
                foreach (string queue in messaging.Routes.SelectMany(r => new[] { r.Input, r.DeadLetter }))
                {
                    var endpoint = new EndpointProperties { Permission = EndpointProperties.EndpointPermission.Consume, AccessType = EndpointProperties.EndpointAccessType.Exclusive };
                    _session.Provision(ContextFactory.Instance.CreateQueue(queue), endpoint,
                        ProvisionFlag.WaitForConfirm | ProvisionFlag.IgnoreErrorIfEndpointAlreadyExists, null);
                }
                logger.LogInformation("Provisioned {Count} Solace queues on the local broker.", messaging.Routes.Count * 2);
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Solace connection to {options.Host} (VPN {options.Vpn}) failed: {ex.Message}", ex);
        }
        logger.LogInformation("Connected to Solace {Host} (VPN {Vpn}) as {Client}.", options.Host, options.Vpn, options.ClientName);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Releases the session and context, failing any publication still waiting for confirmation.
    /// </summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        _up = false;
        _session?.Dispose();
        _context?.Dispose();
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var waiting))
            {
                waiting.TrySetException(new MessagingUnavailableException("Solace session closed before confirmation."));
            }
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Sends a persistent message and waits for the broker's confirmation.
    /// </summary>
    /// <param name="outgoing">The native message, already populated.</param>
    /// <param name="timeout">The longest wait for the confirmation.</param>
    /// <param name="cancellationToken">The token used to stop waiting during shutdown.</param>
    /// <returns>A task that completes once the broker has accepted the message.</returns>
    /// <exception cref="MessagingUnavailableException">The session is down, the broker refused the message or did not confirm it in time.</exception>
    internal async Task SendConfirmedAsync(IMessage outgoing, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var session = _session;
        if (session is null || !_up)
        {
            throw new MessagingUnavailableException("Solace session is not connected.");
        }
        long key = Interlocked.Increment(ref _nextKey);
        var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = confirmed;
        try
        {
            outgoing.CorrelationKey = key;
            var result = session.Send(outgoing);
            if (result != ReturnCode.SOLCLIENT_OK)
            {
                throw new MessagingUnavailableException("Solace send returned " + result + ".");
            }
            await confirmed.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new MessagingUnavailableException("No Solace confirmation within the configured timeout.", ex);
        }
        catch (Exception ex) when (ex is not (MessagingUnavailableException or OperationCanceledException))
        {
            throw new MessagingUnavailableException("Solace publish failed: " + ex.Message, ex);
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Returns the connected session for creating flows.
    /// </summary>
    /// <returns>The session.</returns>
    /// <exception cref="InvalidOperationException">The connection has not been opened.</exception>
    internal ISession Session() => _session ?? throw new InvalidOperationException("Solace connection is not open.");

    /// <summary>
    /// Completes publication waits and tracks connection state.
    /// </summary>
    /// <param name="sender">The session raising the event.</param>
    /// <param name="e">The session event.</param>
    private void OnSessionEvent(object? sender, SessionEventArgs e)
    {
        switch (e.Event)
        {
            case SessionEvent.Acknowledgement:
                if (e.CorrelationKey is long accepted && _pending.TryRemove(accepted, out var done))
                {
                    done.TrySetResult();
                }
                break;
            case SessionEvent.RejectedMessageError:
                if (e.CorrelationKey is long refused && _pending.TryRemove(refused, out var failed))
                {
                    failed.TrySetException(new MessagingUnavailableException($"Solace refused the message ({e.ResponseCode} {e.Info})."));
                }
                break;
            case SessionEvent.Reconnecting or SessionEvent.DownError:
                _up = false;
                logger.LogWarning("Solace session {Event}: {Info}", e.Event, e.Info);
                break;
            case SessionEvent.Reconnected or SessionEvent.UpNotice:
                _up = true;
                logger.LogInformation("Solace session {Event}.", e.Event);
                break;
        }
    }

    #endregion

    #region Properties, Indexers

    /// <summary>
    /// Gets the transport name shown in startup logs.
    /// </summary>
    /// <value>Solace.</value>
    public string Transport => "Solace";

    #endregion
}
