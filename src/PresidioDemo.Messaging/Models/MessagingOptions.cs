using Microsoft.Extensions.Configuration;

namespace PresidioDemo.Messaging;

/// <summary>
/// Broker-neutral routing and delivery settings, bound from the Messaging section of appsettings.json.
/// </summary>
public sealed class MessagingOptions
{

    #region Properties, Indexers

    /// <summary>
    /// Gets or sets the input headers copied to protected output; all other headers are dropped.
    /// </summary>
    /// <value>The allowed header names.</value>
    public List<string> CopyHeaders { get; set; } = [];

    /// <summary>
    /// Gets or sets how many deliveries a message may have before it is dead-lettered without processing.
    /// </summary>
    /// <value>The maximum delivery count, at least 1.</value>
    public int MaxDeliveries { get; set; }

    /// <summary>
    /// Gets or sets the wait for each broker publish confirmation.
    /// </summary>
    /// <value>The timeout in milliseconds.</value>
    public int PublishAckTimeoutMs { get; set; }

    /// <summary>
    /// Gets or sets the wait before publishing again while the broker is unavailable.
    /// </summary>
    /// <value>The retry delay in milliseconds.</value>
    public int PublishRetryDelayMs { get; set; }

    /// <summary>
    /// Gets or sets the input, output and dead-letter destinations for each feed.
    /// </summary>
    /// <value>One route per feed.</value>
    public List<MessageRoute> Routes { get; set; } = [];

    /// <summary>
    /// Gets or sets the broker adapter to use, such as Solace.
    /// </summary>
    /// <value>The transport name.</value>
    public string Transport { get; set; } = "";

    #endregion

    #region Methods

    /// <summary>
    /// Binds the Messaging section strictly and validates it.
    /// </summary>
    /// <param name="configuration">The environment-resolved configuration.</param>
    /// <returns>The validated options.</returns>
    /// <exception cref="InvalidOperationException">The section is missing, has unknown keys or is invalid.</exception>
    public static MessagingOptions Load(IConfiguration configuration)
    {
        var options = configuration.GetSection("Messaging").Get<MessagingOptions>(binder => binder.ErrorOnUnknownConfiguration = true)
            ?? throw new InvalidOperationException("Missing Messaging settings.");
        options.Validate();
        return options;
    }

    /// <summary>
    /// Rejects missing, duplicated or unsafe settings.
    /// </summary>
    /// <exception cref="InvalidOperationException">A setting is missing, duplicated or unsafe.</exception>
    public void Validate()
    {
        static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        Require(!string.IsNullOrWhiteSpace(Transport), "Messaging:Transport is required, for example Solace.");
        Require(MaxDeliveries >= 1, "Messaging:MaxDeliveries must be at least 1.");
        Require(PublishAckTimeoutMs >= 1 && PublishRetryDelayMs >= 1, "Messaging publish timeout and retry delay must be positive.");
        Require(CopyHeaders.All(h => !string.IsNullOrWhiteSpace(h)) && CopyHeaders.Distinct(StringComparer.Ordinal).Count() == CopyHeaders.Count,
            "Messaging:CopyHeaders must not contain empty or duplicate names.");
        Require(Routes.Count > 0, "Messaging:Routes must list one route per feed.");
        foreach (var route in Routes)
        {
            Require(new[] { route.Feed, route.Input, route.Output, route.DeadLetter }.All(v => !string.IsNullOrWhiteSpace(v)),
                "Each messaging route requires Feed, Input, Output and DeadLetter.");
            Require(route.Output.Split('/').All(level => level.Length > 0 && !level.Contains('*') && level != ">"),
                "Messaging output topics must be concrete, without empty levels or wildcards.");
            Require(route.Input != route.DeadLetter, "A route's dead-letter queue must differ from its input.");
        }
        foreach (var values in new[] { Routes.Select(r => r.Feed), Routes.Select(r => r.Input), Routes.Select(r => r.Output), Routes.Select(r => r.DeadLetter) })
        {
            var list = values.ToList();
            Require(list.Distinct(StringComparer.OrdinalIgnoreCase).Count() == list.Count, "Each feed requires its own input, output and dead-letter destinations.");
        }
    }

    #endregion
}

/// <summary>
/// The destinations for one feed.
/// </summary>
public sealed class MessageRoute
{

    #region Properties, Indexers

    /// <summary>
    /// Gets or sets the restricted queue receiving rejected input, which contains personal data.
    /// </summary>
    /// <value>The dead-letter queue name.</value>
    public string DeadLetter { get; set; } = "";

    /// <summary>
    /// Gets or sets the feed name, such as PNR or PNR-Linking.
    /// </summary>
    /// <value>The feed name.</value>
    public string Feed { get; set; } = "";

    /// <summary>
    /// Gets or sets the durable queue holding original feed messages.
    /// </summary>
    /// <value>The input queue name.</value>
    public string Input { get; set; } = "";

    /// <summary>
    /// Gets or sets the topic receiving validated protected messages.
    /// </summary>
    /// <value>The output topic name.</value>
    public string Output { get; set; } = "";

    #endregion
}
