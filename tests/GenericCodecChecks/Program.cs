using System.Text.Json;
using PresidioDemo.Feeds.Shared;

#region Execution

// Generic XDR codec checks (V-03). Output is value-free: offsets, lengths and counts only.
string project = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(project, "PresidioDemo.slnx")))
{
    project = Directory.GetParent(project)?.FullName ?? throw new Exception("Project root not found.");
}
// Candidate counts measured against the removed per-feed schemas in T-003 (the scanner found exactly the schema strings
// of four or more characters), and the frozen value-free parity baseline.
var expectedCandidates = new Dictionary<string, int>
{
    ["ACI.txt"] = 17, ["ACI_02.txt"] = 17, ["ACI_03.txt"] = 15, ["ACI_04.txt"] = 16, ["ACI_05.txt"] = 14, ["ACI_06.txt"] = 17,
    ["ETKT.txt"] = 7, ["ETKT_02.txt"] = 7, ["ETKT_03.txt"] = 17, ["ETKT_04.txt"] = 7, ["ETKT_05.txt"] = 9, ["ETKT_06.txt"] = 7,
    ["PNR.txt"] = 89, ["PNR_02.txt"] = 32
};
using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(project, "tests", "fixtures", "parity-baseline.json")));
var locations = baseline.RootElement.GetProperty("protected").EnumerateArray().Concat(baseline.RootElement.GetProperty("pnrs").EnumerateArray())
    .Where(e => e.GetProperty("feed").GetString() is "ACI" or "ETKT" or "PNR")
    .Select(e => (Sample: e.GetProperty("sample").GetString()!, Offset: int.Parse(e.GetProperty("location").GetString()!), Length: e.GetProperty("length").GetInt32()))
    .ToArray();
var samples = Directory.EnumerateFiles(Path.Combine(project, "docs", "sample-docs"), "*.txt", SearchOption.AllDirectories)
    .Order(StringComparer.Ordinal)
    .Select(path => (Path: path, Feed: FeedFileRouting.FromPath(path)))
    .Where(sample => sample.Feed is FeedType.ACI or FeedType.ETKT or FeedType.PNR)
    .ToArray();
Check(samples.Length == 14, "14 binary samples expected, found " + samples.Length);

int compared = 0;
foreach (var (path, feed) in samples)
{
    string name = Path.GetFileName(path);
    var message = new FeedMessage(new FeedDelivery(feed, Guid.NewGuid(), name, path), File.ReadAllBytes(path));
    var codec = new GenericXdrFeedCodec(feed);
    var parsed = codec.Parse(message);
    var candidates = GenericXdrFeedCodec.Candidates(parsed);

    // A-002: the scanner still finds the strings the removed schemas held, and every baseline value of four or more
    // characters lies inside one candidate (shorter ones are raw words, WR-015).
    Check(candidates.Count == expectedCandidates[name], $"{name}: {candidates.Count} candidates, expected {expectedCandidates[name]}");
    foreach (var location in locations.Where(l => l.Sample == name))
    {
        bool inside = candidates.Any(c => location.Offset >= c.Offset && location.Offset + location.Length <= c.Offset + c.Length);
        var containing = candidates.FirstOrDefault(c => location.Offset >= c.Offset && location.Offset < c.Offset + c.Length);
        Check(inside || containing is null, $"{name}: a baseline value at offset {location.Offset} crosses a candidate boundary");
        Check(inside || location.Length < XdrTextScanner.MinimumLength || IsShortString(envelopeOf(message), location.Offset, location.Length),
            $"{name}: baseline value at offset {location.Offset} is not inside a candidate");
    }
    compared += candidates.Count;

    // Unchanged values rebuild the original bytes exactly.
    var unchanged = parsed.Fields.ToDictionary(f => f.Id, f => f.Value);
    byte[] same = codec.Serialize(parsed, unchanged);
    Check(same.AsSpan().SequenceEqual(message.Content.Span), name + ": unmasked round trip changed bytes");
    codec.Validate(parsed, same);

    // Full-width masks keep framing, length and the string layout.
    var masked = parsed.Fields.ToDictionary(f => f.Id, f => new string('*', f.Value.Length));
    byte[] protectedContent = codec.Serialize(parsed, masked);
    codec.Validate(parsed, protectedContent);
    Check(protectedContent.Length == message.Content.Length, name + ": masked message changed length");

    // A changed width is refused.
    var first = parsed.Fields[0];
    Reject(() => codec.Serialize(parsed, unchanged.ToDictionary(p => p.Key, p => p.Key == first.Id ? p.Value + "X" : p.Value)), name + ": width change");

    // A changed byte outside the strings is refused.
    var envelope = FeedPayloadReader.Read(message);
    byte[] tamperedPayload = envelope.Payload.ToArray();
    int outside = Enumerable.Range(0, tamperedPayload.Length).First(i => !candidates.Any(c => i >= c.Offset && i < c.Offset + c.Length));
    tamperedPayload[outside] ^= 0x01;
    Reject(() => codec.Validate(parsed, envelope.Serialize(tamperedPayload)), name + ": changed raw word");

    if (name == "ETKT_05.txt")
    {
        // WR-015: the words 00000001 31000000 at offsets 132-139 read as a one-character string but are raw words.
        Check(!candidates.Any(c => c.Offset < 140 && c.Offset + c.Length > 132), "ETKT_05: words 132-139 must stay raw words");
        Check(protectedContent.Length == message.Content.Length &&
              FeedPayloadReader.Read(new FeedMessage(message.Delivery, protectedContent)).Payload.AsSpan(132, 8).SequenceEqual(envelope.Payload.AsSpan(132, 8)),
              "ETKT_05: words 132-139 changed");
    }
    Console.WriteLine($"PASS {name,-14} {candidates.Count,3} candidates | baseline locations inside candidates or short raw words");
}

// Unaligned payloads are framing failures.
Reject(() => XdrTextScanner.Scan(new byte[] { 0, 0, 0 }), "unaligned payload");
// Short spans are never candidates.
Check(XdrTextScanner.Scan(new byte[] { 0, 0, 0, 3, 0x31, 0x32, 0x41, 0 }).Count == 0, "3-character span must stay raw words");
Check(XdrTextScanner.Scan(new byte[] { 0, 0, 0, 4, 0x44, 0x45, 0x4E, 0x31 }).Count == 1, "4-character string must be a candidate");
// Non-printable text or non-zero padding is not a string.
Check(XdrTextScanner.Scan(new byte[] { 0, 0, 0, 5, 0x41, 0x42, 0x43, 0x44, 0x45, 1, 0, 0 }).Count == 0, "non-zero padding must not be a string");
// Text in another encoding cannot be checked for personal data, so it fails closed (WR-016), as the removed schemas did.
Reject(() => XdrTextScanner.Scan(new byte[] { 0, 0, 0, 4, 0x41, 0x09, 0x43, 0x44 }), "a tab inside a counted string");
Reject(() => XdrTextScanner.Scan(new byte[] { 0, 0, 0, 6, 0x4D, 0xDC, 0x4C, 0x4C, 0x45, 0x52, 0, 0 }), "Latin-1 text (MÜLLER)");
Reject(() => XdrTextScanner.Scan(new byte[] { 0, 0, 0, 5, 0xE2, 0xD4, 0xC9, 0xE3, 0xC8, 0, 0, 0 }), "EBCDIC text");
// Integers almost always hold NUL bytes, so a length-like word followed by an integer stays raw words.
Check(XdrTextScanner.Scan(new byte[] { 0, 0, 0, 4, 0xFF, 0xFF, 0xFF, 0x00 }).Count == 0, "an integer with a NUL byte stays raw words");
Check(XdrTextScanner.Scan(new byte[] { 0, 0, 0, 4, 0, 0, 0, 0 }).Count == 0, "NUL bytes stay raw words");
// A count followed by integer bytes without NUL (R-013) is not text in another encoding.
Check(XdrTextScanner.Scan(new byte[] { 0, 0, 0, 4, 0x8A, 0x1B, 0x2C, 0x3D }).Count == 0, "a count followed by integer bytes stays raw words");

Console.WriteLine($"PASS Generic XDR codec checks complete | {samples.Length} samples | {compared} candidates");

static byte[] envelopeOf(FeedMessage message) => FeedPayloadReader.Read(message).Payload;

// A short counted string: its length word sits just before the value and equals the value length (1-3 characters).
static bool IsShortString(byte[] payload, int offset, int length) =>
    offset >= 4 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(offset - 4, 4)) == (uint)length;

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
        return;
    }
    throw new Exception("Expected rejection: " + label);
}

#endregion
