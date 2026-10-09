using PresidioDemo;
using PresidioDemo.Feeds;

const string usage = "Usage: dotnet run [-- --docs-root <folder> --policy-file <xml-file> --xdr-layout-file <xml-file> --reference-layout-file <xml-file> --process-folder <source-folder>]";
if (args is ["--help"] or ["-h"])
{
    Console.WriteLine(usage);
    Console.WriteLine("Runs independent feed strategies with configured local Presidio field actions.");
    return;
}
var project = new DirectoryInfo(AppContext.BaseDirectory);
while (project is not null && !File.Exists(Path.Combine(project.FullName, "PresidioDemo.csproj")))
{
    project = project.Parent;
}
string projectRoot = project?.FullName ?? Directory.GetCurrentDirectory();
string docsRoot = Path.Combine(projectRoot, "docs");
string policyFile = Path.Combine(projectRoot, "src", "config", "feed-policies.xml");
string xdrLayoutFile = Path.Combine(projectRoot, "src", "config", "xdr-layouts.xml");
string referenceLayoutFile = Path.Combine(projectRoot, "src", "config", "reference-layouts.xml");
string? processFolder = null;
var supplied = new HashSet<string>(StringComparer.Ordinal);
for (int i = 0; i < args.Length; i += 2)
{
    if (i + 1 >= args.Length || args[i] is not ("--docs-root" or "--policy-file" or "--xdr-layout-file" or "--reference-layout-file" or "--process-folder") || !supplied.Add(args[i]))
    {
        Console.Error.WriteLine(usage);
        Environment.ExitCode = 1;
        return;
    }
    switch (args[i])
    {
        case "--docs-root":
            docsRoot = Path.GetFullPath(args[i + 1]);
            break;
        case "--policy-file":
            policyFile = Path.GetFullPath(args[i + 1]);
            break;
        case "--xdr-layout-file":
            xdrLayoutFile = Path.GetFullPath(args[i + 1]);
            break;
        case "--reference-layout-file":
            referenceLayoutFile = Path.GetFullPath(args[i + 1]);
            break;
        case "--process-folder":
            processFolder = Path.GetFullPath(args[i + 1]);
            break;
    }
}
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};
bool pythonStarted = false;
try
{
    // Validate configuration and reference checksums before claiming any input.
    var policies = FeedPolicyConfiguration.Load(policyFile);
    var xdrSchemas = XdrLayoutConfiguration.Load(xdrLayoutFile);
    var referenceLayouts = ReferenceLayoutConfiguration.Load(referenceLayoutFile);
    foreach (var (feed, reference) in referenceLayouts)
    {
        foreach (var field in reference.Fields)
        {
            _ = policies[feed].Get(field.Name);
        }
    }
    using var transport = new FolderFeedTransport(docsRoot);
    transport.Initialize();
    Console.WriteLine("Initializing embedded local Presidio and the bundled NLP model...");
    PythonHost.Start();
    pythonStarted = true;
    var protector = new PresidioFeedProtector();
    var router = FeedWorkflowRouter.Create(protector, policies, feed => FeedCodecRegistry.Create(feed, xdrSchemas, referenceLayouts));
    using var listener = new FolderFeedListener(transport, router);
    Console.WriteLine("PRESIDIO FEED LISTENER DEMO");
    Console.WriteLine("Input -> listener -> parser -> configured field actions -> anonymization -> serialization -> output");
    Console.WriteLine($"Input:   {transport.InputRoot}");
    Console.WriteLine($"Output:  {transport.OutputRoot}");
    Console.WriteLine($"Failed:  {transport.FailedRoot}");
    foreach (var feed in FeedCatalog.All)
    {
        Console.WriteLine($"Listening: {feed.FolderName,-12} -> {router.Get(feed.Type).GetType().Name}");
    }
    Console.WriteLine("Input formats: MQ export (.txt) and raw payload (.bin). JSON: Seats, PNR Linking. ACI/ETKT: configured reference layouts; general XDR requires producer definitions.");
    Console.WriteLine($"Field policies: {policyFile}");
    Console.WriteLine($"XDR layouts: {xdrLayoutFile} | {xdrSchemas.Count} producer schemas configured");
    Console.WriteLine($"Reference layouts: {referenceLayoutFile} | {referenceLayouts.Count} restricted profiles configured");
    Console.WriteLine("Scheduling: one active delivery per feed; shared local Presidio calls serialized per field.");
    Console.WriteLine(processFolder is null ? "READY - Press Ctrl+C or Enter to stop." : "READY - Processing source-folder batch; press Ctrl+C to interrupt.");
    if (processFolder is not null)
    {
        var batch = await FeedBatchProcessor.RunAsync(processFolder, docsRoot, transport, listener, shutdown.Token);
        foreach (var item in batch.Items)
        {
            Console.WriteLine($"BATCH {item.Feed,-12} {item.Status,-9} {Path.GetFileName(item.SourceFile)}");
        }
        Console.WriteLine($"Batch report: {batch.ReportFile}");
        Environment.ExitCode = batch.AllPublished ? 0 : 2;
    }
    else
    {
        _ = Task.Run(() =>
        {
            if (Console.ReadLine() is not null)
            {
                shutdown.Cancel();
            }
        });
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
    if (pythonStarted)
    {
        PythonHost.Stop();
    }
}
