using System.Buffers.Binary;
using System.Text;
using PresidioDemo.Feeds.Shared;
using PresidioDemo.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using PresidioDemo.Messaging;
using PresidioDemo.Messaging.Testing;
using PresidioDemo.Protection;
using PresidioDemo.Worker;

#region Execution

// This codec is a regression fixture only. It is excluded from the app and is not an airline schema.
string projectRoot = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(projectRoot, "PresidioDemo.slnx")))
{
    projectRoot = Directory.GetParent(projectRoot)?.FullName ?? throw new Exception("Project not found.");
}

string testRoot = Path.GetFullPath(Path.Combine(projectRoot, ".test-runs"));
string docsRoot = Path.GetFullPath(Path.Combine(testRoot, "pipeline-" + Guid.NewGuid().ToString("N")));
if (!docsRoot.StartsWith(testRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
{
    throw new Exception("Invalid test workspace.");
}

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}
static async Task Wait(Func<bool> predicate)
{
    var timeout = DateTime.UtcNow.AddSeconds(20);
    while (!predicate())
    {
        if (DateTime.UtcNow >= timeout)
        {
            throw new Exception("Pipeline outcome timed out.");
        }

        await Task.Delay(50);
    }
}

static MessagingOptions TestMessaging() => new()
{
    Transport = "InMemory",
    MaxDeliveries = 5,
    PublishAckTimeoutMs = 1000,
    PublishRetryDelayMs = 50,
    Routes = FeedCatalog.All.Select(f => new MessageRoute { Feed = f.FolderName, Input = "in." + f.FolderName, Output = "out/" + f.FolderName, DeadLetter = "dlq." + f.FolderName }).ToList()
};
static FeedWorker TestWorker(FeedType feed, MessagingOptions options, InMemoryMessageBroker broker, FeedWorkflowRouter router) =>
    new(feed, options.Routes.Single(r => r.Feed == FeedCatalog.Get(feed).FolderName), options, broker, broker, new FeedMessageProcessor(router), NullLogger.Instance);

static async Task CheckScheduling(IFeedProtector protector, byte[] input)
{
    using (var started = new ManualResetEventSlim())
    {
        using (var release = new ManualResetEventSlim())
        {
            var router = FeedWorkflowRouter.Create(protector, feed => feed == FeedType.ACI ? new BlockingTestCodec(started, release) : new TestOnlyCodec());
            var broker = new InMemoryMessageBroker();
            var options = TestMessaging();
            using (var cancellation = new CancellationTokenSource())
            {
                Task aci = TestWorker(FeedType.ACI, options, broker, router).RunAsync(cancellation.Token);
                Task seats = TestWorker(FeedType.Seats, options, broker, router).RunAsync(cancellation.Token);
                try
                {
                    broker.Send("in.ACI", input);
                    await Wait(() => started.IsSet);
                    broker.Send("in.ACI", input);
                    broker.Send("in.Seats", input);
                    await Wait(() => broker.PublishedTo("out/Seats").Count == 1);
                    Check(broker.PublishedTo("out/ACI").Count == 0, "Slow feed was not blocked.");
                    Check(broker.Pending("in.ACI") == 2, "Same feed ran concurrently.");
                    Console.WriteLine("PASS fast feed delivers while another feed is blocked; only one active message per feed");
                    cancellation.Cancel();
                    await Task.Delay(100);
                    Check(!aci.IsCompleted, "Worker returned before draining active work.");
                    release.Set();
                    await Task.WhenAll(aci, seats);
                    Check(broker.Pending("in.ACI") == 2 && broker.PublishedTo("out/ACI").Count == 0, "Interrupted message was acknowledged or published.");
                    Console.WriteLine("PASS shutdown drains active work before Python disposal and leaves interrupted input unacknowledged for redelivery");
                }
                finally { cancellation.Cancel(); release.Set(); await Task.WhenAll(aci, seats); }
            }
        }
    }
}

bool started = false;
try
{
    FeedTransactionLogChecks.Run();
    JsonCodecChecks.Run(projectRoot);
    await NoAnonymizationRoundTripChecks.RunAsync(projectRoot, Path.Combine(docsRoot, "round-trip-checks"));
    PythonHost.Start();
    started = true;
    await DetectingProtectorChecks.RunAsync(projectRoot);
    // Worker delivery checks use a fixture protector, so they test scheduling, acknowledgment and dead-lettering only.
    var protector = new FixtureProtector();
    var codec = new TestOnlyCodec();
    byte[] input = codec.CreateFixture();
    var metadata = new FeedDelivery(FeedType.ACI, Guid.NewGuid(), "fixture.bin", "test-only");
    var parsed = codec.Parse(new(metadata, input));
    byte[] unchanged = codec.Serialize(parsed, parsed.Fields.ToDictionary(field => field.Id, field => field.Value));
    Check(input.AsSpan().SequenceEqual(unchanged), "Unchanged fixture failed its exact-byte round trip.");
    Console.WriteLine("PASS test-only binary fixture round trips without edits");

    await CheckScheduling(protector, input);
    var router = FeedWorkflowRouter.Create(protector, _ => new TestOnlyCodec());
    var broker = new InMemoryMessageBroker();
    var options = TestMessaging();
    using (var cancellation = new CancellationTokenSource())
    {
        Task running = TestWorker(FeedType.ACI, options, broker, router).RunAsync(cancellation.Token);
        try
        {
            broker.Send("in.ACI", input);
            await Wait(() => broker.PublishedTo("out/ACI").Count == 1 && broker.Pending("in.ACI") == 0);
            byte[] output = broker.PublishedTo("out/ACI").Single().Payload.ToArray();
            var rebuilt = codec.Parse(new(metadata, output));
            var values = rebuilt.Fields.Select(field => field.Value).ToArray();
            Check(values[0] == "K7QX2M", "PNR changed.");
            Check(values[1] == "[PASSENGER_NAME]" && values[4] == "[PASSENGER_NAME]", "Names not anonymized.");
            Check(values[2] == "[EMAIL_ADDRESS]" && values[5] == "[EMAIL_ADDRESS]", "Emails not anonymized.");
            Check(values[3] == "[PASSENGER_ID]" && values[6] == "[PASSENGER_ID]", "Passenger IDs were not anonymized.");
            Check(!Encoding.UTF8.GetString(output).Contains("PAX-TOKEN-"), "Unexpected tokenization.");
            Check(!Directory.Exists(Path.Combine(docsRoot, ".demo-token-vault")), "Unexpected token vault.");
            Console.WriteLine("PASS the workflow protects two passengers before binary rebuilding and delivery");
            Console.WriteLine("PASS the input is acknowledged only after the protected output is confirmed");

            byte[] unknown = (byte[])input.Clone();
            "RULE"u8.CopyTo(unknown);
            broker.Send("in.ACI", unknown);
            await Wait(() => broker.PublishedTo("dlq.ACI").Count == 1 && broker.Pending("in.ACI") == 0);
            var rejected = broker.PublishedTo("dlq.ACI").Single();
            Check(rejected.Payload.Span.SequenceEqual(unknown) && rejected.Headers.ContainsKey(MessageHeaders.RejectReason), "Rejected input was not preserved in the dead-letter queue.");
            Check(broker.PublishedTo("out/ACI").Count == 1, "Unknown-field input was published.");
            Console.WriteLine("PASS a protection failure rejects the entire message to the dead-letter queue without output");

            byte[] extra = [.. input, 0x00];
            broker.Send("in.ACI", extra);
            await Wait(() => broker.PublishedTo("dlq.ACI").Count == 2 && broker.Pending("in.ACI") == 0);
            Check(broker.PublishedTo("dlq.ACI")[1].Payload.Span.SequenceEqual(extra), "Failed input was not preserved in the dead-letter queue.");
            Check(broker.PublishedTo("out/ACI").Count == 1, "Partial parse was published.");
            Console.WriteLine("PASS entire-message parsing rejects trailing bytes and keeps the failed input");
        }
        finally { cancellation.Cancel(); await running; }
    }
}
finally
{
    if (started)
    {
        PythonHost.Stop();
    }
    // Delete only the explicitly verified, newly-created test workspace.
    if (Directory.Exists(docsRoot) && Path.GetFullPath(docsRoot).StartsWith(testRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    {
        Directory.Delete(docsRoot, recursive: true);
    }
}

#endregion

#region Classes

/// <summary>
/// A test-only protector for the fixture codec: replaces names, e-mails and passenger IDs, keeps the PNR, and fails
/// closed on any other field, so worker delivery checks do not depend on detection.
/// </summary>
sealed class FixtureProtector : IFeedProtector
{
    /// <summary>
    /// Protects the fixture fields by name.
    /// </summary>
    /// <param name="feed">The feed route.</param>
    /// <param name="parsed">The fixture parse.</param>
    /// <param name="cancellationToken">The token that interrupts processing.</param>
    /// <returns>The protected values and no events.</returns>
    /// <exception cref="InvalidDataException">A field other than the fixture's four is present.</exception>
    public ValueTask<DetectionOutcome> ProtectAsync(FeedType feed, ParsedFeed parsed, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new DetectionOutcome(parsed.Fields.ToDictionary(field => field.Id, field => field.Name switch
        {
            "pnr" => field.Value,
            "name" => "[PASSENGER_NAME]",
            "email" => "[EMAIL_ADDRESS]",
            "sourcePassengerId" => "[PASSENGER_ID]",
            _ => throw new InvalidDataException("The fixture protector has no rule for this field.")
        }), []));
}


/// <summary>
/// Provides an isolated regression-test format rather than an airline producer schema.
/// </summary>
sealed class TestOnlyCodec : IFeedCodec
{

    #region Fields and Constants

    /// <summary>
    /// The ordered field names in the isolated regression-test format.
    /// </summary>
    private static readonly string[] Names = ["pnr", "name", "email", "sourcePassengerId", "name", "email", "sourcePassengerId"];

    #endregion

    #region Methods

    /// <summary>
    /// Builds a complete payload in the isolated regression-test format.
    /// </summary>
    /// <returns>The complete independently encoded regression-fixture payload.</returns>
    public byte[] CreateFixture() => Write(["K7QX2M", "Emily Carter", "emily.carter@example.com", "PAX-1001", "Michael Brooks", "michael.brooks@example.com", "PAX-1002"]);

    /// <summary>
    /// Parses the complete input, validates its supported shape and retains the layout required for reconstruction.
    /// </summary>
    /// <param name="message">The complete input delivery and its retained message bytes.</param>
    /// <returns>The decoded document or parsed fields and retained reconstruction layout.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    public ParsedFeed Parse(FeedMessage message)
    {
        byte[] bytes = FeedPayloadReader.ReadPayload(message);
        if (bytes.Length < 4 || !(bytes.AsSpan(0, 4).SequenceEqual("TEST"u8) || bytes.AsSpan(0, 4).SequenceEqual("RULE"u8)))
        {
            throw new InvalidDataException("Invalid test-only fixture.");
        }

        int offset = 4;
        List<FeedField> fields = [];
        for (int i = 0; i < Names.Length; i++)
        {
            if (bytes.Length - offset < 4)
            {
                throw new InvalidDataException("Incomplete test-only fixture.");
            }

            int length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
            offset += 4;
            if (length < 0 || length > bytes.Length - offset)
            {
                throw new InvalidDataException("Invalid test-only length.");
            }

            string value = new UTF8Encoding(false, true).GetString(bytes.AsSpan(offset, length));
            offset += length;
            fields.Add(new(i.ToString(), i == 1 && bytes.AsSpan(0, 4).SequenceEqual("RULE"u8) ? "unconfiguredField" : Names[i], value));
        }
        if (offset != bytes.Length)
        {
            throw new InvalidDataException("Test-only fixture has trailing bytes.");
        }

        return new(fields.AsReadOnly(), "TEST");
    }

    /// <summary>
    /// Reconstructs the original message representation using the complete protected field set.
    /// </summary>
    /// <param name="original">The complete extracted field set and its retained codec-specific serialization layout.</param>
    /// <param name="fields">The complete ordered parsed field set or reviewed field definitions.</param>
    /// <returns>The complete reconstructed message in its original transport representation.</returns>
    public byte[] Serialize(ParsedFeed original, IReadOnlyDictionary<string, string> fields) =>
            Write(original.Fields.Select(field => fields[field.Id]).ToArray());

    /// <summary>
    /// Rejects reconstructed data or settings that violate the preserved structure and protection contract.
    /// </summary>
    /// <param name="original">The complete extracted field set and its retained codec-specific serialization layout.</param>
    /// <param name="rebuilt">The complete reconstructed message bytes to validate.</param>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    public void Validate(ParsedFeed original, ReadOnlyMemory<byte> rebuilt)
    {
        var delivery = new FeedDelivery(FeedType.ACI, Guid.Empty, "fixture.bin", "test-only");
        var check = Parse(new(delivery, rebuilt));
        if (check.Fields.Count != original.Fields.Count || check.Fields[0].Value != original.Fields[0].Value)
        {
            throw new InvalidDataException("Invalid test-only rebuilt structure or PNR.");
        }
    }

    /// <summary>
    /// Builds a complete payload in the isolated regression-test format.
    /// </summary>
    /// <param name="values">The ordered original or protected fixture values.</param>
    /// <returns>The complete independently encoded regression-fixture payload.</returns>
    private static byte[] Write(string[] values)
    {
        using (var stream = new MemoryStream())
        {
            stream.Write("TEST"u8);
            Span<byte> length = stackalloc byte[4];
            foreach (string value in values)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
                stream.Write(length);
                stream.Write(bytes);
            }
            return stream.ToArray();
        }
    }

    #endregion
}

/// <summary>
/// Pauses a fixture workflow to verify independent scheduling across feed routes.
/// </summary>
/// <param name="started">The synchronization signal indicating that the paused regression workflow has begun.</param>
/// <param name="release">The synchronization gate that releases a paused regression workflow.</param>
sealed class BlockingTestCodec(ManualResetEventSlim started, ManualResetEventSlim release) : IFeedCodec
{

    #region Fields and Constants

    /// <summary>
    /// The regression fixture codec invoked after the scheduling gate is released.
    /// </summary>
    private readonly TestOnlyCodec _inner = new();

    #endregion

    #region Methods

    /// <summary>
    /// Parses the complete input, validates its supported shape and retains the layout required for reconstruction.
    /// </summary>
    /// <param name="message">The complete input delivery and its retained message bytes.</param>
    /// <returns>The decoded document or parsed fields and retained reconstruction layout.</returns>
    public ParsedFeed Parse(FeedMessage message) { started.Set(); release.Wait(); return _inner.Parse(message); }

    /// <summary>
    /// Reconstructs the original message representation using the complete protected field set.
    /// </summary>
    /// <param name="original">The complete extracted field set and its retained codec-specific serialization layout.</param>
    /// <param name="fields">The complete ordered parsed field set or reviewed field definitions.</param>
    /// <returns>The complete reconstructed message in its original transport representation.</returns>
    public byte[] Serialize(ParsedFeed original, IReadOnlyDictionary<string, string> fields) => _inner.Serialize(original, fields);

    /// <summary>
    /// Rejects reconstructed data or settings that violate the preserved structure and protection contract.
    /// </summary>
    /// <param name="original">The complete extracted field set and its retained codec-specific serialization layout.</param>
    /// <param name="rebuilt">The complete reconstructed message bytes to validate.</param>
    public void Validate(ParsedFeed original, ReadOnlyMemory<byte> rebuilt) => _inner.Validate(original, rebuilt);

    #endregion
}

#endregion
