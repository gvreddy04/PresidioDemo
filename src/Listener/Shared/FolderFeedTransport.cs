using System.Text;
using PresidioDemo.Feeds.Shared;

namespace PresidioDemo.Transport;

/// <summary>
/// Folder adapter: receive, publish, reject and acknowledge a delivery.
/// </summary>
/// <param name="docsRoot">The workspace root containing feed-exchange and diagnostic directories.</param>
public sealed class FolderFeedTransport(string docsRoot) : IFeedTransport
{

    #region Fields and Constants

    /// <summary>
    /// Tracks input sizes and modification times until files are stable enough to claim.
    /// </summary>
    private readonly Dictionary<string, Observation> _observations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The resolved exchange and private-state paths for this workspace.
    /// </summary>
    private readonly FeedWorkspacePaths _paths = new(docsRoot);

    /// <summary>
    /// The owned exclusive file lock preventing concurrent listeners for the same workspace.
    /// </summary>
    private FileStream? _instanceLock;

    #endregion

    #region Methods

    /// <summary>
    /// Removes a completed processing file after its durable terminal outcome is saved.
    /// </summary>
    /// <param name="delivery">The claimed input and its stable feed-route and transaction metadata.</param>
    public void Acknowledge(FeedDelivery delivery)
    {
        // Keep the per-feed processing folder, including its tracked placeholder.
        File.Delete(delivery.WorkingPath);
    }

    /// <summary>
    /// Claims stable or recoverable inputs while excluding feed routes that are already busy.
    /// </summary>
    /// <param name="busyFeeds">The feed routes that already have active processing work.</param>
    /// <returns>The stable or recovered deliveries claimed for currently idle feed routes.</returns>
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

    /// <summary>
    /// Releases the owned streams, file watchers, synchronization objects or workspace lock.
    /// </summary>
    public void Dispose() => _instanceLock?.Dispose();

    /// <summary>
    /// Creates the exchange folders, acquires the workspace lock and recovers legacy pending input.
    /// </summary>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
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

    /// <summary>
    /// Durably saves the validated protected message under its stable transaction identifier.
    /// </summary>
    /// <param name="message">The complete input delivery and its retained message bytes.</param>
    /// <param name="cancellationToken">The token used to interrupt processing while retaining recoverable input.</param>
    /// <returns>A task containing the durable artifact path saved for this transaction.</returns>
    /// <exception cref="IOException">The required artifact cannot be accessed or persisted safely.</exception>
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

    /// <summary>
    /// Reads the complete claimed input without publishing or acknowledging it.
    /// </summary>
    /// <param name="delivery">The claimed input and its stable feed-route and transaction metadata.</param>
    /// <param name="cancellationToken">The token used to interrupt processing while retaining recoverable input.</param>
    /// <returns>A task containing all bytes from the claimed input file.</returns>
    public async Task<byte[]> ReadAsync(FeedDelivery delivery, CancellationToken cancellationToken)
    {
        await using (var input = new FileStream(delivery.WorkingPath, FileMode.Open,
            FileAccess.Read, FileShare.None, 81920, FileOptions.Asynchronous))
        {
            using (var buffer = new MemoryStream())
            {
                await input.CopyToAsync(buffer, cancellationToken);
                return buffer.ToArray();
            }
        }
    }

    /// <summary>
    /// Saves the complete rejected input and its error report before acknowledgment.
    /// </summary>
    /// <param name="message">The complete input delivery and its retained message bytes.</param>
    /// <param name="reason">The validated explanation for retaining or rejecting the complete input.</param>
    /// <param name="cancellationToken">The token used to interrupt processing while retaining recoverable input.</param>
    /// <returns>A task containing the durable artifact path saved for this transaction.</returns>
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

    /// <summary>
    /// Persists complete before-and-after transaction evidence before publication or rejection.
    /// </summary>
    /// <param name="original">The complete delivery metadata and original or protected message bytes.</param>
    /// <param name="result">The validated protected output and its optional diagnostic field sets.</param>
    /// <param name="reason">The validated explanation for retaining or rejecting the complete input.</param>
    /// <param name="cancellationToken">The token used to interrupt processing while retaining recoverable input.</param>
    /// <returns>A task containing the durable artifact path saved for this transaction.</returns>
    public async Task<string> WriteTransactionLogAsync(FeedMessage original, FeedWorkflowResult? result,
            string? reason, CancellationToken cancellationToken)
    {
        string destination = Path.Combine(LogsRoot,
            $"{FeedCatalog.Get(original.Delivery.Feed).FolderName}.{original.Delivery.MessageId:N}.txt");
        byte[] report = Encoding.UTF8.GetBytes(FeedLoggers.Format(original, result, reason));
        await WriteOnceAsync(destination, report, cancellationToken);
        return destination;
    }

    /// <summary>
    /// Builds a transaction-specific output path without changing the original file extension.
    /// </summary>
    /// <param name="root">The project root containing source configuration and immutable samples.</param>
    /// <param name="delivery">The claimed input and its stable feed-route and transaction metadata.</param>
    /// <returns>The stable artifact filename within the requested exchange directory.</returns>
    private string Destination(string root, FeedDelivery delivery) => Path.Combine(root,
            FeedCatalog.Get(delivery.Feed).FolderName,
            $"{Path.GetFileNameWithoutExtension(delivery.OriginalName)}.{delivery.MessageId:N}{Path.GetExtension(delivery.OriginalName)}");

    /// <summary>
    /// Determines whether a path has a supported raw-payload or MQ-export extension.
    /// </summary>
    /// <param name="path">The structural path, source file or workspace location being processed.</param>
    /// <returns>True for a supported .bin payload or .txt MQ export; otherwise, false.</returns>
    private static bool IsFeedFile(string path) =>
            new[] { ".txt", ".bin" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Moves legacy pending deliveries into the current processing folders without losing input.
    /// </summary>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    /// <exception cref="IOException">The required artifact cannot be accessed or persisted safely.</exception>
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

    /// <summary>
    /// Recovers a pending processing file while preserving its stable transaction identifier.
    /// </summary>
    /// <param name="feed">The supported feed route.</param>
    /// <param name="workingPath">The claimed-input path recovered from durable processing storage.</param>
    /// <returns>The recoverable delivery with its original stable transaction identifier.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
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

    /// <summary>
    /// Durably writes one transaction artifact and verifies any pre-existing artifact for a safe retry.
    /// </summary>
    /// <param name="destination">The transaction-specific path receiving the durable artifact.</param>
    /// <param name="content">The complete bytes to persist or process.</param>
    /// <param name="cancellationToken">The token used to interrupt processing while retaining recoverable input.</param>
    /// <returns>A task that completes when the operation and required cleanup have finished.</returns>
    /// <exception cref="IOException">The required artifact cannot be accessed or persisted safely.</exception>
    private static async Task WriteOnceAsync(string destination, ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken)
    {
        if (File.Exists(destination))
        {
            byte[] existing = await File.ReadAllBytesAsync(destination, cancellationToken);
            if (!existing.AsSpan().SequenceEqual(content.Span))
            {
                throw new IOException($"Delivery output already exists with different content: {destination}");
            }

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

    #endregion

    #region Properties, Indexers

    /// <summary>
    /// Gets the directory containing complete rejected inputs and their error reports.
    /// </summary>
    /// <value>The directory containing complete rejected inputs and their error reports.</value>
    public string FailedRoot => _paths.FailedRoot;

    /// <summary>
    /// Gets the directory containing files waiting to be claimed.
    /// </summary>
    /// <value>The directory containing files waiting to be claimed.</value>
    public string InputRoot => _paths.InputRoot;

    /// <summary>
    /// Gets the directory containing transaction-specific diagnostic reports.
    /// </summary>
    /// <value>The directory containing transaction-specific diagnostic reports.</value>
    public string LogsRoot => _paths.LogsRoot;

    /// <summary>
    /// Gets the directory containing validated protected output.
    /// </summary>
    /// <value>The directory containing validated protected output.</value>
    public string OutputRoot => _paths.OutputRoot;

    /// <summary>
    /// Gets the directory containing claimed or recoverable input files.
    /// </summary>
    /// <value>The directory containing claimed or recoverable input files.</value>
    public string ProcessingRoot => _paths.ProcessingRoot;

    #endregion

    #region Nested Types

    /// <summary>
    /// Tracks file size and modification time until an input has remained stable long enough.
    /// </summary>
    /// <param name="Length">The wire span or observed file length in bytes.</param>
    /// <param name="LastWriteTicks">The last observed modification time in UTC ticks.</param>
    /// <param name="StableSince">The UTC time from which the observed file size and modification time have remained unchanged.</param>
    private sealed record Observation(long Length, long LastWriteTicks, DateTime StableSince);

    #endregion
}
