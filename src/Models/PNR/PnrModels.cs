using PresidioDemo.Feeds.Shared;
namespace PresidioDemo.Feeds.PNR;
/// <summary>A field label supported by the sample and related P2 documentation.</summary>
public sealed record PnrFieldMeaning(string Category, string Label, string Evidence);
/// <summary>A decoded scalar and optional evidence-backed meaning.</summary>
public sealed record PnrDecodedField(string Path, string Value, int? Offset, int Length, PnrFieldMeaning? Meaning);
/// <summary>The decoded sample-layout message and its retained wire state.</summary>
public sealed record PnrDocument(IReadOnlyList<PnrDecodedField> Fields, int PayloadLength, ParsedFeed WireFields);
internal sealed record PnrParsedLayout(ParsedFeed Projection, PnrDocument Document);
