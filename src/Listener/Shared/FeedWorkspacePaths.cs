namespace PresidioDemo.Transport;

/// <summary>
/// Locates feed input, processing, output, logs and private listener state.
/// </summary>
public sealed class FeedWorkspacePaths
{

    #region Constructors

    /// <summary>
    /// Resolves exchange and private listener-state paths from one workspace root.
    /// </summary>
    /// <param name="docsRoot">The workspace root containing feed-exchange and diagnostic directories.</param>
    /// <exception cref="ArgumentException">A supplied argument does not satisfy the supported range or configuration contract.</exception>
    public FeedWorkspacePaths(string docsRoot)
    {
        DocsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(docsRoot));
        string parent = Directory.GetParent(DocsRoot)?.FullName
            ?? throw new ArgumentException("The docs root must be a named directory, not a drive root.", nameof(docsRoot));
        RuntimeRoot = Path.Combine(parent, ".runtime", Path.GetFileName(DocsRoot));
    }

    #endregion

    #region Properties, Indexers

    /// <summary>
    /// Gets the configured root containing documentation and feed-exchange directories.
    /// </summary>
    /// <value>The configured root containing documentation and feed-exchange directories.</value>
    public string DocsRoot { get; }

    /// <summary>
    /// Gets the directory containing complete rejected inputs and their error reports.
    /// </summary>
    /// <value>The directory containing complete rejected inputs and their error reports.</value>
    public string FailedRoot => Path.Combine(DocsRoot, "failed-feeds");

    /// <summary>
    /// Gets the directory containing files waiting to be claimed.
    /// </summary>
    /// <value>The directory containing files waiting to be claimed.</value>
    public string InputRoot => Path.Combine(DocsRoot, "input-feeds");

    /// <summary>
    /// Gets the exclusive listener-lock path for this workspace.
    /// </summary>
    /// <value>The exclusive listener-lock path for this workspace.</value>
    public string LockFile => Path.Combine(RuntimeRoot, "listener.lock");

    /// <summary>
    /// Gets the directory containing transaction-specific diagnostic reports.
    /// </summary>
    /// <value>The directory containing transaction-specific diagnostic reports.</value>
    public string LogsRoot => Path.Combine(DocsRoot, "feed-logs");

    /// <summary>
    /// Gets the directory containing validated protected output.
    /// </summary>
    /// <value>The directory containing validated protected output.</value>
    public string OutputRoot => Path.Combine(DocsRoot, "output-feeds");

    /// <summary>
    /// Gets the directory containing claimed or recoverable input files.
    /// </summary>
    /// <value>The directory containing claimed or recoverable input files.</value>
    public string ProcessingRoot => Path.Combine(DocsRoot, "feed-processing");

    /// <summary>
    /// Gets the directory containing batch verification reports.
    /// </summary>
    /// <value>The directory containing batch verification reports.</value>
    public string ReportsRoot => Path.Combine(RuntimeRoot, "reports");

    /// <summary>
    /// Gets the private listener-state directory associated with the feed workspace.
    /// </summary>
    /// <value>The private listener-state directory associated with the feed workspace.</value>
    public string RuntimeRoot { get; }

    #endregion
}
