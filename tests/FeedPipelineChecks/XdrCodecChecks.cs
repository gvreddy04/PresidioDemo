using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
using PresidioDemo.Feeds.Shared;
using PresidioDemo.Pipeline;
using PresidioDemo.Protection;

/// <summary>
/// Verifies XDR primitives, structures, schema validation and protection boundaries.
/// </summary>
static class XdrCodecChecks
{

    #region Fields and Constants

    /// <summary>
    /// Creates reviewed fixture field policies for the requested feed route.
    /// </summary>
    private static readonly XdrFieldDefinition[] Fields = [
            new("Version", "uint32", Expected: 2), Text("pnr", 6),
        new("Passengers", "array", Limit: 10, Members: [new("Item", "struct", Members: [Text("PassengerId"), Text("Name"), Text("Email")])]),
        new("Accepted", "boolean"), new("Note", "optional", Members: [Text("Text")]),
        new("Choice", "union", Cases: new Dictionary<int, XdrFieldDefinition> { [7] = Text("Comment") }),
        new("Opaque", "fixedOpaque", Count: 5), new("Unsigned64", "uint64"), new("Signed64", "int64"),
        new("Float32", "float32"), new("Float64", "float64")
        ];

    #endregion

    #region Methods

    /// <summary>
    /// Verifies that configured protection changes only the reviewed payload spans.
    /// </summary>
    /// <param name="protector">The local configured field-protection service.</param>
    /// <returns>A task that completes when the operation and required cleanup have finished.</returns>
    public static async Task CheckProtection(PresidioFeedProtector protector)
    {
        foreach (var feed in new[] { FeedType.ACI, FeedType.PNR, FeedType.ETKT })
        {
            var codec = new XdrFeedCodec(feed, new(feed, Fields));
            var message = Message(Fixture(), feed);
            var parsed = codec.Parse(message);
            var rules = parsed.Fields.GroupBy(f => f.Name).Select(group =>
                group.Key.EndsWith("/Name") ? new FieldPolicy(group.Key, FieldIdentification.Known, FieldAction.Replace, "PERSON", "[PASSENGER_NAME]") :
                group.Key.EndsWith("/Email") ? new FieldPolicy(group.Key, FieldIdentification.Known, FieldAction.Replace, "EMAIL_ADDRESS", "[EMAIL_ADDRESS]") :
                group.Key.EndsWith("/PassengerId") ? new FieldPolicy(group.Key, FieldIdentification.Known, FieldAction.Replace, "PASSENGER_ID", "[PASSENGER_ID]") :
                new FieldPolicy(group.Key, FieldIdentification.None, FieldAction.Keep)).ToArray();
            var policy = new FeedPolicy(feed, rules);
            IFeedWorkflow workflow = feed switch
            {
                FeedType.ACI => new AciWorkflow(protector, codec, policy),
                FeedType.PNR => new PnrWorkflow(protector, codec, policy),
                _ => new EtktWorkflow(protector, codec, policy)
            };
            var result = await workflow.ProcessAsync(message, CancellationToken.None);
            var check = codec.Parse(message with { Content = result.Content });
            Check(check.Fields.Single(f => f.Id == "/pnr").Value == "K7QX2M" && check.Fields.Count(f => f.Value == "[PASSENGER_NAME]") == 2 &&
                !Encoding.ASCII.GetString(result.Content.Span).Contains("Emily"), FeedCatalog.Get(feed).FolderName + " workflow applies actual local Presidio and XDR serialization using a clearly test-only schema");
        }
    }

    /// <summary>
    /// Executes the related regression cases against the supplied project and isolated workspace.
    /// </summary>
    /// <param name="root">The project root containing source configuration and immutable samples.</param>
    /// <param name="workspace">The isolated directory used for runtime verification artifacts.</param>
    /// <exception cref="Exception">A regression assertion fails or an expected rejection does not occur.</exception>
    public static void Run(string root, string workspace)
    {
        Directory.CreateDirectory(workspace);
        CheckWordsShorthand(root, workspace);
        CheckLinkedList(workspace);
        byte[] pnrSource = File.ReadAllBytes(Path.Combine(root, "docs", "sample-docs", "PNR", "PNR.txt"));
        var pnrMessage = new FeedMessage(new(FeedType.PNR, Guid.Empty, "PNR.txt", "supplied-sample"), pnrSource);
        var pnrEnvelope = FeedPayloadReader.Read(pnrMessage);
        Check(pnrEnvelope.Payload.Length == 5452 && pnrEnvelope.Payload.Length % 4 == 0,
            "updated supplied PNR payload is four-byte aligned without modifying the source");
        var missingPnrSchema = FeedCodecRegistry.Create(FeedType.PNR);
        try
        {
            missingPnrSchema.Parse(pnrMessage);
            throw new Exception("PNR with no producer schema was accepted.");
        }
        catch (InvalidDataException error)
        {
            Check(error.Message.Contains("binary producer schema is not configured", StringComparison.Ordinal),
                "aligned supplied PNR reaches the missing-schema guard instead of the alignment error");
        }
        var truncatedPnr = new FeedMessage(new(FeedType.PNR, Guid.Empty, "truncated-pnr.bin", "test-only"), pnrEnvelope.Payload[..^1]);
        Reject(() => missingPnrSchema.Parse(truncatedPnr), "unaligned PNR truncation is still rejected before schema lookup");
        var codec = new XdrFeedCodec(FeedType.ACI, new(FeedType.ACI, Fields));
        byte[] fixture = Fixture();
        var parsed = codec.Parse(Message(fixture));
        var values = parsed.Fields.ToDictionary(f => f.Id, f => f.Value);
        Check(codec.Serialize(parsed, values).AsSpan().SequenceEqual(fixture), "schema-driven XDR round trip preserves integers, NaN bits, opaque bytes, arrays, optional and union fields exactly");
        values["/Passengers/0/Name"] = "[PASSENGER_NAME_LONGER_THAN_ORIGINAL]";
        byte[] changed = codec.Serialize(parsed, values);
        codec.Validate(parsed, changed);
        Check(changed.Length % 4 == 0 && codec.Parse(Message(changed)).Fields.Single(f => f.Id == "/Passengers/1/Name").Value == "Michael Brooks", "XDR changing string lengths recalculates counts and zero padding without shifting logical passenger associations");
        values["/pnr"] = "OTHER1";
        Reject(() => codec.Validate(parsed, codec.Serialize(parsed, values)), "XDR PNR mutation is rejected");
        values["/pnr"] = "K7QX2M";
        values["/Accepted"] = "false";
        Reject(() => codec.Serialize(parsed, values), "unsupported non-string protection rejects instead of publishing raw values");
        values["/Accepted"] = "true";
        values["/Passengers/0/Name"] = "Emily \u00e9";
        Reject(() => codec.Serialize(parsed, values), "non-ASCII XDR replacement is rejected by explicit ASCII string schema");
        values["/Passengers/0/Name"] = new string('X', 129);
        Reject(() => codec.Serialize(parsed, values), "XDR replacement exceeding declared maximum is rejected");
        Reject(() => codec.Parse(Message(fixture[..^1])), "unaligned XDR truncation rejects complete message");
        Reject(() => codec.Parse(Message(fixture[..^4])), "aligned XDR truncation is detected while consuming schema");
        Reject(() => codec.Parse(Message([.. fixture, 0, 0, 0, 0])), "XDR trailing bytes are rejected");
        byte[] badPadding = (byte[])fixture.Clone(); badPadding[14] = 1;
        Reject(() => codec.Parse(Message(badPadding)), "nonzero XDR string padding is rejected");
        byte[] badCount = (byte[])fixture.Clone(); BinaryPrimitives.WriteUInt32BigEndian(badCount.AsSpan(16, 4), 11);
        Reject(() => codec.Parse(Message(badCount)), "oversized XDR array count is rejected");
        byte[] badVersion = (byte[])fixture.Clone(); badVersion[3] = 3;
        Reject(() => codec.Parse(Message(badVersion)), "XDR expected version constant is enforced");
        Directory.CreateDirectory(workspace);
        string config = Path.Combine(workspace, "test-only-xdr.xml");
        string xml = "<xdrLayouts><feed type=\"ACI\" configured=\"true\"><uint32 name=\"Version\" expected=\"2\"/><string name=\"pnr\" max=\"6\"/><array name=\"Passengers\" max=\"10\"><struct name=\"Item\"><string name=\"Name\" max=\"128\"/></struct></array></feed><feed type=\"PNR\" configured=\"false\"/><feed type=\"ETKT\" configured=\"false\"/></xdrLayouts>";
        File.WriteAllText(config, xml);
        Check(XdrLayoutConfiguration.Load(config)[FeedType.ACI].Fields.Count == 3, "XDR XML loader accepts explicit nested schema with bounds and version constant");
        File.WriteAllText(config, xml.Replace("max=\"10\"", "max=\"0\""));
        Reject(() => XdrLayoutConfiguration.Load(config), "XDR XML rejects invalid collection maximum");
        File.WriteAllText(config, xml.Replace("<uint32 name=\"Version\" expected=\"2\"/>", "<uint32 name=\"Version\" expected=\"2\"/><uint32 name=\"Version\"/>"));
        Reject(() => XdrLayoutConfiguration.Load(config), "XDR XML rejects duplicate schema names");
        File.WriteAllText(config, xml.Replace("<string name=\"pnr\" max=\"6\"/>", "<string name=\"pnr\" max=\"6\" typo=\"yes\"/>"));
        Reject(() => XdrLayoutConfiguration.Load(config), "XDR XML rejects unknown settings");
        File.WriteAllText(config, "<!DOCTYPE xdrLayouts [<!ENTITY ext SYSTEM 'file:///missing'>]><xdrLayouts>&ext;</xdrLayouts>");
        try { XdrLayoutConfiguration.Load(config); throw new Exception("XDR DTD accepted"); }
        catch (System.Xml.XmlException) { Console.WriteLine("PASS XDR configuration rejects DTD and external entities"); }
    }

    /// <summary>
    /// Asserts an XDR fixture condition and reports the successful check.
    /// </summary>
    /// <param name="okay">The regression condition that must hold.</param>
    /// <param name="text">The complete MQ-export text or decoded fixture text.</param>
    /// <exception cref="Exception">A regression assertion fails or an expected rejection does not occur.</exception>
    private static void Check(bool okay, string text) { if (!okay) { throw new Exception(text); } Console.WriteLine("PASS " + text); }

    /// <summary>
    /// Verifies that raw-word shorthand expands to the original fixed-array representation.
    /// </summary>
    /// <param name="root">The project root containing source configuration and immutable samples.</param>
    /// <param name="workspace">The isolated directory used for runtime verification artifacts.</param>
    private static void CheckWordsShorthand(string root, string workspace)
    {
        string active = Path.Combine(root, "src", "Configuration", "xdr-layouts.xml");
        var compact = XdrLayoutConfiguration.Load(active);
        var expanded = XDocument.Load(active);
        var blocks = expanded.Descendants("words").ToArray();
        Check(blocks.Length > 0, "production XDR configuration uses words shorthand");
        foreach (var block in blocks)
        {
            block.ReplaceWith(new XElement("fixedArray", new XAttribute("name", block.Attribute("name")!.Value),
                        new XAttribute("count", block.Attribute("count")!.Value),
                        new XElement("uint32", new XAttribute("name", "Value"))));
        }

        string legacyFile = Path.Combine(workspace, "expanded-original-xdr.xml");
        expanded.Save(legacyFile);
        var legacy = XdrLayoutConfiguration.Load(legacyFile);
        int samples = 0;
        foreach (string source in Directory.GetFiles(Path.Combine(root, "docs", "sample-docs"), "*.txt", SearchOption.AllDirectories))
        {
            string name = Path.GetFileNameWithoutExtension(source);
            FeedType? feed = name.StartsWith("ACI", StringComparison.Ordinal) ? FeedType.ACI :
                name.StartsWith("ETKT", StringComparison.Ordinal) ? FeedType.ETKT :
                name == "PNR" || name.StartsWith("PNR_0", StringComparison.Ordinal) ? FeedType.PNR : null;
            if (feed is null)
            {
                continue;
            }

            var message = new FeedMessage(new(feed.Value, Guid.Empty, Path.GetFileName(source), "supplied-sample"),
                            File.ReadAllBytes(source));
            var codec = new XdrFeedCodec(feed.Value, compact[feed.Value]);
            var originalCodec = new XdrFeedCodec(feed.Value, legacy[feed.Value]);
            var parsed = codec.Parse(message);
            Check(parsed.Fields.SequenceEqual(originalCodec.Parse(message).Fields),
                name + " shorthand and original XML produce identical field IDs, paths and values");
            byte[] output = codec.Serialize(parsed, parsed.Fields.ToDictionary(field => field.Id, field => field.Value));
            codec.Validate(parsed, output);
            Check(output.AsSpan().SequenceEqual(message.Content.Span), name + " words shorthand preserves every source byte");
            samples++;
        }
        Check(samples == 14, "words compatibility covers all 14 supplied binary samples");

        string invalid = Path.Combine(workspace, "invalid-words.xml");
        foreach (string declaration in new[] {
            "<words name='Block' count='0'/>", "<words name='Block' count='-1'/>",
            "<words name='Block' count='1048577'/>", "<words name='Block'/>",
            "<words name='Block' count='2' typo='yes'/>", "<words name='Block' count='2'>unexpected</words>",
            "<words name='Block' count='2'><uint32 name='Value'/></words>" })
        {
            File.WriteAllText(invalid, "<xdrLayouts><feed type='ACI' configured='true'>" + declaration +
                "</feed><feed type='PNR' configured='false'/><feed type='ETKT' configured='false'/></xdrLayouts>");
            Reject(() => XdrLayoutConfiguration.Load(invalid), "invalid words declaration rejected: " + declaration);
        }
    }

    /// <summary>
    /// Verifies rpcgen-style linked lists: presence words, terminator, bounds and structural validation.
    /// </summary>
    /// <param name="workspace">The isolated directory used for runtime verification artifacts.</param>
    private static void CheckLinkedList(string workspace)
    {
        XdrFieldDefinition[] fields = [new("Version", "uint32", Expected: 2),
            new("Items", "list", Limit: 3, Members: [new("Item", "struct", Members: [Text("Name")])]), new("Trailer", "uint32")];
        var codec = new XdrFeedCodec(FeedType.ACI, new(FeedType.ACI, fields));
        byte[] List(uint[] flags, params string[] names)
        {
            using (var stream = new MemoryStream())
            {
                void Number(uint n) { byte[] b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, n); stream.Write(b); }
                Number(2);
                for (int i = 0; i < names.Length; i++)
                {
                    Number(flags[i]);
                    byte[] b = Encoding.ASCII.GetBytes(names[i]);
                    Number((uint)b.Length); stream.Write(b); stream.Write(new byte[(-b.Length) & 3]);
                }
                Number(0); Number(9);
                return stream.ToArray();
            }
        }
        byte[] fixture = List([1, 1], "Emily Carter", "Michael Brooks");
        var parsed = codec.Parse(Message(fixture));
        Check(parsed.Fields.Where(f => f.Name == "/Items/*/Name").Select(f => f.Id).SequenceEqual(["/Items/0/Name", "/Items/1/Name"]) &&
            parsed.Fields.Single(f => f.Id == "/Trailer").Value == "9", "XDR list reads each present element under a wildcard path and continues after the terminator");
        var values = parsed.Fields.ToDictionary(f => f.Id, f => f.Value);
        Check(codec.Serialize(parsed, values).AsSpan().SequenceEqual(fixture), "XDR list round trip preserves presence words and terminator exactly");
        values["/Items/0/Name"] = "[PASSENGER_NAME]";
        byte[] changed = codec.Serialize(parsed, values);
        codec.Validate(parsed, changed);
        Check(codec.Parse(Message(changed)).Fields.Single(f => f.Id == "/Items/1/Name").Value == "Michael Brooks", "XDR list element replacement keeps later elements aligned");
        Check(codec.Parse(Message(List([]))).Fields.All(f => !f.Name.StartsWith("/Items/", StringComparison.Ordinal)), "empty XDR list is a single terminator word");
        Reject(() => codec.Parse(Message(List([1, 2], "A", "B"))), "XDR list presence values other than 0 or 1 are rejected");
        Reject(() => codec.Parse(Message(List([1, 1, 1, 1], "A", "B", "C", "D"))), "XDR list longer than its maximum is rejected");
        Reject(() => codec.Parse(Message(fixture[..^8])), "XDR list without its terminator is rejected");
        Reject(() => codec.Validate(parsed, List([1, 1, 1], "Emily Carter", "Michael Brooks", "Extra")), "XDR list element count change fails structural validation");
        string config = Path.Combine(workspace, "test-only-list.xml");
        string xml = "<xdrLayouts><feed type=\"ACI\" configured=\"true\"><list name=\"Items\" max=\"3\"><struct name=\"Item\"><string name=\"Name\" max=\"64\"/></struct></list></feed><feed type=\"PNR\" configured=\"false\"/><feed type=\"ETKT\" configured=\"false\"/></xdrLayouts>";
        File.WriteAllText(config, xml);
        Check(XdrLayoutConfiguration.Load(config)[FeedType.ACI].Fields.Single().Kind == "list", "XDR XML loader accepts a bounded linked list");
        File.WriteAllText(config, xml.Replace(" max=\"3\"", ""));
        Reject(() => XdrLayoutConfiguration.Load(config), "XDR XML rejects a linked list without a maximum");
        File.WriteAllText(config, xml.Replace("</struct></list>", "</struct><uint32 name=\"Other\"/></list>"));
        Reject(() => XdrLayoutConfiguration.Load(config), "XDR XML rejects a linked list with more than one element type");
    }

    /// <summary>
    /// Builds an independent binary or message fixture for the requested verification case.
    /// </summary>
    /// <returns>The complete independently encoded regression-fixture payload.</returns>
    private static byte[] Fixture()
    {
        using (var stream = new MemoryStream())
        {
            void Number(uint n) { byte[] b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, n); stream.Write(b); }
            void Wide(ulong n) { byte[] b = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, n); stream.Write(b); }
            void String(string value) { byte[] b = Encoding.ASCII.GetBytes(value); Number((uint)b.Length); stream.Write(b); for (int i = 0; i < ((-b.Length) & 3); i++) { stream.WriteByte(0); } }
            Number(2); String("K7QX2M"); Number(2);
            foreach (var passenger in new[] { new[] { "PAX-1001", "Emily Carter", "emily.carter@example.com" }, new[] { "PAX-1002", "Michael Brooks", "michael.brooks@example.com" } })
            {
                foreach (string value in passenger)
                {
                    String(value);
                }
            }

            Number(1); Number(1); String("Operational note"); Number(7); String("Keep this comment");
            stream.Write(new byte[] { 1, 2, 3, 4, 5, 0, 0, 0 });
            Wide(ulong.MaxValue); Wide(unchecked((ulong)-42L)); Number(0x7FC00001); Wide(0x8000000000000000);
            return stream.ToArray();
        }
    }

    /// <summary>
    /// Creates a complete regression-test delivery with the requested feed and input bytes.
    /// </summary>
    /// <param name="bytes">The complete payload or fixture bytes to read, encode or compare.</param>
    /// <param name="feed">The supported feed route.</param>
    /// <returns>The complete fixture delivery and its original bytes.</returns>
    private static FeedMessage Message(byte[] bytes, FeedType feed = FeedType.ACI) => new(new(feed, Guid.Empty, "test-only-xdr.bin", "fixture"), bytes);

    /// <summary>
    /// Asserts that an invalid fixture or configuration is rejected by the expected validation contract.
    /// </summary>
    /// <param name="action">The operation expected to satisfy or reject the regression contract.</param>
    /// <param name="text">The complete MQ-export text or decoded fixture text.</param>
    /// <exception cref="Exception">A regression assertion fails or an expected rejection does not occur.</exception>
    private static void Reject(Action action, string text)
    {
        try { action(); } catch (InvalidDataException) { Console.WriteLine("PASS " + text); return; }
        throw new Exception("Expected XDR rejection: " + text);
    }

    /// <summary>
    /// Creates a bounded counted-string declaration for an independent XDR fixture.
    /// </summary>
    /// <param name="name">The declared member name, configuration setting or fixture filename.</param>
    /// <param name="max">The maximum supported length for the counted value.</param>
    /// <returns>The bounded counted-string declaration used by the independent XDR fixture.</returns>
    private static XdrFieldDefinition Text(string name, int max = 128) => new(name, "string", max);

    #endregion
}
