using Microsoft.Extensions.Configuration;

namespace PresidioDemo.Messaging.Solace;

/// <summary>
/// Solace connection settings, bound from the Solace section of the selected environment.
/// The password is never configured here; it is read from the environment variable this names.
/// </summary>
public sealed class SolaceOptions
{

    #region Properties, Indexers

    /// <summary>
    /// Gets or sets the client name shown on the broker.
    /// </summary>
    /// <value>The client name.</value>
    public string ClientName { get; set; } = "";

    /// <summary>
    /// Gets or sets the initial connection attempts after the first failure.
    /// </summary>
    /// <value>Zero or more retries.</value>
    public int ConnectRetries { get; set; }

    /// <summary>
    /// Gets or sets the broker address, tcp:// for a local broker or tcps:// elsewhere.
    /// </summary>
    /// <value>The host URI, such as tcps://broker.example.com:55443.</value>
    public string Host { get; set; } = "";

    /// <summary>
    /// Gets or sets the keep-alive interval.
    /// </summary>
    /// <value>Milliseconds.</value>
    public int KeepAliveMs { get; set; } = 3000;

    /// <summary>
    /// Gets or sets the environment variable holding the client password.
    /// </summary>
    /// <value>The variable name, such as SOLACE_PASSWORD.</value>
    public string PasswordEnvironmentVariable { get; set; } = "";

    /// <summary>
    /// Gets or sets whether missing queues are created at startup; intended only for a local broker.
    /// </summary>
    /// <value>Whether to create the input and dead-letter queues.</value>
    public bool ProvisionQueues { get; set; }

    /// <summary>
    /// Gets or sets the reconnect attempts after a lost connection; -1 retries forever.
    /// </summary>
    /// <value>The reconnect attempts.</value>
    public int ReconnectRetries { get; set; } = -1;

    /// <summary>
    /// Gets or sets the wait between reconnect attempts.
    /// </summary>
    /// <value>Milliseconds.</value>
    public int ReconnectRetryWaitMs { get; set; } = 3000;

    /// <summary>
    /// Gets or sets the folder of trusted CA certificates, or null for the system defaults.
    /// </summary>
    /// <value>An absolute folder, or one relative to the application folder.</value>
    public string? TrustStoreFolder { get; set; }

    /// <summary>
    /// Gets or sets the client username.
    /// </summary>
    /// <value>The username.</value>
    public string Username { get; set; } = "";

    /// <summary>
    /// Gets or sets whether the broker certificate, its dates and its host name are validated.
    /// </summary>
    /// <value>True except for a plain tcp:// local broker.</value>
    public bool ValidateCertificate { get; set; } = true;

    /// <summary>
    /// Gets or sets the Message VPN; each environment uses its own VPN so queue names never collide.
    /// </summary>
    /// <value>The VPN name.</value>
    public string Vpn { get; set; } = "";

    /// <summary>
    /// Gets a value indicating whether the host uses TLS.
    /// </summary>
    /// <value>Whether the host starts with tcps://.</value>
    public bool UsesTls => Host.StartsWith("tcps://", StringComparison.OrdinalIgnoreCase);

    #endregion

    #region Methods

    /// <summary>
    /// Binds the Solace section strictly and validates it.
    /// </summary>
    /// <param name="configuration">The environment-resolved configuration.</param>
    /// <param name="baseDirectory">The folder that relative paths resolve against.</param>
    /// <param name="environment">Reads an environment variable; defaults to the process environment.</param>
    /// <returns>The validated options.</returns>
    /// <exception cref="InvalidOperationException">The section is missing, has unknown keys or is unsafe.</exception>
    public static SolaceOptions Load(IConfiguration configuration, string baseDirectory, Func<string, string?>? environment = null)
    {
        var options = configuration.GetSection("Solace").Get<SolaceOptions>(binder => binder.ErrorOnUnknownConfiguration = true)
            ?? throw new InvalidOperationException("Missing Solace settings for the selected environment.");
        options.Validate(environment ?? Environment.GetEnvironmentVariable);
        if (options.TrustStoreFolder is { } store)
        {
            options.TrustStoreFolder = Path.GetFullPath(store, baseDirectory);
            if (!Directory.Exists(options.TrustStoreFolder))
            {
                throw new InvalidOperationException("Solace:TrustStoreFolder does not exist.");
            }
        }
        return options;
    }

    /// <summary>
    /// Rejects missing or unsafe settings.
    /// </summary>
    /// <param name="environment">Reads an environment variable.</param>
    /// <exception cref="InvalidOperationException">A setting is missing or unsafe.</exception>
    public void Validate(Func<string, string?> environment)
    {
        if (new[] { Host, Vpn, Username, PasswordEnvironmentVariable, ClientName }.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("Solace requires Host, Vpn, Username, PasswordEnvironmentVariable and ClientName.");
        }
        if (!UsesTls && !Host.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Solace:Host must use the native tcp:// or tcps:// protocol.");
        }
        if (UsesTls && !ValidateCertificate)
        {
            throw new InvalidOperationException("A tcps:// Solace host requires ValidateCertificate to be true.");
        }
        if (ConnectRetries < 0 || ReconnectRetries < -1 || ReconnectRetryWaitMs < 1 || KeepAliveMs < 1)
        {
            throw new InvalidOperationException("Solace retry counts and intervals are out of range.");
        }
        if (string.IsNullOrEmpty(environment(PasswordEnvironmentVariable)))
        {
            throw new InvalidOperationException($"Solace password environment variable {PasswordEnvironmentVariable} is not set.");
        }
    }

    #endregion
}
