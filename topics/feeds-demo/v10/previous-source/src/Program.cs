using PresidioDemo;
using PresidioDemo.Feeds;

if (args is ["--help"] or ["-h"])
{
    Console.WriteLine("Usage: dotnet run [-- --docs-root <folder> --policy-file <xml-file> --xdr-layout-file <xml-file> --process-folder <source-folder>]");
    Console.WriteLine("Runs independent feed strategies with configured local Presidio field actions.");
    return;
}
var project = new DirectoryInfo(AppContext.BaseDirectory);
while (project is not null && !File.Exists(Path.Combine(project.FullName, "PresidioDemo.csproj"))) project = project.Parent;
string projectRoot = project?.FullName ?? Directory.GetCurrentDirectory();
string docsRoot = Path.Combine(projectRoot, "docs");
string policyFile = Path.Combine(projectRoot, "src", "config", "feed-policies.xml");
string xdrLayoutFile = Path.Combine(projectRoot, "src", "config", "xdr-layouts.xml");
string? processFolder = null;
var supplied = new HashSet<string>(StringComparer.Ordinal);
for (int i = 0; i < args.Length; i += 2)
{
    if (i + 1 >= args.Length || args[i] is not ("--docs-root" or "--policy-file" or "--xdr-layout-file" or "--process-folder") || !supplied.Add(args[i]))
    {
        Console.Error.WriteLine("Usage: dotnet run [-- --docs-root <folder> --policy-file <xml-file> --xdr-layout-file <xml-file> --process-folder <source-folder>]");
        Environment.ExitCode = 1;
        return;
    }
    if (args[i] == "--docs-root") docsRoot = Path.GetFullPath(args[i + 1]);
    else if (args[i] == "--policy-file") policyFile = Path.GetFullPath(args[i + 1]);
    else if (args[i] == "--xdr-layout-file") xdrLayoutFile = Path.GetFullPath(args[i + 1]);
    else processFolder = Path.GetFullPath(args[i + 1]);
}
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
bool pythonStarted = false;
try
{
    var policies = FeedPolicyConfiguration.Load(policyFile); // Validate before claiming any input.
    var xdrSchemas = XdrLayoutConfiguration.Load(xdrLayoutFile);
    using var transport = new FolderFeedTransport(docsRoot);
    transport.Initialize(); // Acquire the single-consumer lock before listening.
    Console.WriteLine("Initializing embedded local Presidio and the bundled NLP model...");
    PythonHost.Start();
    pythonStarted = true;
    var protector = new PresidioFeedProtector();
    var router = FeedWorkflowRouter.Create(protector, policies, feed => FeedCodecRegistry.Create(feed, xdrSchemas));
    using var listener = new FolderFeedListener(transport, router);
    Console.WriteLine("PRESIDIO FEED LISTENER DEMO");
    Console.WriteLine("Input -> listener -> parser -> configured PII identification -> anonymization -> serialization -> output");
    Console.WriteLine($"Input:   {transport.InputRoot}");
    Console.WriteLine($"Output:  {transport.OutputRoot}");
    Console.WriteLine($"Failed:  {transport.FailedRoot}");
    foreach (var feed in FeedCatalog.All) Console.WriteLine($"Listening: {feed.FolderName,-12} -> {router.Get(feed.Type).GetType().Name}");
    Console.WriteLine("Input formats: MQ export (.txt) and raw payload (.bin). JSON codecs: Seats, PNR Linking. XDR codecs require explicit producer field definitions.");
    Console.WriteLine($"Field policies: {policyFile}");
    Console.WriteLine($"XDR layouts: {xdrLayoutFile} | {xdrSchemas.Count} producer schemas configured");
    Console.WriteLine("Scheduling: one active delivery per feed; shared local Presidio calls serialized per field.");
    Console.WriteLine(processFolder is null ? "READY - Press Ctrl+C or Enter to stop." : "READY - Processing source-folder batch; press Ctrl+C to interrupt.");
    if (processFolder is not null)
    {
        var batch = await FeedBatchProcessor.RunAsync(processFolder, docsRoot, transport, listener, shutdown.Token);
        foreach (var item in batch.Items) Console.WriteLine($"BATCH {item.Feed,-12} {item.Status,-9} {Path.GetFileName(item.SourceFile)}");
        Console.WriteLine($"Batch report: {batch.ReportFile}");
        Environment.ExitCode = batch.AllPublished ? 0 : 2;
    }
    else
    {
        _ = Task.Run(() => { if (Console.ReadLine() is not null) shutdown.Cancel(); });
        await listener.RunAsync(shutdown.Token);
    }
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