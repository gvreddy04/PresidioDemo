using System.Text;
using PresidioDemo.Feeds.Shared;
using PresidioDemo.Pipeline;

/// <summary>
/// Verifies JSON formatting, field coverage, transport framing and malformed-input rejection.
/// </summary>
static class JsonCodecChecks
{

    #region Methods

    /// <summary>
    /// Executes the related regression cases against the supplied project and isolated workspace.
    /// </summary>
    /// <param name="root">The project root containing source configuration and immutable samples.</param>
    public static void Run(string root)
    {
        var codec = FeedCodecRegistry.Create(FeedType.Seats);
        string json = " {\"UARecloc\":\"K7QX2M\", \"Customers\":[{\"FirstName\":\"Emily\",\"Count\":1e2,\"Active\":true,\"Optional\":null},{\"FirstName\":\"Michael\"}]} \n";
        var message = Message(json);
        var parsed = codec.Parse(message);
        var values = parsed.Fields.ToDictionary(f => f.Id, f => f.Value);
        Require(codec.Serialize(parsed, values).AsSpan().SequenceEqual(message.Content.Span), "JSON no-op serialization is byte-for-byte exact, including numeric spelling and whitespace");
        values["/Customers/0/FirstName"] = "[PERSON]";
        values["/Customers/1/FirstName"] = "[PERSON]";
        byte[] output = codec.Serialize(parsed, values);
        codec.Validate(parsed, output);
        Require(Encoding.UTF8.GetString(output) == json.Replace("Emily", "[PERSON]").Replace("Michael", "[PERSON]"), "serialization changes only selected string spans and preserves passenger grouping");
        values["/Customers/0/Count"] = "100";
        Reject(() => codec.Serialize(parsed, values), "non-string type mutation is rejected");
        Reject(() => codec.Parse(Message("{\"x\":\"a\",\"x\":\"b\"}")), "duplicate JSON keys reject the complete message");
        Reject(() => codec.Parse(Message("{\"x\":\"a\"} {}")), "trailing JSON content rejects the complete message");
        Reject(() => codec.Parse(Message("{\"x\":\"unfinished")), "truncated JSON rejects the complete message");
        Reject(() => codec.Parse(Message("[]")), "incorrect feed root is rejected");
        string export = "A VER 2\r\nA ENC 546\r\nA CCS 1208\r\nA MSI DEMO\r\nX " + Convert.ToHexString(Encoding.UTF8.GetBytes("{\"FirstName\":\"Emily\"}"));
        var exported = codec.Parse(Message(export, "sample.txt"));
        var exportValues = exported.Fields.ToDictionary(f => f.Id, f => f.Value);
        Require(codec.Serialize(exported, exportValues).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(export)), "MQ export no-op round trip preserves exact bytes and missing final newline");
        exportValues["/FirstName"] = "[PASSENGER_NAME_WITH_A_LONGER_VALUE]";
        byte[] rebuilt = codec.Serialize(exported, exportValues);
        codec.Validate(exported, rebuilt);
        Require(!Encoding.UTF8.GetString(rebuilt).EndsWith('\n') && Encoding.UTF8.GetString(rebuilt).StartsWith(export[..export.IndexOf("X ")]), "MQ serialization retains headers, CRLF and final-newline style while payload grows");
        Reject(() => codec.Parse(Message(export.Replace("A CCS 1208", "A CCS 437"), "sample.txt")), "JSON payload with an unsupported CCSID is rejected");
        Reject(() => FeedPayloadReader.Read(Message(export + "\r\nA VER 2", "sample.txt")), "multiple MQ messages in one export are rejected");
        Reject(() => FeedPayloadReader.Read(Message(export[..export.IndexOf("X ")] + "X ZZ", "sample.txt")), "malformed MQ hex is rejected");
        foreach (var pair in new[] { ("ACI.txt", 888, "ACI"), ("PNR.txt", 5452, "PNR"), ("ETKT.txt", 672, "ETKT"), ("SEATS.txt", 1362, "Seats"), ("PNR_LINKING.txt", 2217, "PNR-Linking") })
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(root, "docs", "sample-docs", pair.Item3, pair.Item1));
            var env = FeedPayloadReader.Read(new(new(FeedType.Seats, Guid.Empty, pair.Item1, "fixture"), bytes));
            Require(env.Payload.Length == pair.Item2 && env.Serialize(env.Payload).AsSpan().SequenceEqual(bytes), "finalized " + pair.Item1 + " envelope decodes and round trips exactly");
        }
        var linking = FeedCodecRegistry.Create(FeedType.PnrLinking);
        var link = linking.Parse(Message("[{\"PNROwner\":\"K7QX2M\",\"Customers\":[{\"PaxId\":\"PAX-1001\",\"FirstName\":\"Emily\",\"UaRecordLocator\":\"K7QX2M\",\"DOB\":null}]}]", feed: FeedType.PnrLinking));
        Require(link.Fields.Count == 5 && JsonFeedCodec.StringFields(link).Count == 4 && link.Fields.Single(f => f.Id.EndsWith("/DOB")).Value == "",
            "illustrative PNR Linking exposes four string candidates and keeps the null value out of detection");
    }

    /// <summary>
    /// Creates a complete regression-test delivery with the requested feed and input bytes.
    /// </summary>
    /// <param name="json">The complete JSON fixture text.</param>
    /// <param name="name">The declared member name, configuration setting or fixture filename.</param>
    /// <param name="feed">The supported feed route.</param>
    /// <returns>The complete fixture delivery and its original bytes.</returns>
    private static FeedMessage Message(string json, string name = "sample.bin", FeedType feed = FeedType.Seats) =>
            new(new(feed, Guid.Empty, name, "fixture"), Encoding.UTF8.GetBytes(json));

    /// <summary>
    /// Asserts that an invalid fixture or configuration is rejected by the expected validation contract.
    /// </summary>
    /// <param name="action">The operation expected to satisfy or reject the regression contract.</param>
    /// <param name="label">The readable name identifying this fixture or assertion.</param>
    /// <exception cref="Exception">A regression assertion fails or an expected rejection does not occur.</exception>
    private static void Reject(Action action, string label)
    {
        try { action(); } catch (InvalidDataException) { Console.WriteLine("PASS " + label); return; }
        throw new Exception("Expected rejection: " + label);
    }

    /// <summary>
    /// Asserts a regression condition and reports a successful check.
    /// </summary>
    /// <param name="condition">The regression condition that must hold.</param>
    /// <param name="label">The readable name identifying this fixture or assertion.</param>
    /// <exception cref="Exception">A regression assertion fails or an expected rejection does not occur.</exception>
    private static void Require(bool condition, string label)
    {
        if (!condition)
        {
            throw new Exception(label);
        }

        Console.WriteLine("PASS " + label);
    }

    #endregion
}
