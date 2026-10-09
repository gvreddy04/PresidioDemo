using System.Text;
using System.Xml.Linq;
using PresidioDemo.Feeds;

static class ReferenceLayoutChecks
{
    private static void Check(bool condition, string description)
    {
        if (!condition)
        {
            throw new Exception(description);
        }
        Console.WriteLine("PASS " + description);
    }

    private static void Reject(Action action, string description)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            Console.WriteLine("PASS " + description);
            return;
        }
        throw new Exception("Expected reference-layout rejection: " + description);
    }

    private static FeedMessage Message(byte[] bytes, string filename = "reference.bin", FeedType feed = FeedType.ACI) =>
        new(new(feed, Guid.Empty, filename, "reference-test"), bytes);

    private static void CheckEtkt(string root, IReadOnlyDictionary<FeedType, ReferenceFeedLayout> references)
    {
        var layout = references[FeedType.ETKT];
        var codec = new ReferenceLayoutFeedCodec(layout);
        byte[] source = File.ReadAllBytes(Path.Combine(root, "docs", "sample-docs", "ETKT.txt"));
        var parsed = codec.Parse(Message(source, "ETKT.txt", FeedType.ETKT));
        var values = parsed.Fields.ToDictionary(field => field.Id, field => field.Value);
        Check(parsed.Fields.Count == 9 && codec.Serialize(parsed, values).AsSpan().SequenceEqual(source), "ETKT reference has nine configured text fields and an exact MQ no-op round trip");
        var policy = FeedPolicyConfiguration.Load(Path.Combine(root, "src", "config", "feed-policies.xml"))[FeedType.ETKT];
        var plan = ConfiguredPiiIdentifier.CreatePlan(parsed.Fields, policy);
        Check(plan.Count(item => item.Rule.Action == FieldAction.Mask) == 5 && plan.Count(item => item.Rule.Action == FieldAction.Keep) == 4 &&
            plan.All(item => item.Rule.Identification != FieldIdentification.Analyze), "ETKT resolves five direct masks and four Keeps without NLP detection");
        byte[] expected = FeedPayloadReader.ReadPayload(Message(source, "ETKT.txt", FeedType.ETKT)).ToArray();
        foreach (var (field, rule) in plan)
        {
            if (rule.Action == FieldAction.Mask)
            {
                values[field.Id] = new string('*', field.Value.Length);
                var span = layout.Fields.Single(item => item.Name == field.Id);
                Array.Fill(expected, (byte)'*', span.Offset, span.Length);
            }
        }
        byte[] output = codec.Serialize(parsed, values);
        codec.Validate(parsed, output);
        byte[] actual = FeedPayloadReader.ReadPayload(Message(output, "ETKT.txt", FeedType.ETKT));
        Check(actual.Length == 672 && actual.AsSpan().SequenceEqual(expected), "ETKT output changes only configured identifier bytes and preserves counts, padding, status and unknown binary data");
        Check(codec.Parse(Message(output, "ETKT.txt", FeedType.ETKT)).Fields.Single(field => field.Id == "/pnr").Value == values["/pnr"], "ETKT PNR remains unchanged");
        values["/pnr"] = "K7QX2M";
        Reject(() => codec.Serialize(parsed, values), "ETKT serialization rejects PNR mutation");
        values["/pnr"] = parsed.Fields.Single(field => field.Id == "/pnr").Value;
        values["/Text0152"] = "[TICKET_NUMBER]";
        Reject(() => codec.Serialize(parsed, values), "ETKT rejects protection that changes a field's byte length");
        byte[] raw = FeedPayloadReader.ReadPayload(Message(source, "ETKT.txt", FeedType.ETKT));
        raw[128] ^= 1;
        Reject(() => codec.Parse(Message(raw, feed: FeedType.ETKT)), "ETKT rejects changed unclassified binary flags");
    }

    public static void Run(string root, string workspace)
    {
        string config = Path.Combine(root, "src", "config", "reference-layouts.xml");
        var references = ReferenceLayoutConfiguration.Load(config);
        Check(references.Count == 2 && references.ContainsKey(FeedType.ACI) && references.ContainsKey(FeedType.ETKT), "only the reviewed ACI and ETKT reference layouts are enabled");
        CheckEtkt(root, references);
        var reference = references[FeedType.ACI];
        var codec = new ReferenceLayoutFeedCodec(reference);
        byte[] original = File.ReadAllBytes(Path.Combine(root, "docs", "sample-docs", "ACI.txt"));
        var parsed = codec.Parse(Message(original, "ACI.txt"));
        var values = parsed.Fields.ToDictionary(field => field.Id, field => field.Value);
        Check(parsed.Fields.Count == 23 && codec.Serialize(parsed, values).AsSpan().SequenceEqual(original), "ACI reference MQ export has 23 configured fields and an exact no-op round trip");
        var policies = FeedPolicyConfiguration.Load(Path.Combine(root, "src", "config", "feed-policies.xml"));
        var plan = ConfiguredPiiIdentifier.CreatePlan(parsed.Fields, policies[FeedType.ACI]);
        Check(plan.Count(item => item.Rule.Action == FieldAction.Mask) == 6 &&
            plan.Count(item => item.Rule.Action == FieldAction.Keep) == 17 &&
            plan.All(item => item.Rule.Identification != FieldIdentification.Analyze), "ACI applies six configured masks and 17 Keeps without NLP detection");
        foreach (var (field, rule) in plan)
        {
            if (rule.Action == FieldAction.Mask)
            {
                values[field.Id] = new string('*', field.Value.Length);
            }
        }
        byte[] output = codec.Serialize(parsed, values);
        codec.Validate(parsed, output);
        byte[] before = FeedPayloadReader.ReadPayload(Message(original, "ACI.txt"));
        byte[] after = FeedPayloadReader.ReadPayload(Message(output, "ACI.txt"));
        byte[] expected = before.ToArray();
        foreach (var span in reference.Fields)
        {
            if (policies[FeedType.ACI].Get(span.Name).Action == FieldAction.Mask)
            {
                Array.Fill(expected, (byte)'*', span.Offset, span.Length);
            }
        }
        Check(after.Length == 888 && expected.AsSpan().SequenceEqual(after), "ACI masks change only configured text bytes and preserve all binary framing and lengths");
        var result = codec.Parse(Message(output, "ACI.txt"));
        Check(result.Fields.Where(field => FieldPolicy.IsPnr(field.Name)).All(field => values[field.Id] == field.Value), "both ACI PNR copies remain unchanged while the composite reference prefix is masked");

        var rawParsed = codec.Parse(Message(before));
        Check(codec.Serialize(rawParsed, rawParsed.Fields.ToDictionary(field => field.Id, field => field.Value)).AsSpan().SequenceEqual(before), "raw ACI BIN input preserves its original representation");
        var changedName = new Dictionary<string, string>(values) { ["/PassengerName"] = "CARTER/EMMA" };
        codec.Validate(parsed, codec.Serialize(parsed, changedName));
        Console.WriteLine("PASS ACI field configuration accepts different same-length values without matching literal names");
        changedName["/PassengerName"] = "[PASSENGER_NAME]";
        Reject(() => codec.Serialize(parsed, changedName), "ACI rejects length-changing replacement before publication");
        changedName["/PassengerName"] = "";
        Reject(() => codec.Serialize(parsed, changedName), "ACI reference rejects redaction that would change frame lengths");
        changedName["/PassengerName"] = "CARTER/EMM\u00e9";
        Reject(() => codec.Serialize(parsed, changedName), "ACI rejects non-ASCII replacement");
        var changedPnr = new Dictionary<string, string>(values) { ["/pnr"] = "ABC123" };
        Reject(() => codec.Serialize(parsed, changedPnr), "ACI serializer refuses PNR mutation");
        var missing = new Dictionary<string, string>(values);
        missing.Remove("/PassengerName");
        Reject(() => codec.Serialize(parsed, missing), "ACI serialization requires every configured field");
        var extra = new Dictionary<string, string>(values) { ["/Unknown"] = "value" };
        Reject(() => codec.Serialize(parsed, extra), "ACI serialization rejects unconfigured extra fields");

        Reject(() => codec.Parse(Message(before[..^1])), "ACI rejects unaligned truncation");
        Reject(() => codec.Parse(Message(before[..^4])), "ACI rejects aligned truncation");
        Reject(() => codec.Parse(Message([.. before, 0, 0, 0, 0])), "ACI rejects trailing binary bytes");
        Reject(() => codec.Parse(Message(before, feed: FeedType.ETKT)), "ACI reference codec rejects the wrong feed route");
        foreach (var (offset, label) in new[] { (0, "version"), (403, "unclassified count"), (427, "string count"), (439, "string padding") })
        {
            byte[] altered = before.ToArray();
            altered[offset] ^= 1;
            Reject(() => codec.Parse(Message(altered)), "ACI rejects changed " + label + " bytes outside configured text");
        }
        byte[] nonAscii = before.ToArray();
        nonAscii[428] = 0xFF;
        Reject(() => codec.Parse(Message(nonAscii)), "ACI rejects non-ASCII input text");
        byte[] mismatchedPnr = before.ToArray();
        mismatchedPnr[408] ^= 1;
        Reject(() => codec.Parse(Message(mismatchedPnr)), "ACI rejects inconsistent PNR copies");
        byte[] changedContainer = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(output).Replace("A PRI 0", "A PRI 1", StringComparison.Ordinal));
        Reject(() => codec.Validate(parsed, changedContainer), "ACI validation rejects changes to MQ metadata");
        var emptySchemas = new Dictionary<FeedType, XdrFeedSchema>();
        Check(FeedCodecRegistry.Create(FeedType.ACI, emptySchemas, references) is ReferenceLayoutFeedCodec,
            "the listener registry selects the explicitly configured ACI reference codec");
        var explicitSchema = new Dictionary<FeedType, XdrFeedSchema> { [FeedType.ACI] = new(FeedType.ACI, [new("Version", "uint32")]) };
        Check(FeedCodecRegistry.Create(FeedType.ACI, explicitSchema, references) is XdrFeedCodec,
            "a real configured producer schema takes precedence over the restricted reference profile");

        Directory.CreateDirectory(workspace);
        string temporary = Path.Combine(workspace, "reference-layout-tests.xml");
        XDocument Document()
        {
            var document = XDocument.Load(config);
            document.Root!.Element("feed")!.SetAttributeValue("referenceFile", Path.Combine(root, "docs", "sample-docs", "ACI.txt"));
            return document;
        }
        void Invalid(Action<XElement> change, string label)
        {
            var document = Document();
            change(document.Root!.Element("feed")!);
            document.Save(temporary);
            Reject(() => ReferenceLayoutConfiguration.Load(temporary), label);
        }
        Invalid(feed => feed.SetAttributeValue("referenceSha256", new string('0', 64)), "ACI configuration verifies the original reference checksum");
        Invalid(feed => feed.SetAttributeValue("unknown", "true"), "ACI configuration rejects unknown attributes");
        Invalid(feed => feed.Element("text")!.SetAttributeValue("length", 3), "ACI configuration checks counted-string lengths");
        Invalid(feed => feed.Elements("text").ElementAt(1).SetAttributeValue("offset", 8), "ACI configuration rejects overlapping text spans");
        Invalid(feed => feed.Element("text")!.SetAttributeValue("offset", -1), "ACI configuration rejects invalid offsets");
        Invalid(feed => feed.Elements("text").Last().Elements("part").Last().Remove(), "ACI configuration requires complete coverage of split strings");
        Invalid(feed => feed.Elements("text").ElementAt(1).SetAttributeValue("name", "Text0008"), "ACI configuration rejects duplicate field paths");
        File.WriteAllText(temporary, "<!DOCTYPE referenceLayouts [<!ENTITY ext SYSTEM 'file:///unknown'>]><referenceLayouts>&ext;</referenceLayouts>");
        try
        {
            ReferenceLayoutConfiguration.Load(temporary);
            throw new Exception("Reference layout accepted a DTD.");
        }
        catch (System.Xml.XmlException)
        {
            Console.WriteLine("PASS ACI reference configuration rejects DTD and external entities");
        }
    }
}
