using System.Security.Cryptography;
using System.Text.Json;
using PresidioDemo.Feeds.ACI;
using PresidioDemo.Feeds.Shared;
using PresidioDemo.Pipeline;
using PresidioDemo.Protection;

/// <summary>
/// Runs all-keep workflow cycles and records byte-exact verification evidence.
/// </summary>
public static class NoAnonymizationRoundTripChecks
{

    #region Fields and Constants

    /// <summary>
    /// The number of all-keep parse-and-rebuild cycles executed for each round-trip fixture.
    /// </summary>
    private const int CycleCount = 3;

    #endregion

    #region Methods

    /// <summary>
    /// Runs three all-keep cycles per fixture and records exact-byte and malformed-input results.
    /// </summary>
    /// <param name="root">The project root containing source configuration and immutable samples.</param>
    /// <param name="outputDirectory">The isolated directory receiving verification reports and generated fixture output.</param>
    /// <returns>A task that completes when the operation and required cleanup have finished.</returns>
    /// <exception cref="Exception">A regression assertion fails or an expected rejection does not occur.</exception>
    public static async Task RunAsync(string root, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        string reportPath = Path.Combine(outputDirectory, "verification.json");
        var schemas = XdrLayoutConfiguration.Load(Path.Combine(root, "src/PresidioDemo.Core/Configuration/xdr-layouts.xml"));
        var references = ReferenceLayoutConfiguration.Load(Path.Combine(root, "src/PresidioDemo.Core/Configuration/reference-layouts.xml"), schemas);
        var configured = FeedPolicyConfiguration.Load(Path.Combine(root, "src/PresidioDemo.Core/Configuration/feed-policies.xml"));
        // All-Keep exists only in this test assembly; source configuration files are never edited.
        var keepPolicies = configured.ToDictionary(pair => pair.Key, pair => new FeedPolicy(pair.Key,
            pair.Value.Fields.Values.Select(rule => new FieldPolicy(rule.Name, FieldIdentification.None, FieldAction.Keep))));
        IFeedCodec Codec(FeedType feed) => FeedCodecRegistry.Create(feed, schemas, references);
        var router = FeedWorkflowRouter.Create(new PresidioFeedProtector(), keepPolicies, Codec);
        var sourceFiles = Directory.GetFiles(Path.Combine(root, "docs/sample-docs"), "*.txt", SearchOption.AllDirectories)
            .ToDictionary(file => file, File.ReadAllBytes);
        var examples = RoundTripExamples.Create(root, Codec);
        var results = new List<object>();
        var rejected = new List<object>();
        var console = Console.Out;
        using (var log = new StreamWriter(Path.Combine(outputDirectory, "execution.log")))
        {
            try
            {
                Console.SetOut(log);
                foreach (var example in examples)
                {
                    var codec = Codec(example.Feed);
                    if (example.Feed == FeedType.ACI && codec is not AciFeedCodec)
                    {
                        throw new Exception("Default ACI round trips must exercise the dedicated ACI codec.");
                    }
                    if ((example.Feed is FeedType.PnrLinking or FeedType.Seats) && codec is not JsonFeedCodec)
                    {
                        throw new Exception("Default JSON round trips must exercise the shared System.Text.Json codec.");
                    }
                    byte[] expected = example.Bytes.ToArray();
                    var firstMessage = RoundTripExamples.Message(example.Feed, "case" + example.Extension, example.Bytes);
                    byte[] expectedPayload = FeedPayloadReader.ReadPayload(firstMessage);
                    int fieldCount = 0;
                    byte[] current = expected.ToArray();
                    for (int cycle = 0; cycle < CycleCount; cycle++)
                    {
                        var message = firstMessage with { Content = current };
                        var parsed = codec.Parse(message);
                        if (parsed.Fields.Count == 0)
                        {
                            throw new Exception("Round-trip case did not parse any fields: " + example.Name);
                        }
                        fieldCount = parsed.Fields.Count;
                        var unchanged = parsed.Fields.ToDictionary(field => field.Id, field => field.Value);
                        byte[] direct = codec.Serialize(parsed, unchanged);
                        codec.Validate(parsed, direct);
                        Equal(expected, direct, example.Name + ": direct codec");
                        var result = await router.Get(example.Feed).ProcessAsync(message, CancellationToken.None);
                        Equal(expected, result.Content.Span, example.Name + ": complete workflow");
                        var afterMessage = message with { Content = result.Content };
                        Equal(expectedPayload, FeedPayloadReader.ReadPayload(afterMessage), example.Name + ": decoded payload");
                        EqualFields(parsed.Fields, codec.Parse(afterMessage).Fields, example.Name);
                        if (result.OriginalFields is null || result.ProtectedFields is null)
                        {
                            throw new Exception("Workflow field evidence is missing: " + example.Name);
                        }
                        EqualFields(result.OriginalFields, result.ProtectedFields, example.Name + ": anonymization disabled");
                        if (example.Feed == FeedType.ACI)
                        {
                            // Full independent-of-envelope rebuild, so MQ Original-byte retention cannot hide an encoder error.
                            Equal(expectedPayload, new AciEncoder().Encode(new AciDecoder().Decode(expectedPayload)), example.Name + ": ACI full rebuild");
                        }
                        Equal(expected, example.Bytes, example.Name + ": input buffer unchanged");
                        Equal(expected, message.Content.Span, example.Name + ": parsed input buffer unchanged");
                        current = result.Content.ToArray();
                    }
                    results.Add(new
                    {
                        Feed = FeedCatalog.Get(example.Feed).FolderName,
                        example.Name,
                        example.SourceSample,
                        example.Category,
                        Format = example.Extension == ".bin" ? "Raw payload" : "MQ text container",
                        Codec = codec.GetType().Name,
                        InputBytes = expected.Length,
                        OutputBytes = current.Length,
                        InputSha256 = Hash(expected),
                        OutputSha256 = Hash(current),
                        PayloadSha256 = Hash(expectedPayload),
                        ParsedFields = fieldCount,
                        Cycles = CycleCount,
                        DirectCodecExact = true,
                        WorkflowExact = true,
                        PayloadExact = true,
                        FieldsUnchanged = true,
                        InputBufferUnchanged = true
                    });
                }
                foreach (var feed in FeedCatalog.All)
                {
                    var baseline = examples.First(example => example.Feed == feed.Type && example.Category == "Supplied raw payload");
                    byte[] payload = baseline.Bytes;
                    if (feed.Type is FeedType.Seats or FeedType.PnrLinking)
                    {
                        payload = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(payload).TrimEnd());
                    }
                    foreach (var invalid in new[] { (Name: "truncated-payload", Bytes: payload[..^1]),
                        (Name: "trailing-garbage", Bytes: payload.Concat(new byte[] { 0x7F }).ToArray()) })
                    {
                        bool wasRejected = false;
                        try
                        {
                            _ = await router.Get(feed.Type).ProcessAsync(
                                RoundTripExamples.Message(feed.Type, "invalid.bin", invalid.Bytes), CancellationToken.None);
                        }
                        catch (InvalidDataException)
                        {
                            wasRejected = true;
                        }
                        if (!wasRejected)
                        {
                            throw new Exception($"Invalid input was not rejected: {feed.FolderName}/{invalid.Name}");
                        }
                        rejected.Add(new { Feed = feed.FolderName, invalid.Name, Rejected = true });
                    }
                }
            }
            finally
            {
                Console.SetOut(console);
            }
        }
        foreach (var (file, original) in sourceFiles)
        {
            Equal(original, File.ReadAllBytes(file), "Source file preserved: " + Path.GetFileName(file));
        }
        var counts = FeedCatalog.All.Select(feed => new
        {
            Feed = feed.FolderName,
            Examples = examples.Count(example => example.Feed == feed.Type),
            RawExamples = examples.Count(example => example.Feed == feed.Type && example.Extension == ".bin"),
            ContainerExamples = examples.Count(example => example.Feed == feed.Type && example.Extension == ".txt")
        }).ToArray();
        var report = new
        {
            AllPassed = true,
            Mode = "No anonymization: in-memory All-Keep policies through production codecs and workflows",
            GuaranteeScope = "Supported, valid examples tested; malformed and unsupported inputs are rejected, not passed through",
            PositiveCases = results.Count,
            CyclesPerCase = CycleCount,
            MalformedCasesRejected = rejected.Count,
            SourceFilesUnchanged = sourceFiles.Count,
            InitializesPython = false,
            FeedSummary = counts,
            Cases = results,
            Rejections = rejected
        };
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        foreach (var count in counts)
        {
            Console.WriteLine($"PASS {count.Feed,-12} {count.Examples,3} examples ({count.RawExamples} raw, {count.ContainerExamples} containers) x {CycleCount} cycles");
        }
        Console.WriteLine($"PASS {results.Count} byte-exact cases; {rejected.Count} malformed inputs rejected; {sourceFiles.Count} source files unchanged");
        Console.WriteLine("Evidence: " + reportPath);
    }

    /// <summary>
    /// Asserts that complete expected and actual byte sequences are identical.
    /// </summary>
    /// <param name="expected">The original bytes, parsed fields or expected regression result.</param>
    /// <param name="actual">The actual output bytes, parsed fields or observed result.</param>
    /// <param name="label">The readable name identifying this fixture or assertion.</param>
    /// <exception cref="Exception">A regression assertion fails or an expected rejection does not occur.</exception>
    private static void Equal(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string label)
    {
        if (!expected.SequenceEqual(actual))
        {
            int offset = 0;
            while (offset < Math.Min(expected.Length, actual.Length) && expected[offset] == actual[offset])
            {
                offset++;
            }
            throw new Exception($"Byte mismatch: {label}; first difference {offset}; input length {expected.Length}, output length {actual.Length}.");
        }
    }

    /// <summary>
    /// Asserts that parsed field identities, policy paths and values remain unchanged.
    /// </summary>
    /// <param name="before">The original message or decoded layout retained before protection.</param>
    /// <param name="after">The reconstructed message or decoded layout being validated.</param>
    /// <param name="label">The readable name identifying this fixture or assertion.</param>
    /// <exception cref="Exception">A regression assertion fails or an expected rejection does not occur.</exception>
    private static void EqualFields(IReadOnlyList<FeedField> before, IReadOnlyList<FeedField> after, string label)
    {
        if (!before.SequenceEqual(after))
        {
            throw new Exception("Decoded field IDs, paths or values changed: " + label);
        }
    }

    /// <summary>
    /// Computes the SHA-256 digest used to identify preserved fixture bytes.
    /// </summary>
    /// <param name="bytes">The complete payload or fixture bytes to read, encode or compare.</param>
    /// <returns>The SHA-256 digest of the complete input bytes.</returns>
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    #endregion
}
