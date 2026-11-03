using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PresidioDemo.Feeds.Shared;
using PresidioDemo.Messaging;
using PresidioDemo.Worker;

#region Execution

// Publishes sample feed files to the input queues of the environment selected in the worker's appsettings.json
// (DOTNET_ENVIRONMENT, else CurrentEnvironment). Use it with a running worker on the Local broker or in Dev.
// .txt files are sent as IBM MQ exports and .bin files as raw payloads; each file goes to its feed's input queue.
const string usage = "Usage: dotnet run --project tools/FeedSampleSender -- [--input <folder or file>] [--settings <appsettings.json>]";
string project = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(project, "PresidioDemo.slnx")))
{
    project = Directory.GetParent(project)?.FullName ?? throw new Exception("Project root not found.");
}
string input = Path.Combine(project, "docs", "sample-docs");
string settingsFile = Path.Combine(project, "src", "PresidioDemo.Worker", "appsettings.json");
for (int i = 0; i < args.Length; i += 2)
{
    switch (i + 1 < args.Length ? args[i] : "")
    {
        case "--input": input = Path.GetFullPath(args[i + 1]); break;
        case "--settings": settingsFile = Path.GetFullPath(args[i + 1]); break;
        default: Console.Error.WriteLine(usage); return 1;
    }
}

try
{
    var (settings, environment) = EnvironmentConfiguration.Build(settingsFile);
    // The broker allows one connection per client name, so the sender must not reuse the running worker's name.
    IConfiguration configuration = settings["Solace:ClientName"] is { Length: > 0 } clientName
        ? new ConfigurationBuilder().AddConfiguration(settings)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Solace:ClientName"] = clientName + "-sender" }).Build()
        : settings;
    var messaging = MessagingOptions.Load(configuration);
    var routes = FeedWorkerService.Routes(messaging);
    string[] files = File.Exists(input) ? [input]
        : Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories).Where(FeedFileRouting.IsFeedFile).Order(StringComparer.Ordinal).ToArray();
    var services = new ServiceCollection().AddLogging(log => log.AddSimpleConsole(c => c.SingleLine = true));
    services.AddConfiguredMessaging(configuration, messaging, Path.GetDirectoryName(settingsFile)!);
    await using var provider = services.BuildServiceProvider();
    var connection = provider.GetRequiredService<IMessagingConnection>();
    var publisher = provider.GetRequiredService<IMessagePublisher>();
    Console.WriteLine($"Sending {files.Length} files to {connection.Transport} ({environment}).");
    await connection.ConnectAsync(CancellationToken.None);
    foreach (string file in files)
    {
        var feed = FeedFileRouting.FromPath(file);
        if (!routes.TryGetValue(feed, out var route))
        {
            // The selected environment does not route this feed, so no worker would consume the file.
            Console.WriteLine($"SKIP {FeedCatalog.Get(feed).FolderName,-12} {Path.GetFileName(file)} (no route in {environment})");
            continue;
        }
        string format = Path.GetExtension(file).Equals(".txt", StringComparison.OrdinalIgnoreCase) ? FeedWorker.MqExportFormat : FeedWorker.RawFormat;
        await publisher.PublishAsync(new(new(route.Input, DestinationKind.Queue), await File.ReadAllBytesAsync(file), Guid.NewGuid().ToString("N"),
            Path.GetFileName(file), new Dictionary<string, string> { [MessageHeaders.PayloadFormat] = format }), CancellationToken.None);
        Console.WriteLine($"SENT {route.Feed,-12} {Path.GetFileName(file)} -> queue:{route.Input}");
    }
    await connection.DisposeAsync();
    return 0;
}
catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
{
    Console.Error.WriteLine("Sender failed: " + ex.Message);
    return 1;
}

#endregion
