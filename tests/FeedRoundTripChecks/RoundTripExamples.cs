using System.Buffers.Binary;
using System.Text;
using PresidioDemo.Feeds.Shared;

#region Models

/// <summary>
/// Describes one named raw or MQ-encoded round-trip fixture.
/// </summary>
/// <param name="Feed">The supported feed route.</param>
/// <param name="Name">The declared field name or reviewed layout identifier.</param>
/// <param name="SourceSample">The supplied sample used to construct this independent fixture.</param>
/// <param name="Category">The diagnostic category assigned to this fixture.</param>
/// <param name="Extension">The file extension identifying the fixture&apos;s transport representation.</param>
/// <param name="Bytes">The complete raw or transport-encoded fixture bytes.</param>
internal sealed record RoundTripExample(FeedType Feed, string Name, string SourceSample,
    string Category, string Extension, byte[] Bytes);

#endregion

/// <summary>
/// Builds supplied and synthetic encoding cases for complete-message round-trip checks.
/// </summary>
internal static class RoundTripExamples
{

    #region Methods

    /// <summary>
    /// Builds supplied and synthetic raw-payload and MQ-encoded round-trip fixtures.
    /// </summary>
    /// <param name="root">The project root containing source configuration and immutable samples.</param>
    /// <param name="codecFactory">The factory that selects the reviewed codec for each feed route.</param>
    /// <returns>The complete independently generated fixture collection.</returns>
    /// <exception cref="Exception">A regression assertion fails or an expected rejection does not occur.</exception>
    public static IReadOnlyList<RoundTripExample> Create(string root, Func<FeedType, IFeedCodec> codecFactory)
    {
        var examples = new List<RoundTripExample>();
        var baselines = new Dictionary<FeedType, (string Sample, FeedEnvelope Envelope)>();
        foreach (string file in Directory.GetFiles(Path.Combine(root, "docs/sample-docs"), "*.txt", SearchOption.AllDirectories).Order())
        {
            string sample = Path.GetFileName(file);
            if (sample.Contains("_Negative_Testing", StringComparison.Ordinal))
            {
                // Negative-testing samples are rejected by design and are covered by the batch checks.
                continue;
            }

            FeedType feed = Route(file);
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
                // The generic codec has no field names, so the varied string is chosen by its shape: the first passenger
                // name (ACI), the 13-digit history sequence number (ETKT), the first letters-only string (PNR surname).
                Func<string, bool> target = feed switch
                {
                    FeedType.ACI => value => value.Contains('/') && value.All(c => char.IsAsciiLetterUpper(c) || c == '/'),
                    FeedType.ETKT => value => value.Length == 13 && value.All(char.IsAsciiDigit),
                    _ => value => value.Length >= 4 && value.All(char.IsAsciiLetterUpper)
                };
                var parsed = codecFactory(feed).Parse(Message(feed, "example.bin", baseline.Envelope.Payload));
                string current = parsed.Fields.First(field => target(field.Value)).Value;
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

    /// <summary>
    /// Creates a complete regression-test delivery with the requested feed and input bytes.
    /// </summary>
    /// <param name="feed">The supported feed route.</param>
    /// <param name="name">The declared member name, configuration setting or fixture filename.</param>
    /// <param name="bytes">The complete payload or fixture bytes to read, encode or compare.</param>
    /// <returns>The complete fixture delivery and its original bytes.</returns>
    internal static FeedMessage Message(FeedType feed, string name, byte[] bytes) =>
            new(new(feed, Guid.Empty, name, "round-trip-test-only"), bytes);

    /// <summary>
    /// Builds a complete diagnostic MQ dump with the requested encoding variation.
    /// </summary>
    /// <param name="payload">The complete decoded payload bytes without transport metadata.</param>
    /// <returns>The complete independently rebuilt fixture bytes.</returns>
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

    /// <summary>
    /// Builds an MQ text export with the requested line endings, row width and hexadecimal case.
    /// </summary>
    /// <param name="source">The original fixture or source bytes retained for comparison.</param>
    /// <param name="payload">The complete decoded payload bytes without transport metadata.</param>
    /// <param name="newline">The line-ending sequence used by the generated text fixture.</param>
    /// <param name="finalNewline">Whether the generated fixture ends with its selected line-ending sequence.</param>
    /// <param name="lowercase">Whether the generated MQ fixture uses lowercase hexadecimal digits.</param>
    /// <param name="rowWidth">The number of payload bytes represented by each generated MQ row.</param>
    /// <param name="irregular">Whether the fixture uses deliberately varied supported encoding widths.</param>
    /// <returns>The complete independently rebuilt fixture bytes.</returns>
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

    /// <summary>
    /// Changes one fixture XDR string while independently rebuilding its length and padding.
    /// </summary>
    /// <param name="payload">The complete decoded payload bytes without transport metadata.</param>
    /// <param name="original">The complete input or retained parsed representation before protection.</param>
    /// <param name="replacement">The replacement text used to vary the independent fixture.</param>
    /// <returns>The complete independently rebuilt fixture bytes.</returns>
    /// <exception cref="Exception">A regression assertion fails or an expected rejection does not occur.</exception>
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

    /// <summary>
    /// Resolves a supplied sample to its feed route from the per-feed folder that contains it.
    /// </summary>
    /// <param name="file">The complete path of a supplied sample under docs/sample-docs/&lt;feed&gt;.</param>
    /// <returns>The supported feed route named by the sample's folder.</returns>
    /// <exception cref="Exception">A regression assertion fails or an expected rejection does not occur.</exception>
    private static FeedType Route(string file) =>
        FeedCatalog.All.SingleOrDefault(feed => feed.FolderName == Path.GetFileName(Path.GetDirectoryName(file)))?.Type
            ?? throw new Exception("Sample is not in a feed folder: " + file);

    #endregion
}
