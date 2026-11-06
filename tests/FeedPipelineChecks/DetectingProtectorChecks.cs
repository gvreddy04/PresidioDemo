using System.Buffers.Binary;
using System.Text;
using PresidioDemo.Feeds.Shared;
using PresidioDemo.Pipeline;
using PresidioDemo.Protection;

/// <summary>
/// Checks the decoded-text protector (T-007): PNR anchors fail closed (V-06), detection failures dead-letter,
/// unmatched strings are masked, and every production sample rebuilds and validates.
/// </summary>
internal static class DetectingProtectorChecks
{

    #region Methods

    /// <summary>
    /// Runs the checks. Python must be started; the protector is configured here, after the failure-injection check.
    /// </summary>
    /// <param name="projectRoot">The repository root.</param>
    /// <returns>A task that completes when every check passed.</returns>
    public static async Task RunAsync(string projectRoot)
    {
        var sets = RecognizerConfiguration.Load(Path.Combine(projectRoot, "src", "PresidioDemo.Core", "Configuration", "recognizers"));
        string samples = Path.Combine(projectRoot, "docs", "sample-docs");
        var protector = new DetectingFeedProtector(sets);

        // Detection failure: the bridge has no registry for an unconfigured feed name, so the call fails and dead-letters.
        var unconfigured = new DetectingFeedProtector(new Dictionary<FeedType, FeedRecognizerSet> { [FeedType.ACI] = sets[FeedType.ACI] with { Feed = FeedType.ACI } });
        await RejectAsync(() => unconfigured.ProtectAsync(FeedType.ACI, Parse(Path.Combine(samples, "ACI", "ACI.txt")), CancellationToken.None),
            "Local Presidio detection failed", "a Presidio failure before configuration dead-letters");
        protector.Configure();

        int published = 0;
        foreach (string path in Directory.EnumerateFiles(samples, "*.txt", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(path).Contains("Negative_Testing", StringComparison.Ordinal))
            {
                continue;
            }
            var feed = FeedFileRouting.FromPath(path);
            var codec = Codec(feed);
            var message = Message(path, feed);
            var parsed = codec.Parse(message);
            var outcome = await protector.ProtectAsync(feed, parsed, CancellationToken.None);
            byte[] output = codec.Serialize(parsed, outcome.Values);
            codec.Validate(parsed, output);
            var anchors = outcome.Events.Where(e => e.Decision == DetectionDecision.Anchor).ToArray();
            Check(anchors.Length > 0 || feed == FeedType.ETKT, Path.GetFileName(path) + ": a PNR anchor was found");
            var decoded = (feed is FeedType.ACI or FeedType.ETKT or FeedType.PNR ? parsed.Fields : JsonFeedCodec.StringFields(parsed))
                .Select(f => f.Value).Where(v => v.Any(char.IsAsciiLetterOrDigit) && DetectingFeedProtector.Shape(v) != v).ToHashSet(StringComparer.Ordinal);
            Check(outcome.Events.All(e => !decoded.Contains(e.Shape) && !decoded.Contains(e.Key) && !decoded.Contains(e.Format) && !decoded.Contains(e.Entity)),
                Path.GetFileName(path) + ": no detection event field carries a decoded value");
            published++;
        }
        Console.WriteLine($"PASS {published} production samples protected, rebuilt and validated with the recognizer sets");

        // ACI: the PNR is found next to the passenger name whether it is string 9 or string 8 (W-019).
        foreach (string name in new[] { "ACI.txt", "ACI_05.txt" })
        {
            var outcome = await protector.ProtectAsync(FeedType.ACI, Parse(Path.Combine(samples, "ACI", name)), CancellationToken.None);
            var anchor = outcome.Events.Single(e => e.Decision == DetectionDecision.Anchor);
            var nameEvent = outcome.Events.First(e => e.Format == "passengerName");
            Check(anchor.Slot == nameEvent.Slot - 1 && anchor.Length == 6, name + ": PNR anchored just before the passenger name");
        }
        Console.WriteLine("PASS ACI PNR anchored before the first passenger name in ACI.txt and ACI_05.txt");

        // V-06: a removed neighbour or a PNR that is no longer PNR-shaped dead-letters the message.
        string aci = Path.Combine(samples, "ACI", "ACI.txt");
        await RejectAsync(() => protector.ProtectAsync(FeedType.ACI, ParseEdited(aci, FeedType.ACI, (fields, payload) =>
            Overwrite(payload, fields.First(f => f.Value.Contains('/') && f.Value.Count(c => c == '/') == 1 && !f.Value.Any(char.IsDigit)), '/', 'X')), CancellationToken.None),
            "PNR anchor is missing", "a missing passenger-name neighbour dead-letters ACI");
        await RejectAsync(() => protector.ProtectAsync(FeedType.ACI, ParseEdited(aci, FeedType.ACI, (fields, payload) =>
        {
            int nameSlot = fields.ToList().FindIndex(f => f.Value.Contains('/') && !f.Value.Any(char.IsDigit));
            OverwriteFirst(payload, fields[nameSlot - 1], '-');
        }), CancellationToken.None), "anchor position is not PNR-shaped", "a PNR that is not PNR-shaped dead-letters ACI");
        await RejectAsync(() => protector.ProtectAsync(FeedType.PNR, ParseEdited(Path.Combine(samples, "PNR", "PNR.txt"), FeedType.PNR, (fields, payload) =>
            OverwriteFirst(payload, fields.First(f => f.Value.Length == 9 && f.Value.All(char.IsAsciiDigit)), 'A')), CancellationToken.None),
            "PNR anchor is missing", "a missing creation-time neighbour dead-letters PNR");
        var linking = Parse(Path.Combine(samples, "PNR-Linking", "PNR_LINKING.txt"));
        var brokenLinking = new ParsedFeed(linking.Fields.Select(f => f.Name.EndsWith("/UaRecordLocator", StringComparison.Ordinal) ? f with { Value = "AB" } : f).ToArray(), linking.Layout);
        await RejectAsync(() => protector.ProtectAsync(FeedType.PnrLinking, StringLayout(linking, brokenLinking), CancellationToken.None),
            "not PNR-shaped", "a JSON PNR anchor that is not PNR-shaped dead-letters PNR-Linking");

        // A number under a personal-data key (PNR_LINKING_03's numeric DOB) cannot be protected in its type: dead-letter.
        await RejectAsync(() => protector.ProtectAsync(FeedType.PnrLinking, Parse(Path.Combine(samples, "PNR-Linking", "PNR_LINKING_03_Negative_Testing.txt")), CancellationToken.None),
            "number or boolean", "a numeric value under a personal-data key dead-letters PNR_LINKING_03");

        // Unmatched strings are masked across their full width, keeping literal * separators (WR-014).
        Check(DetectingFeedProtector.DefaultMask("Q7 ZZ*AB", '#') == "#####*##", "default mask keeps width and literal *");
        var etkt = await protector.ProtectAsync(FeedType.ETKT, Parse(Path.Combine(samples, "ETKT", "ETKT_05.txt")), CancellationToken.None);
        Check(etkt.Events.Count(e => e.Decision == DetectionDecision.Anchor) == 2, "ETKT_05 divide-event PNRs anchored after the status record");
        var etktPlain = await protector.ProtectAsync(FeedType.ETKT, Parse(Path.Combine(samples, "ETKT", "ETKT.txt")), CancellationToken.None);
        Check(etktPlain.Events.All(e => e.Decision != DetectionDecision.Anchor), "ETKT without divide-event PNRs has no anchor and is not rejected");
        Check(etktPlain.Events.Any(e => e.Decision == DetectionDecision.MaskedByDefault) || etktPlain.Events.Any(e => e.Decision == DetectionDecision.Masked),
            "ETKT protects its ticket and reference strings");
        Console.WriteLine("PASS PNR anchors fail closed and unmatched strings are masked by default");

        // R-002, WR-017: a malformed divide-event PNR at an optional ETKT anchor position dead-letters instead of being
        // masked and published; so does a malformed PNR embedded in the ACI unresolved record reference.
        string etkt05 = Path.Combine(samples, "ETKT", "ETKT_05.txt");
        await RejectAsync(() => protector.ProtectAsync(FeedType.ETKT, ParseEdited(etkt05, FeedType.ETKT, (fields, payload) =>
        {
            int status = fields.ToList().FindIndex(f => f.Value.Length == 8 && f.Value.All(char.IsAsciiDigit));
            OverwriteFirst(payload, fields[status + 1], '-');
        }), CancellationToken.None), "anchor position is not PNR-shaped", "a malformed ETKT divide-event PNR dead-letters");
        await RejectAsync(() => protector.ProtectAsync(FeedType.ACI, ParseEdited(aci, FeedType.ACI, (fields, payload) =>
        {
            var reference = fields.Last(f => f.Value.Length == 15 && f.Value[..9].All(char.IsAsciiDigit));
            payload[Locate(payload, reference.Value) + 9] = (byte)'-';
        }), CancellationToken.None), "embedded PNR", "a malformed PNR embedded in the ACI record reference dead-letters");
        var aciOutcome = await protector.ProtectAsync(FeedType.ACI, Parse(aci), CancellationToken.None);
        var aciFields = Parse(aci).Fields;
        var referenceField = aciFields.Last(f => f.Value.Length == 15 && f.Value[..9].All(char.IsAsciiDigit));
        Check(aciOutcome.Values[referenceField.Id][9..] == referenceField.Value[9..] && aciOutcome.Values[referenceField.Id][..9].All(c => c == '*'),
            "ACI record reference: prefix masked, embedded PNR unchanged");
        Console.WriteLine("PASS malformed optional and embedded PNRs dead-letter; the embedded ACI PNR is kept while its prefix is masked");

        // R-004, R-006, R-009: JSON keys match regardless of case, reason text is scanned, a number nested under a
        // personal-data key dead-letters, and a key the recognizer file does not declare is logged as ?.
        var linkingCodec = FeedCodecRegistry.Create(FeedType.PnrLinking);
        var json = linkingCodec.Parse(new FeedMessage(new FeedDelivery(FeedType.PnrLinking, Guid.NewGuid(), "fixture.bin", ""), Encoding.UTF8.GetBytes(
            "[{\"PNROwner\":\"K7QX2M\",\"dob\":\"1985-03-12\",\"ReasonForLeaving\":\"REBOOKED, CONTACT MILLER/JORDAN\",\"contacts\":{\"jordan.miller@example.com\":\"HOME\"}}]")));
        var jsonOutcome = await protector.ProtectAsync(FeedType.PnrLinking, json, CancellationToken.None);
        string Value(string suffix) => jsonOutcome.Values[json.Fields.Single(f => f.Id.EndsWith(suffix, StringComparison.Ordinal)).Id];
        Check(Value("/dob") == "[DATE_OF_BIRTH]", "a lower-case dob key is replaced, not kept as a timestamp");
        Check(!Value("/ReasonForLeaving").Contains("MILLER", StringComparison.Ordinal) && Value("/ReasonForLeaving").StartsWith("REBOOKED, ", StringComparison.Ordinal),
            "a name inside reason text is masked while the reason is kept");
        Check(Value("/PNROwner") == "K7QX2M", "the PNR stays unchanged");
        Check(jsonOutcome.Events.Single(e => e.Decision == DetectionDecision.MaskedByDefault).Key == "?", "an undeclared key is logged as ?");
        await RejectAsync(() => protector.ProtectAsync(FeedType.PnrLinking, linkingCodec.Parse(new FeedMessage(new FeedDelivery(FeedType.PnrLinking, Guid.NewGuid(), "fixture.bin", ""),
            Encoding.UTF8.GetBytes("[{\"PNROwner\":\"K7QX2M\",\"DOB\":{\"Year\":1985}}]"))), CancellationToken.None),
            "number or boolean", "a number nested under a personal-data key dead-letters");
        Console.WriteLine("PASS JSON keys ignore case, reason text is scanned, nested personal-data numbers dead-letter, undeclared keys log as ?");

        // R-003: a recognizer pattern that times out in Presidio dead-letters instead of publishing unchecked text. This
        // reconfigures the Seats registry, so it restores the reviewed configuration afterwards.
        string slow = Path.Combine(Path.GetTempPath(), "slow-seats-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(slow, File.ReadAllText(Path.Combine(projectRoot, "src", "PresidioDemo.Core", "Configuration", "recognizers", "seats.xml"))
            .Replace("<keep name=\"operational\">", "<format name=\"slowCheck\" entity=\"DEMO_CODE\"><pattern>(A|AA)+$</pattern></format>\n  <keep name=\"operational\">", StringComparison.Ordinal));
        try
        {
            var slowSets = sets.ToDictionary(pair => pair.Key, pair => pair.Value);
            slowSets[FeedType.Seats] = RecognizerConfiguration.LoadFile(slow, FeedType.Seats);
            var slowProtector = new DetectingFeedProtector(slowSets);
            slowProtector.Configure();
            var seatsCodec = FeedCodecRegistry.Create(FeedType.Seats);
            var seats = seatsCodec.Parse(new FeedMessage(new FeedDelivery(FeedType.Seats, Guid.NewGuid(), "fixture.bin", ""),
                Encoding.UTF8.GetBytes("{\"UARecloc\":\"K7QX2M\",\"Note\":\"" + new string('A', 40) + "!\"}")));
            await RejectAsync(() => slowProtector.ProtectAsync(FeedType.Seats, seats, CancellationToken.None),
                "Local Presidio detection failed", "a Presidio pattern timeout dead-letters the message");
        }
        finally
        {
            File.Delete(slow);
            protector.Configure();
        }

        // V-12, W-017: the ACI passenger-association check is gone. Raw words such as numeric passenger IDs are never
        // decoded or changed, so a message whose ID word differs is published with that word exactly as received.
        var aciCodec = new GenericXdrFeedCodec(FeedType.ACI);
        var aciMessage = Message(aci, FeedType.ACI);
        var aciEnvelope = FeedPayloadReader.Read(aciMessage);
        var aciCandidates = GenericXdrFeedCodec.Candidates(aciCodec.Parse(aciMessage));
        int nameEnd = aciCandidates.First(c => c.Value.Contains('/')).Offset + aciCandidates.First(c => c.Value.Contains('/')).Length;
        int idWord = Enumerable.Range(0, aciEnvelope.Payload.Length / 4).Select(i => i * 4)
            .First(o => o > nameEnd && !aciCandidates.Any(c => o + 4 > c.Offset - 4 && o < c.Offset + c.Length + 4) &&
                BinaryPrimitives.ReadUInt32BigEndian(aciEnvelope.Payload.AsSpan(o, 4)) is > 1 and < 100_000_000);
        byte[] mismatched = aciEnvelope.Payload.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(mismatched.AsSpan(idWord, 4), BinaryPrimitives.ReadUInt32BigEndian(mismatched.AsSpan(idWord, 4)) + 1);
        var mismatchMessage = new FeedMessage(aciMessage.Delivery, aciEnvelope.Serialize(mismatched));
        var mismatchParsed = aciCodec.Parse(mismatchMessage);
        var mismatchOutcome = await protector.ProtectAsync(FeedType.ACI, mismatchParsed, CancellationToken.None);
        byte[] mismatchOutput = aciCodec.Serialize(mismatchParsed, mismatchOutcome.Values);
        aciCodec.Validate(mismatchParsed, mismatchOutput);
        Check(FeedPayloadReader.Read(new FeedMessage(aciMessage.Delivery, mismatchOutput)).Payload.AsSpan(idWord, 4).SequenceEqual(mismatched.AsSpan(idWord, 4)),
            "ACI ID word published unchanged");
        Console.WriteLine("PASS an ACI message with a changed passenger-ID word is published with the word unchanged (no association check)");

        // V-09, WR-019: narrowing a format breaks parity with the frozen baseline before merge. This reconfigures the
        // PNR registry, so it runs last and restores the reviewed configuration afterwards.
        string recognizers = Path.Combine(projectRoot, "src", "PresidioDemo.Core", "Configuration", "recognizers");
        string narrowed = Path.Combine(Path.GetTempPath(), "narrowed-pnr-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(narrowed, File.ReadAllText(Path.Combine(recognizers, "pnr.xml"))
            .Replace("<pattern>(?&lt;![A-Z])[A-Z]{2,}(?:[ '-][A-Z]+)*/[A-Z]*(?:[ '-][A-Z]+)*</pattern>", "<pattern>(?&lt;![A-Z])[A-Z]{30,}/[A-Z]{30,}</pattern>", StringComparison.Ordinal));
        try
        {
            var narrowedSets = sets.ToDictionary(pair => pair.Key, pair => pair.Value);
            narrowedSets[FeedType.PNR] = RecognizerConfiguration.LoadFile(narrowed, FeedType.PNR);
            int reviewedMisses = await ParityMissesAsync(protector, projectRoot, FeedType.PNR);
            Check(reviewedMisses == 0, "reviewed recognizers must have no unapproved PNR parity miss");
            // The bridge keeps one registry per feed name, so configuring the narrowed set replaces the reviewed one.
            var narrowedProtector = new DetectingFeedProtector(narrowedSets);
            narrowedProtector.Configure();
            int narrowedMisses = await ParityMissesAsync(narrowedProtector, projectRoot, FeedType.PNR);
            Check(narrowedMisses > 0, "a narrowed name format must fail parity");
            Console.WriteLine($"PASS a narrowed PNR name format fails parity with the frozen baseline ({narrowedMisses} unapproved misses)");
        }
        finally
        {
            File.Delete(narrowed);
            protector.Configure();
        }
    }

    /// <summary>
    /// Counts baseline values of one binary feed that the protector publishes unchanged and that are not approved
    /// exceptions (WR-019). Value-free: compares bytes at the frozen locations only.
    /// </summary>
    /// <param name="protector">The protector to measure.</param>
    /// <param name="projectRoot">The repository root.</param>
    /// <param name="feed">A binary feed.</param>
    /// <returns>The number of unapproved misses.</returns>
    private static async Task<int> ParityMissesAsync(DetectingFeedProtector protector, string projectRoot, FeedType feed)
    {
        using var baseline = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(projectRoot, "tests", "fixtures", "parity-baseline.json")));
        using var approved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(projectRoot, "tests", "fixtures", "parity-exceptions.json")));
        var exceptions = approved.RootElement.EnumerateArray().Select(e => (e.GetProperty("sample").GetString(), e.GetProperty("location").GetString())).ToHashSet();
        string folder = FeedCatalog.Get(feed).FolderName;
        int misses = 0;
        foreach (var group in baseline.RootElement.GetProperty("protected").EnumerateArray().Where(e => e.GetProperty("feed").GetString() == folder)
            .GroupBy(e => e.GetProperty("sample").GetString()!))
        {
            string path = Path.Combine(projectRoot, "docs", "sample-docs", folder, group.Key);
            var codec = new GenericXdrFeedCodec(feed);
            var message = Message(path, feed);
            var parsed = codec.Parse(message);
            var outcome = await protector.ProtectAsync(feed, parsed, CancellationToken.None);
            byte[] before = FeedPayloadReader.Read(message).Payload;
            byte[] after = FeedPayloadReader.Read(new FeedMessage(message.Delivery, codec.Serialize(parsed, outcome.Values))).Payload;
            foreach (var entry in group)
            {
                int offset = int.Parse(entry.GetProperty("location").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                int length = entry.GetProperty("length").GetInt32();
                // Only characters that were not already * need to change; a value that is all * was masked before it arrived.
                bool caught = Enumerable.Range(offset, length).Where(i => before[i] != (byte)'*').All(i => before[i] != after[i]);
                if (!caught && !exceptions.Contains((group.Key, entry.GetProperty("location").GetString())))
                {
                    misses++;
                }
            }
        }
        return misses;
    }

    /// <summary>
    /// Returns the codec the decoded-text path uses for a feed.
    /// </summary>
    private static IFeedCodec Codec(FeedType feed) => feed is FeedType.ACI or FeedType.ETKT or FeedType.PNR ? new GenericXdrFeedCodec(feed) : FeedCodecRegistry.Create(feed);

    /// <summary>
    /// Builds a message from a sample file.
    /// </summary>
    private static FeedMessage Message(string path, FeedType feed) => new(new FeedDelivery(feed, Guid.NewGuid(), Path.GetFileName(path), path), File.ReadAllBytes(path));

    /// <summary>
    /// Parses a sample with the decoded-text codec of its feed.
    /// </summary>
    private static ParsedFeed Parse(string path)
    {
        var feed = FeedFileRouting.FromPath(path);
        return Codec(feed).Parse(Message(path, feed));
    }

    /// <summary>
    /// Parses a binary sample after editing its payload bytes in place (same width), as a changed producer would.
    /// </summary>
    private static ParsedFeed ParseEdited(string path, FeedType feed, Action<IReadOnlyList<FeedField>, byte[]> edit)
    {
        var message = Message(path, feed);
        var codec = new GenericXdrFeedCodec(feed);
        var parsed = codec.Parse(message);
        var envelope = FeedPayloadReader.Read(message);
        byte[] payload = envelope.Payload.ToArray();
        edit(parsed.Fields, payload);
        return codec.Parse(new FeedMessage(message.Delivery, envelope.Serialize(payload)));
    }

    /// <summary>
    /// Replaces one character of a candidate's text in the payload.
    /// </summary>
    private static void Overwrite(byte[] payload, FeedField field, char from, char to)
    {
        int offset = Locate(payload, field.Value);
        payload[offset + field.Value.IndexOf(from)] = (byte)to;
    }

    /// <summary>
    /// Replaces the first character of a candidate's text in the payload.
    /// </summary>
    private static void OverwriteFirst(byte[] payload, FeedField field, char to) => payload[Locate(payload, field.Value)] = (byte)to;

    /// <summary>
    /// Finds a candidate's text in the payload.
    /// </summary>
    private static int Locate(byte[] payload, string value) => payload.AsSpan().IndexOf(Encoding.ASCII.GetBytes(value));

    /// <summary>
    /// Keeps the JSON layout of the original parse but uses edited field values, for anchor checks that never serialize.
    /// </summary>
    private static ParsedFeed StringLayout(ParsedFeed original, ParsedFeed edited) => new(edited.Fields, original.Layout);

    /// <summary>
    /// Requires an InvalidDataException whose message contains the expected text.
    /// </summary>
    private static async Task RejectAsync(Func<ValueTask<DetectionOutcome>> action, string expected, string label)
    {
        try
        {
            await action();
        }
        catch (InvalidDataException ex) when (ex.Message.Contains(expected, StringComparison.Ordinal))
        {
            Console.WriteLine("PASS " + label);
            return;
        }
        throw new Exception("Expected rejection: " + label);
    }

    /// <summary>
    /// Requires a condition.
    /// </summary>
    private static void Check(bool condition, string label)
    {
        if (!condition)
        {
            throw new Exception(label);
        }
    }

    #endregion
}
