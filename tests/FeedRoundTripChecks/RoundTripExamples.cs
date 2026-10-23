using System.Buffers.Binary;
using System.Text;
using PresidioDemo.Feeds.Shared;

internal sealed record RoundTripExample(FeedType Feed, string Name, string SourceSample,
    string Category, string Extension, byte[] Bytes);

internal static class RoundTripExamples
{
    public static IReadOnlyList<RoundTripExample> Create(string root, Func<FeedType, IFeedCodec> codecFactory)
    {
        var examples = new List<RoundTripExample>();
        var baselines = new Dictionary<FeedType, (string Sample, FeedEnvelope Envelope)>();
        foreach (string file in Directory.GetFiles(Path.Combine(root, "docs/sample-docs"), "*.txt").Order())
        {
            string sample = Path.GetFileName(file);
            FeedType feed = Route(sample);
            byte[] bytes = File.ReadAllBytes(file);
            var envelope = FeedPayloadReader.Read(Message(feed, sample, bytes));
            examples.Add(new(feed, sample + "/supplied-export", sample, "Supplied export", ".txt", bytes));
            examples.Add(new(feed, sample + "/raw-payload", sample, "Supplied raw payload", ".bin", envelope.Payload));
            baselines.TryAdd(feed, (sample, envelope));
        }
        if (baselines.Count != FeedCatalog.All.Count)
        {
            throw new Exception("Round-trip examples must cover every feed route.");
        }
        foreach (var (feed, baseline) in baselines)
        {
            // Exercise metadata/framing bytes as well as the payload, including a one-byte hex row.
            foreach (string newline in new[] { "\n", "\r\n" })
            {
                foreach (bool finalNewline in new[] { false, true })
                {
                    foreach (bool lowercase in new[] { false, true })
                    {
                        foreach (int rowWidth in new[] { 1, 17, 64 })
                        {
                            string name = $"{FeedCatalog.Get(feed).FolderName}/export-{(newline.Length == 1 ? "LF" : "CRLF")}-{finalNewline}-{lowercase}-{rowWidth}";
                            byte[] bytes = Export(baseline.Envelope, baseline.Envelope.Payload, newline, finalNewline, lowercase, rowWidth);
                            examples.Add(new(feed, name, baseline.Sample, "MQ framing variation", ".txt", bytes));
                        }
                    }
                }
            }
            examples.Add(new(feed, feed + "/irregular-hex-rows", baseline.Sample, "MQ framing variation", ".txt",
                Export(baseline.Envelope, baseline.Envelope.Payload, "\r\n", false, true, 5, irregular: true)));
            examples.Add(new(feed, feed + "/diagnostic-dump", baseline.Sample, "Complete diagnostic dump", ".txt",
                Diagnostic(baseline.Envelope.Payload)));

            if (feed is FeedType.ACI or FeedType.ETKT or FeedType.PNR)
            {
                string fieldName = feed switch
                {
                    FeedType.ACI => "/passengerName",
                    FeedType.ETKT => "/ticketNumber",
                    _ => "/passengerSurname"
                };
                var parsed = codecFactory(feed).Parse(Message(feed, "example.bin", baseline.Envelope.Payload));
                string current = parsed.Fields.Single(field => field.Name == fieldName).Value;
                string[] replacements = feed switch
                {
                    FeedType.ACI => ["", "REED/AVA", "COLE/JACK", "FOX/OLIVIA", "CARTER/EMMA", "BROOKS/MICHAEL"],
                    FeedType.ETKT => ["", "1", "12", "123", "1234", "0161234567890", "0161234567890123456789"],
                    _ => ["", "REED", "STONE", "CARTER", "FOX", "WASHINGTON"]
                };
                foreach (string replacement in replacements)
                {
                    // Build fixtures independently, rather than use the serializer under test.
                    byte[] payload = ReplaceCountedString(baseline.Envelope.Payload, current, replacement);
                    AddPayload(feed, baseline, $"binary-string-length-{replacement.Length}", "Binary width/padding variation", payload);
                }
            }
            else
            {
                foreach (var (name, bytes) in JsonRoundTripExamples.Create(baseline.Envelope.Payload))
                {
                    AddPayload(feed, baseline, name, "JSON value/format variation", bytes);
                }
            }
        }
        return examples.AsReadOnly();

        void AddPayload(FeedType feed, (string Sample, FeedEnvelope Envelope) baseline, string name, string category, byte[] payload)
        {
            examples.Add(new(feed, feed + "/" + name + "/raw", baseline.Sample, category, ".bin", payload));
            examples.Add(new(feed, feed + "/" + name + "/export", baseline.Sample, category, ".txt",
                Export(baseline.Envelope, payload, "\n", false, true, 17)));
        }
    }

    internal static FeedMessage Message(FeedType feed, string name, byte[] bytes) =>
        new(new(feed, Guid.Empty, name, "round-trip-test-only"), bytes);

    private static FeedType Route(string sample) => sample switch
    {
        "SEATS.txt" => FeedType.Seats,
        "PNR_LINKING.txt" => FeedType.PnrLinking,
        "PNR.txt" => FeedType.PNR,
        _ when sample.StartsWith("ACI", StringComparison.Ordinal) => FeedType.ACI,
        _ when sample.StartsWith("ETKT", StringComparison.Ordinal) => FeedType.ETKT,
        _ => throw new Exception("Unmapped sample feed: " + sample)
    };

    private static byte[] Export(FeedEnvelope source, byte[] payload, string newline, bool finalNewline,
        bool lowercase, int rowWidth, bool irregular = false)
    {
        string header = source.Header.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newline, StringComparison.Ordinal);
        var output = new StringBuilder(header);
        int row = 0;
        for (int offset = 0; offset < payload.Length; row++)
        {
            int width = Math.Min(irregular ? new[] { 5, 19, 3, 47 }[row % 4] : rowWidth, payload.Length - offset);
            string hex = Convert.ToHexString(payload.AsSpan(offset, width));
            output.Append("X ").Append(lowercase ? hex.ToLowerInvariant() : hex);
            offset += width;
            if (offset < payload.Length || finalNewline)
            {
                output.Append(newline);
            }
        }
        return Encoding.UTF8.GetBytes(output.ToString());
    }

    private static byte[] Diagnostic(byte[] payload)
    {
        var output = new StringBuilder($"AMQSBCG0\r\nlength - {payload.Length} bytes\r\n");
        for (int offset = 0; offset < payload.Length; offset += 16)
        {
            output.Append(offset.ToString("X8")).Append(": ");
            foreach (byte value in payload.AsSpan(offset, Math.Min(16, payload.Length - offset)))
            {
                output.Append(value.ToString("x2")).Append(' ');
            }
            output.Append("'fixture'\r\n");
        }
        return Encoding.ASCII.GetBytes(output.ToString());
    }

    private static byte[] ReplaceCountedString(byte[] payload, string original, string replacement)
    {
        static byte[] Counted(string value)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(value);
            byte[] result = new byte[4 + bytes.Length + ((-bytes.Length) & 3)];
            BinaryPrimitives.WriteInt32BigEndian(result, bytes.Length);
            bytes.CopyTo(result, 4);
            return result;
        }
        byte[] source = Counted(original), target = Counted(replacement);
        int start = payload.AsSpan().IndexOf(source);
        if (start < 0 || payload.AsSpan(start + 1).IndexOf(source) >= 0)
        {
            throw new Exception("Binary fixture field must have exactly one counted-string occurrence.");
        }
        return [.. payload.AsSpan(0, start), .. target, .. payload.AsSpan(start + source.Length)];
    }
}
