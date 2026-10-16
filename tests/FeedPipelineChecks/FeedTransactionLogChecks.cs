using System.Text;
using PresidioDemo.Feeds;

static class FeedTransactionLogChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    private static void RequireOrder(string text, params string[] stages)
    {
        int next = 0;
        foreach (string stage in stages)
        {
            int position = text.IndexOf(stage, next, StringComparison.Ordinal);
            Require(position >= 0, $"Missing or out-of-order log stage: {stage}");
            next = position + stage.Length;
        }
    }

    public static async Task RunAsync(string workspace)
    {
        using var transport = new FolderFeedTransport(Path.Combine(workspace, "transaction-logs"));
        transport.Initialize();
        var delivery = new FeedDelivery(FeedType.Seats, Guid.NewGuid(), "same-name.bin", "fixture");
        var input = new FeedMessage(delivery, Encoding.UTF8.GetBytes("{\"Name\":\"Emily Carter\"}"));
        var result = new FeedWorkflowResult(Encoding.UTF8.GetBytes("{\"Name\":\"[PERSON]\"}"),
            [new("/Name", "/Name", "Emily Carter")], [new("/Name", "/Name", "[PERSON]")]);
        string path = await transport.WriteTransactionLogAsync(input, result, null, CancellationToken.None);
        string text = await File.ReadAllTextAsync(path);
        string[] sections = text.Split("AFTER ANONYMIZATION");
        Require(sections.Length == 2 && sections[0].Contains("Emily Carter") && !sections[1].Contains("Emily Carter") && sections[1].Contains("[PERSON]"), "Comparison sections mix original and protected data.");
        Require(text.Contains("BEFORE ANONYMIZATION") && text.Contains(new string('=', 72)), "Missing headings or separators.");
        Require(sections[0].Contains(Encoding.UTF8.GetString(input.Content.Span)) && sections[1].Contains(Encoding.UTF8.GetString(result.Content.Span)), "Complete JSON data is missing.");
        RequireOrder(text, "BEFORE ANONYMIZATION", "Complete feed data", Encoding.UTF8.GetString(input.Content.Span),
            "Parsed fields", "AFTER ANONYMIZATION", "Parsed fields", "Complete feed data", Encoding.UTF8.GetString(result.Content.Span));
        byte[] saved = await File.ReadAllBytesAsync(path);
        string repeated = await transport.WriteTransactionLogAsync(input, result, null, CancellationToken.None);
        byte[] repeatedBytes = await File.ReadAllBytesAsync(path);
        Require(repeated == path && Directory.GetFiles(transport.LogsRoot, "*.txt").Length == 1 && saved.AsSpan().SequenceEqual(repeatedBytes), "Retry changed or duplicated a transaction log.");
        try
        {
            await transport.WriteTransactionLogAsync(input, result with { Content = "changed"u8.ToArray() }, null, CancellationToken.None);
            throw new Exception("Conflicting transaction log was accepted.");
        }
        catch (IOException)
        {
            byte[] unchanged = await File.ReadAllBytesAsync(path);
            Require(saved.AsSpan().SequenceEqual(unchanged), "Conflicting write replaced original evidence.");
        }
        await transport.WriteTransactionLogAsync(input with { Delivery = delivery with { MessageId = Guid.NewGuid() } }, result, null, CancellationToken.None);
        Require(Directory.GetFiles(transport.LogsRoot, "*.txt").Length == 2, "Same filename collided across transactions.");

        byte[] binary = [0, 255, 65, 66, 127, 128];
        string rejected = FeedTransactionLog.Format(input with { Content = binary }, null, "Malformed fixture.");
        Require(rejected.Contains("00 FF 41 42 7F 80") && rejected.Contains("| ..AB..") && rejected.Contains("No validated anonymized output was produced.") && rejected.Contains("Malformed fixture."), "Rejected binary log lost bytes or failure reason.");
        RequireOrder(rejected, "BEFORE ANONYMIZATION", "Complete feed data", "Hex dump", "00 FF 41 42 7F 80",
            "AFTER ANONYMIZATION", "No validated anonymized output was produced.", "Reason: Malformed fixture.");
        string export = "A VER 2\nA MSI DEMO\nA ENC 546\nA CCS 1208\nX " + Convert.ToHexString(input.Content.Span) + "\n";
        string exported = FeedTransactionLog.Format(input with { Delivery = delivery with { OriginalName = "sample.txt" }, Content = Encoding.UTF8.GetBytes(export) }, null, "Test rejection.");
        Require(exported.Contains(export) && exported.Contains("Decoded MQ payload") && exported.Contains(Encoding.UTF8.GetString(input.Content.Span)), "MQ export lacks original transport bytes or readable payload.");
        string protectedExport = "A VER 2\nA MSI DEMO\nA ENC 546\nA CCS 1208\nX " + Convert.ToHexString(result.Content.Span) + "\n";
        string mqLog = FeedTransactionLog.Format(input with { Delivery = delivery with { OriginalName = "sample.txt" }, Content = Encoding.UTF8.GetBytes(export) },
            result with { Content = Encoding.UTF8.GetBytes(protectedExport) }, null);
        RequireOrder(mqLog, "BEFORE ANONYMIZATION", "Complete feed data", export, "Decoded MQ payload", Encoding.UTF8.GetString(input.Content.Span),
            "Parsed fields", "AFTER ANONYMIZATION", "Parsed fields", "Complete feed data", protectedExport, "Decoded MQ payload", Encoding.UTF8.GetString(result.Content.Span));
        string binaryLog = FeedTransactionLog.Format(input with { Content = binary }, result with { Content = new byte[] { 0, 255, 42 } }, null);
        RequireOrder(binaryLog, "BEFORE ANONYMIZATION", "Complete feed data", "Hex dump", "Parsed fields",
            "AFTER ANONYMIZATION", "Parsed fields", "Complete feed data", "Hex dump", "00 FF 2A");
        Require(!Directory.GetFiles(transport.LogsRoot, "*.pending").Any(), "Incomplete log was left exposed.");
        Console.WriteLine("PASS transaction logs follow workflow order for JSON, binary, MQ and rejected input, separate original/protected data, and reuse stable transaction IDs");
    }
}
