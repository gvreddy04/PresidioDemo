using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PresidioDemo.Messaging.Solace;

/// <summary>
/// Registers the Solace adapter's connection, listener and publisher behind the broker-neutral ports.
/// </summary>
public static class SolaceMessaging
{

    #region Fields and Constants

    /// <summary>
    /// The Messaging:Transport value that selects this adapter.
    /// </summary>
    public const string TransportName = "Solace";

    #endregion

    #region Methods

    /// <summary>
    /// Validates the Solace section and registers the adapter as the messaging connection, consumer and publisher.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The environment-resolved configuration.</param>
    /// <param name="messaging">The validated broker-neutral settings.</param>
    /// <param name="baseDirectory">The folder that relative paths resolve against.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="InvalidOperationException">The Solace settings are missing or unsafe.</exception>
    public static IServiceCollection AddSolaceMessaging(this IServiceCollection services, IConfiguration configuration,
        MessagingOptions messaging, string baseDirectory)
    {
        var options = SolaceOptions.Load(configuration, baseDirectory);
        services.AddSingleton(options);
        services.AddSingleton(provider => new SolaceConnection(options, messaging, provider.GetRequiredService<ILogger<SolaceConnection>>()));
        services.AddSingleton<IMessagingConnection>(provider => provider.GetRequiredService<SolaceConnection>());
        services.AddSingleton<IMessageConsumer>(provider => new SolaceMessageConsumer(provider.GetRequiredService<SolaceConnection>()));
        services.AddSingleton<IMessagePublisher>(provider => new SolaceMessagePublisher(provider.GetRequiredService<SolaceConnection>(), messaging));
        return services;
    }

    #endregion
}
