using System.Text;
using Microsoft.Extensions.Logging;
using PresidioDemo.Messaging;
using PresidioDemo.Messaging.Solace;
using PresidioDemo.Messaging.Testing;

#region Execution

// Every broker adapter must pass the same contract. The in-memory broker always runs; Solace runs when
// SOLACE_CONTRACT_TESTS=1 with SOLACE_PASSWORD set, against the Local broker (deploy/docker-compose.yml).
// A new adapter (for example Kafka) adds one entry to this list.
var adapters = new List<(string Name, Func<string[], AdapterUnderTest> Create)>
{
    ("InMemory", _ =>
    {
        var broker = new InMemoryMessageBroker();
        return new(broker, broker, broker);
    })
};
if (Environment.GetEnvironmentVariable("SOLACE_CONTRACT_TESTS") == "1")
{
    adapters.Add(("Solace", queues =>
    {
        var messaging = new MessagingOptions
        {
            Transport = SolaceMessaging.TransportName, MaxDeliveries = 5, PublishAckTimeoutMs = 10000, PublishRetryDelayMs = 1000,
            Routes = queues.Select((q, i) => new MessageRoute { Feed = "contract" + i, Input = q, Output = "contract/out/" + i, DeadLetter = q + ".dlq" }).ToList()
        };
        var solace = new SolaceOptions
        {
            Host = Environment.GetEnvironmentVariable("SOLACE_CONTRACT_HOST") ?? "tcp://localhost:55555", Vpn = "default", Username = "presidio",
            PasswordEnvironmentVariable = "SOLACE_PASSWORD", ClientName = "contract-" + Guid.NewGuid().ToString("N")[..8], ValidateCertificate = false, ProvisionQueues = true
        };
        var connection = new SolaceConnection(solace, messaging, LoggerFactory.Create(b => b.AddConsole()).CreateLogger<SolaceConnection>());
        return new(connection, new SolaceMessageConsumer(connection), new SolaceMessagePublisher(connection, messaging));
    }));
}
else
{
    Console.WriteLine("SKIP Solace contract (set SOLACE_CONTRACT_TESTS=1 and SOLACE_PASSWORD with the local broker running)");
}

foreach (var (name, create) in adapters)
{
    string queue = "contract." + Guid.NewGuid().ToString("N")[..12];
    var adapter = create([queue]);
    await adapter.Connection.ConnectAsync(CancellationToken.None);
    try
    {
        var headers = new Dictionary<string, string> { ["sourceSystem"] = "res-host" };
        await adapter.Publisher.PublishAsync(Message(queue, "first", headers, "corr-1"), CancellationToken.None);
        var first = await ReadOne(adapter.Consumer, queue, complete: false);
        Require(first is not null && Encoding.UTF8.GetString(first.Payload.Span) == "first" && first.CorrelationId == "corr-1" &&
            first.Headers.GetValueOrDefault("sourceSystem") == "res-host" && first.DeliveryCount == 1 && first.StableId.Length > 0,
            name, "a published message is received with its payload, correlation ID, headers and delivery count 1");
        var again = await ReadOne(adapter.Consumer, queue, complete: true);
        Require(again is not null && again.StableId == first!.StableId && again.DeliveryCount >= 2,
            name, "an unacknowledged message is redelivered with the same stable ID and a higher delivery count");
        Require(await ReadOne(adapter.Consumer, queue, complete: false, wait: TimeSpan.FromSeconds(2)) is null,
            name, "an acknowledged message is not delivered again");
        foreach (string text in new[] { "a", "b", "c" })
        {
            await adapter.Publisher.PublishAsync(Message(queue, text), CancellationToken.None);
        }
        var order = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            order.Add(Encoding.UTF8.GetString((await ReadOne(adapter.Consumer, queue, complete: true))!.Payload.Span));
        }
        Require(order.SequenceEqual(["a", "b", "c"]), name, "messages on one queue arrive in publish order");
    }
    finally
    {
        await adapter.Connection.DisposeAsync();
    }
}
Console.WriteLine("All messaging contract checks passed.");

#endregion

#region Methods

static OutboundMessage Message(string queue, string text, Dictionary<string, string>? headers = null, string? correlation = null) =>
    new(new(queue, DestinationKind.Queue), Encoding.UTF8.GetBytes(text), Guid.NewGuid().ToString("N"), correlation, headers ?? []);

// Opens a reader, takes one message, optionally acknowledges it, and closes the reader as a restart would.
static async Task<InboundMessage?> ReadOne(IMessageConsumer consumer, string queue, bool complete, TimeSpan? wait = null)
{
    using var stop = new CancellationTokenSource(wait ?? TimeSpan.FromSeconds(15));
    try
    {
        await foreach (var message in consumer.ReadAsync(queue, stop.Token))
        {
            if (complete)
            {
                await message.CompleteAsync(stop.Token);
            }
            return message;
        }
    }
    catch (OperationCanceledException) { }
    return null;
}

static void Require(bool condition, string adapter, string label)
{
    if (!condition)
    {
        throw new Exception($"{adapter}: {label}");
    }
    Console.WriteLine($"PASS {adapter}: {label}");
}

#endregion

#region Nested Types

// The three ports an adapter provides.
sealed record AdapterUnderTest(IMessagingConnection Connection, IMessageConsumer Consumer, IMessagePublisher Publisher);

#endregion
