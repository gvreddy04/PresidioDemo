using PresidioDemo.Feeds.Shared;
namespace PresidioDemo.Feeds.PNR;
/// <summary>Reconstructs the observed PNR XDR representation.</summary>
public sealed class PnrXdrWriter(PnrXdrReader reader)
{
    /// <summary>Writes a full set of scalar values.</summary>
    public byte[] Write(ParsedFeed fields, IReadOnlyDictionary<string, string> values) => reader.Codec.Serialize(fields, values);
}
