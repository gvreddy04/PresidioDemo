namespace PresidioDemo.Feeds.ACI;

/// <summary>A readable source-data view alongside exact scalar values and offsets.</summary>
public static class AciReportFormatter
{
    private static readonly IReadOnlySet<string> CharacterArrays = new HashSet<string>(StringComparer.Ordinal)
    {
        "class_code", "orig_cls", "chg_cls", "xfer_to_cls", "sec_cat", "agt_int", "agt_dc", "oa_carcode", "oa_cls"
    };
    private static readonly IReadOnlySet<string> Characters = new HashSet<string>(StringComparer.Ordinal)
    {
        "cmpt_code", "pax_type", "cmpt_flown", "cmpt_id", "tier_level"
    };

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

    private static object[] List(AciNode? node) => AciParser.EnumerateList(node).Select(Node).ToArray();

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

    private static string Character(int value) => value is >= 32 and <= 126 ? ((char)value).ToString() : $"\\x{value:X2}";
}
