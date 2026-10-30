using PresidioDemo.Feeds.Shared;
namespace PresidioDemo.Feeds.PNR;
/// <summary>Parses PNR without inventing passenger identities or unknown field meanings.</summary>
public sealed class PnrParser(PnrDecoder decoder)
{
    /// <summary>Parses the complete reviewed sample-family representation.</summary>
    public PnrDocument Parse(FeedMessage message) => decoder.Decode(message);
}
