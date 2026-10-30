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

#endregion

/// <summary>
/// Read-only status interpretation using the agreed provisional MSB-first convention.
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
        uint recognized = 1; // Document bit 31 has a conditional two-state meaning, not a flag.
        var flags = new List<EtktStatusFlag>();
        foreach (var (bit, label) in StatusLabels.OrderBy(pair => pair.Key))
        {
            uint mask = 1u << (31 - bit);
            recognized |= mask;
            if ((raw & mask) != 0)
            {
                flags.Add(new(bit, $"0x{mask:X8}", label + " (provisional)"));
            }
        }
        string? exchangeDestination = null;
        if ((raw & (1u << (31 - 7))) != 0)
        {
            exchangeDestination = (raw & 1) == 0 ? "ETKT (provisional)" : "Paper (provisional)";
        }
        return new(value, $"0x{raw:X8}", "Document bit 0 = most-significant bit (0x80000000)",
            "Provisional; sample-supported convention, pending producer confirmation",
            flags.AsReadOnly(), $"0x{raw & ~recognized:X8}", exchangeDestination);
    }

    #endregion
}
