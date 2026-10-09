namespace PresidioDemo.Feeds;

/// <summary>Separates feed exchange folders from listener recovery state and reports.</summary>
public sealed class FeedWorkspacePaths
{
    public FeedWorkspacePaths(string docsRoot)
    {
        DocsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(docsRoot));
        string parent = Directory.GetParent(DocsRoot)?.FullName
            ?? throw new ArgumentException("The docs root must be a named directory, not a drive root.", nameof(docsRoot));
        RuntimeRoot = Path.Combine(parent, ".runtime", Path.GetFileName(DocsRoot));
    }

    public string DocsRoot { get; }
    public string RuntimeRoot { get; }
    public string InputRoot => Path.Combine(DocsRoot, "input-feeds");
    public string OutputRoot => Path.Combine(DocsRoot, "output-feeds");
    public string FailedRoot => Path.Combine(DocsRoot, "failed-feeds");
    public string ProcessingRoot => Path.Combine(RuntimeRoot, "processing");
    public string ReportsRoot => Path.Combine(RuntimeRoot, "reports");
    public string LockFile => Path.Combine(RuntimeRoot, "listener.lock");
}
