using PresidioDemo.Feeds;

if (args is ["--help"] or ["-h"])
{
    Console.WriteLine("Usage: dotnet run [-- --docs-root <folder>]");
    Console.WriteLine("Starts the five-feed JSON listener. Press Ctrl+C or Enter to stop.");
    return;
}

string docsRoot;
if (args.Length == 0)
{
    var project = new DirectoryInfo(AppContext.BaseDirectory);
    while (project is not null && !File.Exists(Path.Combine(project.FullName, "PresidioDemo.csproj")))
        project = project.Parent;
    docsRoot = Path.Combine(project?.FullName ?? Directory.GetCurrentDirectory(), "docs");
}
else if (args is ["--docs-root", var path])
{
    docsRoot = Path.GetFullPath(path);
}
else
{
    Console.Error.WriteLine("Usage: dotnet run [-- --docs-root <folder>]");
    Environment.ExitCode = 1;
    return;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };

try
{
    // Constructor injection keeps workflows independent of folder transport.
    var router = new FeedWorkflowRouter([
        new EtktWorkflow(), new PnrWorkflow(), new PnrLinkingWorkflow(),
        new SeatsWorkflow(), new AciWorkflow()
    ]);
    using var transport = new FolderFeedTransport(docsRoot);
    using var listener = new FolderFeedListener(transport, router);
    transport.Initialize();

    Console.WriteLine("SOLACE FEED LISTENER DEMO");
    Console.WriteLine("JSON validation and unchanged file delivery");
    Console.WriteLine($"Input:   {transport.InputRoot}");
    Console.WriteLine($"Output:  {transport.OutputRoot}");
    Console.WriteLine($"Failed:  {transport.FailedRoot}");
    foreach (var feed in FeedCatalog.All)
        Console.WriteLine($"Listening: {feed.FolderName,-12} -> {router.Get(feed.Type).GetType().Name}");
    Console.WriteLine("Copy samples from docs/sample-docs into the matching input folders.");
    Console.WriteLine("READY - Press Ctrl+C or Enter to stop.");

    _ = Task.Run(() =>
    {
        // EOF from a background launch is not a request to stop.
        if (Console.ReadLine() is not null) shutdown.Cancel();
    });
    await listener.RunAsync(shutdown.Token);
    Console.WriteLine("Listener stopped. Pending deliveries will resume next time.");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Listener could not start or continue: {ex.Message}");
    Environment.ExitCode = 1;
}
