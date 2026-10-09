using PresidioDemo;
using PresidioDemo.Feeds;

if (args is ["--help"] or ["-h"])
{
    Console.WriteLine("Usage: dotnet run [-- --docs-root <folder> --policy-file <xml-file>]");
    Console.WriteLine("Runs independent feed strategies with configured local Presidio field actions.");
    return;
}
var project = new DirectoryInfo(AppContext.BaseDirectory);
while (project is not null && !File.Exists(Path.Combine(project.FullName, "PresidioDemo.csproj"))) project = project.Parent;
string projectRoot = project?.FullName ?? Directory.GetCurrentDirectory();
string docsRoot = Path.Combine(projectRoot, "docs");
string policyFile = Path.Combine(projectRoot, "config", "feed-policies.xml");
var supplied = new HashSet<string>(StringComparer.Ordinal);
for (int i = 0; i < args.Length; i += 2)
{
    if (i + 1 >= args.Length || args[i] is not ("--docs-root" or "--policy-file") || !supplied.Add(args[i]))
    {
        Console.Error.WriteLine("Usage: dotnet run [-- --docs-root <folder> --policy-file <xml-file>]");
        Environment.ExitCode = 1;
        return;
    }
    if (args[i] == "--docs-root") docsRoot = Path.GetFullPath(args[i + 1]);
    else policyFile = Path.GetFullPath(args[i + 1]);
}
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
bool pythonStarted = false;
try
{
    var policies = FeedPolicyConfiguration.Load(policyFile); // Validate before claiming any input.
    using var transport = new FolderFeedTransport(docsRoot);
    transport.Initialize(); // Acquire the single-consumer lock before listening.
    Console.WriteLine("Initializing embedded local Presidio and the bundled NLP model...");
    PythonHost.Start();
    pythonStarted = true;
    var protector = new PresidioFeedProtector();
    var router = FeedWorkflowRouter.Create(protector, policies, _ => new UnconfiguredBinaryFeedCodec());
    using var listener = new FolderFeedListener(transport, router);
    Console.WriteLine("PRESIDIO FEED LISTENER DEMO");
    Console.WriteLine("Parse -> local Presidio -> rebuild original format -> output feed");
    Console.WriteLine($"Input:   {transport.InputRoot}");
    Console.WriteLine($"Output:  {transport.OutputRoot}");
    Console.WriteLine($"Failed:  {transport.FailedRoot}");
    foreach (var feed in FeedCatalog.All) Console.WriteLine($"Listening: {feed.FolderName,-12} -> {router.Get(feed.Type).GetType().Name}");
    Console.WriteLine("Input formats: IBM MQ dump (.txt) and binary payload (.bin). A producer-specific binary codec is required.");
    Console.WriteLine($"Field policies: {policyFile}");
    Console.WriteLine("Scheduling: one active delivery per feed; shared local Presidio calls serialized per field.");
    Console.WriteLine("READY - Press Ctrl+C or Enter to stop.");
    _ = Task.Run(() => { if (Console.ReadLine() is not null) shutdown.Cancel(); });
    await listener.RunAsync(shutdown.Token);
    Console.WriteLine("Listener stopped. Pending deliveries will resume next time.");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Listener could not start or continue: {ex.Message}");
    Environment.ExitCode = 1;
}
finally
{
    if (pythonStarted) PythonHost.Stop();
}