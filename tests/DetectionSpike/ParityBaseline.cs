using System.Text.Json;
using System.Text.Json.Serialization;
using PresidioDemo.Feeds.Shared;

namespace PresidioDemo.Spike;

#region Models

/// <summary>
/// One value that the reviewed field policies protect today, located without its value.
/// </summary>
/// <param name="Sample">The sample file name.</param>
/// <param name="Feed">The feed folder name.</param>
/// <param name="Location">The payload byte offset for binary feeds, or the JSON instance path.</param>
/// <param name="Length">The original value length in characters.</param>
/// <param name="Entity">The entity type today's policy assigns.</param>
public sealed record ParityEntry(string Sample, string Feed, string Location, int Length, string Entity);

/// <summary>
/// One PNR occurrence that today's policies keep unchanged, located without its value.
/// </summary>
/// <param name="Sample">The sample file name.</param>
/// <param name="Feed">The feed folder name.</param>
/// <param name="Location">The payload byte offset for binary feeds, or the JSON instance path.</param>
/// <param name="Length">The PNR length in characters.</param>
public sealed record PnrEntry(string Sample, string Feed, string Location, int Length);

/// <summary>
/// The value-free parity baseline: every value protected and every PNR kept by today's policies.
/// </summary>
/// <param name="Source">What produced the baseline.</param>
/// <param name="Samples">The production samples covered.</param>
/// <param name="Protected">The protected values.</param>
/// <param name="Pnrs">The kept PNR occurrences.</param>
/// <param name="AlreadyMasked">Populated protected fields whose sample value is already a full mask, so no value changes.</param>
public sealed record ParityBaselineDocument(string Source, IReadOnlyList<string> Samples, IReadOnlyList<ParityEntry> Protected, IReadOnlyList<PnrEntry> Pnrs, int AlreadyMasked);

#endregion

/// <summary>
/// The WR-019 parity baseline fixture. It was frozen from the reviewed field policies before they were removed
/// (work item 2026110501, T-002) and cannot be regenerated; a recognizer change must keep parity with it.
/// </summary>
public static class ParityBaseline
{

    #region Fields and Constants

    /// <summary>
    /// The JSON options used for the fixtures: indented, camel case.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    #endregion

    #region Methods

    /// <summary>
    /// Loads the frozen baseline.
    /// </summary>
    /// <param name="root">The repository root.</param>
    /// <returns>The baseline document.</returns>
    /// <exception cref="InvalidDataException">The fixture is missing.</exception>
    public static ParityBaselineDocument Load(string root) =>
        JsonSerializer.Deserialize<ParityBaselineDocument>(File.ReadAllText(Path.Combine(root, "tests", "fixtures", "parity-baseline.json")), JsonOptions)
            ?? throw new InvalidDataException("The parity baseline is missing.");

    #endregion
}

/// <summary>
/// Lists the supplied sample files.
/// </summary>
public static class SampleFiles
{

    #region Methods

    /// <summary>
    /// Returns whether a feed uses the binary XDR transport.
    /// </summary>
    /// <param name="feed">The feed route.</param>
    /// <returns>True for ACI, ETKT and PNR.</returns>
    public static bool IsBinary(FeedType feed) => feed is FeedType.ACI or FeedType.ETKT or FeedType.PNR;

    /// <summary>
    /// Returns the production samples (all samples except the negative tests), in ordinal order.
    /// </summary>
    /// <param name="root">The repository root.</param>
    /// <returns>The sample paths.</returns>
    public static IReadOnlyList<string> Production(string root) => All(root)
        .Where(path => !Path.GetFileName(path).Contains("Negative_Testing", StringComparison.OrdinalIgnoreCase)).ToArray();

    /// <summary>
    /// Returns every supplied sample, in ordinal order.
    /// </summary>
    /// <param name="root">The repository root.</param>
    /// <returns>The sample paths.</returns>
    public static IReadOnlyList<string> All(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "docs", "sample-docs"), "*", SearchOption.AllDirectories)
            .Where(FeedFileRouting.IsFeedFile).Order(StringComparer.Ordinal).ToArray();

    #endregion
}
