using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PresidioDemo.Feeds.Shared;
using PresidioDemo.Protection;

namespace PresidioDemo.Spike;

#region Models

/// <summary>
/// One approved parity exception: a baseline-protected value that decoded-text detection publishes (W-011, W-016).
/// </summary>
/// <param name="Sample">The sample file name.</param>
/// <param name="Location">The baseline location.</param>
/// <param name="Reason">Why it is published: a keep-format match or a binary string shorter than four characters.</param>
/// <param name="ApprovedBy">Who approved it, recorded in decisions.md.</param>
public sealed record ParityException(string Sample, string Location, string Reason, string ApprovedBy);

/// <summary>
/// A value-free finding: where, what entity, which format decided, and the character shape.
/// </summary>
/// <param name="Sample">The sample file name.</param>
/// <param name="Feed">The feed folder name.</param>
/// <param name="Location">The payload offset or JSON path.</param>
/// <param name="Length">The value length.</param>
/// <param name="Entity">The baseline entity, or empty for a false positive.</param>
/// <param name="Shape">The character shape of the value.</param>
/// <param name="Decision">The decoded-text decision for the containing string.</param>
/// <param name="Format">The format or keep that decided.</param>
public sealed record SpikeFinding(string Sample, string Feed, string Location, int Length, string Entity, string Shape, string Decision, string Format);

#endregion

/// <summary>
/// The decoded-text spike (T-009, V-01, V-02, V-11): parity against the value-free baseline, PNRs, rebuilds,
/// false positives, negative samples and throughput.
/// </summary>
public static class Spike
{

    #region Methods

    /// <summary>
    /// Runs the spike and writes <c>report.json</c> (values, Local only) and <c>summary.json</c> (value-free).
    /// </summary>
    /// <param name="root">The repository root.</param>
    /// <param name="output">The run folder under <c>.test-runs</c>.</param>
    /// <param name="rounds">How many rounds over the production samples the throughput run makes.</param>
    /// <returns>True when parity holds.</returns>
    public static async Task<bool> RunAsync(string root, string output, int rounds)
    {
        var baseline = ParityBaseline.Load(root);
        string exceptionsFile = Path.Combine(root, "tests", "fixtures", "parity-exceptions.json");
        var exceptions = File.Exists(exceptionsFile)
            ? JsonSerializer.Deserialize<List<ParityException>>(File.ReadAllText(exceptionsFile), ParityBaseline.JsonOptions) ?? []
            : [];
        var protector = DetectionRun.CreateProtector(root);

        var results = new List<SampleResult>();
        foreach (string path in SampleFiles.All(root))
        {
            results.Add(await DetectionRun.ProcessAsync(protector, path, CancellationToken.None));
        }
        var byName = results.ToDictionary(r => Path.GetFileName(r.Path), StringComparer.Ordinal);

        var caught = new List<ParityEntry>();
        var misses = new List<SpikeFinding>();
        var missValues = new List<object>();
        foreach (var entry in baseline.Protected)
        {
            var result = byName[entry.Sample];
            var (isCaught, before, after, evt) = Inspect(result, entry.Location, entry.Length);
            if (isCaught)
            {
                caught.Add(entry);
                continue;
            }
            misses.Add(new SpikeFinding(entry.Sample, entry.Feed, entry.Location, entry.Length, entry.Entity, DetectingFeedProtector.Shape(before),
                evt is null ? "rejected" : Transport.DetectionLog.Decision(evt.Decision), evt?.Format ?? ""));
            missValues.Add(new { entry.Sample, entry.Location, entry.Entity, Original = before, Published = after });
        }
        var unapproved = misses.Where(m => !exceptions.Any(e => e.Sample == m.Sample && e.Location == m.Location)).ToArray();

        int pnrKept = baseline.Pnrs.Count(pnr => Inspect(byName[pnr.Sample], pnr.Location, pnr.Length) is { Before: var b, After: var a } && b == a && a.Length > 0);
        var production = results.Where(r => !Path.GetFileName(r.Path).Contains("Negative_Testing", StringComparison.Ordinal)).ToArray();
        int rebuilt = production.Count(r => r.Output is not null);

        var falsePositives = new List<SpikeFinding>();
        foreach (var result in production.Where(r => r.Output is not null))
        {
            falsePositives.AddRange(FalsePositives(result, baseline.Protected.Where(e => e.Sample == Path.GetFileName(result.Path)).ToArray()));
        }

        var throughput = await Throughput(protector, production.Select(r => r.Path).ToArray(), rounds);
        bool parity = unapproved.Length == 0 && pnrKept == baseline.Pnrs.Count && rebuilt == production.Length;
        var negatives = results.Where(r => Path.GetFileName(r.Path).Contains("Negative_Testing", StringComparison.Ordinal))
            .Select(r => new { Sample = Path.GetFileName(r.Path), Status = r.Output is null ? "rejected" : "published", Reason = r.Reason ?? "" }).ToArray();

        var summary = new
        {
            Parity = parity ? "PASS" : "FAIL",
            Protected = new { Baseline = baseline.Protected.Count, Caught = caught.Count, Missed = misses.Count, ApprovedExceptions = misses.Count - unapproved.Length, Unapproved = unapproved.Length },
            Pnrs = new { Baseline = baseline.Pnrs.Count, Unchanged = pnrKept },
            Rebuilds = new { Production = production.Length, Valid = rebuilt },
            FalsePositives = falsePositives.Count,
            Negatives = negatives,
            Misses = misses,
            FalsePositiveList = falsePositives,
            Throughput = throughput,
            Rejected = results.Where(r => r.Output is null).Select(r => new { Sample = Path.GetFileName(r.Path), Reason = r.Reason ?? "" })
        };
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(summary, ParityBaseline.JsonOptions) + Environment.NewLine);
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
        {
            Note = "Local spike report: contains original sample values. Never commit or copy outside .test-runs.",
            Misses = missValues,
            FalsePositives = falsePositives.Select(f => new { f.Sample, f.Location, f.Format, Original = Value(byName[f.Sample], f.Location, f.Length) })
        }, ParityBaseline.JsonOptions) + Environment.NewLine);
        Console.WriteLine($"SPIKE parity={summary.Parity} | protected {caught.Count}/{baseline.Protected.Count} caught, {misses.Count} missed ({unapproved.Length} unapproved) | " +
            $"PNRs {pnrKept}/{baseline.Pnrs.Count} | rebuilds {rebuilt}/{production.Length} | false positives {falsePositives.Count}");
        Console.WriteLine("SPIKE throughput " + JsonSerializer.Serialize(throughput));
        Console.WriteLine("SPIKE summary " + Path.Combine(output, "summary.json"));
        return parity;
    }

    /// <summary>
    /// Returns whether a baseline location is protected in the decoded-text output, with the before and after text.
    /// </summary>
    /// <param name="result">The sample result.</param>
    /// <param name="location">The payload offset or JSON path.</param>
    /// <param name="length">The value length.</param>
    /// <returns>Caught when every non-<c>*</c> character changed (binary) or the JSON value changed.</returns>
    private static (bool Caught, string Before, string After, DetectionEvent? Event) Inspect(SampleResult result, string location, int length)
    {
        string before = Value(result.Original, result, location, length, original: true);
        if (result.Output is null)
        {
            return (true, before, "", null);
        }
        string after = Value(result.Output, result, location, length, original: false);
        bool caught = SampleFiles.IsBinary(result.Feed)
            ? before.Zip(after).All(pair => pair.First == '*' || pair.First != pair.Second)
            : before != after;
        return (caught, before, after, Containing(result, location, length));
    }

    /// <summary>
    /// Returns the value at a location in the original sample.
    /// </summary>
    private static string Value(SampleResult result, string location, int length) => Value(result.Original, result, location, length, original: true);

    /// <summary>
    /// Returns the value at a location in a message: payload bytes for binary feeds, the JSON scalar for JSON feeds.
    /// </summary>
    private static string Value(byte[] message, SampleResult result, string location, int length, bool original)
    {
        byte[] payload = FeedPayloadReader.ReadPayload(new FeedMessage(new FeedDelivery(result.Feed, Guid.NewGuid(), Path.GetFileName(result.Path), result.Path), message));
        if (SampleFiles.IsBinary(result.Feed))
        {
            return Encoding.ASCII.GetString(payload, int.Parse(location, System.Globalization.CultureInfo.InvariantCulture), length);
        }
        JsonNode? node = JsonNode.Parse(payload);
        foreach (string segment in location.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string key = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            node = node is JsonArray array && int.TryParse(key, out int index) ? array[index] : node?[key];
        }
        return node is JsonValue value && value.TryGetValue(out string? text) ? text : node?.ToJsonString() ?? "";
    }

    /// <summary>
    /// Returns the event of the decoded string that contains a location.
    /// </summary>
    private static DetectionEvent? Containing(SampleResult result, string location, int length)
    {
        if (result.Outcome is null || result.Parsed is null)
        {
            return null;
        }
        if (SampleFiles.IsBinary(result.Feed))
        {
            int offset = int.Parse(location, System.Globalization.CultureInfo.InvariantCulture);
            var candidate = GenericXdrFeedCodec.Candidates(result.Parsed).FirstOrDefault(c => offset >= c.Offset && offset + length <= c.Offset + c.Length);
            return candidate is null ? null : result.Outcome.Events.FirstOrDefault(e => e.Slot == candidate.Slot);
        }
        var strings = JsonFeedCodec.StringFields(result.Parsed);
        int slot = strings.ToList().FindIndex(f => f.Id == location);
        return slot < 0 ? null : result.Outcome.Events.FirstOrDefault(e => e.Slot == slot);
    }

    /// <summary>
    /// Lists decoded strings that changed although the baseline protects nothing inside them.
    /// </summary>
    private static IEnumerable<SpikeFinding> FalsePositives(SampleResult result, IReadOnlyList<ParityEntry> entries)
    {
        string feed = FeedCatalog.Get(result.Feed).FolderName;
        string sample = Path.GetFileName(result.Path);
        if (SampleFiles.IsBinary(result.Feed))
        {
            foreach (var candidate in GenericXdrFeedCodec.Candidates(result.Parsed!))
            {
                bool protectedToday = entries.Any(e => int.Parse(e.Location, System.Globalization.CultureInfo.InvariantCulture) is var o && o >= candidate.Offset && o < candidate.Offset + candidate.Length);
                if (!protectedToday && result.Outcome!.Values[GenericXdrFeedCodec.SlotId(candidate.Slot)] != candidate.Value)
                {
                    var evt = result.Outcome.Events[candidate.Slot];
                    yield return new SpikeFinding(sample, feed, candidate.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture), candidate.Length, "", evt.Shape, Transport.DetectionLog.Decision(evt.Decision), evt.Format);
                }
            }
            yield break;
        }
        var strings = JsonFeedCodec.StringFields(result.Parsed!);
        for (int slot = 0; slot < strings.Count; slot++)
        {
            var field = strings[slot];
            if (!entries.Any(e => e.Location == field.Id) && result.Outcome!.Values[field.Id] != field.Value)
            {
                var evt = result.Outcome.Events[slot];
                yield return new SpikeFinding(sample, feed, field.Id, field.Value.Length, "", evt.Shape, Transport.DetectionLog.Decision(evt.Decision), evt.Format);
            }
        }
    }

    /// <summary>
    /// Measures messages per second for one process (one ECS task) with spaCy NER on, per feed and overall, and the
    /// task count that sustains 2,000 messages per second if the peak is shared equally by the five feeds.
    /// </summary>
    private static async Task<object> Throughput(DetectingFeedProtector protector, IReadOnlyList<string> samples, int rounds)
    {
        foreach (string path in samples)
        {
            await DetectionRun.ProcessAsync(protector, path, CancellationToken.None);
        }
        var perFeed = new Dictionary<string, (int Messages, double Seconds)>(StringComparer.Ordinal);
        var total = Stopwatch.StartNew();
        int messages = 0;
        for (int round = 0; round < rounds; round++)
        {
            foreach (string path in samples)
            {
                var timer = Stopwatch.StartNew();
                var result = await DetectionRun.ProcessAsync(protector, path, CancellationToken.None);
                string feed = FeedCatalog.Get(result.Feed).FolderName;
                perFeed[feed] = perFeed.TryGetValue(feed, out var current) ? (current.Messages + 1, current.Seconds + timer.Elapsed.TotalSeconds) : (1, timer.Elapsed.TotalSeconds);
                messages++;
            }
        }
        total.Stop();
        const double peak = 2000, feeds = 5;
        var rows = perFeed.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
        {
            double rate = p.Value.Messages / p.Value.Seconds;
            return new { Feed = p.Key, MessagesPerSecondPerTask = Math.Round(rate, 1), TasksForEqualShareOfPeak = (int)Math.Ceiling(peak / feeds / rate) };
        }).ToArray();
        return new
        {
            Rounds = rounds,
            Messages = messages,
            NlpNer = "on (spaCy en_core_web_lg)",
            MessagesPerSecondPerTask = Math.Round(messages / total.Elapsed.TotalSeconds, 1),
            Feeds = rows,
            TasksForPeak = rows.Sum(r => r.TasksForEqualShareOfPeak),
            Assumption = "2,000 msg/s shared equally by the five feeds; one worker process per ECS task"
        };
    }

    #endregion
}
