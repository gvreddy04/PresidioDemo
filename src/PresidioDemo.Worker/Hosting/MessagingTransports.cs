using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PresidioDemo.Messaging;
using PresidioDemo.Messaging.Solace;

namespace PresidioDemo.Worker;

/// <summary>
/// The one place a broker adapter is chosen from Messaging:Transport.
/// A new broker adds a case here and its own PresidioDemo.Messaging.&lt;Broker&gt; project.
/// </summary>
public static class MessagingTransports
{

    #region Methods

    /// <summary>
    /// Registers the configured adapter's connection, consumer and publisher.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The environment-resolved configuration.</param>
    /// <param name="messaging">The validated broker-neutral settings.</param>
    /// <param name="baseDirectory">The folder that relative paths resolve against.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="InvalidOperationException">The transport is unknown or its settings are invalid.</exception>
    public static IServiceCollection AddConfiguredMessaging(this IServiceCollection services, IConfiguration configuration,
        MessagingOptions messaging, string baseDirectory) => messaging.Transport switch
        {
            SolaceMessaging.TransportName => services.AddSolaceMessaging(configuration, messaging, baseDirectory),
            _ => throw new InvalidOperationException($"Unknown Messaging:Transport '{messaging.Transport}'. Supported: {SolaceMessaging.TransportName}.")
        };

    #endregion
}
