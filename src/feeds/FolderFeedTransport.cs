using System.Text;

namespace PresidioDemo.Feeds;

/// <summary>Folder adapter: receive, publish, reject and acknowledge a delivery.</summary>
public sealed class FolderFeedTransport(string docsRoot) : IFeedTransport
{
    private sealed record Observation(long Length, long LastWriteTicks, DateTime StableSince);
    private readonly Dictionary<string, Observation> _observations = new(StringComparer.OrdinalIgnoreCase);
    private readonly FeedWorkspacePaths _paths = new(docsRoot);
    private FileStream? _instanceLock;

    public string InputRoot => _paths.InputRoot;
    public string OutputRoot => _paths.OutputRoot;
    public string FailedRoot => _paths.FailedRoot;
    public string LogsRoot => _paths.LogsRoot;
    public string ProcessingRoot => _paths.ProcessingRoot;

    public void Initialize()
    {
        if (Directory.Exists(Path.Combine(_paths.DocsRoot, ".processing")))
        {
            throw new InvalidDataException("Legacy working deliveries found in docs/.processing. Migrate them to feed-processing before starting.");
        }
        Directory.CreateDirectory(_paths.DocsRoot);
        Directory.CreateDirectory(_paths.RuntimeRoot);
        // A single consumer owns a docs root. This is released automatically on exit.
        _instanceLock = new FileStream(_paths.LockFile,
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Directory.CreateDirectory(LogsRoot);
        foreach (var feed in FeedCatalog.All)
        {
            foreach (var root in new[] { InputRoot, OutputRoot, FailedRoot, ProcessingRoot })
            {
                Directory.CreateDirectory(Path.Combine(root, feed.FolderName));
            }
        }
        MigrateRuntimeProcessing();
    }

    public IReadOnlyList<FeedDelivery> ClaimReadyDeliveries(IReadOnlySet<FeedType>? busyFeeds = null)
    {
        List<FeedDelivery> deliveries = [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var feed in FeedCatalog.All)
        {
            // A worker may delete or reject paths in this feed. Scan it only while idle.
            if (busyFeeds?.Contains(feed.Type) == true)
            {
                continue;
            }
            string workRoot = Path.Combine(ProcessingRoot, feed.FolderName);
            // The filename persists both the source name and delivery ID across restarts.
            foreach (string workingPath in Directory.EnumerateFiles(workRoot).Where(IsFeedFile).Order())
            {
                var delivery = RecoverDelivery(feed.Type, workingPath);
                string failed = Destination(FailedRoot, delivery);
                if (File.Exists(failed) && File.Exists(Path.ChangeExtension(failed, ".error.txt")))
                {
                    // A crash may leave a completed rejection waiting only for cleanup.
                    try
                    {
                        Acknowledge(delivery);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Keep the original until deletion can be retried on the next scan.
                    }
                    continue;
                }
                deliveries.Add(delivery);
            }

            string inbox = Path.Combine(InputRoot, feed.FolderName);
            foreach (string path in Directory.EnumerateFiles(inbox).Where(IsFeedFile).Order())
            {
                seen.Add(path);
                try
                {
                    var info = new FileInfo(path);
                    var now = DateTime.UtcNow;
                    if (!_observations.TryGetValue(path, out var previous) ||
                        previous.Length != info.Length || previous.LastWriteTicks != info.LastWriteTimeUtc.Ticks)
                    {
                        _observations[path] = new(info.Length, info.LastWriteTimeUtc.Ticks, now);
                        continue;
                    }
                    if (now - previous.StableSince < TimeSpan.FromSeconds(1))
                    {
                        continue;
                    }

                    // Acquire exclusively before moving. Copy-in-progress files stay pending.
                    using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                    var messageId = Guid.NewGuid();
                    var delivery = new FeedDelivery(feed.Type, messageId, Path.GetFileName(path), "");
                    string workingPath = Destination(ProcessingRoot, delivery);
                    File.Move(path, workingPath);
                    _observations.Remove(path);
                    deliveries.Add(new(feed.Type, messageId, Path.GetFileName(path), workingPath));
                }
                catch (FileNotFoundException) { _observations.Remove(path); }
                catch (IOException) { /* Locked copy or temporary access problem: rescan later. */ }
                catch (UnauthorizedAccessException) { /* Retry without discarding the input. */ }
            }
        }
        foreach (string stale in _observations.Keys.Where(path => !seen.Contains(path)).ToArray())
        {
            _observations.Remove(stale);
        }
        return deliveries;
    }

    public async Task<byte[]> ReadAsync(FeedDelivery delivery, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(delivery.WorkingPath, FileMode.Open,
            FileAccess.Read, FileShare.None, 81920, FileOptions.Asynchronous);
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    public async Task<string> WriteTransactionLogAsync(FeedMessage original, FeedWorkflowResult? result,
        string? reason, CancellationToken cancellationToken)
    {
        string destination = Path.Combine(LogsRoot,
            $"{FeedCatalog.Get(original.Delivery.Feed).FolderName}.{original.Delivery.MessageId:N}.txt");
        byte[] report = Encoding.UTF8.GetBytes(FeedTransactionLog.Format(original, result, reason));
        await WriteOnceAsync(destination, report, cancellationToken);
        return destination;
    }

    public async Task<string> PublishAsync(FeedMessage message, CancellationToken cancellationToken)
    {
        if (File.Exists(Destination(FailedRoot, message.Delivery)))
        {
            throw new IOException("Delivery was already moved to the failed feed.");
        }
        string output = Destination(OutputRoot, message.Delivery);
        await WriteOnceAsync(output, message.Content, cancellationToken);
        return output;
    }

    public async Task<string> RejectAsync(FeedMessage message, string reason, CancellationToken cancellationToken)
    {
        string failed = Destination(FailedRoot, message.Delivery);
        await WriteOnceAsync(failed, message.Content, cancellationToken);
        string errorPath = Path.ChangeExtension(failed, ".error.txt");
        byte[] report = Encoding.UTF8.GetBytes(
            $"messageId: {message.Delivery.MessageId}\n" +
            $"feed: {FeedCatalog.Get(message.Delivery.Feed).FolderName}\n" +
            $"sourceFile: {message.Delivery.OriginalName}\n" +
            $"error: {reason}\n");
        // A recovered rejection already has a stable report; don't replace it.
        if (!File.Exists(errorPath))
        {
            await WriteOnceAsync(errorPath, report, cancellationToken);
        }
        return failed;
    }

    public void Acknowledge(FeedDelivery delivery)
    {
        // Keep the per-feed processing folder, including its tracked placeholder.
        File.Delete(delivery.WorkingPath);
    }

    private static FeedDelivery RecoverDelivery(FeedType feed, string workingPath)
    {
        string stem = Path.GetFileNameWithoutExtension(workingPath);
        int separator = stem.LastIndexOf('.');
        if (separator < 0 || !Guid.TryParseExact(stem[(separator + 1)..], "N", out var messageId))
        {
            throw new InvalidDataException($"Processing filename must include a delivery ID: {Path.GetFileName(workingPath)}");
        }
        string originalName = stem[..separator] + Path.GetExtension(workingPath);
        return new(feed, messageId, originalName, workingPath);
    }

    private void MigrateRuntimeProcessing()
    {
        string legacyRoot = Path.Combine(_paths.RuntimeRoot, "processing");
        foreach (var feed in FeedCatalog.All)
        {
            string legacyFeed = Path.Combine(legacyRoot, feed.FolderName);
            if (!Directory.Exists(legacyFeed))
            {
                continue;
            }
            foreach (string directory in Directory.EnumerateDirectories(legacyFeed))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var messageId))
                {
                    throw new InvalidDataException("Legacy processing directory has no valid delivery ID.");
                }
                foreach (string source in Directory.EnumerateFiles(directory).Where(IsFeedFile))
                {
                    var delivery = new FeedDelivery(feed.Type, messageId, Path.GetFileName(source), source);
                    string destination = Destination(ProcessingRoot, delivery);
                    if (File.Exists(destination))
                    {
                        if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(File.ReadAllBytes(destination)))
                        {
                            throw new IOException("Legacy and current processing files conflict; both were retained.");
                        }
                        File.Delete(source);
                    }
                    else
                    {
                        File.Move(source, destination);
                    }
                }
                if (!Directory.EnumerateFiles(directory).Any(IsFeedFile))
                {
                    File.Delete(Path.Combine(directory, ".failure-retained"));
                }
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            if (!Directory.EnumerateFileSystemEntries(legacyFeed).Any())
            {
                Directory.Delete(legacyFeed);
            }
        }
        if (Directory.Exists(legacyRoot) && !Directory.EnumerateFileSystemEntries(legacyRoot).Any())
        {
            Directory.Delete(legacyRoot);
        }
    }

    private string Destination(string root, FeedDelivery delivery) => Path.Combine(root,
        FeedCatalog.Get(delivery.Feed).FolderName,
        $"{Path.GetFileNameWithoutExtension(delivery.OriginalName)}.{delivery.MessageId:N}{Path.GetExtension(delivery.OriginalName)}");

    private static async Task WriteOnceAsync(string destination, ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destination))
        {
            byte[] existing = await File.ReadAllBytesAsync(destination, cancellationToken);
            if (!existing.AsSpan().SequenceEqual(content.Span))
                throw new IOException($"Delivery output already exists with different content: {destination}");
            return;
        }

        // Consumers see the original extension only after the complete bytes have been flushed.
        string temporary = destination + ".pending";
        await using (var output = new FileStream(temporary, FileMode.Create,
            FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        {
            await output.WriteAsync(content, cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }
        File.Move(temporary, destination);
    }

    private static bool IsFeedFile(string path) =>
        new[] { ".txt", ".bin" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public void Dispose() => _instanceLock?.Dispose();
}
