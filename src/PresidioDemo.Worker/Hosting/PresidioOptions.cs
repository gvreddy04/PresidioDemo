using Microsoft.Extensions.Configuration;

namespace PresidioDemo.Worker;

/// <summary>
/// The protection configuration files, bound from the Presidio section; relative paths resolve against the application folder.
/// </summary>
public sealed class PresidioOptions
{

    #region Properties, Indexers

    /// <summary>
    /// Gets or sets the feed-policies XML file.
    /// </summary>
    /// <value>The file path.</value>
    public string PolicyFile { get; set; } = "Configuration/feed-policies.xml";

    /// <summary>
    /// Gets or sets the Python virtual environment folder, or null for the project's src/.venv.
    /// </summary>
    /// <value>The folder that holds pyvenv.cfg, such as /opt/venv in the container image.</value>
    public string? PythonEnvironment { get; set; }

    /// <summary>
    /// Gets or sets the reference layouts XML file.
    /// </summary>
    /// <value>The file path.</value>
    public string ReferenceLayoutFile { get; set; } = "Configuration/reference-layouts.xml";

    /// <summary>
    /// Gets or sets the XDR layouts XML file.
    /// </summary>
    /// <value>The file path.</value>
    public string XdrLayoutFile { get; set; } = "Configuration/xdr-layouts.xml";

    #endregion

    #region Methods

    /// <summary>
    /// Binds the Presidio section strictly and resolves the file paths.
    /// </summary>
    /// <param name="configuration">The environment-resolved configuration.</param>
    /// <param name="baseDirectory">The folder that relative paths resolve against.</param>
    /// <returns>The options with absolute paths.</returns>
    /// <exception cref="InvalidOperationException">The configured Python environment has no pyvenv.cfg.</exception>
    public static PresidioOptions Load(IConfiguration configuration, string baseDirectory)
    {
        var options = configuration.GetSection("Presidio").Get<PresidioOptions>(binder => binder.ErrorOnUnknownConfiguration = true) ?? new();
        options.PolicyFile = Path.GetFullPath(options.PolicyFile, baseDirectory);
        options.ReferenceLayoutFile = Path.GetFullPath(options.ReferenceLayoutFile, baseDirectory);
        options.XdrLayoutFile = Path.GetFullPath(options.XdrLayoutFile, baseDirectory);
        if (string.IsNullOrWhiteSpace(options.PythonEnvironment))
        {
            options.PythonEnvironment = null;
        }
        else
        {
            options.PythonEnvironment = Path.GetFullPath(options.PythonEnvironment, baseDirectory);
            if (!File.Exists(Path.Combine(options.PythonEnvironment, "pyvenv.cfg")))
            {
                throw new InvalidOperationException("Presidio:PythonEnvironment has no pyvenv.cfg: " + options.PythonEnvironment);
            }
        }
        return options;
    }

    #endregion
}
