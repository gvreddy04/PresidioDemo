using System.Buffers.Binary;
using System.Text;
using PresidioDemo;
using PresidioDemo.Feeds;

// This codec is a regression fixture only. It is excluded from the app and is not an airline schema.
string projectRoot = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(projectRoot, "PresidioDemo.csproj")))
    projectRoot = Directory.GetParent(projectRoot)?.FullName ?? throw new Exception("Project not found.");
string testRoot = Path.GetFullPath(Path.Combine(projectRoot, ".test-runs"));
string docsRoot = Path.GetFullPath(Path.Combine(testRoot, "pipeline-" + Guid.NewGuid().ToString("N")));
if (!docsRoot.StartsWith(testRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new Exception("Invalid test workspace.");

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static async Task Wait(Func<bool> predicate)
{
    var timeout = DateTime.UtcNow.AddSeconds(20);
    while (!predicate())
    {
        if (DateTime.UtcNow >= timeout) throw new Exception("Pipeline outcome timed out.");
        await Task.Delay(50);
    }
}

static void ExpectInvalid(Action action, string label)
{
    try { action(); } catch (InvalidDataException) { Console.WriteLine("PASS " + label); return; }
    throw new Exception("Expected rejected policy: " + label);
}
static void CheckPolicies(string root, string workspace)
{
    var policies = FeedPolicyConfiguration.Load(Path.Combine(root, "src", "config", "feed-policies.xml"));
    Check(policies.Count == 5 && policies[FeedType.Seats].Get("/OrderChangeNotif/Old/Customers/*/FirstName").Identification == FieldIdentification.Known, "Feed policies are not distinct.");
    Check(policies[FeedType.PnrLinking].Get("linkedPnr").Action == FieldAction.Keep, "Linked PNR must be kept.");
    ExpectInvalid(() => policies[FeedType.Seats].Get("unconfiguredField"), "unknown fields fail closed");
    ExpectInvalid(() => new FeedPolicy(FeedType.ACI, [new("pnr", FieldIdentification.Known, FieldAction.Redact, "PNR")]), "PNR protection override rejected");
    ExpectInvalid(() => new FeedPolicy(FeedType.ACI, [new("name", FieldIdentification.Known, FieldAction.Keep, "PERSON")]), "incompatible identification and action rejected");
    ExpectInvalid(() => new FeedPolicy(FeedType.ACI, [new("notes", FieldIdentification.Analyze, FieldAction.Redact, Entities: "PERSON", Threshold: 1.1)]), "invalid detection threshold rejected");
    ExpectInvalid(() => new FeedPolicy(FeedType.ACI, [new("phone", FieldIdentification.Known, FieldAction.Mask, "PHONE_NUMBER", MaskCharacters: 0)]), "zero masking count rejected");
    ExpectInvalid(() => new FeedPolicy(FeedType.ACI, [new("name", FieldIdentification.Known, FieldAction.Redact, "PERSON"), new("NAME", FieldIdentification.None, FieldAction.Keep)]), "duplicate field policies rejected");
    Directory.CreateDirectory(workspace);
    string invalid = Path.Combine(workspace, "invalid-policies.xml");
    File.WriteAllText(invalid, "<feedPolicies><feed type=\"ACI\"><field name=\"name\" identification=\"Known\" action=\"Tokenize\" entity=\"PERSON\" /></feed></feedPolicies>");
    ExpectInvalid(() => FeedPolicyConfiguration.Load(invalid), "tokenization action rejected");
    File.WriteAllText(invalid, "<feedPolicies><feed type=\"ACI\"><field name=\"name\" identification=\"Known\" action=\"Redact\" entity=\"PERSON\" typo=\"yes\" /></feed></feedPolicies>");
    ExpectInvalid(() => FeedPolicyConfiguration.Load(invalid), "unknown XML setting rejected");
    File.WriteAllText(invalid, "<feedPolicies><feed type=\"ACI\"><field name=\"name\" identification=\"Known\" action=\"Redact\" entity=\"PERSON\" threshold=\"0.5\" /></feed></feedPolicies>");
    ExpectInvalid(() => FeedPolicyConfiguration.Load(invalid), "unused detection setting rejected");
    File.WriteAllText(invalid, "<feedPolicies><feed type=\"ACI\"><field name=\"name\" identification=\"1\" action=\"Redact\" entity=\"PERSON\" /></feed></feedPolicies>");
    ExpectInvalid(() => FeedPolicyConfiguration.Load(invalid), "numeric identification alias rejected");
    File.WriteAllText(invalid, "<!DOCTYPE feedPolicies [<!ENTITY ext SYSTEM 'file:///unknown'>]><feedPolicies>&ext;</feedPolicies>");
    try { FeedPolicyConfiguration.Load(invalid); throw new Exception("DTD was accepted"); }
    catch (System.Xml.XmlException) { Console.WriteLine("PASS configuration rejects DTD and external entities"); }
}
static async Task CheckProtection(PresidioFeedProtector protector)
{
    var policy = new FeedPolicy(FeedType.ACI, [
        new("pnr", FieldIdentification.None, FieldAction.Keep),
        new("name", FieldIdentification.Known, FieldAction.Replace, "PERSON", "[DUMMY_NAME]"),
        new("phone", FieldIdentification.Known, FieldAction.Mask, "PHONE_NUMBER", MaskCharacters: 6),
        new("passport", FieldIdentification.Known, FieldAction.Redact, "PASSPORT_NUMBER"),
        new("notes", FieldIdentification.Analyze, FieldAction.Replace, Replacement: "[CONTACT]", Entities: "EMAIL_ADDRESS", Threshold: 0.5)
    ]);
    FeedField[] fields = [new("0", "pnr", "K7QX2M"), new("1", "name", "Qzx"), new("2", "phone", "1234567890"),
        new("3", "passport", "X1234567"), new("4", "notes", "Send to emily.carter@example.com today")];
    var result = await protector.ProtectAsync(fields, policy, CancellationToken.None);
    Check(result["0"] == "K7QX2M" && result["1"] == "[DUMMY_NAME]", "Configured Keep or known-field replacement failed.");
    Check(result["2"] == "1234******" && result["3"] == "", "Mask or Redact failed.");
    Check(result["4"] == "Send to [CONTACT] today", "Selected-entity detection or replacement failed.");
    Console.WriteLine("PASS real Presidio applies configured Keep, Replace, Mask, Redact and targeted Analyze policies");
    try { await protector.ProtectAsync([new("0", "missing", "value")], policy, CancellationToken.None); throw new Exception("Unknown field accepted"); }
    catch (InvalidDataException) { Console.WriteLine("PASS missing policy retains input before any protection"); }
}
static async Task CheckScheduling(string workspace, PresidioFeedProtector protector, byte[] input, IReadOnlyDictionary<FeedType, FeedPolicy> policies)
{
    using var started = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    using var transport = new FolderFeedTransport(Path.Combine(workspace, "scheduling"));
    transport.Initialize();
    var routes = policies.ToDictionary(p => p.Key, p => p.Value);
    routes[FeedType.Seats] = new FeedPolicy(FeedType.Seats, policies[FeedType.ACI].Fields.Values);
    var router = FeedWorkflowRouter.Create(protector, routes, feed => feed == FeedType.ACI ? new BlockingTestCodec(started, release) : new TestOnlyCodec());
    using var listener = new FolderFeedListener(transport, router);
    using var cancellation = new CancellationTokenSource();
    Task running = listener.RunAsync(cancellation.Token);
    try
    {
        await File.WriteAllBytesAsync(Path.Combine(transport.InputRoot, "ACI", "slow.bin"), input);
        await Wait(() => started.IsSet);
        await File.WriteAllBytesAsync(Path.Combine(transport.InputRoot, "ACI", "second.bin"), input);
        await File.WriteAllBytesAsync(Path.Combine(transport.InputRoot, "Seats", "fast.bin"), input);
        await Wait(() => Directory.EnumerateFiles(Path.Combine(transport.OutputRoot, "Seats"), "fast.*.bin").Any());
        Check(!Directory.EnumerateFiles(Path.Combine(transport.OutputRoot, "ACI"), "slow.*.bin").Any(), "Slow feed was not blocked.");
        Check(File.Exists(Path.Combine(transport.InputRoot, "ACI", "second.bin")), "Same feed ran concurrently.");
        Console.WriteLine("PASS fast feed delivers while another feed is blocked; only one active delivery per feed");
        cancellation.Cancel();
        await Task.Delay(100);
        Check(!running.IsCompleted, "Listener returned before draining active workers.");
        release.Set();
        await running;
        Check(Directory.EnumerateFiles(Path.Combine(workspace, "scheduling", ".processing", "ACI"), "slow.bin", SearchOption.AllDirectories).Any(), "Cancelled input was discarded.");
        Check(!Directory.EnumerateFiles(Path.Combine(transport.OutputRoot, "ACI"), "slow.*.bin").Any(), "Cancelled processing was published.");
        Console.WriteLine("PASS shutdown drains active work before Python disposal and retains interrupted input");
    }
    finally { cancellation.Cancel(); release.Set(); await running; }
}

bool started = false;
try
{
    CheckPolicies(projectRoot, docsRoot);
    JsonCodecChecks.Run(projectRoot);
    XdrCodecChecks.Run(projectRoot, docsRoot);
    PythonHost.Start();
    started = true;
    var protector = new PresidioFeedProtector();
    await CheckProtection(protector);
    await XdrCodecChecks.CheckProtection(protector);
    var codec = new TestOnlyCodec();
    byte[] input = codec.CreateFixture();
    var metadata = new FeedDelivery(FeedType.ACI, Guid.NewGuid(), "fixture.bin", "test-only");
    var parsed = codec.Parse(new(metadata, input));
    byte[] unchanged = codec.Serialize(parsed, parsed.Fields.ToDictionary(field => field.Id, field => field.Value));
    Check(input.AsSpan().SequenceEqual(unchanged), "Unchanged fixture failed its exact-byte round trip.");
    Console.WriteLine("PASS test-only binary fixture round trips without edits");

    using var transport = new FolderFeedTransport(docsRoot);
    transport.Initialize();
    var policies = FeedPolicyConfiguration.Load(Path.Combine(projectRoot, "src", "config", "feed-policies.xml"));
    await CheckScheduling(docsRoot, protector, input, policies);
    var router = FeedWorkflowRouter.Create(protector, policies, _ => new TestOnlyCodec());
    using var listener = new FolderFeedListener(transport, router);
    using var cancellation = new CancellationTokenSource();
    Task running = listener.RunAsync(cancellation.Token);
    try
    {
        string inbox = Path.Combine(transport.InputRoot, "ACI", "fixture.bin");
        await File.WriteAllBytesAsync(inbox, input);
        string outputDirectory = Path.Combine(transport.OutputRoot, "ACI");
        await Wait(() => Directory.EnumerateFiles(outputDirectory, "fixture.*.bin").Any() && !File.Exists(inbox) &&
            !Directory.EnumerateFiles(Path.Combine(docsRoot, ".processing", "ACI"), "fixture.bin", SearchOption.AllDirectories).Any());
        byte[] output = await File.ReadAllBytesAsync(Directory.EnumerateFiles(outputDirectory, "fixture.*.bin").Single());
        var rebuilt = codec.Parse(new(metadata, output));
        var values = rebuilt.Fields.Select(field => field.Value).ToArray();
        Check(values[0] == "K7QX2M", "PNR changed.");
        Check(values[1] == "[PASSENGER_NAME]" && values[4] == "[PASSENGER_NAME]", "Names not anonymized.");
        Check(values[2] == "[EMAIL_ADDRESS]" && values[5] == "[EMAIL_ADDRESS]", "Emails not anonymized.");
        Check(values[3] == "[PASSENGER_ID]" && values[6] == "[PASSENGER_ID]", "Passenger IDs were not anonymized.");
        Check(!Encoding.UTF8.GetString(output).Contains("PAX-TOKEN-"), "Unexpected tokenization.");
        Check(!Directory.Exists(Path.Combine(docsRoot, ".demo-token-vault")), "Unexpected token vault.");
        Console.WriteLine("PASS actual embedded Presidio anonymizes two passengers before binary rebuilding and delivery");
        Console.WriteLine("PASS successful output delivery deletes the working input");

        byte[] unknown = (byte[])input.Clone();
        "RULE"u8.CopyTo(unknown);
        await File.WriteAllBytesAsync(Path.Combine(transport.InputRoot, "ACI", "unknown.bin"), unknown);
        await Wait(() => Directory.EnumerateFiles(Path.Combine(transport.FailedRoot, "ACI"), "unknown.*.error.txt").Any());
        Check(Directory.EnumerateFiles(Path.Combine(docsRoot, ".processing", "ACI"), "unknown.bin", SearchOption.AllDirectories).Any(), "Unknown-field input was deleted.");
        Check(!Directory.EnumerateFiles(outputDirectory, "unknown.*").Any(), "Unknown-field input was published.");
        Console.WriteLine("PASS unconfigured field rejects entire delivery without output or input deletion");

        string malformed = Path.Combine(transport.InputRoot, "ACI", "trailing.bin");
        byte[] extra = [.. input, 0x00];
        await File.WriteAllBytesAsync(malformed, extra);
        string failureDirectory = Path.Combine(transport.FailedRoot, "ACI");
        await Wait(() => Directory.EnumerateFiles(failureDirectory, "trailing.*.error.txt").Any());
        Check(Directory.EnumerateFiles(Path.Combine(docsRoot, ".processing", "ACI"), "trailing.bin", SearchOption.AllDirectories).Any(), "Failed input was deleted.");
        Check(!Directory.EnumerateFiles(outputDirectory, "trailing.*").Any(), "Partial parse was published.");
        Console.WriteLine("PASS entire-file parsing rejects trailing bytes and retains the failed input");
    }
    finally { cancellation.Cancel(); await running; }
}
finally
{
    if (started) PythonHost.Stop();
    // Delete only the explicitly verified, newly-created test workspace.
    if (Directory.Exists(docsRoot) && Path.GetFullPath(docsRoot).StartsWith(testRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        Directory.Delete(docsRoot, recursive: true);
}

sealed class TestOnlyCodec : IFeedCodec
{
    private static readonly string[] Names = ["pnr", "name", "email", "sourcePassengerId", "name", "email", "sourcePassengerId"];
    public byte[] CreateFixture() => Write(["K7QX2M", "Emily Carter", "emily.carter@example.com", "PAX-1001", "Michael Brooks", "michael.brooks@example.com", "PAX-1002"]);
    public ParsedFeed Parse(FeedMessage message)
    {
        byte[] bytes = FeedPayloadReader.ReadPayload(message);
        if (bytes.Length < 4 || !(bytes.AsSpan(0, 4).SequenceEqual("TEST"u8) || bytes.AsSpan(0, 4).SequenceEqual("RULE"u8))) throw new InvalidDataException("Invalid test-only fixture.");
        int offset = 4;
        List<FeedField> fields = [];
        for (int i = 0; i < Names.Length; i++)
        {
            if (bytes.Length - offset < 4) throw new InvalidDataException("Incomplete test-only fixture.");
            int length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
            offset += 4;
            if (length < 0 || length > bytes.Length - offset) throw new InvalidDataException("Invalid test-only length.");
            string value = new UTF8Encoding(false, true).GetString(bytes.AsSpan(offset, length));
            offset += length;
            fields.Add(new(i.ToString(), i == 1 && bytes.AsSpan(0, 4).SequenceEqual("RULE"u8) ? "unconfiguredField" : Names[i], value));
        }
        if (offset != bytes.Length) throw new InvalidDataException("Test-only fixture has trailing bytes.");
        return new(fields.AsReadOnly(), "TEST");
    }
    public byte[] Serialize(ParsedFeed original, IReadOnlyDictionary<string, string> fields) =>
        Write(original.Fields.Select(field => fields[field.Id]).ToArray());
    public void Validate(ParsedFeed original, ReadOnlyMemory<byte> rebuilt)
    {
        var delivery = new FeedDelivery(FeedType.ACI, Guid.Empty, "fixture.bin", "test-only");
        var check = Parse(new(delivery, rebuilt));
        if (check.Fields.Count != original.Fields.Count || check.Fields[0].Value != original.Fields[0].Value)
            throw new InvalidDataException("Invalid test-only rebuilt structure or PNR.");
    }
    private static byte[] Write(string[] values)
    {
        using var stream = new MemoryStream();
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
sealed class BlockingTestCodec(ManualResetEventSlim started, ManualResetEventSlim release) : IFeedCodec
{
    private readonly TestOnlyCodec _inner = new();
    public ParsedFeed Parse(FeedMessage message) { started.Set(); release.Wait(); return _inner.Parse(message); }
    public byte[] Serialize(ParsedFeed original, IReadOnlyDictionary<string, string> fields) => _inner.Serialize(original, fields);
    public void Validate(ParsedFeed original, ReadOnlyMemory<byte> rebuilt) => _inner.Validate(original, rebuilt);
}
