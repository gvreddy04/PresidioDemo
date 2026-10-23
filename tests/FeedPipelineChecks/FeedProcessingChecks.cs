using PresidioDemo.Feeds.Shared;
using PresidioDemo.Transport;

#region Classes

/// <summary>
/// Verifies claimed-file recovery, publication cleanup and legacy pending-file migration.
/// </summary>
static class FeedProcessingChecks
{

    #region Methods

    /// <summary>
    /// Executes asynchronous processing or verification using the supplied workspace and cancellation contract.
    /// </summary>
    /// <param name="workspace">The isolated directory used for runtime verification artifacts.</param>
    /// <returns>A task that completes when the operation and required cleanup have finished.</returns>
    /// <exception cref="Exception">A regression assertion fails or an expected rejection does not occur.</exception>
    public static async Task RunAsync(string workspace)
    {
        string docs = Path.Combine(workspace, "processing-lifecycle");
        FeedDelivery first;
        FeedDelivery second;
        using (var transport = new FolderFeedTransport(docs))
        {
            transport.Initialize();
            string input = Path.Combine(transport.InputRoot, "ACI", "same.feed.bin");
            await File.WriteAllBytesAsync(input, new byte[] { 1, 2, 3 });
            Require(transport.ClaimReadyDeliveries().Count == 0 && File.Exists(input), "Unstable input was claimed.");
            await Task.Delay(1100);
            first = transport.ClaimReadyDeliveries().Single();
            Require(!File.Exists(input) && File.Exists(first.WorkingPath), "Input was not moved before processing.");
            Require(first.WorkingPath == Path.Combine(transport.ProcessingRoot, "ACI", $"same.feed.{first.MessageId:N}.bin"), "Working file is not directly under the feed folder with a unique name.");
            Require(first.OriginalName == "same.feed.bin" && File.ReadAllBytes(first.WorkingPath).AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }), "Move changed original bytes or name.");
            await File.WriteAllBytesAsync(input, new byte[] { 4, 5, 6 });
            transport.ClaimReadyDeliveries();
            await Task.Delay(1100);
            second = transport.ClaimReadyDeliveries().Single(d => d.MessageId != first.MessageId);
            Require(!File.Exists(input) && second.WorkingPath != first.WorkingPath && File.Exists(first.WorkingPath), "Repeated source filename collided or stayed in input.");
        }
        using (var recovered = new FolderFeedTransport(docs))
        {
            recovered.Initialize();
            var pending = recovered.ClaimReadyDeliveries();
            Require(pending.Count == 2 && pending.Any(d => d == first) && pending.Any(d => d == second), "Restart changed pending IDs or original names.");
            await recovered.PublishAsync(new(first, new byte[] { 7, 8, 9 }), CancellationToken.None);
            recovered.Acknowledge(first);
            Require(!File.Exists(first.WorkingPath) && Directory.Exists(Path.GetDirectoryName(first.WorkingPath)), "Successful cleanup removed the feed folder or left the file.");
            string failed = await recovered.RejectAsync(new(second, new byte[] { 4, 5, 6 }), "Test rejection.", CancellationToken.None);
            Require(File.Exists(second.WorkingPath) && File.Exists(failed) && File.Exists(Path.ChangeExtension(failed, ".error.txt")), "Rejection did not save original and error before cleanup.");
            // Simulate stopping after quarantine but before working-file deletion.
        }
        using (var restarted = new FolderFeedTransport(docs))
        {
            restarted.Initialize();
            Require(restarted.ClaimReadyDeliveries().Count == 0 && !File.Exists(second.WorkingPath), "Completed rejection was retried instead of cleaned up after restart.");
            Require(Directory.Exists(Path.Combine(restarted.ProcessingRoot, "ACI")), "Processing feed folder disappeared.");
        }
        Console.WriteLine("PASS processing moves preserve bytes, uniquely name repeated inputs, recover stable IDs, and clean completed transactions while keeping feed folders");

        string migratedDocs = Path.Combine(workspace, "processing-migration");
        var paths = new FeedWorkspacePaths(migratedDocs);
        var id = Guid.NewGuid();
        string legacyDirectory = Path.Combine(paths.RuntimeRoot, "processing", "ACI", id.ToString("N"));
        Directory.CreateDirectory(legacyDirectory);
        string legacyFile = Path.Combine(legacyDirectory, "pending.bin");
        await File.WriteAllBytesAsync(legacyFile, new byte[] { 11, 12, 13 });
        using (var migrated = new FolderFeedTransport(migratedDocs))
        {
            migrated.Initialize();
            var pending = migrated.ClaimReadyDeliveries().Single();
            Require(pending.MessageId == id && pending.OriginalName == "pending.bin" && !File.Exists(legacyFile), "Migration lost the original delivery identity or did not move it.");
            Require(File.ReadAllBytes(pending.WorkingPath).AsSpan().SequenceEqual(new byte[] { 11, 12, 13 }), "Migration changed pending bytes.");
            Require(pending.WorkingPath == Path.Combine(paths.ProcessingRoot, "ACI", $"pending.{id:N}.bin"), "Migration did not use the visible processing folder.");
        }
        // An interrupted migration may have already created the target file.
        Directory.CreateDirectory(legacyDirectory);
        await File.WriteAllBytesAsync(legacyFile, new byte[] { 99 });
        using (var conflicting = new FolderFeedTransport(migratedDocs))
        {
            try
            {
                conflicting.Initialize();
                throw new Exception("Conflicting legacy data was silently replaced.");
            }
            catch (IOException)
            {
                Require(File.ReadAllBytes(legacyFile).AsSpan().SequenceEqual(new byte[] { 99 }), "Migration deleted conflicting legacy data.");
                Require(File.ReadAllBytes(Path.Combine(paths.ProcessingRoot, "ACI", $"pending.{id:N}.bin")).AsSpan().SequenceEqual(new byte[] { 11, 12, 13 }), "Migration replaced existing processing data.");
            }
        }
        Console.WriteLine("PASS legacy pending deliveries migrate with original bytes and IDs; conflicting files are both retained");
    }

    /// <summary>
    /// Asserts a regression condition and reports a successful check.
    /// </summary>
    /// <param name="condition">The regression condition that must hold.</param>
    /// <param name="message">The diagnostic explanation identifying this verification condition.</param>
    /// <exception cref="Exception">A regression assertion fails or an expected rejection does not occur.</exception>
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    #endregion
}

#endregion
