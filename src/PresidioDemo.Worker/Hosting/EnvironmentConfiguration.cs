using Microsoft.Extensions.Configuration;

namespace PresidioDemo.Worker;

/// <summary>
/// Builds configuration from the single appsettings.json: shared settings, then the selected environment's
/// section, then environment variables. The deployment selects the environment with DOTNET_ENVIRONMENT;
/// when it is not set, the file's CurrentEnvironment value applies (Local on developer machines).
/// </summary>
public static class EnvironmentConfiguration
{

    #region Fields and Constants

    /// <summary>
    /// The standard .NET variable that the AWS task definition sets to select an environment.
    /// </summary>
    public const string EnvironmentVariable = "DOTNET_ENVIRONMENT";

    /// <summary>
    /// The settings allowed at the top of the file and inside each environment section.
    /// </summary>
    private static readonly string[] Sections = ["Messaging", "Solace", "Presidio", "Logging"];

    #endregion

    #region Methods

    /// <summary>
    /// Loads appsettings.json and resolves the selected environment.
    /// </summary>
    /// <param name="settingsFile">The appsettings.json path.</param>
    /// <param name="environment">Reads an environment variable; defaults to the process environment.</param>
    /// <returns>The merged configuration and the selected environment name.</returns>
    /// <exception cref="InvalidOperationException">The file has unknown sections or the selected environment is not configured.</exception>
    public static (IConfigurationRoot Configuration, string Environment) Build(string settingsFile, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        // Structure is checked against the file alone; environment variables hold many unrelated keys.
        var fileOnly = new ConfigurationBuilder().AddJsonFile(settingsFile, optional: false, reloadOnChange: false).Build();
        var unknown = fileOnly.GetChildren().Select(s => s.Key).Where(k => k is not ("CurrentEnvironment" or "Environments") && !Sections.Contains(k)).ToArray();
        if (unknown.Length > 0)
        {
            throw new InvalidOperationException("Unknown appsettings.json sections: " + string.Join(", ", unknown));
        }
        var file = new ConfigurationBuilder().AddConfiguration(fileOnly).AddEnvironmentVariables().Build();

        string name = environment(EnvironmentVariable) is { Length: > 0 } selected ? selected
            : file["CurrentEnvironment"] is { Length: > 0 } fallback ? fallback
            : throw new InvalidOperationException($"Set {EnvironmentVariable} or CurrentEnvironment in appsettings.json.");
        var section = file.GetSection("Environments").GetChildren().SingleOrDefault(s => s.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Environment {name} is not configured. Add an Environments:{name} section to appsettings.json.");
        var badKeys = section.GetChildren().Select(s => s.Key).Where(k => !Sections.Contains(k)).ToArray();
        if (badKeys.Length > 0)
        {
            throw new InvalidOperationException($"Unknown settings in Environments:{name}: " + string.Join(", ", badKeys));
        }

        // The environment's values replace the shared ones; environment variables (such as Solace__Host) still win.
        var overlay = section.AsEnumerable(makePathsRelative: true).Where(pair => pair.Value is not null);
        var merged = new ConfigurationBuilder()
            .AddJsonFile(settingsFile, optional: false, reloadOnChange: false)
            .AddInMemoryCollection(overlay)
            .AddEnvironmentVariables()
            .Build();
        return (merged, section.Key);
    }

    #endregion
}
