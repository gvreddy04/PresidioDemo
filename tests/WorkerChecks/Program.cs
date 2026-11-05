using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PresidioDemo.Feeds.Shared;
using PresidioDemo.Messaging;
using PresidioDemo.Messaging.Solace;
using PresidioDemo.Messaging.Testing;
using PresidioDemo.Pipeline;
using PresidioDemo.Worker;

#region Execution

// Verifies environment selection, settings validation and FeedWorker delivery rules without Python or a broker.
string project = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(project, "PresidioDemo.slnx")))
{
    project = Directory.GetParent(project)?.FullName ?? throw new Exception("Project root not found.");
}
string runRoot = Path.Combine(project, ".test-runs", "worker-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(runRoot);
string settingsFile = Path.Combine(project, "src", "PresidioDemo.Worker", "appsettings.json");
Func<string, string?> noVariables = _ => null;
Func<string, string?> password = name => name == "SOLACE_PASSWORD" ? "fixture-password" : null;

// Environment selection
var (local, localName) = EnvironmentConfiguration.Build(settingsFile, noVariables);
Require(localName == "Local" && local["Solace:Host"] == "tcp://localhost:55555", "without DOTNET_ENVIRONMENT the file's CurrentEnvironment (Local) applies");
var (dev, devName) = EnvironmentConfiguration.Build(settingsFile, name => name == EnvironmentConfiguration.EnvironmentVariable ? "Dev" : null);
Require(devName == "Dev" && dev["Solace:Host"]!.StartsWith("tcps://") && dev["Solace:Vpn"] == "dev-demoair-vpn", "DOTNET_ENVIRONMENT=Dev selects the Dev section");
Require(local["Messaging:Routes:2:Input"] == "q.pnr.raw" && dev["Messaging:Routes:2:Input"] == "DEMOAIR.OPS.PIIDEMO.COS.PNR.MI.1"
    && dev["Messaging:Routes:3:Feed"] is null && dev["Messaging:Transport"] == "Solace",
    "each environment lists its own routes; shared Messaging settings still apply");
Reject(() => EnvironmentConfiguration.Build(settingsFile, name => name == EnvironmentConfiguration.EnvironmentVariable ? "QA" : null),
    "an environment without a section stops startup");
RejectVariant(json => json["Solaceee"] = new JsonObject(), "an unknown top-level section is rejected");
RejectVariant(json => json["Environments"]!["Local"]!["Solcae"] = new JsonObject(), "an unknown setting inside an environment section is rejected");
RejectVariant(json => json.AsObject().Remove("CurrentEnvironment"), "a file without CurrentEnvironment needs DOTNET_ENVIRONMENT");
Environment.SetEnvironmentVariable("Solace__Host", "tcp://override.example.com:55555");
try
{
    var (overridden, _) = EnvironmentConfiguration.Build(settingsFile, noVariables);
    Require(overridden["Solace:Host"] == "tcp://override.example.com:55555", "an environment variable such as Solace__Host overrides the selected section");
}
finally { Environment.SetEnvironmentVariable("Solace__Host", null); }

// Settings validation
var messaging = MessagingOptions.Load(local);
Require(messaging.Transport == "Solace" && FeedWorkerService.Routes(messaging).Count == FeedCatalog.All.Count, "the Local settings route every feed");
Require(FeedWorkerService.Routes(MessagingOptions.Load(dev)).Keys.ToHashSet().SetEquals([FeedType.ACI, FeedType.ETKT, FeedType.PNR]),
    "Dev routes only the feeds its broker carries; the other feeds are not consumed");
Require(SolaceOptions.Load(local, runRoot, password).ProvisionQueues && !SolaceOptions.Load(dev, runRoot, password).ProvisionQueues,
    "Local and Dev Solace sections validate; only Local creates queues");
var onePnr = MessagingOptions.Load(new ConfigurationBuilder().AddConfiguration(local).AddInMemoryCollection([new("Messaging:Feed", "pnr")]).Build());
Require(onePnr.Routes.Count == 1 && FeedWorkerService.Routes(onePnr).Keys.Single() == FeedType.PNR,
    "Messaging:Feed keeps only that feed's route, so one service runs one feed");
var oneDevAci = MessagingOptions.Load(new ConfigurationBuilder().AddConfiguration(dev).AddInMemoryCollection([new("Messaging:Feed", "ACI")]).Build());
Require(oneDevAci.Routes.Single().Input == "DEMOAIR.OPS.PIIDEMO.COS.ACI.MI.1", "Messaging:Feed selects one route in Dev too");
RejectMessaging("Messaging:Feed", "IND", "Messaging:Feed without a route is rejected");
var localSolace = SolaceOptions.Load(local, runRoot, password);
Require(localSolace.ClientName == "presidio-worker-local", "without UniqueClientName the client name is used as configured");
var devFirst = SolaceOptions.Load(dev, runRoot, password);
var devSecond = SolaceOptions.Load(dev, runRoot, password);
Require(devFirst.ClientName.StartsWith("piidemo-worker-dev-") && devFirst.ClientName != devSecond.ClientName,
    "Dev adds a per-process suffix, so scaled-out tasks never share a Solace client name");
string longName = SolaceOptions.WithInstanceSuffix(new string('w', 200), "ip-10-0-1-23.ec2.internal");
Require(longName.Length == SolaceOptions.MaxClientNameLength && longName.Contains("-ip-10-0-1-23-ec2-internal-"),
    "the suffixed client name stays within the broker limit and uses a safe host name");
RejectSolace("ClientName", new string('w', 161), "a client name longer than 160 characters is rejected");
Reject(() => SolaceOptions.Load(local, runRoot, noVariables), "a missing password environment variable stops startup");
RejectSolace("Host", "https://broker.example.com", "HTTP hosts are rejected; only native tcp:// or tcps://");
RejectSolace("ValidateCertificate", "false", "tcps:// without certificate validation is rejected", "Dev");
RejectSolace("Password", "secret", "a password in the settings is rejected as an unknown key");
RejectMessaging("Messaging:Transport", "", "a missing transport is rejected");
RejectMessaging("Messaging:MaxDeliveries", "0", "MaxDeliveries below 1 is rejected");
RejectMessaging("Messaging:Routes:1:Input", "q.aci.raw", "two feeds sharing a queue are rejected");
RejectMessaging("Messaging:Routes:0:Output", "airline/>", "wildcard output topics are rejected");
RejectMessaging("Messaging:Routes:0:DeadLetter", "q.aci.raw", "a dead-letter queue equal to the input is rejected");
RejectMessaging("Messaging:Routes:0:Typo", "x", "unknown route keys are rejected");
var duplicateFeed = new ConfigurationBuilder().AddConfiguration(local).AddInMemoryCollection([new("Messaging:Routes:4:Feed", "ACI")]).Build();
Reject(() => FeedWorkerService.Routes(MessagingOptions.Load(duplicateFeed)), "a feed with two routes is rejected");
var unknownFeed = new ConfigurationBuilder().AddConfiguration(local).AddInMemoryCollection([new("Messaging:Routes:4:Feed", "IND")]).Build();
Reject(() => FeedWorkerService.Routes(MessagingOptions.Load(unknownFeed)), "a route for a feed without a codec is rejected");
Require(PresidioOptions.Load(local, runRoot).PythonEnvironment is null, "without Presidio:PythonEnvironment the project's src/.venv applies");
string venv = Directory.CreateDirectory(Path.Combine(runRoot, "venv")).FullName;
File.WriteAllText(Path.Combine(venv, "pyvenv.cfg"), "home = /usr/bin\nversion = 3.12.3\n");
var withVenv = new ConfigurationBuilder().AddConfiguration(local).AddInMemoryCollection([new("Presidio:PythonEnvironment", "venv")]).Build();
Require(PresidioOptions.Load(withVenv, runRoot).PythonEnvironment == venv, "Presidio:PythonEnvironment resolves against the application folder");
var noVenv = new ConfigurationBuilder().AddConfiguration(local).AddInMemoryCollection([new("Presidio:PythonEnvironment", "missing")]).Build();
Reject(() => PresidioOptions.Load(noVenv, runRoot), "a Python environment without pyvenv.cfg stops startup");

// FeedWorker delivery rules
var options = MessagingOptions.Load(local);
options.PublishRetryDelayMs = 20;
options.CopyHeaders = ["sourceSystem"];
var seats = options.Routes.Single(r => r.Feed == "Seats");
byte[] payload = Encoding.UTF8.GetBytes("{\"x\":\"y\"}");
var headers = new Dictionary<string, string> { ["sourceSystem"] = "res-host", ["internal"] = "drop-me" };

{
    var (broker, workflows) = Setup();
    string id = broker.Send(seats.Input, payload, headers, "corr-1");
    await RunUntil(broker, workflows, () => broker.Pending(seats.Input) == 0);
    var output = broker.PublishedTo(seats.Output).Single();
    Require(output.Destination.Kind == DestinationKind.Topic && output.Payload.Span.SequenceEqual(payload), "validated output goes to the feed's output topic");
    Require(output.MessageId == FeedWorker.TransactionId(seats.Input, id).ToString("N") && output.CorrelationId == "corr-1",
        "output carries a transaction ID derived from the broker message ID, and the correlation ID");
    Require(output.Headers.Count == 2 && output.Headers["sourceSystem"] == "res-host" && output.Headers[MessageHeaders.Feed] == "Seats",
        "only allowed headers are copied, plus the feed name");
    Require(workflows.Names.Single().EndsWith(".bin"), "messages without a payload-format header are processed as raw payloads");
}
{
    var (broker, workflows) = Setup();
    broker.FailNextPublishes = 2;
    broker.Send(seats.Input, payload);
    await RunUntil(broker, workflows, () => broker.Pending(seats.Input) == 0);
    Require(broker.PublishAttempts == 3 && broker.PublishedTo(seats.Output).Count == 1, "a broker outage is retried until the output is confirmed");
}
{
    var (broker, workflows) = Setup();
    workflows.BlockNext = true;
    string id = broker.Send(seats.Input, payload);
    await RunUntil(broker, workflows, () => workflows.Blocked);
    Require(broker.Pending(seats.Input) == 1 && broker.PublishedTo(seats.Output).Count == 0, "a crash during processing leaves the message unacknowledged");
    await RunUntil(broker, workflows, () => broker.Pending(seats.Input) == 0);
    var output = broker.PublishedTo(seats.Output).Single();
    Require(output.MessageId == FeedWorker.TransactionId(seats.Input, id).ToString("N") && workflows.Deliveries.Last() == 2,
        "the redelivered message is processed again under the same transaction ID");
}
{
    var (broker, workflows) = Setup(maxDeliveries: 1);
    workflows.BlockNext = true;
    broker.Send(seats.Input, payload);
    await RunUntil(broker, workflows, () => workflows.Blocked);
    await RunUntil(broker, workflows, () => broker.Pending(seats.Input) == 0);
    var rejected = broker.PublishedTo(seats.DeadLetter).Single();
    Require(rejected.Destination.Kind == DestinationKind.Queue && rejected.Headers[MessageHeaders.RejectReason].Contains("Exceeded 1 deliveries"),
        "a message delivered more than MaxDeliveries times is dead-lettered without processing");
}
{
    var (broker, workflows) = Setup();
    workflows.RejectNext = true;
    broker.Send(seats.Input, payload, new Dictionary<string, string> { [MessageHeaders.PayloadFormat] = FeedWorker.MqExportFormat });
    broker.Send(seats.Input, payload, new Dictionary<string, string> { [MessageHeaders.PayloadFormat] = "xml" });
    await RunUntil(broker, workflows, () => broker.Pending(seats.Input) == 0);
    var dead = broker.PublishedTo(seats.DeadLetter);
    Require(workflows.Names.Single().EndsWith(".txt"), "payload-format mq-export is processed as an IBM MQ export");
    Require(dead.Count == 2 && dead[0].Payload.Span.SequenceEqual(payload) && dead[0].Headers[MessageHeaders.RejectReason] == "Fixture rejection." &&
        dead[0].Headers[MessageHeaders.PayloadFormat] == FeedWorker.MqExportFormat, "rejected input goes to the dead-letter queue with its original bytes, reason and format");
    Require(dead[1].Headers[MessageHeaders.RejectReason].Contains("Unsupported"), "an unknown payload format is dead-lettered");
    Require(broker.PublishedTo(seats.Output).Count == 0, "rejected input is never published to the output topic");
}
Directory.Delete(runRoot, recursive: true);
Console.WriteLine("All worker checks passed.");

#endregion

#region Methods

(InMemoryMessageBroker Broker, FixtureWorkflows Workflows) Setup(int maxDeliveries = 5)
{
    options.MaxDeliveries = maxDeliveries;
    return (new InMemoryMessageBroker(), new FixtureWorkflows());
}

// Runs the Seats worker until the condition holds, then stops it as a crash or shutdown would.
async Task RunUntil(InMemoryMessageBroker broker, FixtureWorkflows workflows, Func<bool> condition)
{
    using var stop = new CancellationTokenSource();
    var worker = new FeedWorker(FeedType.Seats, seats, options, broker, broker,
        new FeedMessageProcessor(new FeedWorkflowRouter(FeedCatalog.All.Select(f => workflows.For(f.Type)))), NullLogger.Instance);
    var running = worker.RunAsync(stop.Token);
    var limit = DateTime.UtcNow.AddSeconds(20);
    while (!condition())
    {
        if (DateTime.UtcNow > limit)
        {
            throw new Exception("Worker condition timed out.");
        }
        await Task.Delay(20);
    }
    workflows.Release();
    stop.Cancel();
    await running;
}

void RejectVariant(Action<JsonNode> change, string label)
{
    var json = JsonNode.Parse(File.ReadAllText(settingsFile))!;
    change(json);
    string path = Path.Combine(runRoot, "appsettings.json");
    File.WriteAllText(path, json.ToJsonString());
    Reject(() => EnvironmentConfiguration.Build(path, noVariables), label);
}

void RejectSolace(string key, string value, string label, string environment = "Local")
{
    var (configuration, _) = EnvironmentConfiguration.Build(settingsFile, name => name == EnvironmentConfiguration.EnvironmentVariable ? environment : null);
    var changed = new ConfigurationBuilder().AddConfiguration(configuration).AddInMemoryCollection([new("Solace:" + key, value)]).Build();
    Reject(() => SolaceOptions.Load(changed, runRoot, password), label);
}

void RejectMessaging(string key, string value, string label)
{
    var changed = new ConfigurationBuilder().AddConfiguration(local).AddInMemoryCollection([new(key, value)]).Build();
    Reject(() => MessagingOptions.Load(changed), label);
}

static void Reject(Action action, string label)
{
    try { action(); }
    catch (InvalidOperationException) { Console.WriteLine("PASS " + label); return; }
    throw new Exception("Expected rejection: " + label);
}

static void Require(bool condition, string label)
{
    if (!condition)
    {
        throw new Exception(label);
    }
    Console.WriteLine("PASS " + label);
}

#endregion

#region Nested Types

// Pass-through workflows that can block (to simulate a crash mid-processing) or reject the next message.
sealed class FixtureWorkflows
{
    private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool BlockNext { get; set; }
    public bool Blocked { get; private set; }
    public bool RejectNext { get; set; }
    public List<int> Deliveries { get; } = [];
    public List<string> Names { get; } = [];
    public IFeedWorkflow For(FeedType feed) => new Workflow(feed, this);
    public void Release()
    {
        _release.TrySetResult();
        _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Blocked = false;
    }

    private sealed class Workflow(FeedType feed, FixtureWorkflows owner) : IFeedWorkflow
    {
        public FeedType Feed => feed;
        public async ValueTask<FeedWorkflowResult> ProcessAsync(FeedMessage message, CancellationToken cancellationToken)
        {
            owner.Names.Add(message.Delivery.OriginalName);
            owner.Deliveries.Add(owner.Deliveries.Count + 1);
            if (owner.BlockNext)
            {
                owner.BlockNext = false;
                owner.Blocked = true;
                await owner._release.Task.WaitAsync(cancellationToken);
            }
            if (owner.RejectNext)
            {
                owner.RejectNext = false;
                throw new InvalidDataException("Fixture rejection.");
            }
            return new(message.Content);
        }
    }
}

#endregion
