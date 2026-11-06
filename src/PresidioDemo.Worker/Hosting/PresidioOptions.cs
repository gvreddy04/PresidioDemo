using Microsoft.Extensions.Configuration;

namespace PresidioDemo.Worker;

/// <summary>
/// The protection configuration files, bound from the Presidio section; relative paths resolve against the application folder.
/// </summary>
public sealed class PresidioOptions
{

    #region Properties, Indexers

    /// <summary>
    /// Gets or sets whether the value-free detection log is written (WR-018). Off by default; an environment settings file
    /// or the <c>Presidio__DetectionLog</c> environment variable turns it on for one feed service. Read at startup.
    /// </summary>
    /// <value>True to write one value-free line per decoded string.</value>
    public bool DetectionLog { get; set; }

    /// <summary>
    /// Gets or sets the Python virtual environment folder, or null for the project's src/.venv.
    /// </summary>
    /// <value>The folder that holds pyvenv.cfg, such as /opt/venv in the container image.</value>
    public string? PythonEnvironment { get; set; }

    /// <summary>
    /// Gets or sets the folder that holds one recognizer XML file per feed (WR-014). The files ship inside the image
    /// (PR-005); the folder is resolved against the application folder.
    /// </summary>
    /// <value>The folder path, by default <c>Configuration/recognizers</c>.</value>
    public string RecognizerDirectory { get; set; } = "Configuration/recognizers";

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
        options.RecognizerDirectory = Path.GetFullPath(options.RecognizerDirectory, baseDirectory);
        string application = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory)) + Path.DirectorySeparatorChar;
        if (!options.RecognizerDirectory.StartsWith(application, StringComparison.OrdinalIgnoreCase))
        {
            // Recognizer files ship only inside the reviewed image (PR-005), never from a mounted or other folder.
            throw new InvalidOperationException("Presidio:RecognizerDirectory must be a folder inside the application folder.");
        }
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
