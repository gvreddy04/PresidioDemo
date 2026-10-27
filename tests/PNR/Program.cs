using System.Text.Json;
using PresidioDemo.Feeds.PNR;
using PresidioDemo.Feeds.Shared;
using PresidioDemo.Pipeline;

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var schemas = XdrLayoutConfiguration.Load(Path.Combine(root, "src/Configuration/xdr-layouts.xml"));
var references = ReferenceLayoutConfiguration.Load(Path.Combine(root, "src/Configuration/reference-layouts.xml"), schemas);
var codec = FeedCodecRegistry.Create(FeedType.PNR, schemas, references);
if (codec is not PnrFeedCodec)
{
    throw new InvalidDataException("The PNR module was not selected.");
}
byte[] source = File.ReadAllBytes(Path.Combine(root, "docs/sample-docs/PNR.txt"));
var message = new FeedMessage(new(FeedType.PNR, Guid.Empty, "PNR.txt", ""), source);
var parsed = codec.Parse(message);
var document = PnrFeedCodec.Document(parsed);
var reader = new PnrXdrReader(schemas[FeedType.PNR]);
var decoded = new PnrDecoder(reader).Decode(message);
if (!new PnrEncoder(new PnrXdrWriter(reader)).Encode(decoded).SequenceEqual(source))
{
    throw new InvalidDataException("PNR XDR round-trip failed.");
}
string target = args.Length == 1 ? Path.GetFullPath(args[0]) : Path.Combine(root, ".test-runs", "PNR.decoded.json");
Directory.CreateDirectory(Path.GetDirectoryName(target)!);
File.WriteAllText(target, JsonSerializer.Serialize(PnrReportFormatter.Format(document), new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS PNR decode and exact MQ round trip; {document.Fields.Count} scalars, {document.Fields.Count(f => f.Meaning is not null)} labeled. Report: {target}");
