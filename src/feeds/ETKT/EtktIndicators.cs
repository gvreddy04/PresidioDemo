using System.Collections.ObjectModel;

namespace PresidioDemo.Feeds.ETKT;

public sealed record EtktStatusFlag(int DocumentBit, string Mask, string Label);
public sealed record EtktStatusInterpretation(int Number, string Hex, string BitNumbering,
    string Confidence, IReadOnlyList<EtktStatusFlag> SetDocumentedFlags,
    string UnassignedSetBits, string? ExchangeDestination);

/// <summary>Read-only status interpretation using the agreed provisional MSB-first convention.</summary>
public static class EtktIndicators
{
    private static readonly IReadOnlyDictionary<int, string> StatusLabels = new ReadOnlyDictionary<int, string>(
        new Dictionary<int, string>
        {
            [0] = "Created", [1] = "Used", [2] = "Unused after being used", [3] = "No-show",
            [4] = "Oversold", [5] = "Voided", [6] = "Revoked", [7] = "Exchanged",
            [8] = "Refund requested", [9] = "Refunded", [10] = "Refund denied", [11] = "Refund adjusted",
            [12] = "Printed", [13] = "Printed last transaction", [14] = "Adjusted name", [15] = "Adjusted flight",
            [16] = "Adjusted purge date", [17] = "Divided ETKT", [18] = "Checked in", [19] = "Coupon used by agent entry",
            [20] = "Used / converted to FIM", [21] = "Marked ABP", [22] = "ATO control", [23] = "Restored",
            [24] = "Receipt printed", [25] = "Unavailable", [26] = "Boarded / lifted", [30] = "Flight adjustment was re-accommodation"
        });

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
}
