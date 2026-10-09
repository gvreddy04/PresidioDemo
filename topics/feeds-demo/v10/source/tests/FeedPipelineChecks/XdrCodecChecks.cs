using System.Buffers.Binary;
using System.Text;
using PresidioDemo.Feeds;

static class XdrCodecChecks
{
    private static XdrFieldDefinition Text(string name, int max = 128) => new(name, "string", max);
    private static readonly XdrFieldDefinition[] Fields = [
        new("Version", "uint32", Expected: 2), Text("pnr", 6),
        new("Passengers", "array", Limit: 10, Members: [new("Item", "struct", Members: [Text("PassengerId"), Text("Name"), Text("Email")])]),
        new("Accepted", "boolean"), new("Note", "optional", Members: [Text("Text")]),
        new("Choice", "union", Cases: new Dictionary<int, XdrFieldDefinition> { [7] = Text("Comment") }),
        new("Opaque", "fixedOpaque", Count: 5), new("Unsigned64", "uint64"), new("Signed64", "int64"),
        new("Float32", "float32"), new("Float64", "float64")
    ];
    private static byte[] Fixture()
    {
        using var stream = new MemoryStream();
        void Number(uint n) { byte[] b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, n); stream.Write(b); }
        void Wide(ulong n) { byte[] b = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, n); stream.Write(b); }
        void String(string value) { byte[] b = Encoding.ASCII.GetBytes(value); Number((uint)b.Length); stream.Write(b); for (int i = 0; i < ((-b.Length) & 3); i++) stream.WriteByte(0); }
        Number(2); String("K7QX2M"); Number(2);
        foreach (var passenger in new[] { new[] { "PAX-1001", "Emily Carter", "emily.carter@example.com" }, new[] { "PAX-1002", "Michael Brooks", "michael.brooks@example.com" } })
            foreach (string value in passenger) String(value);
        Number(1); Number(1); String("Operational note"); Number(7); String("Keep this comment");
        stream.Write(new byte[] { 1, 2, 3, 4, 5, 0, 0, 0 });
        Wide(ulong.MaxValue); Wide(unchecked((ulong)-42L)); Number(0x7FC00001); Wide(0x8000000000000000);
        return stream.ToArray();
    }
    private static FeedMessage Message(byte[] bytes, FeedType feed = FeedType.ACI) => new(new(feed, Guid.Empty, "test-only-xdr.bin", "fixture"), bytes);
    private static void Check(bool okay, string text) { if (!okay) throw new Exception(text); Console.WriteLine("PASS " + text); }
    private static void Reject(Action action, string text)
    {
        try { action(); } catch (InvalidDataException) { Console.WriteLine("PASS " + text); return; }
        throw new Exception("Expected XDR rejection: " + text);
    }
    public static void Run(string root, string workspace)
    {
        Check(XdrLayoutConfiguration.Load(Path.Combine(root, "src", "config", "xdr-layouts.xml")).Count == 0, "production XDR layouts remain explicitly unconfigured rather than guessed");
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
}
