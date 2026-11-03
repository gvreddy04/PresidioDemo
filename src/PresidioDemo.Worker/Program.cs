using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PresidioDemo.Messaging;
using PresidioDemo.Pipeline;
using PresidioDemo.Worker;

#region Execution

// Every setting is validated before the broker connects or Presidio starts.
string baseDirectory = AppContext.BaseDirectory;
IHost host;
string environment;
try
{
    (var configuration, environment) = EnvironmentConfiguration.Build(Path.Combine(baseDirectory, "appsettings.json"));
    var messaging = MessagingOptions.Load(configuration);
    FeedWorkerService.Routes(messaging);
    var presidio = PresidioOptions.Load(configuration, baseDirectory);
    var pipeline = FeedPipelineConfiguration.Load(presidio.PolicyFile, presidio.XdrLayoutFile, presidio.ReferenceLayoutFile);

    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        DisableDefaults = true,
        ContentRootPath = baseDirectory,
        EnvironmentName = environment,
        Args = args
    });
    builder.Configuration.AddConfiguration(configuration);
    builder.Logging.AddConfiguration(configuration.GetSection("Logging"));
    builder.Logging.AddSimpleConsole(console =>
    {
        console.SingleLine = true;
        console.TimestampFormat = "HH:mm:ss ";
    });
    builder.Services.Configure<HostOptions>(options =>
    {
        options.ShutdownTimeout = TimeSpan.FromSeconds(30);
        options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
    });
    builder.Services.AddSingleton(messaging);
    builder.Services.AddSingleton(pipeline);
    builder.Services.AddSingleton(presidio);

    // Messaging:Transport selects the broker adapter; see MessagingTransports.
    builder.Services.AddConfiguredMessaging(configuration, messaging, baseDirectory);
    builder.Services.AddSingleton<FeedWorkerService>();
    builder.Services.AddHostedService(provider => provider.GetRequiredService<FeedWorkerService>());
    host = builder.Build();
}
catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("Worker could not start: " + ex.Message);
    return 1;
}

using (host)
{
    Console.WriteLine($"PRESIDIO FEED WORKER | environment {environment} | Ctrl+C or SIGTERM stops after the current messages.");
    var worker = host.Services.GetRequiredService<FeedWorkerService>();
    await host.RunAsync();
    // A worker failure (for example an unreachable broker) stops the host; report it in the exit code.
    return worker.Failure is null ? 0 : 1;
}

#endregion
