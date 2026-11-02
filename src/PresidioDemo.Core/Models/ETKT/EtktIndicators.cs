using System.Collections.ObjectModel;

namespace PresidioDemo.Feeds.ETKT;

#region Models

/// <summary>
/// Describes a documented ETKT status bit using its original mask.
/// </summary>
/// <param name="DocumentBit">The bit index used by the source status declaration.</param>
/// <param name="Mask">The original status-bit mask in hexadecimal.</param>
/// <param name="Label">The readable description associated with the original bit or fixture.</param>
public sealed record EtktStatusFlag(int DocumentBit, string Mask, string Label);

/// <summary>
/// Contains raw ETKT status values and their provisional readable interpretation.
/// </summary>
/// <param name="Number">The original decoded integer value, when the scalar is numeric.</param>
/// <param name="Hex">The original word represented in hexadecimal.</param>
/// <param name="BitNumbering">The documented convention used to associate bit numbers with masks.</param>
/// <param name="Confidence">The qualification applied to the provisional field or status interpretation.</param>
/// <param name="SetDocumentedFlags">The documented status flags present in the original numeric word.</param>
/// <param name="UnassignedSetBits">The original set bits that have no reviewed status label.</param>
/// <param name="ExchangeDestination">The provisional destination interpretation when the exchange flag is set.</param>
public sealed record EtktStatusInterpretation(int Number, string Hex, string BitNumbering,
    string Confidence, IReadOnlyList<EtktStatusFlag> SetDocumentedFlags,
    string UnassignedSetBits, string? ExchangeDestination);

/// <summary>
/// Contains a raw ETKT history indicator word and its provisional readable interpretation.
/// </summary>
/// <param name="Number">The original decoded integer value, when the scalar is numeric.</param>
/// <param name="Hex">The original word represented in hexadecimal.</param>
/// <param name="BitNumbering">The documented convention used to associate bit numbers with masks.</param>
/// <param name="Confidence">The qualification applied to the provisional field or status interpretation.</param>
/// <param name="IdType">The three-bit identifier type held in document bits 0–2.</param>
/// <param name="IdTypeLabel">The documented meaning of the identifier type, when one is documented.</param>
/// <param name="SetDocumentedFlags">The documented indicator flags present in the original numeric word.</param>
/// <param name="UnassignedSetBits">The original set bits that have no reviewed indicator label.</param>
public sealed record EtktIndicatorInterpretation(int Number, string Hex, string BitNumbering,
    string Confidence, int IdType, string? IdTypeLabel, IReadOnlyList<EtktStatusFlag> SetDocumentedFlags,
    string UnassignedSetBits);

#endregion

/// <summary>
/// Read-only status and indicator interpretation using the agreed provisional MSB-first convention.
/// </summary>
public static class EtktIndicators
{

    #region Fields and Constants

    /// <summary>
    /// Maps documented ETKT status bits to provisional readable labels.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, string> StatusLabels = new ReadOnlyDictionary<int, string>(
            new Dictionary<int, string>
            {
                [0] = "Created",
                [1] = "Used",
                [2] = "Unused after being used",
                [3] = "No-show",
                [4] = "Oversold",
                [5] = "Voided",
                [6] = "Revoked",
                [7] = "Exchanged",
                [8] = "Refund requested",
                [9] = "Refunded",
                [10] = "Refund denied",
                [11] = "Refund adjusted",
                [12] = "Printed",
                [13] = "Printed last transaction",
                [14] = "Adjusted name",
                [15] = "Adjusted flight",
                [16] = "Adjusted purge date",
                [17] = "Divided ETKT",
                [18] = "Checked in",
                [19] = "Coupon used by agent entry",
                [20] = "Used / converted to FIM",
                [21] = "Marked ABP",
                [22] = "ATO control",
                [23] = "Restored",
                [24] = "Receipt printed",
                [25] = "Unavailable",
                [26] = "Boarded / lifted",
                [30] = "Flight adjustment was re-accommodation"
            });

    /// <summary>
    /// Maps documented hist_data.h_indicator bits to provisional readable labels; bits 0–2 hold the ID type.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, string> IndicatorLabels = new ReadOnlyDictionary<int, string>(
            new Dictionary<int, string>
            {
                [3] = "ETKT was in PNR in AAA",
                [4] = "Recovery utility",
                [7] = "Actioned all subsequent coupons",
                [8] = "A.S.A.P. set",
                [9] = "Training mode",
                [10] = "Lift TTY inhibit",
                [11] = "CRS/OA action",
                [12] = "Action for OA",
                [13] = "CDS action",
                [14] = "OA-created ticket"
            });

    /// <summary>
    /// The documented MSB-first bit convention shared by status and indicator words.
    /// </summary>
    private const string BitNumbering = "Document bit 0 = most-significant bit (0x80000000)";

    /// <summary>
    /// The qualification shared by status and indicator interpretations.
    /// </summary>
    private const string Confidence = "Provisional; sample-supported convention, pending producer confirmation";

    #endregion

    #region Methods

    /// <summary>
    /// Returns raw ETKT status values and provisional labels while retaining unassigned bits.
    /// </summary>
    /// <param name="value">The original signed ETKT status word whose bits are interpreted without modification.</param>
    /// <returns>The raw status word, provisional documented flags and unassigned set bits.</returns>
    public static EtktStatusInterpretation DescribeStatus(int value)
    {
        uint raw = unchecked((uint)value);
        // Document bit 31 has a conditional two-state meaning, not a flag.
        var (flags, unassigned) = Flags(raw, StatusLabels, recognized: 1);
        string? exchangeDestination = null;
        if ((raw & (1u << (31 - 7))) != 0)
        {
            exchangeDestination = (raw & 1) == 0 ? "ETKT (provisional)" : "Paper (provisional)";
        }
        return new(value, $"0x{raw:X8}", BitNumbering, Confidence, flags, unassigned, exchangeDestination);
    }

    /// <summary>
    /// Returns the raw ETKT history indicator word, its ID type and provisional labels while retaining unassigned bits.
    /// </summary>
    /// <param name="value">The original signed ETKT indicator word whose bits are interpreted without modification.</param>
    /// <returns>The raw indicator word, ID type, provisional documented flags and unassigned set bits.</returns>
    public static EtktIndicatorInterpretation DescribeIndicator(int value)
    {
        uint raw = unchecked((uint)value);
        // Document bits 0–2 form one three-bit ID type field, not three flags.
        var (flags, unassigned) = Flags(raw, IndicatorLabels, recognized: 0xE0000000);
        int idType = (int)(raw >> 29);
        return new(value, $"0x{raw:X8}", BitNumbering, Confidence, idType,
            idType == 1 ? "ETKT (provisional)" : null, flags, unassigned);
    }

    /// <summary>
    /// Lists the labelled set bits of a word and the set bits that are neither labelled nor otherwise recognized.
    /// </summary>
    /// <param name="raw">The original word whose bits are interpreted without modification.</param>
    /// <param name="labels">The documented labels indexed by MSB-first document bit.</param>
    /// <param name="recognized">The bits interpreted outside the labels, which are never reported as unassigned.</param>
    /// <returns>The set documented flags and the hexadecimal unassigned set bits.</returns>
    private static (IReadOnlyList<EtktStatusFlag> Flags, string Unassigned) Flags(uint raw,
        IReadOnlyDictionary<int, string> labels, uint recognized)
    {
        var flags = new List<EtktStatusFlag>();
        foreach (var (bit, label) in labels.OrderBy(pair => pair.Key))
        {
            uint mask = 1u << (31 - bit);
            recognized |= mask;
            if ((raw & mask) != 0)
            {
                flags.Add(new(bit, $"0x{mask:X8}", label + " (provisional)"));
            }
        }
        return (flags.AsReadOnly(), $"0x{raw & ~recognized:X8}");
    }

    #endregion
}
