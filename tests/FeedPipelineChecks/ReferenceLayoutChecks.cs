using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
using PresidioDemo.Feeds.ACI;
using PresidioDemo.Feeds.ETKT;
using PresidioDemo.Feeds.Shared;
using PresidioDemo.Pipeline;
using PresidioDemo.Protection;

/// <summary>
/// Verifies reference projections, policy coverage and preserved binary payload bytes.
/// </summary>
static class ReferenceLayoutChecks
{

    #region Methods

    /// <summary>
    /// Verifies that configured protection changes only the reviewed payload spans.
    /// </summary>
    /// <param name="root">The project root containing source configuration and immutable samples.</param>
    /// <param name="workspace">The isolated directory used for runtime verification artifacts.</param>
    /// <param name="protector">The local configured field-protection service.</param>
    /// <returns>A task that completes when the operation and required cleanup have finished.</returns>
    public static async Task CheckProtection(string root, string workspace, PresidioFeedProtector protector)
    {
        var config = Load(root);
        foreach (var (feed, file) in Samples(root))
        {
            var codec = Codec(feed, config);
            var message = Message(feed, File.ReadAllBytes(file), Path.GetFileName(file));
            var parsed = codec.Parse(message);
            var values = await protector.ProtectAsync(parsed.Fields, config.Policies[feed], CancellationToken.None);
            byte[] output = codec.Serialize(parsed, values); codec.Validate(parsed, output);
            var again = codec.Parse(message with { Content = output });
            foreach (var field in again.Fields.Where(f => config.Policies[feed].Get(f.Name).Action == FieldAction.Mask))
            {
                Check(field.Value.All(c => c == config.Policies[feed].Get(field.Name).MaskCharacter[0]), Path.GetFileName(file) + " masks " + field.Name);
            }

            var second = await protector.ProtectAsync(again.Fields, config.Policies[feed], CancellationToken.None);
            Check(codec.Serialize(again, second).AsSpan().SequenceEqual(output), Path.GetFileName(file) + " masking is idempotent");
            if (Path.GetFileName(file) == "ETKT_05.txt")
            {
                byte[] before = FeedPayloadReader.ReadPayload(message), after = FeedPayloadReader.ReadPayload(message with { Content = output });
                Check(before.AsSpan(132, 8).SequenceEqual(after.AsSpan(132, 8)), "Text0132 remains two unchanged raw words");
                Check(after.AsSpan(488, 6).SequenceEqual("FWGXKN"u8) && after.AsSpan(500, 6).SequenceEqual("FWDZRF"u8), "divide-event new and old PNRs remain unchanged");
            }
        }
    }

    /// <summary>
    /// Executes the related regression cases against the supplied project and isolated workspace.
    /// </summary>
    /// <param name="root">The project root containing source configuration and immutable samples.</param>
    /// <param name="workspace">The isolated directory used for runtime verification artifacts.</param>
    public static void Run(string root, string workspace)
    {
        var config = Load(root);
        Check(config.References.Count == 3 && config.Policies.Count == 5, "original reference, policy and XDR formats load together");
        foreach (var (feed, file) in Samples(root))
        {
            var codec = Codec(feed, config);
            byte[] source = File.ReadAllBytes(file);
            var parsed = codec.Parse(Message(feed, source, Path.GetFileName(file)));
            bool expectedCodec = feed switch
            {
                FeedType.ACI => codec is PresidioDemo.Feeds.ACI.AciFeedCodec,
                FeedType.ETKT => codec is PresidioDemo.Feeds.ETKT.EtktFeedCodec,
                _ => codec is ReferenceLayoutFeedCodec
            };
            Check(expectedCodec &&
                codec.Serialize(parsed, parsed.Fields.ToDictionary(f => f.Id, f => f.Value)).AsSpan().SequenceEqual(source),
                Path.GetFileName(file) + " selected codec preserves every MQ byte");
            _ = ConfiguredPiiIdentifier.CreatePlan(parsed.Fields, config.Policies[feed]);
        }
        var aci = Codec(FeedType.ACI, config);
        byte[] raw = FeedPayloadReader.ReadPayload(Message(FeedType.ACI, File.ReadAllBytes(Path.Combine(root, "docs/sample-docs/ACI.txt")), "ACI.txt"));
        var original = aci.Parse(Message(FeedType.ACI, raw));
        string name = original.Fields.Single(f => f.Name == "/passengerName").Value;
        int offset = raw.AsSpan().IndexOf(Encoding.ASCII.GetBytes(name)) - 4;
        byte[] nextName = Encoding.ASCII.GetBytes("SMITH/ALEXANDRA");
        using (var changed = new MemoryStream())
        {
            changed.Write(raw.AsSpan(0, offset));
            byte[] count = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(count, (uint)nextName.Length);
            changed.Write(count); changed.Write(nextName); changed.Write(new byte[(-nextName.Length) & 3]);
            changed.Write(raw.AsSpan(offset + 4 + name.Length + ((-name.Length) & 3)));
            Check(aci.Parse(Message(FeedType.ACI, changed.ToArray())).Fields.Single(f => f.Name == "/passengerName").Value == "SMITH/ALEXANDRA", "actual name width and following offsets vary without changing reference baseline or code");
            var values = original.Fields.ToDictionary(f => f.Id, f => f.Value);
            values["/passengerName"] = "X";
            Reject(() => aci.Serialize(original, values), "reference serialization rejects width changes");
            values["/passengerName"] = name; values["/pnr"] = "OTHER1";
            Reject(() => aci.Serialize(original, values), "reference serialization rejects PNR changes");
            Reject(() => aci.Parse(Message(FeedType.ACI, [.. raw, 0, 0, 0, 0])), "unknown trailing binary regions are retained");
            byte[] broken = (byte[])raw.Clone(); broken[869] ^= 1;
            Reject(() => aci.Parse(Message(FeedType.ACI, broken)), "reference PNR copy must agree with main PNR");

            string referenceFile = Path.Combine(workspace, "isolated-reference.xml");
            string policyFile = Path.Combine(workspace, "isolated-policies.xml");
            var baseline = XDocument.Load(Path.Combine(root, "src/Configuration/reference-layouts.xml"));
            foreach (var feed in baseline.Root!.Elements("feed"))
            {
                feed.SetAttributeValue("referenceFile", "missing-review-only-sample.txt");
            }

            baseline.Save(referenceFile);
            Check(Load(root, reference: referenceFile).References.Count == 3, "schema-driven mode never opens referenceFile provenance");
            void Invalid(Action<XDocument> mutation, string label)
            {
                var document = new XDocument(baseline); mutation(document); document.Save(referenceFile);
                Reject(() => Load(root, reference: referenceFile), label);
            }
            Invalid(doc => doc.Descendants("text").First().SetAttributeValue("path", "/Missing"), "missing XDR text mapping fails startup");
            Invalid(doc => doc.Descendants("text").First().SetAttributeValue("pattern", "["), "invalid reference pattern fails startup");
            Invalid(doc => doc.Root!.Add(new XElement(doc.Root.Elements("feed").First())), "duplicate reference feed fails startup");
            Invalid(doc => doc.Descendants("part").First(p => p.Attribute("equalTo") is not null).SetAttributeValue("equalTo", "/Missing"), "unknown PNR reference target fails startup");
            var policies = XDocument.Load(Path.Combine(root, "src/Configuration/feed-policies.xml"));
            policies.Descendants("field").First().SetAttributeValue("policy", "Missing"); policies.Save(policyFile);
            Reject(() => FeedPolicyConfiguration.Load(policyFile), "unknown shared policy fails startup");

            // Existing XML without extension attributes retains its exact pinned-frame behavior.
            string legacyReference = Path.Combine(workspace, "legacy-reference.xml");
            byte[] fixture = [0, 0, 0, 2, 0, 0, 0, 6, .. "K7QX2M"u8.ToArray(), 0, 0];
            string fixtureFile = Path.Combine(workspace, "legacy.bin"); File.WriteAllBytes(fixtureFile, fixture);
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fixture));
            new XDocument(new XElement("referenceLayouts", new XElement("feed", new XAttribute("type", "ACI"), new XAttribute("name", "Legacy"), new XAttribute("referenceFile", fixtureFile), new XAttribute("referenceSha256", hash), new XElement("text", new XAttribute("name", "pnr"), new XAttribute("offset", 4), new XAttribute("length", 6))))).Save(legacyReference);
            var legacy = ReferenceLayoutConfiguration.Load(legacyReference);
            var legacyCodec = FeedCodecRegistry.Create(FeedType.ACI, new Dictionary<FeedType, XdrFeedSchema>(), legacy);
            var legacyParsed = legacyCodec.Parse(Message(FeedType.ACI, fixture));
            Check(legacyCodec.Serialize(legacyParsed, legacyParsed.Fields.ToDictionary(f => f.Id, f => f.Value)).AsSpan().SequenceEqual(fixture), "unextended legacy XML and reference codec remain compatible");
            fixture[3] = 3;
            Reject(() => legacyCodec.Parse(Message(FeedType.ACI, fixture)), "legacy unclassified fixed bytes remain pinned");
        }
    }

    /// <summary>
    /// Asserts a reference-layout regression condition and reports the successful check.
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
    /// Creates the codec selected by the fixture&apos;s reviewed schemas and reference mappings.
    /// </summary>
    /// <param name="feed">The supported feed route.</param>
    /// <param name="config">The reviewed schemas, reference mappings and policies used by the fixture.</param>
    /// <returns>The feed codec selected by the reviewed fixture configuration.</returns>
    private static IFeedCodec Codec(FeedType feed, Configuration config) => FeedCodecRegistry.Create(feed, config.Schemas, config.References);

    /// <summary>
    /// Loads the reviewed XDR schemas, reference mappings and field policies used by regression checks.
    /// </summary>
    /// <param name="root">The project root containing source configuration and immutable samples.</param>
    /// <param name="policy">The reviewed field rules for this feed route.</param>
    /// <param name="reference">The optional alternate reviewed reference-configuration path.</param>
    /// <returns>The complete validated configuration indexed by feed route, or its verification bundle.</returns>
    private static Configuration Load(string root, string? policy = null, string? reference = null)
    {
        var schemas = XdrLayoutConfiguration.Load(Path.Combine(root, "src/Configuration/xdr-layouts.xml"));
        return new(schemas, ReferenceLayoutConfiguration.Load(reference ?? Path.Combine(root, "src/Configuration/reference-layouts.xml"), schemas),
            FeedPolicyConfiguration.Load(policy ?? Path.Combine(root, "src/Configuration/feed-policies.xml")));
    }

    /// <summary>
    /// Creates a complete regression-test delivery with the requested feed and input bytes.
    /// </summary>
    /// <param name="feed">The supported feed route.</param>
    /// <param name="bytes">The complete payload or fixture bytes to read, encode or compare.</param>
    /// <param name="name">The declared member name, configuration setting or fixture filename.</param>
    /// <returns>The complete fixture delivery and its original bytes.</returns>
    private static FeedMessage Message(FeedType feed, byte[] bytes, string name = "fixture.bin") => new(new(feed, Guid.Empty, name, "test"), bytes);

    /// <summary>
    /// Asserts that an invalid fixture or configuration is rejected by the expected validation contract.
    /// </summary>
    /// <param name="action">The operation expected to satisfy or reject the regression contract.</param>
    /// <param name="message">The diagnostic explanation identifying this verification condition.</param>
    /// <exception cref="Exception">A regression assertion fails or an expected rejection does not occur.</exception>
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (InvalidDataException) { Console.WriteLine("PASS " + message); return; }
        throw new Exception("Expected rejected configuration/input: " + message);
    }

    /// <summary>
    /// Enumerates the reviewed binary sample files and their feed routes.
    /// </summary>
    /// <param name="root">The project root containing source configuration and immutable samples.</param>
    /// <returns>The reviewed binary sample paths paired with their feed routes.</returns>
    private static IEnumerable<(FeedType Feed, string File)> Samples(string root) =>
            Directory.GetFiles(Path.Combine(root, "docs/sample-docs"), "*.txt")
                .Where(file => Path.GetFileName(file).StartsWith("ACI", StringComparison.Ordinal) || Path.GetFileName(file).StartsWith("ETKT", StringComparison.Ordinal) || Path.GetFileName(file) == "PNR.txt")
                .Select(file => (Path.GetFileName(file).StartsWith("ACI", StringComparison.Ordinal) ? FeedType.ACI : Path.GetFileName(file).StartsWith("ETKT", StringComparison.Ordinal) ? FeedType.ETKT : FeedType.PNR, file));

    #endregion

    #region Nested Types

    /// <summary>
    /// Groups the reviewed XDR schemas, reference projections and protection policies used by checks.
    /// </summary>
    /// <param name="Schemas">The reviewed XDR schemas indexed by feed route.</param>
    /// <param name="References">The reviewed reference projections indexed by feed route.</param>
    /// <param name="Policies">The reviewed protection policies indexed by feed route.</param>
    private sealed record Configuration(IReadOnlyDictionary<FeedType, XdrFeedSchema> Schemas,
            IReadOnlyDictionary<FeedType, ReferenceFeedLayout> References, IReadOnlyDictionary<FeedType, FeedPolicy> Policies);

    #endregion
}
