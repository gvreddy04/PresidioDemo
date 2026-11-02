namespace PresidioDemo.Feeds.ETKT;

/// <summary>
/// Shows source spellings, raw flags and character arrays without inventing enum meanings.
/// </summary>
public static class EtktReportFormatter
{

    #region Methods

    /// <summary>
    /// Creates the readable local diagnostic representation of the supplied message or document.
    /// </summary>
    /// <param name="document">The decoded ETKT document containing original values and wire locations.</param>
    /// <returns>The complete readable diagnostic report retaining supplied source values.</returns>
    public static object Format(EtktDocument document) => new
    {
        document.SampleType,
        Profile = "Observed type-5 framing; screenshot history-field order",
        document.PayloadLength,
        FieldLabels = new[]
            {
            new
            {
                Path = "/history/h_aaa_lniata",
                Label = "Agent terminal address — provisional mapping",
                Alias = "/agentTerminalAddress",
                Treatment = "Keep unchanged"
            },
            new
            {
                Path = "/history/h_tkt_nbr",
                Label = "Ticket number",
                Alias = "/ticketNumber",
                Treatment = "Mask"
            },
            new
            {
                Path = "/history/glob_tkt_hist_seq_nbr",
                Label = "Global ticket history sequence number",
                Alias = "/historySequenceNumber",
                Treatment = "Mask"
            },
            new
            {
                Path = "/history/h_new_recloc",
                Label = "New PNR — provisional",
                Alias = "/newPnr",
                Treatment = "Keep unchanged"
            },
            new
            {
                Path = "/history/h_old_recloc",
                Label = "Old PNR — provisional",
                Alias = "/oldPnr",
                Treatment = "Keep unchanged"
            }
        },
        Groups = document.Scalars.GroupBy(scalar => scalar.Definition.Group).ToDictionary(
                group => group.Key, group => group.GroupBy(scalar => scalar.Definition.SourceName).ToDictionary(
                    members => members.Key, members => Value(members.ToArray()))),
        Notes = new[]
            {
            "The source defines error=1, issue=2 and update=3. The meaning of observed type 5 is unconfirmed.",
            "Names describe a provisional structural match; type-5 business meanings need producer confirmation.",
            "Original sample values are intentionally visible in these local analysis artifacts.",
            "Zero-only fields do not establish support for their populated variants.",
            "Status and indicator labels use the approved provisional MSB-first convention; original numeric and hex values are retained.",
            "No passenger identity or tokenization is inferred from names, PNRs or ticket numbers."
        }
    };

    /// <summary>
    /// Formats decoded ETKT text, character arrays or numeric words without changing their values.
    /// </summary>
    /// <param name="fields">The complete ordered parsed field set or reviewed field definitions.</param>
    /// <returns>The readable text, character array or numeric-word representation.</returns>
    private static object Value(EtktScalar[] fields)
    {
        if (fields[0].Kind == EtktWireKind.String)
        {
            return fields[0].Text!;
        }
        if (fields[0].Kind == EtktWireKind.Character)
        {
            return new
            {
                Text = string.Concat(fields.Select(field => field.Number is >= 32 and <= 126
                    ? ((char)field.Number.Value).ToString() : $"\\x{unchecked((byte)field.Number!.Value):X2}")),
                Codes = fields.Select(field => field.Number!.Value).ToArray()
            };
        }
        int number = fields[0].Number!.Value;
        if (fields[0].Path == "/history/h_status")
        {
            return EtktIndicators.DescribeStatus(number);
        }
        if (fields[0].Path == "/history/h_indicator")
        {
            return EtktIndicators.DescribeIndicator(number);
        }
        return new { Number = number, Hex = $"0x{unchecked((uint)number):X8}" };
    }

    #endregion
}
