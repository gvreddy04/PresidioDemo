namespace PresidioDemo.Feeds.ACI;

/// <summary>
/// A readable source-data view alongside exact scalar values and offsets.
/// </summary>
public static class AciReportFormatter
{

    #region Fields and Constants

    /// <summary>
    /// the documented ACI fields interpreted as character arrays for reporting
    /// </summary>
    private static readonly IReadOnlySet<string> CharacterArrays = new HashSet<string>(StringComparer.Ordinal)
    {
        "class_code", "orig_cls", "chg_cls", "xfer_to_cls", "sec_cat", "agt_int", "agt_dc", "oa_carcode", "oa_cls"
    };

    /// <summary>
    /// the documented ACI fields interpreted as individual characters for reporting
    /// </summary>
    private static readonly IReadOnlySet<string> Characters = new HashSet<string>(StringComparer.Ordinal)
    {
        "cmpt_code", "pax_type", "cmpt_flown", "cmpt_id", "tier_level"
    };

    #endregion

    #region Methods

    /// <summary>
    /// Creates the readable local diagnostic representation of the supplied message or document.
    /// </summary>
    /// <param name="document">The decoded ACI document containing original values and wire locations.</param>
    /// <returns>The complete readable diagnostic report retaining supplied source values.</returns>
    public static object Format(AciDocument document) => new
    {
        Utype = document.Utype == 2 ? "DATA" : "ERROR",
        document.PayloadLength,
        FlightList = List(document.FlightList),
        PassengerList = List(document.PassengerList),
        document.ErrorText,
        Notes = new[]
            {
            "Decoded supplied sample data; original values are intentionally visible in this analysis artifact.",
            "unresolved_extension has an observed sample layout; its business meaning is unconfirmed.",
            "Numeric passenger IDs are preserved by agreement. PNR is not a unique passenger identity.",
            "Empty/absent lists do not validate populated variants; processing also requires reviewed XML policy coverage."
        }
    };

    /// <summary>
    /// Formats a decoded character word without hiding its original numeric representation.
    /// </summary>
    /// <param name="value">The scalar value to inspect, format, encode or protect.</param>
    /// <returns>The printable character or an escaped representation of its original numeric value.</returns>
    private static string Character(int value) => value is >= 32 and <= 126 ? ((char)value).ToString() : $"\\x{value:X2}";

    /// <summary>
    /// Formats a decoded ACI linked list while preserving its node order.
    /// </summary>
    /// <param name="node">The decoded structure node to inspect or format.</param>
    /// <returns>The readable representations of the decoded linked-list nodes in source order.</returns>
    private static object[] List(AciNode? node) => AciParser.EnumerateList(node).Select(Node).ToArray();

    /// <summary>
    /// Formats a decoded ACI node with its nested members and original scalar values.
    /// </summary>
    /// <param name="node">The decoded structure node to inspect or format.</param>
    /// <returns>The readable node containing original scalar values and nested structures.</returns>
    private static object Node(AciNode node)
    {
        var result = new Dictionary<string, object?> { ["$structure"] = node.Type, ["$path"] = node.Path };
        foreach (var member in node.Members)
        {
            string name = member.Definition.Name;
            if (name == "next")
            {
                continue;
            }
            if (member.Definition.Kind == AciWireKind.Pointer)
            {
                result[name] = List(member.Child);
            }
            else if (member.Definition.Kind == AciWireKind.String)
            {
                result[name] = member.Scalar!.Text;
            }
            else if (name.EndsWith("_0", StringComparison.Ordinal) && CharacterArrays.Contains(name[..^2]))
            {
                string prefix = name[..^2];
                int first = member.Scalar!.Number!.Value, second = node.Scalar(prefix + "_1").Number!.Value;
                result[prefix] = new { Text = Character(first) + Character(second), Codes = new[] { first, second } };
            }
            else if (name.EndsWith("_1", StringComparison.Ordinal) && CharacterArrays.Contains(name[..^2]))
            {
                continue;
            }
            else
            {
                int value = member.Scalar!.Number!.Value;
                object? interpretation = AciIndicators.Describe(name, value);
                result[name] = interpretation ?? (Characters.Contains(name)
                    ? new { Code = value, Text = Character(value) } : (object)value);
            }
        }
        return result;
    }

    #endregion
}
