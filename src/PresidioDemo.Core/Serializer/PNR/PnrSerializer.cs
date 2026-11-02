using PresidioDemo.Feeds.Shared;
namespace PresidioDemo.Feeds.PNR;
/// <summary>Applies and validates the existing reviewed protection projection.</summary>
public sealed class PnrSerializer(ReferenceLayoutFeedCodec projection)
{
    internal byte[] Serialize(PnrParsedLayout layout, IReadOnlyDictionary<string, string> fields) => projection.Serialize(layout.Projection, fields);
    internal void Validate(PnrParsedLayout layout, ReadOnlyMemory<byte> serialized) => projection.Validate(layout.Projection, serialized);
}
