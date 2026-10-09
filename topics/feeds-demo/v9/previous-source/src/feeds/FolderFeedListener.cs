using System.Collections.Concurrent;

namespace PresidioDemo.Feeds;

public sealed class FolderFeedListener(IFeedTransport transport, FeedWorkflowRouter router) : IDisposable
{
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<string, DateTime> _retryAfter = new(StringComparer.OrdinalIgnoreCase);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        foreach (var feed in FeedCatalog.All)
        {
            var watcher = new FileSystemWatcher(Path.Combine(transport.InputRoot, feed.FolderName))
            {
                Filter = "*",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false
            };
            _watchers.Add(watcher);
            watcher.Created += (_, _) => Wake();
            watcher.Changed += (_, _) => Wake();
            watcher.Renamed += (_, _) => Wake();
            watcher.Error += (_, _) =>
            {
                Log(feed.Type, "RESCAN", "Folder notifications interrupted; periodic scans remain active.");
                Wake();
            };
            watcher.EnableRaisingEvents = true;
        }

        var running = new Dictionary<FeedType, Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Each feed gets one independent worker, bounding memory and active work.
                // .NET parsing/rebuilding and I/O can overlap across feeds; shared Python calls cannot.
                foreach (var completed in running.Where(pair => pair.Value.IsCompleted).ToArray())
                {
                    await completed.Value;
                    running.Remove(completed.Key);
                }
                foreach (var delivery in transport.ClaimReadyDeliveries(running.Keys.ToHashSet()))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (running.ContainsKey(delivery.Feed)) continue; // Retained on disk until this feed is idle.
                    if (_retryAfter.TryGetValue(delivery.WorkingPath, out var after) && DateTime.UtcNow < after) continue;
                    running.Add(delivery.Feed, Task.Run(() => ProcessAsync(delivery, cancellationToken)));
                }
                await _wake.WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            foreach (var watcher in _watchers) watcher.EnableRaisingEvents = false;
            // Drain all workers before disposing transport or shutting down embedded Python.
            try { await Task.WhenAll(running.Values); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
    }

    private async Task ProcessAsync(FeedDelivery delivery, CancellationToken cancellationToken)
    {
        byte[] content;
        try
        {
            content = await transport.ReadAsync(delivery, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Retry(delivery, "Waiting for file access; input remains in processing.");
            return;
        }

        var message = new FeedMessage(delivery, content);
        var workflow = router.Get(delivery.Feed);
        Log(delivery.Feed, "RECEIVED", $"{delivery.OriginalName} | message {delivery.MessageId:N}");
        Log(delivery.Feed, "WORKFLOW", workflow.GetType().Name);
        string destination;
        try
        {
            var result = await workflow.ProcessAsync(message, cancellationToken);
            Log(delivery.Feed, "VALIDATED", "Protected feed format");
            destination = await transport.PublishAsync(message with { Content = result.Content }, cancellationToken);
            Log(delivery.Feed, "PUBLISHED", Path.GetFileName(destination));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            string reason = ex is InvalidDataException ? ex.Message : "Feed processing or output delivery failed.";
            try
            {
                destination = await transport.RejectAsync(message, reason, cancellationToken);
                Log(delivery.Feed, "FAILED", $"{Path.GetFileName(destination)} | {reason}");
                _retryAfter.TryRemove(delivery.WorkingPath, out _);
                Log(delivery.Feed, "RETAINED", "Input retained in processing; no successful output delivery.");
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                Retry(delivery, "Cannot save failed delivery; input remains in processing.");
                return;
            }
        }

        // Only successful output delivery permits acknowledgment. On an ack failure,
        // reuse the same delivery ID and output rather than emitting another copy.
        try
        {
            transport.Acknowledge(delivery);
            _retryAfter.TryRemove(delivery.WorkingPath, out _);
            Log(delivery.Feed, "CONSUMED", "Input deleted");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Retry(delivery, "Output saved; acknowledgment pending.");
        }
    }

    private void Retry(FeedDelivery delivery, string reason)
    {
        _retryAfter[delivery.WorkingPath] = DateTime.UtcNow.AddSeconds(5);
        Log(delivery.Feed, "PENDING", $"{delivery.OriginalName} | {reason}");
    }

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private static void Log(FeedType feed, string stage, string detail) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {FeedCatalog.Get(feed).FolderName,-12} {stage,-10} {detail}");

    public void Dispose()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        _wake.Dispose();
    }
}
