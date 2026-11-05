using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PresidioDemo.Feeds.Shared;
using PresidioDemo.Messaging;
using PresidioDemo.Pipeline;
using PresidioDemo.Protection;

namespace PresidioDemo.Worker;

/// <summary>
/// Connects the broker, starts embedded Presidio and runs one feed worker per route until the host stops.
/// </summary>
/// <param name="connection">The selected broker adapter's connection.</param>
/// <param name="consumer">The selected broker adapter's consumer.</param>
/// <param name="publisher">The selected broker adapter's publisher.</param>
/// <param name="messaging">The validated broker-neutral settings.</param>
/// <param name="pipeline">The validated protection configuration.</param>
/// <param name="presidio">The protection settings, including the Python virtual environment.</param>
/// <param name="loggers">Creates one logger per feed worker.</param>
/// <param name="lifetime">Stops the host when the worker cannot continue.</param>
public sealed class FeedWorkerService(IMessagingConnection connection, IMessageConsumer consumer, IMessagePublisher publisher,
    MessagingOptions messaging, FeedPipelineConfiguration pipeline, PresidioOptions presidio, ILoggerFactory loggers, IHostApplicationLifetime lifetime) : BackgroundService
{

    #region Properties, Indexers

    /// <summary>
    /// Gets the reason the worker stopped on its own, or null after a normal shutdown.
    /// </summary>
    /// <value>The failure message.</value>
    public string? Failure { get; private set; }

    #endregion

    #region Methods

    /// <summary>
    /// Maps each configured route to its feed. An environment routes only the feeds its broker carries;
    /// feeds without a route are not consumed.
    /// </summary>
    /// <param name="messaging">The validated broker-neutral settings.</param>
    /// <returns>The route for each routed feed.</returns>
    /// <exception cref="InvalidOperationException">A route names an unknown feed or a feed has more than one route.</exception>
    public static IReadOnlyDictionary<FeedType, MessageRoute> Routes(MessagingOptions messaging)
    {
        var routes = new Dictionary<FeedType, MessageRoute>();
        foreach (var route in messaging.Routes)
        {
            var feed = FeedCatalog.All.SingleOrDefault(f => f.FolderName.Equals(route.Feed, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Unknown feed {route.Feed} in Messaging:Routes.");
            if (!routes.TryAdd(feed.Type, route))
            {
                throw new InvalidOperationException($"Feed {route.Feed} has more than one route.");
            }
        }
        return routes;
    }

    /// <summary>
    /// Runs every feed worker; a worker failure stops the host.
    /// </summary>
    /// <param name="stoppingToken">Signalled when the host stops, for example on SIGTERM from ECS.</param>
    /// <returns>A task that completes once every worker has stopped and Presidio has shut down.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var log = loggers.CreateLogger<FeedWorkerService>();
        bool pythonStarted = false;
        try
        {
            await connection.ConnectAsync(stoppingToken);
            log.LogInformation("Initializing embedded local Presidio and the bundled NLP model...");
            PythonHost.Start(presidio.PythonEnvironment);
            pythonStarted = true;
            var processor = new FeedMessageProcessor(pipeline.CreateRouter(new PresidioFeedProtector()));
            // ConsumersPerTask workers per feed, each on its own broker flow: .NET parsing and broker round trips overlap
            // across consumers and feeds; shared Python calls are serialized.
            var routes = Routes(messaging);
            var unrouted = FeedCatalog.All.Where(f => !routes.ContainsKey(f.Type)).Select(f => f.FolderName).ToArray();
            if (unrouted.Length > 0)
            {
                log.LogInformation("No route for {Feeds}; these feeds are not consumed.", string.Join(", ", unrouted));
            }
            var workers = routes.SelectMany(pair => Enumerable.Range(0, messaging.ConsumersPerTask).Select(_ =>
                new FeedWorker(pair.Key, pair.Value, messaging, consumer, publisher, processor,
                    loggers.CreateLogger("PresidioDemo.Worker." + FeedCatalog.Get(pair.Key).FolderName)).RunAsync(stoppingToken))).ToArray();
            if (messaging.ConsumersPerTask > 1)
            {
                log.LogInformation("{Consumers} consumers per feed.", messaging.ConsumersPerTask);
            }
            log.LogInformation("READY: {Count} feed workers on {Transport}.", routes.Count, connection.Transport);
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Unacknowledged messages stay on the broker; the orchestrator restarts the task.
            Failure = ex.Message;
            log.LogCritical("Worker stopped: {Reason}", ex.Message);
            lifetime.StopApplication();
        }
        finally
        {
            if (pythonStarted)
            {
                PythonHost.Stop();
            }
        }
    }

    #endregion
}
