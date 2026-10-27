using PresidioDemo.Feeds.PNR;
using System.Text;
using System.Xml.Linq;
using PresidioDemo.Feeds.Shared;
using PresidioDemo.Pipeline;
using PresidioDemo.Protection;

/// <summary>
/// Verifies the reviewed PNR projection and preserved structural bytes.
/// </summary>
static class PnrReferenceChecks
{

    #region Methods

    /// <summary>
    /// Executes the related regression cases against the supplied project and isolated workspace.
    /// </summary>
    /// <param name="root">The project root containing source configuration and immutable samples.</param>
    /// <param name="workspace">The isolated directory used for runtime verification artifacts.</param>
    public static void Run(string root, string workspace)
    {
        string config = Path.Combine(root, "src/Configuration/reference-layouts.xml");
        var references = ReferenceLayoutConfiguration.Load(config, XdrLayoutConfiguration.Load(Path.Combine(root, "src/Configuration/xdr-layouts.xml")));
        var layout = references[FeedType.PNR];
        var codec = FeedCodecRegistry.Create(FeedType.PNR, new Dictionary<FeedType, XdrFeedSchema>(), references);
        Check(codec is PnrFeedCodec, "PNR registry selects the dedicated PNR codec");
        byte[] source = File.ReadAllBytes(Path.Combine(root, "docs/sample-docs/PNR.txt"));
        var delivery = new FeedDelivery(FeedType.PNR, Guid.Empty, "PNR.txt", "reference-check");
        var message = new FeedMessage(delivery, source);
        var parsed = codec.Parse(message);
        var document = PnrFeedCodec.Document(parsed);
        Check(document.PayloadLength == 5452 && document.Fields.Single(field => field.Path == "/pnr").Value == "FWGV4L" &&
            document.Fields.Single(field => field.Path == "/pnr").Meaning?.Label == "Record locator" &&
            document.Fields.Single(field => field.Path == "/passengerSurname").Meaning?.Label == "Surname" &&
            document.Fields.Single(field => field.Path == "/Origin2484").Value == "IAH" &&
            document.Fields.Single(field => field.Path == "/Origin2484").Meaning?.Label == "Board point" &&
            document.Fields.Single(field => field.Path == "/RecordTag260").Value == "FQTR" &&
            document.Fields.Single(field => field.Path == "/RecordTag260").Meaning?.Label == "Service code" &&
            document.Fields.Single(field => field.Path == "/Word000").Meaning is null,
            "PNR maps general, passenger, frequent flyer and itinerary fields and leaves unknown meanings unresolved");
        var reader = new PnrXdrReader(XdrLayoutConfiguration.Load(Path.Combine(root, "src/Configuration/xdr-layouts.xml"))[FeedType.PNR]);
        var standalone = new PnrDecoder(reader).Decode(message);
        Check(new PnrEncoder(new PnrXdrWriter(reader)).Encode(standalone).AsSpan().SequenceEqual(source),
            "Dedicated PNR reader and writer preserve the complete MQ export");
        var values = parsed.Fields.ToDictionary(field => field.Id, field => field.Value);
        Check(values.Count == 106 && codec.Serialize(parsed, values).AsSpan().SequenceEqual(source),
            "PNR no-op round trip preserves the complete MQ export exactly");
        var policy = FeedPolicyConfiguration.Load(Path.Combine(root, "src/Configuration/feed-policies.xml"))[FeedType.PNR];
        var plan = ConfiguredPiiIdentifier.CreatePlan(parsed.Fields, policy);
        Check(plan.Count(item => item.Rule.Action == FieldAction.Mask) == 76 && plan.Count(item => item.Rule.Action == FieldAction.Keep) == 30 &&
            plan.All(item => item.Rule.Identification != FieldIdentification.Analyze),
            "PNR reference resolves 76 direct masks and 30 Keeps without NLP inference");
        byte[] expected = FeedPayloadReader.ReadPayload(message);
        foreach (var (field, rule) in plan)
        {
            if (rule.Action == FieldAction.Mask)
            {
                var span = layout.Fields.Single(item => item.Name == field.Name);
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
            Reject(() => ReferenceLayoutConfiguration.Load(temporary, XdrLayoutConfiguration.Load(Path.Combine(root, "src/Configuration/xdr-layouts.xml"))), label);
        }
        Invalid(entry => entry.Descendants("part").First().SetAttributeValue("fixed", "yes"),
            "reference loader rejects malformed fixed-part flags");
        Invalid(entry => entry.Elements("text").First(text => text.Elements("part").Count() > 1).Elements("part").ElementAt(1).SetAttributeValue("name", "Literal1"),
            "reference loader rejects duplicate names across fixed and mutable parts");
    }

    /// <summary>
    /// Asserts a reviewed PNR-layout condition and reports the successful check.
    /// </summary>
    /// <param name="condition">The regression condition that must hold.</param>
    /// <param name="message">The diagnostic explanation identifying this verification condition.</param>
    /// <exception cref="Exception">A regression assertion fails or an expected rejection does not occur.</exception>
    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
        Console.WriteLine("PASS " + message);
    }

    /// <summary>
    /// Asserts that an invalid fixture or configuration is rejected by the expected validation contract.
    /// </summary>
    /// <param name="action">The operation expected to satisfy or reject the regression contract.</param>
    /// <param name="message">The diagnostic explanation identifying this verification condition.</param>
    /// <exception cref="Exception">A regression assertion fails or an expected rejection does not occur.</exception>
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

    #endregion
}
