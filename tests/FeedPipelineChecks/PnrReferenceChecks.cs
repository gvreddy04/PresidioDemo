using PresidioDemo.Feeds.PNR;
using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
using PresidioDemo.Feeds.Shared;
using PresidioDemo.Pipeline;
using PresidioDemo.Protection;

/// <summary>
/// Verifies the list-based PNR projection and preserved structural bytes for every supplied PNR sample.
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
        string schemaFile = Path.Combine(root, "src/Configuration/xdr-layouts.xml");
        var references = ReferenceLayoutConfiguration.Load(config, XdrLayoutConfiguration.Load(schemaFile));
        var codec = FeedCodecRegistry.Create(FeedType.PNR, new Dictionary<FeedType, XdrFeedSchema>(), references);
        Check(codec is PnrFeedCodec, "PNR registry selects the dedicated PNR codec");
        var policy = FeedPolicyConfiguration.Load(Path.Combine(root, "src/Configuration/feed-policies.xml"))[FeedType.PNR];
        var reader = new PnrXdrReader(XdrLayoutConfiguration.Load(schemaFile)[FeedType.PNR]);
        var samples = new[]
        {
            (File: "PNR.txt", Length: 5452, Pnr: "FWGV4L", Facts: 15, Segments: 2, Remarks: 31, Masks: 71, Keeps: 71, BoardPoint: "IAH",
                Personal: new[] { "JESTE", "MOHAN.CHALLA", "SX397927", "8728251761", "21434079510", "31AUG88", "1600 SMITH ST", "127.0.0.1" }),
            (File: "PNR_02.txt", Length: 1780, Pnr: "FWGWVG", Facts: 4, Segments: 1, Remarks: 2, Masks: 21, Keeps: 34, BoardPoint: "SFO",
                Personal: new[] { "ADMIN", "DEUSERDYRDAHFMW", "01JAN90", "890989000", "45526666", "LA5K", "RXXRDV" })
        };
        foreach (var sample in samples)
        {
            byte[] source = File.ReadAllBytes(Path.Combine(root, "docs/sample-docs", sample.File));
            var delivery = new FeedDelivery(FeedType.PNR, Guid.Empty, sample.File, "reference-check");
            var message = new FeedMessage(delivery, source);
            var parsed = codec.Parse(message);
            var document = PnrFeedCodec.Document(parsed);
            int Count(string path) => document.Fields.Count(field => field.Path.Length > 0 && Wildcard(field.Path) == path);
            Check(document.PayloadLength == sample.Length && document.Fields.Single(field => field.Path == "/general/pnr").Value == sample.Pnr &&
                document.Fields.Single(field => field.Path == "/general/pnr").Meaning?.Label == "Record locator" &&
                document.Fields.Single(field => field.Path == "/names/0/surname").Meaning?.Label == "Surname" &&
                document.Fields.Single(field => field.Path == "/itinerary/0/boardPoint").Value == sample.BoardPoint &&
                document.Fields.Single(field => field.Path == "/itinerary/0/boardPoint").Meaning?.Label == "Board point" &&
                document.Fields.Single(field => field.Path == "/version").Meaning is null,
                sample.File + " maps general, passenger and itinerary fields and leaves unknown meanings unresolved");
            Check(Count("/facts/*/text") == sample.Facts && Count("/itinerary/*/boardPoint") == sample.Segments && Count("/remarks/*/text") == sample.Remarks,
                sample.File + $" decodes {sample.Facts} SSR/OSI items, {sample.Segments} segments and {sample.Remarks} remarks as linked lists");
            var standalone = new PnrDecoder(reader).Decode(message);
            Check(new PnrEncoder(new PnrXdrWriter(reader)).Encode(standalone).AsSpan().SequenceEqual(source),
                sample.File + " dedicated PNR reader and writer preserve the complete MQ export");
            var values = parsed.Fields.ToDictionary(field => field.Id, field => field.Value);
            Check(codec.Serialize(parsed, values).AsSpan().SequenceEqual(source), sample.File + " no-op round trip preserves the complete MQ export exactly");
            var plan = ConfiguredPiiIdentifier.CreatePlan(parsed.Fields, policy);
            Check(plan.Count(item => item.Rule.Action == FieldAction.Mask) == sample.Masks && plan.Count(item => item.Rule.Action == FieldAction.Keep) == sample.Keeps &&
                plan.All(item => item.Rule.Identification != FieldIdentification.Analyze),
                sample.File + $" resolves {sample.Masks} direct masks and {sample.Keeps} Keeps without NLP inference");
            byte[] expected = FeedPayloadReader.ReadPayload(message);
            foreach (var (field, rule) in plan)
            {
                if (rule.Action == FieldAction.Mask)
                {
                    var span = document.Fields.Single(item => item.Path == field.Id);
                    Check(span.Offset is not null && rule.MaskCount(field.Value) == span.Length, sample.File + " mask length matches " + field.Id);
                    values[field.Id] = new string(rule.MaskCharacter[0], span.Length);
                    Array.Fill(expected, (byte)rule.MaskCharacter[0], span.Offset!.Value, span.Length);
                }
            }
            byte[] output = codec.Serialize(parsed, values);
            codec.Validate(parsed, output);
            byte[] payload = FeedPayloadReader.ReadPayload(new(delivery, output));
            Check(payload.Length == sample.Length && payload.AsSpan().SequenceEqual(expected),
                sample.File + " serialization changes only selected bytes and preserves list flags, counts, padding and unknown bytes");
            string text = Encoding.ASCII.GetString(payload);
            Check(sample.Personal.All(value => !text.Contains(value, StringComparison.Ordinal)),
                sample.File + " selected personal values are removed from every occurrence");
            Check(codec.Parse(new(delivery, output)).Fields.Single(field => field.Id == "/general/pnr").Value == sample.Pnr,
                sample.File + " locator remains unchanged after protection");
            values["/general/pnr"] = "K7QX2M";
            Reject(() => codec.Serialize(parsed, values), sample.File + " serializer rejects locator mutation");
        }

        var baseline = FeedPayloadReader.ReadPayload(new(new(FeedType.PNR, Guid.Empty, "PNR.txt", ""), File.ReadAllBytes(Path.Combine(root, "docs/sample-docs/PNR.txt"))));
        var rawDelivery = new FeedDelivery(FeedType.PNR, Guid.Empty, "reference.bin", "reference-check");
        byte[] changed = (byte[])baseline.Clone();
        changed[3] ^= 1;
        Reject(() => codec.Parse(new(rawDelivery, changed)), "PNR rejects a changed version word");
        changed = (byte[])baseline.Clone();
        // The word after the given-name list is an empty list in every sample; content there is unmodelled.
        BinaryPrimitives.WriteUInt32BigEndian(changed.AsSpan(468, 4), 1);
        Reject(() => codec.Parse(new(rawDelivery, changed)), "PNR rejects content in an unmodelled empty list");
        changed = (byte[])baseline.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(changed.AsSpan(476, 4), 2);
        Reject(() => codec.Parse(new(rawDelivery, changed)), "PNR rejects an invalid SSR/OSI list presence word");
        Reject(() => codec.Parse(new(rawDelivery, baseline[..^4])), "PNR rejects aligned truncation");
        Reject(() => codec.Parse(new(rawDelivery, new byte[5456])), "PNR rejects different frame lengths");

        string temporary = Path.Combine(workspace, "pnr-reference-invalid.xml");
        void Invalid(Action<XElement> change, string label)
        {
            var entry = new XElement(XDocument.Load(config).Root!.Elements("feed").Single(feed => (string?)feed.Attribute("type") == "PNR"));
            entry.SetAttributeValue("referenceFile", Path.Combine(root, "docs/sample-docs/PNR.txt"));
            change(entry);
            new XDocument(new XElement("referenceLayouts", entry)).Save(temporary);
            Reject(() => ReferenceLayoutConfiguration.Load(temporary, XdrLayoutConfiguration.Load(schemaFile)), label);
        }
        Invalid(entry => entry.Elements("text").Single(text => (string?)text.Attribute("name") == "factText").Remove(),
            "reference loader rejects a list string without a mapping");
        Invalid(entry => entry.Elements("text").Single(text => (string?)text.Attribute("name") == "remarkText").SetAttributeValue("name", "factText"),
            "reference loader rejects duplicate policy aliases");
    }

    /// <summary>
    /// Converts an instance path such as /facts/3/text to its schema path /facts/*/text.
    /// </summary>
    /// <param name="path">The instance path locating one value within this message.</param>
    /// <returns>The schema path with list and array indexes replaced by wildcards.</returns>
    private static string Wildcard(string path) =>
        string.Join('/', path.Split('/').Select(part => part.Length > 0 && part.All(char.IsAsciiDigit) ? "*" : part));

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
