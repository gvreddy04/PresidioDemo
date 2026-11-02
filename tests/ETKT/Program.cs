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
var schemas = XdrLayoutConfiguration.Load(Path.Combine(project, "src/PresidioDemo.Core/Configuration/xdr-layouts.xml"));
var references = ReferenceLayoutConfiguration.Load(Path.Combine(project, "src/PresidioDemo.Core/Configuration/reference-layouts.xml"), schemas);
var policies = FeedPolicyConfiguration.Load(Path.Combine(project, "src/PresidioDemo.Core/Configuration/feed-policies.xml"));
var codec = FeedCodecRegistry.Create(FeedType.ETKT, schemas, references);
Check(codec is EtktFeedCodec, "configured ETKT route selects the dedicated module");
var results = new List<object>();
foreach (string file in Directory.GetFiles(Path.Combine(project, "docs/sample-docs/ETKT"), "ETKT*.txt").Order())
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

byte[] baseline = FeedPayloadReader.Read(Message(File.ReadAllBytes(Path.Combine(project, "docs/sample-docs/ETKT/ETKT.txt")), "ETKT.txt")).Payload;
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
var expandedParsed = codec.Parse(Message(expanded, "expanded.bin"));
Check(expandedParsed.Fields.Single(field => field.Name == "/passengerName").Value == "Emily Carter", "populated passenger name is mapped");
Check(new EtktDecoder().Decode(Protect(expandedParsed)).Field("/history/h_pax_name").Text == "************", "populated passenger name is masked");

// Every documented string that is empty in the samples is mapped, and protected as configured once populated.
var optionalStrings = baselineDocument.Scalars.Where(scalar => scalar.Kind == EtktWireKind.String && scalar.Text!.Length == 0)
    .Select((scalar, index) => (scalar.Path, Value: scalar.Path is "/history/h_new_recloc" or "/history/h_old_recloc"
        ? "K7QX2" + (char)('A' + index % 26) : $"Q {index:D2}")).ToDictionary(item => item.Path, item => item.Value);
Check(optionalStrings.Count == 61, "the baseline leaves 61 documented strings empty");
var populatedParsed = codec.Parse(Message(new EtktEncoder().Encode(baselineDocument, optionalStrings), "all-populated.bin"));
var populatedAfter = new EtktDecoder().Decode(Protect(populatedParsed));
var keptStrings = new HashSet<string>(["/createDate", "/newCarrier", "/newFlightNumber", "/newFlightDate", "/newDepartureTime",
    "/newArrivalTime", "/newBoardPoint", "/newOffPoint", "/newClass", "/newOperatingFlightNumber", "/previousFlightNumber",
    "/previousFlightDate", "/previousDepartureTime", "/previousArrivalTime", "/previousBoardPoint", "/previousOffPoint",
    "/previousOperatingFlightNumber", "/newPurgeDate", "/newUnflownDate", "/oldPurgeDate", "/oldUnflownDate", "/newPnr", "/oldPnr"]);
foreach (var (path, value) in optionalStrings)
{
    var field = populatedParsed.Fields.Single(field => field.Value == value);
    var policy = policies[FeedType.ETKT].Get(field.Name);
    Check(policy.Action == (keptStrings.Contains(field.Name) ? FieldAction.Keep : FieldAction.Mask), "reviewed treatment for " + field.Name);
    string after = populatedAfter.Field(path).Text!;
    Check(policy.Action == FieldAction.Keep ? after == value : after == new string(policy.MaskCharacter[0], value.Length),
        "populated documented string is kept or masked as configured: " + field.Name);
}
foreach (var (alias, entity) in new[] { ("/passengerName", "PERSON"), ("/newPassengerName", "PERSON"), ("/previousPassengerName", "PERSON"),
    ("/formOfPayment", "PAYMENT_REFERENCE"), ("/creditCardNumber", "PAYMENT_REFERENCE"), ("/totalFare", "FINANCIAL_INFORMATION"),
    ("/newTicketNumber", "TICKET_REFERENCE"), ("/oldTicketNumber", "TICKET_REFERENCE"), ("/fim1Coupon1", "TICKET_REFERENCE") })
{
    Check(policies[FeedType.ETKT].Get(alias).Entity == entity, "personal, payment and ticket text uses its entity: " + alias);
}

var parsedBaseline = codec.Parse(Message(baseline, "baseline.bin"));
var preserved = parsedBaseline.Fields.ToDictionary(field => field.Id, field => field.Value);
var terminalField = parsedBaseline.Fields.Single(field => field.Name == "/agentTerminalAddress");
var alteredTerminal = new Dictionary<string, string>(preserved) { [terminalField.Id] = "K7QX2M" };
Reject(() => codec.Serialize(parsedBaseline, alteredTerminal), "agent terminal address remains unchanged");
var ticket = parsedBaseline.Fields.Single(field => field.Name == "/ticketNumber");
Check(ticket.Value == baselineDocument.Field("/history/h_tkt_nbr").Text &&
    policies[FeedType.ETKT].Get("/ticketNumber").Entity == "TICKET_REFERENCE", "ticket number maps to h_tkt_nbr as a ticket reference");
Check(parsedBaseline.Fields.Single(field => field.Name == "/historySequenceNumber").Value ==
    baselineDocument.Field("/history/glob_tkt_hist_seq_nbr").Text, "history sequence number maps to glob_tkt_hist_seq_nbr");
var alteredWidth = new Dictionary<string, string>(preserved) { [ticket.Id] = "*" };
Reject(() => codec.Serialize(parsedBaseline, alteredWidth), "processing serializer rejects width changes");
byte[] tampered = baseline.ToArray();
BinaryPrimitives.WriteInt32BigEndian(tampered.AsSpan(numeric.Offset), 42);
Reject(() => codec.Validate(parsedBaseline, tampered), "validation catches numeric corruption");
byte[] mutatedKept = baseline.ToArray();
mutatedKept[terminal.ValueOffset] = (byte)'Z';
Reject(() => codec.Validate(parsedBaseline, mutatedKept), "validation catches kept-field corruption");

byte[] divideExport = File.ReadAllBytes(Path.Combine(project, "docs/sample-docs/ETKT/ETKT_05.txt"));
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
var originalConfiguration = XDocument.Load(Path.Combine(project, "src/PresidioDemo.Core/Configuration/reference-layouts.xml"));
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
var plainIndicator = EtktIndicators.DescribeIndicator(0x20000000);
Check(plainIndicator.IdType == 1 && plainIndicator.IdTypeLabel == "ETKT (provisional)" && plainIndicator.SetDocumentedFlags.Count == 0 &&
    plainIndicator.UnassignedSetBits == "0x00000000", "indicator bits 0-2 hold the ETKT ID type");
var subsequentIndicator = EtktIndicators.DescribeIndicator(0x31000000);
Check(subsequentIndicator.SetDocumentedFlags.Select(flag => flag.Label).SequenceEqual(
    ["ETKT was in PNR in AAA (provisional)", "Actioned all subsequent coupons (provisional)"]), "ETKT_05 indicator labels");
var oaIndicator = EtktIndicators.DescribeIndicator(0x34080000);
Check(oaIndicator.SetDocumentedFlags.Select(flag => flag.DocumentBit).SequenceEqual([3, 12]) &&
    oaIndicator.UnassignedSetBits == "0x04000000", "undocumented indicator bit 5 remains visible");
Check(EtktIndicators.DescribeIndicator(unchecked((int)0xE0000000)).IdTypeLabel is null, "undocumented ID types are not labelled");

File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(new
{
    Scope = "Configured ETKT route uses the dedicated module; provisional new/old PNRs remain unchanged",
    Samples = results,
    MalformedAndPolicyChecksPassed = true
}, options) + "\n");
Console.WriteLine("PASS malformed-input, coverage, width, metadata and kept-field checks");

FeedMessage Message(byte[] bytes, string name) => new(new(FeedType.ETKT, Guid.Empty, name, ""), bytes);
byte[] Protect(ParsedFeed parsed)
{
    var values = parsed.Fields.ToDictionary(field => field.Id, field =>
    {
        var policy = policies[FeedType.ETKT].Get(field.Name);
        return policy.Action == FieldAction.Mask ? new string(policy.MaskCharacter[0], field.Value.Length) : field.Value;
    });
    byte[] protectedBytes = codec.Serialize(parsed, values);
    codec.Validate(parsed, protectedBytes);
    return protectedBytes;
}
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
