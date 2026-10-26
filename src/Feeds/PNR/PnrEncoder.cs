namespace PresidioDemo.Feeds.PNR;
/// <summary>Reconstructs an unchanged PNR document.</summary>
public sealed class PnrEncoder(PnrXdrWriter writer)
{
    /// <summary>Encodes the complete decoded message in its original container.</summary>
    public byte[] Encode(PnrDocument document) => writer.Write(document.WireFields,
        document.WireFields.Fields.ToDictionary(field => field.Id, field => field.Value));
}
