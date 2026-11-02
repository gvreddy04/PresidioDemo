using System.Text.Json;
using PresidioDemo.Feeds.PNR;
using PresidioDemo.Feeds.Shared;
using PresidioDemo.Pipeline;

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var schemas = XdrLayoutConfiguration.Load(Path.Combine(root, "src/PresidioDemo.Core/Configuration/xdr-layouts.xml"));
var references = ReferenceLayoutConfiguration.Load(Path.Combine(root, "src/PresidioDemo.Core/Configuration/reference-layouts.xml"), schemas);
var codec = FeedCodecRegistry.Create(FeedType.PNR, schemas, references);
if (codec is not PnrFeedCodec)
{
    throw new InvalidDataException("The PNR module was not selected.");
}
string target = args.Length == 1 ? Path.GetFullPath(args[0]) : Path.Combine(root, ".test-runs");
Directory.CreateDirectory(target);
var reader = new PnrXdrReader(schemas[FeedType.PNR]);
foreach (string sample in new[] { "PNR.txt", "PNR_02.txt" })
{
    byte[] source = File.ReadAllBytes(Path.Combine(root, "docs/sample-docs/PNR", sample));
    var message = new FeedMessage(new(FeedType.PNR, Guid.Empty, sample, ""), source);
    var document = PnrFeedCodec.Document(codec.Parse(message));
    var decoded = new PnrDecoder(reader).Decode(message);
    if (!new PnrEncoder(new PnrXdrWriter(reader)).Encode(decoded).SequenceEqual(source))
    {
        throw new InvalidDataException(sample + " XDR round-trip failed.");
    }
    string report = Path.Combine(target, Path.GetFileNameWithoutExtension(sample) + ".decoded.json");
    File.WriteAllText(report, JsonSerializer.Serialize(PnrReportFormatter.Format(document), new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"PASS {sample} decode and exact MQ round trip; {document.Fields.Count} scalars, {document.Fields.Count(f => f.Meaning is not null)} labeled. Report: {report}");
}
