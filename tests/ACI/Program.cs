using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using PresidioDemo.Feeds.ACI;
using PresidioDemo.Feeds.Shared;
using PresidioDemo.Pipeline;
using PresidioDemo.Protection;

#region Execution

string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string output = args.Length == 1 ? Path.GetFullPath(args[0]) : Path.Combine(project, ".test-runs", "aci-checks");
Directory.CreateDirectory(output);
var schemas = XdrLayoutConfiguration.Load(Path.Combine(project, "src/Configuration/xdr-layouts.xml"));
var references = ReferenceLayoutConfiguration.Load(Path.Combine(project, "src/Configuration/reference-layouts.xml"), schemas);
var policies = FeedPolicyConfiguration.Load(Path.Combine(project, "src/Configuration/feed-policies.xml"));
var codec = FeedCodecRegistry.Create(FeedType.ACI, schemas, references);
Check(codec is AciFeedCodec, "ACI route uses the new module");
var results = new List<object>();
var options = new JsonSerializerOptions { WriteIndented = true };
foreach (string file in Directory.GetFiles(Path.Combine(project, "docs/sample-docs/ACI"), "ACI*.txt").Order())
{
    byte[] original = File.ReadAllBytes(file);
    var delivery = new FeedDelivery(FeedType.ACI, Guid.NewGuid(), Path.GetFileName(file), file);
    var message = new FeedMessage(delivery, original);
    var envelope = FeedPayloadReader.Read(message);
    var decoded = new AciDecoder().Decode(envelope.Payload);
    byte[] roundTrip = new AciEncoder().Encode(decoded);
    Check(roundTrip.SequenceEqual(envelope.Payload), "Complete decode/encode round trip");
    _ = new AciParser().Parse(envelope.Payload);
    var parsed = codec.Parse(message);
    var unchanged = parsed.Fields.ToDictionary(field => field.Id, field => field.Value);
    Check(codec.Serialize(parsed, unchanged).SequenceEqual(original), "Original MQ export round trip");
    var masks = parsed.Fields.ToDictionary(field => field.Id, field =>
    {
        var rule = policies[FeedType.ACI].Get(field.Name);
        return rule.Action == FieldAction.Mask ? new string(rule.MaskCharacter[0], field.Value.Length) : field.Value;
    });
    byte[] protectedBytes = codec.Serialize(parsed, masks);
    codec.Validate(parsed, protectedBytes);
    var protectedEnvelope = FeedPayloadReader.Read(message with { Content = protectedBytes });
    var protectedDocument = new AciParser().Parse(protectedEnvelope.Payload);
    Check(decoded.Scalars.Where(s => s.Number is not null).Select(s => (s.Path, s.Number))
        .SequenceEqual(protectedDocument.Scalars.Where(s => s.Number is not null).Select(s => (s.Path, s.Number))), "All numeric fields and passenger IDs preserved");
    int masksCount = parsed.Fields.Count(field => field.Value.Length > 0 && policies[FeedType.ACI].Get(field.Name).Action == FieldAction.Mask);
    int keptCount = parsed.Fields.Count(field => field.Value.Length > 0 && policies[FeedType.ACI].Get(field.Name).Action == FieldAction.Keep);
    File.WriteAllText(Path.Combine(output, Path.GetFileNameWithoutExtension(file) + ".decoded.json"),
        JsonSerializer.Serialize(new
        {
            Source = Path.GetFileName(file),
            SourceSha256 = Convert.ToHexString(SHA256.HashData(original)),
            Decoded = AciReportFormatter.Format(decoded),
            Scalars = decoded.Scalars
        }, options) + Environment.NewLine);
    File.WriteAllBytes(Path.Combine(output, Path.GetFileNameWithoutExtension(file) + ".protected.txt"), protectedBytes);
    results.Add(new
    {
        Sample = Path.GetFileName(file),
        PayloadBytes = decoded.PayloadLength,
        ScalarCount = decoded.Scalars.Count,
        KeptFields = keptCount,
        MaskedFields = masksCount,
        DecodeEncodeExact = true,
        MqExportExact = true,
        NumericValuesUnchanged = true
    });
    Console.WriteLine($"PASS {Path.GetFileName(file)} | {decoded.PayloadLength} bytes | {masksCount} masks");
}

byte[] baseline = FeedPayloadReader.ReadPayload(new(new(FeedType.ACI, Guid.Empty, "ACI.txt", ""), File.ReadAllBytes(Path.Combine(project, "docs/sample-docs/ACI/ACI.txt"))));
var baselineDocument = new AciDecoder().Decode(baseline);
Reject(() => new AciDecoder().Decode(baseline[..^1]), "truncated payload");
Reject(() => new AciDecoder().Decode(baseline.Concat(new byte[4]).ToArray()), "trailing bytes");
MutateReject(0, 3, "unknown union discriminator");
MutateReject(4, 2, "invalid pointer presence");
MutateReject(8, int.MaxValue, "oversized string");
var pnr = baselineDocument.Scalars.Single(s => s.Path == "/pax_list/rec_loc");
byte[] brokenPadding = baseline.ToArray();
brokenPadding[pnr.ValueOffset + pnr.ValueLength] = 1;
Reject(() => new AciDecoder().Decode(brokenPadding), "nonzero padding");
var passengerId = baselineDocument.Scalars.Single(s => s.Path == "/pax_list/paxflt_items/pax_id");
MutateReject(passengerId.Offset, passengerId.Number!.Value + 1, "passenger ID link mismatch", semantic: true);
var extension = baselineDocument.Scalars.Single(s => s.Path.EndsWith("/unresolvedRecordReference", StringComparison.Ordinal));
byte[] wrongPnr = baseline.ToArray();
wrongPnr[extension.ValueOffset + extension.ValueLength - 1] = (byte)'Z';
Reject(() => new AciParser().Parse(wrongPnr), "extension PNR mismatch");
var rawMessage = new FeedMessage(new(FeedType.ACI, Guid.Empty, "ACI.bin", ""), baseline);
var rawParsed = codec.Parse(rawMessage);
var changedWidths = rawParsed.Fields.ToDictionary(f => f.Id, f => f.Value);
var nameField = rawParsed.Fields.Single(f => f.Name == "/passengerName");
changedWidths[nameField.Id] += "X";
Reject(() => codec.Serialize(rawParsed, changedWidths), "changed protection width");
var changedPnr = rawParsed.Fields.ToDictionary(f => f.Id, f => f.Value);
changedPnr[rawParsed.Fields.Single(f => f.Name == "/pnr").Id] = "OTHER1";
Reject(() => codec.Serialize(rawParsed, changedPnr), "changed PNR");

// Unknown nonempty spare text needs a policy even when the old word projection accepts it.
var spare = baselineDocument.Scalars.Single(s => s.Path == "/flight_list/flt_list_str2");
var addedText = new AciEncoder().Encode(baselineDocument, new Dictionary<string, string> { [spare.Path] = "EMILY" });
Reject(() => codec.Parse(rawMessage with { Content = addedText }), "unmapped nonempty text");

// The structure permits multiple passengers in one PNR. IDs, not list order or names,
// establish each passenger-flight association. This synthetic case is decoder coverage;
// the production route still requires a reviewed policy projection for such a shape.
var passenger = baselineDocument.PassengerList!;
AciNode ChangeId(AciNode node, int id)
{
    return node with
    {
        Members = node.Members.Select(member => member.Definition.Name is "paxl_id" or "pax_id"
        ? member with { Scalar = member.Scalar! with { Number = id } }
        : member.Child is not null ? member with { Child = ChangeId(member.Child, id) } : member).ToArray()
    };
}
var second = ChangeId(passenger, 186);
second = second with
{
    Members = second.Members.Select(member => member.Definition.Name == "pax_name"
    ? member with { Scalar = member.Scalar! with { Text = "BROOKS/MICHAEL" } } : member).ToArray()
};
var first = passenger with
{
    Members = passenger.Members.Select(member => member.Definition.Name == "pax_name"
    ? member with { Scalar = member.Scalar! with { Text = "CARTER/EMILY" } }
    : member.Definition.Name == "next" ? member with { Child = second } : member).ToArray()
};
byte[] multiple = new AciEncoder().Encode(baselineDocument with { PassengerList = first });
var multipleParsed = new AciParser().Parse(multiple);
var passengers = AciParser.EnumerateList(multipleParsed.PassengerList).ToArray();
Check(passengers.Length == 2 && passengers.Select(p => p.Scalar("rec_loc").Text).Distinct().Count() == 1 &&
    passengers.Select(p => p.Scalar("paxl_id").Number).Distinct().Count() == 2, "Two passengers share PNR with distinct linked IDs");
Reject(() => codec.Parse(rawMessage with { Content = multiple }), "unreviewed multi-passenger processing projection");

using (var writer = new AciXdrWriter())
{
    writer.WriteInt32(1);
    writer.WriteString("Example producer error");
    var error = new AciDecoder().Decode(writer.ToArray());
    Check(new AciEncoder().Encode(error).SequenceEqual(writer.ToArray()), "ERROR union decode/encode");
    Reject(() => new AciParser().Parse(writer.ToArray()), "ERROR record publication");
}
File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(new
{
    AllPassed = true,
    Samples = results,
    Checks = "Round trip, real route selection, masks, numeric IDs, malformed data, unmapped text, PNRs and multi-passenger decoder association"
}, options) + Environment.NewLine);
Console.WriteLine("PASS ACI checks complete");

static void Check(bool condition, string label)
{
    if (!condition)
    {
        throw new Exception(label);
    }
}
static void Reject(Action action, string label)
{
    try
    {
        action();
    }
    catch (InvalidDataException)
    {
        Console.WriteLine("PASS reject " + label);
        return;
    }
    throw new Exception("Expected rejection: " + label);
}
void MutateReject(int offset, int value, string label, bool semantic = false)
{
    byte[] bytes = baseline.ToArray();
    BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset, 4), value);
    Reject(() => { if (semantic) { _ = new AciParser().Parse(bytes); } else { _ = new AciDecoder().Decode(bytes); } }, label);
}

#endregion
