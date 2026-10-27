using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using PresidioDemo.Feeds.ETKT;
using PresidioDemo.Feeds.Shared;
using PresidioDemo.Pipeline;
using PresidioDemo.Protection;

#region Execution

string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string output = args.Length == 1 ? Path.GetFullPath(args[0]) : Path.Combine(project, ".test-runs/etkt-checks");
Directory.CreateDirectory(output);
var options = new JsonSerializerOptions { WriteIndented = true };
var schemas = XdrLayoutConfiguration.Load(Path.Combine(project, "src/Configuration/xdr-layouts.xml"));
var references = ReferenceLayoutConfiguration.Load(Path.Combine(project, "src/Configuration/reference-layouts.xml"), schemas);
var policies = FeedPolicyConfiguration.Load(Path.Combine(project, "src/Configuration/feed-policies.xml"));
var codec = FeedCodecRegistry.Create(FeedType.ETKT, schemas, references);
Check(codec is EtktFeedCodec, "configured ETKT route selects the dedicated module");
var results = new List<object>();
foreach (string file in Directory.GetFiles(Path.Combine(project, "docs/sample-docs"), "ETKT*.txt").Order())
{
    byte[] original = File.ReadAllBytes(file);
    var message = Message(original, Path.GetFileName(file));
    var envelope = FeedPayloadReader.Read(message);
    var decoded = new EtktDecoder().Decode(envelope.Payload);
    var status = EtktIndicators.DescribeStatus(decoded.Field("/history/h_status").Number!.Value);
    string expectedStatus = Path.GetFileName(file) switch
    {
        "ETKT_03.txt" => "Adjusted flight (provisional)",
        "ETKT_05.txt" => "Divided ETKT (provisional)",
        _ => "Checked in (provisional)"
    };
    Check(status.SetDocumentedFlags.Any(flag => flag.Label == expectedStatus), "sample-supported provisional status interpretation");
    Check(new EtktEncoder().Encode(decoded).SequenceEqual(envelope.Payload), "complete XDR round trip");
    var parsed = codec.Parse(message);
    var unchanged = parsed.Fields.ToDictionary(field => field.Id, field => field.Value);
    Check(codec.Serialize(parsed, unchanged).SequenceEqual(original), "complete MQ export round trip");
    var protectedFields = parsed.Fields.ToDictionary(field => field.Id, field =>
    {
        var policy = policies[FeedType.ETKT].Get(field.Name);
        return policy.Action == FieldAction.Mask ? new string(policy.MaskCharacter[0], field.Value.Length) : field.Value;
    });
    byte[] protectedBytes = codec.Serialize(parsed, protectedFields);
    codec.Validate(parsed, protectedBytes);
    var currentCodec = new ReferenceLayoutFeedCodec(references[FeedType.ETKT]);
    var currentParsed = currentCodec.Parse(message);
    Check(currentCodec.Serialize(currentParsed, protectedFields).SequenceEqual(protectedBytes), "existing protection behavior retained");
    var after = new EtktDecoder().Decode(FeedPayloadReader.Read(Message(protectedBytes, Path.GetFileName(file))).Payload);
    foreach (string path in new[] { "/history/h_new_recloc", "/history/h_old_recloc" })
    {
        Check(decoded.Field(path).Text == after.Field(path).Text, "divide-event PNR retained");
    }
    Check(decoded.Scalars.Where(s => s.Kind != EtktWireKind.String).Select(s => (s.Path, s.Number))
        .SequenceEqual(after.Scalars.Where(s => s.Kind != EtktWireKind.String).Select(s => (s.Path, s.Number))), "numeric and character slots retained");
    var protectedParsed = codec.Parse(message with { Content = protectedBytes });
    Check(codec.Serialize(protectedParsed, protectedParsed.Fields.ToDictionary(field => field.Id, field => field.Value))
        .SequenceEqual(protectedBytes), "protected export re-parses and re-encodes");
    File.WriteAllText(Path.Combine(output, Path.GetFileNameWithoutExtension(file) + ".decoded.json"),
        JsonSerializer.Serialize(new { Source = Path.GetFileName(file), Decoded = EtktReportFormatter.Format(decoded), Scalars = decoded.Scalars }, options) + "\n");
    File.WriteAllBytes(Path.Combine(output, Path.GetFileNameWithoutExtension(file) + ".protected.txt"), protectedBytes);
    results.Add(new
    {
        Sample = Path.GetFileName(file),
        SourceSha256 = Convert.ToHexString(SHA256.HashData(original)),
        PayloadBytes = decoded.PayloadLength,
        ScalarCount = decoded.Scalars.Count,
        DecodeEncodeExact = true,
        MqExportExact = true,
        ProjectionOutputExact = true,
        DividePnrsUnchanged = true
    });
    Console.WriteLine($"PASS {Path.GetFileName(file)}: {decoded.PayloadLength} bytes, {decoded.Scalars.Count} fields, exact binary and export round trips");
}

byte[] baseline = FeedPayloadReader.Read(Message(File.ReadAllBytes(Path.Combine(project, "docs/sample-docs/ETKT.txt")), "ETKT.txt")).Payload;
var baselineDocument = new EtktDecoder().Decode(baseline);
Reject(() => new EtktDecoder().Decode(baseline[..^1]), "unaligned/truncated payload");
Reject(() => new EtktDecoder().Decode(baseline[..^4]), "aligned truncation");
Reject(() => new EtktDecoder().Decode(baseline.Concat(new byte[4]).ToArray()), "trailing bytes");
MutateReject(0, 3, "documented enum is not silently substituted for type 5");
MutateReject(4, 0, "absent history is outside the sample profile");
MutateReject(8, 1, "populated error branch is outside the sample profile");
MutateReject(12, int.MaxValue, "oversized string length");
MutateReject(12, -1, "negative string length");
var terminal = baselineDocument.Field("/history/h_aaa_lniata");
byte[] padding = baseline.ToArray();
padding[terminal.ValueOffset + terminal.ValueLength] = 1;
Reject(() => new EtktDecoder().Decode(padding), "nonzero padding");
byte[] badAscii = baseline.ToArray();
badAscii[terminal.ValueOffset] = 255;
Reject(() => new EtktDecoder().Decode(badAscii), "non-ASCII text");
var numeric = baselineDocument.Field("/history/h_sequence_nbr");
byte[] changedNumeric = baseline.ToArray();
BinaryPrimitives.WriteInt32BigEndian(changedNumeric.AsSpan(numeric.Offset), 123456);
Check(new EtktDecoder().Decode(changedNumeric).Field(numeric.Path).Number == 123456, "sequence number is decoded as a numeric word");
Reject(() => new EtktEncoder().Encode(baselineDocument, new Dictionary<string, string> { ["/missing"] = "x" }), "unknown replacement path");
byte[] expanded = new EtktEncoder().Encode(baselineDocument, new Dictionary<string, string> { ["/history/h_pax_name"] = "Emily Carter" });
Check(new EtktDecoder().Decode(expanded).Field("/history/h_pax_name").Text == "Emily Carter", "structural encoder handles a counted string becoming populated");
Reject(() => codec.Parse(Message(expanded, "expanded.bin")), "unreviewed populated field cannot pass the existing processing projection");

var parsedBaseline = codec.Parse(Message(baseline, "baseline.bin"));
var preserved = parsedBaseline.Fields.ToDictionary(field => field.Id, field => field.Value);
var legacyPnr = parsedBaseline.Fields.Single(field => field.Name == "/pnr");
var alteredAlias = new Dictionary<string, string>(preserved) { [legacyPnr.Id] = "K7QX2M" };
Reject(() => codec.Serialize(parsedBaseline, alteredAlias), "legacy PNR alias remains unchanged");
var ticket = parsedBaseline.Fields.Single(field => field.Name == "/Text0088");
var alteredWidth = new Dictionary<string, string>(preserved) { [ticket.Id] = "*" };
Reject(() => codec.Serialize(parsedBaseline, alteredWidth), "processing serializer rejects width changes");
byte[] tampered = baseline.ToArray();
BinaryPrimitives.WriteInt32BigEndian(tampered.AsSpan(numeric.Offset), 42);
Reject(() => codec.Validate(parsedBaseline, tampered), "validation catches numeric corruption");
byte[] mutatedKept = baseline.ToArray();
mutatedKept[terminal.ValueOffset] = (byte)'Z';
Reject(() => codec.Validate(parsedBaseline, mutatedKept), "validation catches kept-field corruption");

byte[] divideExport = File.ReadAllBytes(Path.Combine(project, "docs/sample-docs/ETKT_05.txt"));
var divideMessage = Message(divideExport, "ETKT_05.txt");
var divideEnvelope = FeedPayloadReader.Read(divideMessage);
var divideDocument = new EtktDecoder().Decode(divideEnvelope.Payload);
var divideParsed = codec.Parse(divideMessage);
var divideValues = divideParsed.Fields.ToDictionary(field => field.Id, field => field.Value);
foreach (string alias in new[] { "/newPnr", "/oldPnr" })
{
    Check(policies[FeedType.ETKT].Get(alias).Action == FieldAction.Keep, "divide PNR policy is Keep");
    var field = divideParsed.Fields.Single(field => field.Name == alias);
    var changed = new Dictionary<string, string>(divideValues) { [field.Id] = "K7QX2M" };
    Reject(() => codec.Serialize(divideParsed, changed), "serializer rejects PNR replacement");
}
foreach (string path in new[] { "/history/h_new_recloc", "/history/h_old_recloc" })
{
    byte[] changed = divideEnvelope.Payload.ToArray();
    changed[divideDocument.Field(path).ValueOffset] = (byte)'Z';
    Reject(() => codec.Validate(divideParsed, divideEnvelope.Serialize(changed)), "validator rejects changed divide PNR");
    byte[] invalid = new EtktEncoder().Encode(divideDocument, new Dictionary<string, string> { [path] = "badpnr" });
    Reject(() => new EtktParser().Parse(invalid), "lowercase divide PNR is rejected");
}

string configurationRoot = Path.Combine(project, ".test-runs", "etkt-config-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(configurationRoot);
string configurationFile = Path.Combine(configurationRoot, "reference-layouts.xml");
var originalConfiguration = XDocument.Load(Path.Combine(project, "src/Configuration/reference-layouts.xml"));
var compatibilityConfiguration = new XDocument(originalConfiguration);
compatibilityConfiguration.Root!.Elements("feed").Single(element => (string?)element.Attribute("type") == "ETKT").Attribute("etktStructures")!.Remove();
compatibilityConfiguration.Save(configurationFile);
var compatibilityReferences = ReferenceLayoutConfiguration.Load(configurationFile, schemas);
Check(FeedCodecRegistry.Create(FeedType.ETKT, schemas, compatibilityReferences) is ReferenceLayoutFeedCodec,
    "custom reference configurations without ETKT opt-in retain their codec");
foreach (string invalidValue in new[] { "True", "1", "yes" })
{
    InvalidConfiguration(document => EtktEntry(document).SetAttributeValue("etktStructures", invalidValue), "invalid ETKT flag");
}
InvalidConfiguration(document => EtktEntry(document).SetAttributeValue("schemaDriven", "false"), "ETKT flag requires schema-driven mode");
InvalidConfiguration(document => document.Root!.Elements("feed").Single(element => (string?)element.Attribute("type") == "PNR")
    .SetAttributeValue("etktStructures", "true"), "ETKT flag cannot select a different feed route");

Check(EtktIndicators.DescribeStatus(unchecked((int)0x80000000)).SetDocumentedFlags.Single().DocumentBit == 0,
    "document bit zero maps to most-significant bit, including signed negative values");
Check(EtktIndicators.DescribeStatus(0).SetDocumentedFlags.Count == 0 && EtktIndicators.DescribeStatus(0).ExchangeDestination is null,
    "zero status does not fabricate a state or exchange event");
Check(EtktIndicators.DescribeStatus(0x1C).UnassignedSetBits == "0x0000001C", "reserved set bits remain visible");
Check(EtktIndicators.DescribeStatus(1).ExchangeDestination is null, "bit 31 alone does not create an exchanged status");
Check(EtktIndicators.DescribeStatus(0x01000000).ExchangeDestination == "ETKT (provisional)" &&
    EtktIndicators.DescribeStatus(0x01000001).ExchangeDestination == "Paper (provisional)", "exchange destination follows bit 31 only for exchanged status");

File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(new
{
    Scope = "Configured ETKT route uses the dedicated module; provisional new/old PNRs remain unchanged",
    Samples = results,
    MalformedAndPolicyChecksPassed = true
}, options) + "\n");
Console.WriteLine("PASS malformed-input, coverage, width, metadata and kept-field checks");

FeedMessage Message(byte[] bytes, string name) => new(new(FeedType.ETKT, Guid.Empty, name, ""), bytes);
void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}
void Reject(Action action, string message)
{
    try
    {
        action();
    }
    catch (InvalidDataException)
    {
        return;
    }
    throw new Exception("Expected rejection: " + message);
}
void MutateReject(int offset, int value, string message)
{
    byte[] changed = baseline.ToArray();
    BinaryPrimitives.WriteInt32BigEndian(changed.AsSpan(offset), value);
    Reject(() => new EtktDecoder().Decode(changed), message);
}
XElement EtktEntry(XDocument document) => document.Root!.Elements("feed").Single(element => (string?)element.Attribute("type") == "ETKT");
void InvalidConfiguration(Action<XDocument> mutate, string message)
{
    var document = new XDocument(originalConfiguration);
    mutate(document);
    document.Save(configurationFile);
    Reject(() => ReferenceLayoutConfiguration.Load(configurationFile, schemas), message);
}

#endregion
