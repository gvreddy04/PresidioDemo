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
            "Labels are based on sample content and related P2 business descriptions; P2 uses a different wire format.",
            "Null meanings are unresolved; numeric offsets are not exposed by the shared decoder.",
            "One sample does not establish optional structures, stable passenger identities or a producer contract."
        }
    };
}
