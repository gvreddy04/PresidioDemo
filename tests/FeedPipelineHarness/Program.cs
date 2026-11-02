using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PresidioDemo.Feeds.Shared;
using PresidioDemo.Messaging;
using PresidioDemo.Messaging.Testing;
using PresidioDemo.Pipeline;
using PresidioDemo.Protection;
using PresidioDemo.Transport;
using PresidioDemo.Worker;

#region Execution

// Test-only batch runner. Sends every .txt (IBM MQ export) and .bin (raw payload) file under --input through the
// real FeedWorkers and embedded Presidio over the in-memory broker, then writes:
//   <output>/published/<feed>/<file>          validated protected output
//   <output>/dead-letter/<feed>/<file>        rejected original input, with <file>.reason.txt
//   <output>/logs/<feed>/<file>.log.txt       full before/after transaction log (contains original data; tests only)
//   <output>/report.json                      one item per input file
// Exit codes: 0 every file published, 2 at least one rejected, 1 configuration or usage error.
const string usage = "Usage: FeedPipelineHarness --input <folder> --output <folder> [--policy-file <xml>] [--xdr-layout-file <xml>] [--reference-layout-file <xml>] [--fail-publishes <n>]";
string project = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(project, "PresidioDemo.slnx")))
{
    project = Directory.GetParent(project)?.FullName ?? throw new Exception("Project root not found.");
}
string configuration = Path.Combine(project, "src", "PresidioDemo.Core", "Configuration");
var settings = new Dictionary<string, string>
{
    ["--policy-file"] = Path.Combine(configuration, "feed-policies.xml"),
    ["--xdr-layout-file"] = Path.Combine(configuration, "xdr-layouts.xml"),
    ["--reference-layout-file"] = Path.Combine(configuration, "reference-layouts.xml"),
    ["--fail-publishes"] = "0"
};
var supplied = new HashSet<string>();
for (int i = 0; i < args.Length; i += 2)
{
    if (i + 1 >= args.Length || args[i] is not ("--input" or "--output" or "--policy-file" or "--xdr-layout-file" or "--reference-layout-file" or "--fail-publishes") || !supplied.Add(args[i]))
    {
        Console.Error.WriteLine(usage);
        return 1;
    }
    settings[args[i]] = args[i] == "--fail-publishes" ? args[i + 1] : Path.GetFullPath(args[i + 1]);
}
if (!settings.ContainsKey("--input") || !settings.ContainsKey("--output") || !int.TryParse(settings["--fail-publishes"], out int failPublishes))
{
    Console.Error.WriteLine(usage);
    return 1;
}

FeedPipelineConfiguration pipeline;
List<(string Path, FeedType Feed, byte[] Content)> inputs = [];
try
{
    pipeline = FeedPipelineConfiguration.Load(settings["--policy-file"], settings["--xdr-layout-file"], settings["--reference-layout-file"]);
    foreach (string file in Directory.EnumerateFiles(settings["--input"], "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        if (FeedFileRouting.IsFeedFile(file))
        {
            inputs.Add((file, FeedFileRouting.FromPath(file), File.ReadAllBytes(file)));
        }
    }
    if (inputs.Count == 0)
    {
        throw new InvalidDataException("No .txt or .bin input files were found.");
    }
}
catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Xml.XmlException)
{
    Console.Error.WriteLine("Harness could not start: " + ex.Message);
    return 1;
}

var broker = new InMemoryMessageBroker { FailNextPublishes = failPublishes };
var messaging = new MessagingOptions
{
    Transport = "InMemory", MaxDeliveries = 5, PublishAckTimeoutMs = 1000, PublishRetryDelayMs = 50,
    Routes = FeedCatalog.All.Select(f => new MessageRoute { Feed = f.FolderName, Input = "in." + f.FolderName, Output = "out/" + f.FolderName, DeadLetter = "dlq." + f.FolderName }).ToList()
};
var routes = FeedWorkerService.Routes(messaging);
var outcomes = new ConcurrentDictionary<string, FeedWorkerOutcome>();
Console.WriteLine("Initializing embedded local Presidio and the bundled NLP model...");
PythonHost.Start();
try
{
    var processor = new FeedMessageProcessor(pipeline.CreateRouter(new PresidioFeedProtector()));
    using var stop = new CancellationTokenSource();
    var workers = routes.Select(pair => new FeedWorker(pair.Key, pair.Value, messaging, broker, broker, processor, NullLogger.Instance, outcome =>
    {
        outcomes[outcome.Input.CorrelationId!] = outcome;
        return ValueTask.CompletedTask;
    }).RunAsync(stop.Token)).ToArray();
    for (int i = 0; i < inputs.Count; i++)
    {
        var (path, feed, content) = inputs[i];
        string format = Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase) ? FeedWorker.MqExportFormat : FeedWorker.RawFormat;
        broker.Send(routes[feed].Input, content, new Dictionary<string, string> { [MessageHeaders.PayloadFormat] = format }, i.ToString());
    }
    var deadline = DateTime.UtcNow.AddMinutes(10);
    while (outcomes.Count < inputs.Count && DateTime.UtcNow < deadline)
    {
        await Task.Delay(50);
    }
    stop.Cancel();
    await Task.WhenAll(workers);
}
finally
{
    PythonHost.Stop();
}

string output = settings["--output"];
var items = new List<object>();
for (int i = 0; i < inputs.Count; i++)
{
    var (path, feed, content) = inputs[i];
    string folder = FeedCatalog.Get(feed).FolderName;
    string name = Path.GetFileName(path);
    string sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    if (!outcomes.TryGetValue(i.ToString(), out var outcome))
    {
        items.Add(new { Source = path, Feed = folder, Sha256 = sha, Bytes = content.Length, Status = "Pending" });
        Console.WriteLine($"HARNESS {folder,-12} Pending   {name}");
        continue;
    }
    string log = Write(Path.Combine(output, "logs", folder, name + ".log.txt"), System.Text.Encoding.UTF8.GetBytes(FeedLoggers.Format(
        outcome.Result.Message with { Delivery = outcome.Result.Message.Delivery with { OriginalName = name } }, outcome.Result.Workflow, outcome.Result.Reason)));
    if (outcome.Result.Published)
    {
        string published = Write(Path.Combine(output, "published", folder, name), outcome.Published.Payload.ToArray());
        items.Add(new { Source = path, Feed = folder, Sha256 = sha, Bytes = content.Length, Status = "Published", Output = published, Log = log, MessageId = outcome.Published.MessageId });
        Console.WriteLine($"HARNESS {folder,-12} Published {name}");
    }
    else
    {
        string dead = Write(Path.Combine(output, "dead-letter", folder, name), outcome.Published.Payload.ToArray());
        Write(dead + ".reason.txt", System.Text.Encoding.UTF8.GetBytes(outcome.Result.Reason!));
        items.Add(new { Source = path, Feed = folder, Sha256 = sha, Bytes = content.Length, Status = "Rejected", DeadLetter = dead, Reason = outcome.Result.Reason, Log = log, MessageId = outcome.Published.MessageId });
        Console.WriteLine($"HARNESS {folder,-12} Rejected  {name} | {outcome.Result.Reason}");
    }
}
string report = Write(Path.Combine(output, "report.json"), JsonSerializer.SerializeToUtf8Bytes(new { PublishAttempts = broker.PublishAttempts, Items = items }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Harness report: " + report);
return items.All(item => item.GetType().GetProperty("Status")!.GetValue(item) as string == "Published") ? 0 : 2;

#endregion

#region Methods

static string Write(string path, byte[] content)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, content);
    return path;
}

#endregion
