namespace PresidioDemo.Feeds.PNR;
/// <summary>Creates a diagnostic report containing source data and field evidence.</summary>
public static class PnrReportFormatter
{
    /// <summary>Formats all decoded scalars, including unresolved fields.</summary>
    public static object Format(PnrDocument document) => new
    {
        document.PayloadLength,
        Fields = document.Fields,
        Notes = new[] {
            "Labels follow the P2 PNR Record Layout; P2 uses a different wire format from this XDR feed.",
            "Null meanings are unresolved; numeric offsets are not exposed by the shared decoder.",
            "List shapes are fitted to two samples; they do not establish stable passenger identities or a producer contract."
        }
    };
}
