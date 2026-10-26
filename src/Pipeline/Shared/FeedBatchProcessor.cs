using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using PresidioDemo.Feeds.Shared;
using PresidioDemo.Transport;

namespace PresidioDemo.Pipeline;

#region Models

/// <summary>
/// Records the outcome and retained evidence paths for one batch input file.
/// </summary>
/// <param name="SourceFile">The original source-file path processed by the batch.</param>
/// <param name="Feed">The supported feed route.</param>
/// <param name="SourceSha256">The SHA-256 digest identifying the unchanged source file.</param>
/// <param name="SourceBytes">The complete source-file size in bytes.</param>
/// <param name="Status">The published, rejected or pending batch outcome.</param>
/// <param name="OutputFile">The saved protected-output path for this batch input.</param>
/// <param name="RetainedFile">The path containing a complete rejected or pending input.</param>
/// <param name="ErrorReport">The saved error-report path for a rejected batch input.</param>
public sealed record FeedBatchItem(string SourceFile, string Feed, string SourceSha256, int SourceBytes,
    string Status, string? OutputFile, string? RetainedFile, string? ErrorReport);

/// <summary>
/// Contains the batch identifier, report path and individual input outcomes.
/// </summary>
/// <param name="RunId">The identifier used to correlate this batch and its imported files.</param>
/// <param name="ReportFile">The saved batch verification report path.</param>
/// <param name="Items">The individual input outcomes recorded for the batch.</param>
public sealed record FeedBatchResult(string RunId, string ReportFile, IReadOnlyList<FeedBatchItem> Items)
{

    #region Properties, Indexers

    /// <summary>
    /// Gets a value indicating whether every supplied batch input was published.
    /// </summary>
    /// <value>A value indicating whether every supplied batch input was published.</value>
    public bool AllPublished => Items.All(item => item.Status == "Published");

    #endregion
}

#endregion

/// <summary>
/// Copies actual source files into the normal listener and records every terminal outcome.
/// </summary>
public static class FeedBatchProcessor
{

    #region Methods

    /// <summary>
    /// Imports supported source files and records each durable batch outcome.
    /// </summary>
    /// <param name="sourceRoot">The directory containing the supported source files to import.</param>
    /// <param name="docsRoot">The workspace root containing feed-exchange and diagnostic directories.</param>
    /// <param name="transport">The durable input, output, rejection and acknowledgment service.</param>
    /// <param name="listener">The listener coordinating the imported batch deliveries.</param>
    /// <param name="cancellationToken">The token used to interrupt processing while retaining recoverable input.</param>
    /// <returns>A task containing the batch identifier, report path and outcome for every imported source.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    public static async Task<FeedBatchResult> RunAsync(string sourceRoot, string docsRoot,
            IFeedTransport transport, FolderFeedListener listener, CancellationToken cancellationToken)
    {
        var paths = new FeedWorkspacePaths(docsRoot);
        string runId = Guid.NewGuid().ToString("N");
        var jobs = new List<Job>();
        foreach (string source in Directory.EnumerateFiles(Path.GetFullPath(sourceRoot)).Order())
        {
            string extension = Path.GetExtension(source);
            if (!new[] { ".txt", ".bin" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            string route = Path.GetFileNameWithoutExtension(source).Replace('_', '-').ToUpperInvariant();
            route = Regex.Replace(route, "-[0-9]+$", "", RegexOptions.CultureInvariant);
            FeedType feed = route switch
            {
                "ACI" => FeedType.ACI,
                "PNR" => FeedType.PNR,
                "PNR-LINKING" => FeedType.PnrLinking,
                "SEATS" => FeedType.Seats,
                "ETKT" or "TKT" => FeedType.ETKT,
                _ => throw new InvalidDataException("Unknown source feed filename. Use ACI, PNR, PNR_LINKING, SEATS, ETKT or TKT, optionally followed by a numeric _NN or -NN suffix.")
            };
            byte[] content = await File.ReadAllBytesAsync(source, cancellationToken);
            string imported = FeedCatalog.Get(feed).FolderName + ".run-" + runId + "-" + jobs.Count + extension;
            jobs.Add(new(source, feed, imported, content, Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()));
        }
        if (jobs.Count == 0)
        {
            throw new InvalidDataException("No recognized TXT/BIN source feeds were found.");
        }

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
        using (var listenerStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
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
                        string? working = outcome is null ? null : Path.Combine(workingRoot, Path.GetFileName(outcome));
                        bool pendingInput = File.Exists(Path.Combine(transport.InputRoot, folder, job.ImportedName));
                        if (outputs.Length == 1 && !File.Exists(working) && !pendingInput)
                        {
                            results.Add(job.ImportedName, new(job.SourceFile, folder, job.Sha256, job.Content.Length, "Published", outputs[0], null, null));
                        }
                        else if (failures.Length == 1 && !File.Exists(working) && !pendingInput && File.Exists(Path.ChangeExtension(failures[0], ".error.txt")))
                        {
                            results.Add(job.ImportedName, new(job.SourceFile, folder, job.Sha256, job.Content.Length, "Rejected", null, failures[0], Path.ChangeExtension(failures[0], ".error.txt")));
                        }
                    }
                    if (DateTime.UtcNow >= deadline)
                    {
                        break;
                    }

                    await Task.Delay(100, cancellationToken);
                }
            }
            finally { listenerStop.Cancel(); await running; }
            foreach (var job in jobs.Where(j => !results.ContainsKey(j.ImportedName)))
            {
                results.Add(job.ImportedName, new(job.SourceFile, FeedCatalog.Get(job.Feed).FolderName, job.Sha256, job.Content.Length, "Pending", null, null, null));
            }

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

    #endregion

    #region Nested Types

    /// <summary>
    /// Associates a batch source file with its imported name and destination feed route.
    /// </summary>
    /// <param name="SourceFile">The original source-file path processed by the batch.</param>
    /// <param name="Feed">The supported feed route.</param>
    /// <param name="ImportedName">The transaction-specific filename used to import this batch source.</param>
    /// <param name="Content">The complete original or protected message bytes.</param>
    /// <param name="Sha256">The SHA-256 digest identifying the original source bytes.</param>
    private sealed record Job(string SourceFile, FeedType Feed, string ImportedName, byte[] Content, string Sha256);

    #endregion
}
