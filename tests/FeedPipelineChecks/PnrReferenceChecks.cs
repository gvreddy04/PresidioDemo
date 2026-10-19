using System.Text;
using System.Xml.Linq;
using PresidioDemo.Feeds;

static class PnrReferenceChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
        Console.WriteLine("PASS " + message);
    }
    private static void Reject(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            Console.WriteLine("PASS " + message);
            return;
        }
        throw new Exception("Expected PNR reference rejection: " + message);
    }
    public static void Run(string root, string workspace)
    {
        string config = Path.Combine(root, "src/config/reference-layouts.xml");
        var references = ReferenceLayoutConfiguration.Load(config, XdrLayoutConfiguration.Load(Path.Combine(root, "src/config/xdr-layouts.xml")));
        var layout = references[FeedType.PNR];
        var codec = FeedCodecRegistry.Create(FeedType.PNR, new Dictionary<FeedType, XdrFeedSchema>(), references);
        Check(codec is ReferenceLayoutFeedCodec, "PNR registry selects only the reviewed reference profile");
        byte[] source = File.ReadAllBytes(Path.Combine(root, "docs/sample-docs/PNR.txt"));
        var delivery = new FeedDelivery(FeedType.PNR, Guid.Empty, "PNR.txt", "reference-check");
        var message = new FeedMessage(delivery, source);
        var parsed = codec.Parse(message);
        var values = parsed.Fields.ToDictionary(field => field.Id, field => field.Value);
        Check(values.Count == 106 && codec.Serialize(parsed, values).AsSpan().SequenceEqual(source),
            "PNR no-op round trip preserves the complete MQ export exactly");
        var policy = FeedPolicyConfiguration.Load(Path.Combine(root, "src/config/feed-policies.xml"))[FeedType.PNR];
        var plan = ConfiguredPiiIdentifier.CreatePlan(parsed.Fields, policy);
        Check(plan.Count(item => item.Rule.Action == FieldAction.Mask) == 76 && plan.Count(item => item.Rule.Action == FieldAction.Keep) == 30 &&
            plan.All(item => item.Rule.Identification != FieldIdentification.Analyze),
            "PNR reference resolves 76 direct masks and 30 Keeps without NLP inference");
        byte[] expected = FeedPayloadReader.ReadPayload(message);
        foreach (var (field, rule) in plan)
        {
            if (rule.Action == FieldAction.Mask)
            {
                var span = layout.Fields.Single(item => item.Name == field.Id);
                Check(rule.MaskCount(field.Value) == span.Length, "PNR mask length matches " + field.Id);
                values[field.Id] = new string(rule.MaskCharacter[0], span.Length);
                Array.Fill(expected, (byte)rule.MaskCharacter[0], span.Offset, span.Length);
            }
        }
        byte[] output = codec.Serialize(parsed, values);
        codec.Validate(parsed, output);
        byte[] payload = FeedPayloadReader.ReadPayload(new(delivery, output));
        Check(payload.Length == 5452 && payload.AsSpan().SequenceEqual(expected),
            "PNR serialization changes only selected bytes and preserves all counts, padding, syntax and unknown bytes");
        string text = Encoding.ASCII.GetString(payload);
        Check(new[] { "JESTE", "MOHAN.CHALLA", "SX397927", "8728251761", "21434079510", "31AUG88", "1600 SMITH ST", "127.0.0.1" }.All(value => !text.Contains(value, StringComparison.Ordinal)),
            "selected repeated PNR personal values are removed from every occurrence in this sample");
        Check(codec.Parse(new(delivery, output)).Fields.Single(field => field.Id == "/pnr").Value == values["/pnr"],
            "PNR locator remains unchanged after reference protection");
        values["/pnr"] = "K7QX2M";
        Reject(() => codec.Serialize(parsed, values), "PNR reference serializer rejects locator mutation");
        var rawDelivery = new FeedDelivery(FeedType.PNR, Guid.Empty, "reference.bin", "reference-check");
        byte[] changed = FeedPayloadReader.ReadPayload(message);
        changed[3] ^= 1;
        Reject(() => codec.Parse(new(rawDelivery, changed)), "PNR rejects changed unclassified binary words");
        changed = FeedPayloadReader.ReadPayload(message);
        changed[500] = (byte)'Z';
        Reject(() => codec.Parse(new(rawDelivery, changed)), "PNR fixed DOCS prefix is pinned and cannot be reinterpreted");
        Reject(() => codec.Parse(new(rawDelivery, payload[..^4])), "PNR rejects aligned truncation");
        Reject(() => codec.Parse(new(rawDelivery, new byte[5456])), "PNR rejects different frame lengths");

        string temporary = Path.Combine(workspace, "pnr-reference-invalid.xml");
        void Invalid(Action<XElement> change, string label)
        {
            var entry = new XElement(XDocument.Load(config).Root!.Elements("feed").Single(feed => (string?)feed.Attribute("type") == "PNR"));
            entry.SetAttributeValue("referenceFile", Path.Combine(root, "docs/sample-docs/PNR.txt"));
            change(entry);
            new XDocument(new XElement("referenceLayouts", entry)).Save(temporary);
            Reject(() => ReferenceLayoutConfiguration.Load(temporary, XdrLayoutConfiguration.Load(Path.Combine(root, "src/config/xdr-layouts.xml"))), label);
        }
        Invalid(entry => entry.Descendants("part").First().SetAttributeValue("fixed", "yes"),
            "reference loader rejects malformed fixed-part flags");
        Invalid(entry => entry.Elements("text").First(text => text.Elements("part").Count() > 1).Elements("part").ElementAt(1).SetAttributeValue("name", "Literal1"),
            "reference loader rejects duplicate names across fixed and mutable parts");
    }
}
