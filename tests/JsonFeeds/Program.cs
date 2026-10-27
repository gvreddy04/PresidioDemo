using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PresidioDemo.Feeds.PNRLinking;
using PresidioDemo.Feeds.Seats;
using PresidioDemo.Feeds.Shared;
using PresidioDemo.Pipeline;
using PresidioDemo.Protection;

#region Execution

string project = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(project, "PresidioDemo.csproj")))
{
    project = Directory.GetParent(project)?.FullName ?? throw new Exception("Project root not found.");
}
string[] outputs = args.Length == 2 ? args.Select(Path.GetFullPath).ToArray() :
    [Path.Combine(project, ".test-runs/json-feeds/pnr-linking"), Path.Combine(project, ".test-runs/json-feeds/seats")];
var policies = FeedPolicyConfiguration.Load(Path.Combine(project, "src/Configuration/feed-policies.xml"));
var options = new JsonSerializerOptions { WriteIndented = true };
int checks = 0;
foreach (FeedType feed in new[] { FeedType.PnrLinking, FeedType.Seats })
{
    bool linking = feed == FeedType.PnrLinking;
    string output = outputs[linking ? 0 : 1];
    Directory.CreateDirectory(output);
    var codec = FeedCodecRegistry.Create(feed);
    Check(codec is JsonFeedCodec, "standard registry selects the shared System.Text.Json codec");
    Check(linking ? codec is PnrLinkingFeedCodec : codec is SeatsFeedCodec,
        "each feed keeps its own codec and JSON root contract");
    Check(FeedCodecRegistry.Create(feed, new Dictionary<FeedType, XdrFeedSchema>()).GetType() == codec.GetType(),
        "schema registry overload selects the shared JSON codec");
    var results = new List<object>();
    string name = linking ? "PNR_LINKING.txt" : "SEATS.txt";
    foreach (var sample in new[]
    {
        (Name: name, Path: Path.Combine(project, "docs/sample-docs", name), Expected: linking ? 76 : 59),
        (Name: "two-passengers.txt", Path: Path.Combine(project, "tests/unit-tests/fixtures",
            linking ? "pnr-linking-mq-export.txt" : "seats-mq-export.txt"), Expected: 0)
    })
    {
        byte[] original = File.ReadAllBytes(sample.Path);
        var message = Message(feed, original, sample.Name);
        var envelope = FeedPayloadReader.Read(message);
        var parsed = codec.Parse(message);
        var independent = Independent(envelope.Payload);
        Check(parsed.Fields.SequenceEqual(independent), "independent JSON reader agrees on every scalar, path and value");
        Check(sample.Expected == 0 || parsed.Fields.Count == sample.Expected, "supplied scalar count retained");
        var unchanged = parsed.Fields.ToDictionary(field => field.Id, field => field.Value);
        Check(codec.Serialize(parsed, unchanged).SequenceEqual(original), "exact MQ export round trip");
        byte[] raw = envelope.Payload;
        var rawParsed = codec.Parse(Message(feed, raw));
        Check(codec.Serialize(rawParsed, unchanged).SequenceEqual(raw), "exact raw JSON round trip");
        using (var document = JsonDocument.Parse(raw))
        {
            object report = new
            {
                Feed = FeedCatalog.Get(feed).FolderName,
                Format = "UTF-8 JSON",
                PayloadBytes = raw.Length,
                Payload = document.RootElement.Clone(),
                Fields = parsed.Fields
            };
            if (linking)
            {
                Check(document.RootElement.GetArrayLength() >= 1 &&
                    document.RootElement[0].GetProperty("Customers").GetArrayLength() == 2,
                    "link records retain both customers in the original JSON structure");
            }
            else
            {
                var customers = document.RootElement.GetProperty("OrderChangeNotif").GetProperty("Old").GetProperty("Customers");
                Check(customers.GetArrayLength() >= 1 && customers[0].GetProperty("ServiceItems").GetArrayLength() >= 1,
                    "notification, customer and service-item locations remain associated");
            }
            var plan = ConfiguredPiiIdentifier.CreatePlan(parsed.Fields, policies[feed]);
            var replacements = plan.ToDictionary(item => item.Field.Id, item =>
                item.Rule.Action == FieldAction.Keep || item.Field.Value.Length == 0 ? item.Field.Value : item.Rule.Replacement);
            byte[] protectedBytes = codec.Serialize(parsed, replacements);
            codec.Validate(parsed, protectedBytes);
            var afterEnvelope = FeedPayloadReader.Read(message with { Content = protectedBytes });
            var independentAfter = Independent(afterEnvelope.Payload);
            Check(independentAfter.Count == independent.Count && independentAfter.All(field => field.Value == replacements[field.Id]),
                "independent reader confirms all configured replacements and kept values");
            Check(independent.Where(field => FieldPolicy.IsPnr(field.Name)).All(before =>
                independentAfter.Single(after => after.Id == before.Id).Value == before.Value), "all PNRs remain unchanged");
            Check(sample.Expected == 0 || plan.Select(item => item.Field.Name).Distinct().Count() == (linking ? 49 : 39),
                "supplied feed retains its existing XML policy coverage");
            var reparsed = codec.Parse(message with { Content = protectedBytes });
            Check(codec.Serialize(reparsed, reparsed.Fields.ToDictionary(field => field.Id, field => field.Value)).SequenceEqual(protectedBytes),
                "protected export parses and re-encodes exactly");
            File.WriteAllText(Path.Combine(output, Path.GetFileNameWithoutExtension(sample.Name) + ".decoded.json"),
                JsonSerializer.Serialize(report, options) + "\n");
            File.WriteAllBytes(Path.Combine(output, Path.GetFileNameWithoutExtension(sample.Name) + ".protected.txt"), protectedBytes);
            results.Add(new
            {
                Sample = sample.Name,
                SourceSha256 = Convert.ToHexString(SHA256.HashData(original)),
                ScalarCount = parsed.Fields.Count,
                PolicyPaths = plan.Select(item => item.Field.Name).Distinct().Count(),
                PayloadBytes = raw.Length,
                ExactRawRoundTrip = true,
                ExactMqRoundTrip = true,
                IndependentReaderAgreement = true
            });
        }
    }

    string Wrap(string json) => linking ? "[" + json + "]" : json;
    ParsedFeed Parse(string json) => codec.Parse(Message(feed, Encoding.UTF8.GetBytes(Wrap(json))));
    var lexical = Parse(" {\"PNROwner\":\"K7QX2M\", \"Customers\":[{\"PaxId\":\"PAX-1001\",\"FirstName\":\"Em\\u0069ly\"},{\"PaxId\":\"PAX-1002\",\"FirstName\":\"Michael\"}],\"x/y~z\":\"Carter\",\"Count\":1e+02,\"Active\":true,\"Optional\":null,\"Empty\":[],\"Object\":{}} \n");
    var values = lexical.Fields.ToDictionary(field => field.Id, field => field.Value);
    string first = linking ? "/0/Customers/0/FirstName" : "/Customers/0/FirstName";
    string pnr = linking ? "/0/PNROwner" : "/PNROwner";
    string count = linking ? "/0/Count" : "/Count";
    string escaped = linking ? "/0/x~1y~0z" : "/x~1y~0z";
    Check(values[escaped] == "Carter", "JSON pointer escapes retain exact policy and instance paths");
    values[first] = "[PASSENGER_NAME_WITH_LONGER_TEXT]";
    byte[] changed = codec.Serialize(lexical, values);
    codec.Validate(lexical, changed);
    Check(Encoding.UTF8.GetString(changed).Contains("1e+02") && Encoding.UTF8.GetString(changed).Contains("\"Optional\":null"),
        "string growth preserves numeric spelling and null types");
    Reject(() => codec.Validate(lexical, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(changed).Replace("1e+02", "100"))),
        "external numeric spelling mutation");
    Reject(() => codec.Validate(lexical, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(changed).Replace("\"Empty\":[]", "\"Empty\":[{}]"))),
        "external empty-container grouping mutation");
    Reject(() => codec.Validate(lexical, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(changed).Replace("K7QX2M", "NEWPNR"))),
        "external PNR mutation");
    values[pnr] = "NEWPNR";
    Reject(() => codec.Serialize(lexical, values), "PNR replacement");
    values[pnr] = "K7QX2M";
    values[count] = "100";
    Reject(() => codec.Serialize(lexical, values), "non-string replacement");
    values[count] = "1e+02";
    values.Remove(first);
    Reject(() => codec.Serialize(lexical, values), "missing protection field");
    values["unknown"] = "extra";
    Reject(() => codec.Serialize(lexical, values), "substituted field ID with matching count");
    values.Remove("unknown");
    values[first] = null!;
    Reject(() => codec.Serialize(lexical, values), "null replacement is not a JSON-null conversion");
    Reject(() => Parse("{\"x\":\"a\",\"x\":\"b\"}"), "duplicate property");
    Reject(() => Parse("{\"x\":\"a\",\"\\u0078\":\"b\"}"), "escaped duplicate property");
    Reject(() => Parse("{\"*\":\"ambiguous\"}"), "wildcard property name");
    Reject(() => Parse("{\"FirstName\":\"unfinished"), "truncated JSON");
    Reject(() => codec.Parse(Message(feed, Encoding.UTF8.GetBytes(linking ? "{}" : "[]"))), "incorrect feed root");
    Reject(() => codec.Parse(Message(feed, Encoding.UTF8.GetBytes(Wrap("{\"x\":\"y\"}") + " {}"))), "trailing JSON");
    Reject(() => codec.Parse(Message(linking ? FeedType.Seats : FeedType.PnrLinking,
        Encoding.UTF8.GetBytes(Wrap("{\"x\":\"y\"}")))), "wrong delivery route");
    Reject(() => ConfiguredPiiIdentifier.CreatePlan(Parse("{\"UnreviewedEmail\":\"emily.carter@example.com\"}").Fields, policies[feed]),
        "unreviewed populated path rejects complete message before protection");
    string export = "A VER 2\r\nA ENC 546\r\nA CCS 1208\r\nA MSI DEMO\r\nX " + Convert.ToHexString(Encoding.UTF8.GetBytes(Wrap("{\"FirstName\":\"Emily\"}")));
    var exported = codec.Parse(Message(feed, Encoding.UTF8.GetBytes(export), "fixture.txt"));
    var exportValues = exported.Fields.ToDictionary(field => field.Id, field => "[PASSENGER_NAME]");
    byte[] rebuilt = codec.Serialize(exported, exportValues);
    codec.Validate(exported, rebuilt);
    Check(Encoding.UTF8.GetString(rebuilt).StartsWith("A VER 2\r\nA ENC 546\r\nA CCS 1208\r\nA MSI DEMO\r\nX ") && rebuilt[^1] != '\n',
        "MQ CRLF and absent final newline retained during protection");
    Reject(() => codec.Validate(exported, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(rebuilt).Replace("A VER 2", "A VER 3"))),
        "MQ header mutation");
    Reject(() => codec.Parse(Message(feed, Encoding.UTF8.GetBytes(export.Replace("1208", "437")), "fixture.txt")), "unsupported JSON CCSID");
    var other = FeedCodecRegistry.Create(linking ? FeedType.Seats : FeedType.PnrLinking);
    Reject(() => other.Serialize(lexical, lexical.Fields.ToDictionary(field => field.Id, field => field.Value)), "cross-feed serializer layout");
    File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(new
    {
        Feed = FeedCatalog.Get(feed).FolderName,
        Codec = codec.GetType().Name,
        Samples = results,
        ProtectionPolicyChanged = false,
        NewIdentityOrTokenization = false
    }, options) + "\n");
}
Console.WriteLine($"PASS {checks} shared System.Text.Json checks");

void Check(bool condition, string label)
{
    if (!condition)
    {
        throw new Exception(label);
    }
    checks++;
    Console.WriteLine("PASS " + label);
}

void Reject(Action action, string label)
{
    try
    {
        action();
    }
    catch (InvalidDataException)
    {
        checks++;
        Console.WriteLine("PASS rejected " + label);
        return;
    }
    throw new Exception("Expected rejection: " + label);
}

static FeedMessage Message(FeedType feed, byte[] bytes, string name = "fixture.bin") =>
    new(new(feed, Guid.Empty, name, "fixture"), bytes);

// This reader uses the independent DOM API and has no dependency on production JSON span code.
static IReadOnlyList<FeedField> Independent(byte[] payload)
{
    using (var document = JsonDocument.Parse(payload))
    {
        var fields = new List<FeedField>();
        Walk(document.RootElement, "", "");
        return fields;

        void Walk(JsonElement element, string id, string policy)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    string name = property.Name.Replace("~", "~0").Replace("/", "~1");
                    Walk(property.Value, id + "/" + name, policy + "/" + name);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (var child in element.EnumerateArray())
                {
                    Walk(child, id + "/" + index++, policy + "/*");
                }
            }
            else
            {
                string value = element.ValueKind == JsonValueKind.String ? element.GetString()! :
                    element.ValueKind == JsonValueKind.Null ? "" : element.GetRawText();
                fields.Add(new(id, policy, value));
            }
        }
    }
}

#endregion
