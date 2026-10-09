using System.Security.Cryptography;
using System.Text.Json;

namespace PresidioDemo.Feeds;

public sealed record FeedBatchItem(string SourceFile, string Feed, string SourceSha256, int SourceBytes,
    string Status, string? OutputFile, string? RetainedFile, string? ErrorReport);
public sealed record FeedBatchResult(string RunId, string ReportFile, IReadOnlyList<FeedBatchItem> Items)
{
    public bool AllPublished => Items.All(item => item.Status == "Published");
}

/// <summary>Copies actual source files into the normal listener and records every terminal outcome.</summary>
public static class FeedBatchProcessor
{
    private sealed record Job(string SourceFile, FeedType Feed, string ImportedName, byte[] Content, string Sha256);
    public static async Task<FeedBatchResult> RunAsync(string sourceRoot, string docsRoot,
        IFeedTransport transport, FolderFeedListener listener, CancellationToken cancellationToken)
    {
        var paths = new FeedWorkspacePaths(docsRoot);
        string runId = Guid.NewGuid().ToString("N");
        var jobs = new List<Job>();
        foreach (string source in Directory.EnumerateFiles(Path.GetFullPath(sourceRoot)).Order())
        {
            string extension = Path.GetExtension(source);
            if (!new[] { ".txt", ".bin" }.Contains(extension, StringComparer.OrdinalIgnoreCase)) continue;
            string route = Path.GetFileNameWithoutExtension(source).Replace('_', '-').ToUpperInvariant();
            FeedType feed = route switch
            {
                "ACI" => FeedType.ACI, "PNR" => FeedType.PNR, "PNR-LINKING" => FeedType.PnrLinking,
                "SEATS" => FeedType.Seats, "ETKT" or "TKT" => FeedType.ETKT,
                _ => throw new InvalidDataException("Unknown source feed filename. Use ACI, PNR, PNR_LINKING, SEATS, or ETKT.")
            };
            byte[] content = await File.ReadAllBytesAsync(source, cancellationToken);
            string imported = FeedCatalog.Get(feed).FolderName + ".run-" + runId + "-" + jobs.Count + extension;
            jobs.Add(new(source, feed, imported, content, Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()));
        }
        if (jobs.Count == 0) throw new InvalidDataException("No recognized TXT/BIN source feeds were found.");
        foreach (var job in jobs)
        {
            string input = Path.Combine(transport.InputRoot, FeedCatalog.Get(job.Feed).FolderName, job.ImportedName);
            string temporary = input + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(job.Content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, input);
        }
        using var listenerStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var results = new Dictionary<string, FeedBatchItem>(StringComparer.Ordinal);
        Task running = listener.RunAsync(listenerStop.Token);
        DateTime deadline = DateTime.UtcNow.AddMinutes(2);
        try
        {
            while (results.Count < jobs.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (running.IsCompleted) { await running; throw new InvalidDataException("Listener stopped before batch completion."); }
                foreach (var job in jobs.Where(j => !results.ContainsKey(j.ImportedName)))
                {
                    string folder = FeedCatalog.Get(job.Feed).FolderName;
                    string pattern = Path.GetFileNameWithoutExtension(job.ImportedName) + ".*" + Path.GetExtension(job.ImportedName);
                    string[] outputs = Directory.GetFiles(Path.Combine(transport.OutputRoot, folder), pattern).Where(p => !p.EndsWith(".error.txt", StringComparison.Ordinal)).ToArray();
                    string[] failures = Directory.GetFiles(Path.Combine(transport.FailedRoot, folder), pattern).Where(p => !p.EndsWith(".error.txt", StringComparison.Ordinal)).ToArray();
                    string workingRoot = Path.Combine(paths.ProcessingRoot, folder);
                    string? outcome = outputs.SingleOrDefault() ?? failures.SingleOrDefault();
                    string? deliveryId = outcome is null ? null : Path.GetFileNameWithoutExtension(outcome).Split('.')[^1];
                    string? working = deliveryId is null ? null : Path.Combine(workingRoot, deliveryId, job.ImportedName);
                    bool pendingInput = File.Exists(Path.Combine(transport.InputRoot, folder, job.ImportedName));
                    if (outputs.Length == 1 && !File.Exists(working) && !pendingInput)
                        results.Add(job.ImportedName, new(job.SourceFile, folder, job.Sha256, job.Content.Length, "Published", outputs[0], null, null));
                    else if (failures.Length == 1 && File.Exists(working) && File.Exists(Path.ChangeExtension(failures[0], ".error.txt")) &&
                        File.Exists(Path.Combine(Path.GetDirectoryName(working!)!, ".failure-retained")))
                        results.Add(job.ImportedName, new(job.SourceFile, folder, job.Sha256, job.Content.Length, "Rejected", null, working, Path.ChangeExtension(failures[0], ".error.txt")));
                }
                if (DateTime.UtcNow >= deadline) break;
                await Task.Delay(100, cancellationToken);
            }
        }
        finally { listenerStop.Cancel(); await running; }
        foreach (var job in jobs.Where(j => !results.ContainsKey(j.ImportedName)))
            results.Add(job.ImportedName, new(job.SourceFile, FeedCatalog.Get(job.Feed).FolderName, job.Sha256, job.Content.Length, "Pending", null, null, null));
        string reportRoot = paths.ReportsRoot;
        Directory.CreateDirectory(reportRoot);
        string reportFile = Path.Combine(reportRoot, "feed-batch-" + runId + ".json");
        var result = new FeedBatchResult(runId, reportFile, jobs.Select(job => results[job.ImportedName]).ToArray());
        string reportTemporary = reportFile + ".pending";
        await File.WriteAllTextAsync(reportTemporary, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        File.Move(reportTemporary, reportFile);
        return result;
    }
}
