using PresidioDemo.Feeds.Shared;
namespace PresidioDemo.Feeds.PNR;
/// <summary>Decodes the sample-supported sequence and attaches supported meanings.</summary>
public sealed class PnrDecoder(PnrXdrReader reader)
{
    /// <summary>Returns all scalars, including unresolved numeric words.</summary>
    public PnrDocument Decode(FeedMessage message)
    {
        var envelope = FeedPayloadReader.Read(message);
        var parsed = reader.Read(message);
        var locations = XdrFeedCodec.TextLocations(parsed);
        var fields = parsed.Fields.Select(field =>
        {
            bool text = locations.TryGetValue(field.Id, out var span);
            return new PnrDecodedField(field.Id, field.Value, text ? span.Offset : null,
                text ? span.Length : 4, PnrStructureDefinitions.Find(field.Id));
        }).ToArray();
        return new(fields, envelope.Payload.Length, parsed);
    }
}
