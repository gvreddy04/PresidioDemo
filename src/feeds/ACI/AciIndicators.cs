namespace PresidioDemo.Feeds.ACI;

#region Classes

/// <summary>
/// Documented byte/bit meanings. Byte 0 is the first byte of the big-endian word.
/// </summary>
public static class AciIndicators
{

    #region Fields and Constants

    /// <summary>
    /// the documented ACI indicator-label definitions
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Definitions = new Dictionary<string, string>
    {
        ["flt_ind"] = "2.0=Common map assigned;2.1=Ship assigned;2.2=Advance seat assignment;2.3=Landing cancel at terminating city;2.4=Date change flight;2.5=Outbound PNL sent;2.6=PNL process complete;2.7=Partial seat selection/open seating;3.0=Hardblock flight;3.1=OA operational flight;3.2=OA check-in flight;3.3=OA shared flight;3.4=APIS flight;3.5=APIS sent;3.6=APIS manually set off;3.7=Marketing check-in flight",
        ["leg_ind"] = "0.0=Leg open for seat assignment;0.1=Leg closed for seat assignment;0.2=Standby boarding started;0.3=Standby boarding complete;0.4=City reopened;0.5=Preliminary close done;0.6=Subsequent close done;0.7=Mark non-revenue passengers as no-meal;1.0=Landing cancel inbound;1.1=Leg restricted to board agent;1.2=City in control;1.3=Landing cancel outbound station;1.4=Check-in restricted to this city;1.6=Mark NR passengers as no-meal;2.0=Secondary equipment;2.1=Common map for secondary equipment;2.2=Post departure completed this leg;2.3=PCC with bypass;2.4=IFM message;2.5=Manual mode;2.6=BSM this leg;2.7=Non-smoking leg;3.0=APIS boardpoint;3.1=Flight remarks;3.2=No APIS this boardpoint;3.3=TCI inhibited;3.4=Immediate standby boarding enabled for revenue passenger;3.5=Force non-confirmed passenger to standby;3.6=ACI log message;3.7=Security profile this leg",
        ["leg_list_int1"] = "0.0=Boarding closed/PRC close done;0.1=Boarding reopened/PRC reopen done",
        ["pnr_ind"] = "3.0=Primary name in PNR;3.1=Member of group PNR;3.2=APIS PNR",
        ["pax_ind"] = "0.0=No-show;0.1=Misconnect;0.2=No meal;0.3=Protect from post departure processing;0.4=Multiple segments this flight/date;0.5=Holding other flight;0.6=APIS bypassed;0.7=APIS completed;1.0=Advanced seats assigned;1.1=Advanced boarding pass;1.2=Held seats assigned;1.3=Passenger checked-in;1.4=Bag tags issued;1.5=Interline check-in;1.6=Passenger seated on TCI connection;1.7=Exit seats assigned;2.0=High yield;2.1=Other airline passenger;2.2=E-ticket passenger;2.3=Desiring through passenger;2.4=Mass transferred;2.5=Split party;2.6=Partial routed by agent entry;2.7=Bag profile random selectee;3.0=Grade change;3.1=Upgrade standby for other compartment;3.2=Class changed by 6-RG entry;3.3=Processed from standby list;3.4=Conditional standby;3.5=Voluntary denied boarding;3.6=Extra crew;3.7=Diplomat/Law Enforcement",
        ["pax_ind2"] = "2.0=Special service requested;2.2=Message this passenger;2.3=Remarks exist;2.4=Item processed through reaccom;2.5=Child/bag/infants processed;2.6=Post departure processed;2.7=Image e-ticket;3.0=Military;3.1=Wrap flight passenger;3.2=Previous flight in consolidated flight;3.3=Next flight in consolidated flight;3.4=ACI record cancelled",
        ["spec_ind"] = "2.0=Armed passenger;2.1=Standby for F class;2.2=Standby for J class;2.3=Standby for C class;3.0=Ticketed bag;3.1=Unescorted minor;3.2=Disabled;3.3=Prisoner/guard;3.4=Incapacitated;3.5=Passenger on oxygen;3.6=Forced-sell;3.7=Go-show",
        ["paxflt_item_int1"] = "0.0=Item processed from standby list;0.1=Checked in to standby list;0.2=First class standby;0.3=Standby seated in lower compartment;0.4=Positive standby wanting upgrade;0.5=Voluntary upgrade;0.6=Alternate flight;0.7=Cancelled from standby list;1.0=Not seated due to insufficient seats;1.1=Processed by 6:KF entry;1.3=Cancelled and transferred by 6:KT;1.4=OA passenger;1.5=Processed by 6:GO;1.6=Internal elite access upgrade",
        ["seat_typ"] = "3.0=Indifferent;3.1=Non-smoking;3.2=Smoking;3.3=Held seats;3.5=WAD API not used in ACI;3.6=WAD API OK not used in ACI;3.7=WAD no API not used in ACI",
        ["bag_ind"] = "2.0=Weight in pounds;2.1=Weight in kilograms;2.2=Bag count input;2.3=Bag weight input;2.4=Unchecked bag count input;2.5=Unchecked bag weight input;3.0=Excess bag;3.1=VIP bag;3.2=NRSA bag;3.3=REV-SB bag;3.4=Heavy bag",
        ["prd_ind"] = "3.0=Weight in pounds;3.1=Weight in kilograms",
        ["tci_ind"] = "3.0=URP for outbound other airline flight;3.1=URP for inbound other airline flight",
        ["misc_ind"] = "3.0=Boarding pass issued on full flight",
        ["codeshr_ind"] = "0.0=TTY booking;0.1=PNL/ADL booking;1.0=Codeshare passenger"
    };

    #endregion

    #region Methods

    /// <summary>
    /// Returns readable labels for the documented indicator values without changing their bits.
    /// </summary>
    /// <param name="name">The declared member name, configuration setting or fixture filename.</param>
    /// <param name="value">The scalar value to inspect, format, encode or protect.</param>
    /// <returns>The readable documented flags, or null when this indicator has no reviewed definition.</returns>
    public static object? Describe(string name, int value)
    {
        uint raw = unchecked((uint)value);
        if (name == "seq_nbr")
        {
            return new
            {
                Hex = raw.ToString("X8"),
                SequenceNumber = raw & 0x00FFFFFF,
                BoardingStatusByte = raw >> 24,
                Note = "Byte-0 0x80 reconciled is documented as unused in ACI."
            };
        }
        if (name == "fqtv_ind")
        {
            string[] eliteLevels = ["Not defined", "GLOBAL SERVICES", "PREMIER 1K", "PLATINUM", "GOLD", "SILVER", "PRESIDENTIAL PLATINUM", "Spare"];
            var common = new List<string>();
            foreach (var (bit, description) in new[] { (3, "Preferred/elite member"), (4, "FQTR number/redeem"),
                (5, "OA FQTV number"), (6, "Name initial checked-in"), (7, "Presidential Platinum / UA Premier Associate") })
            {
                if ((raw & (1u << bit)) != 0)
                {
                    common.Add(description);
                }
            }
            string[] alternate = ["Preferred class 7/platinum UA1K", "Preferred class 6/gold UA premier executive", "Preferred class 5/silver UA premier"];
            return new
            {
                Hex = raw.ToString("X8"),
                LowThreeBits = raw & 7,
                IfGlobalServicesPremier1KMode = eliteLevels[raw & 7],
                IfLegacyMode = alternate.Where((_, bit) => (raw & (1u << bit)) != 0).ToArray(),
                CommonSetFlags = common,
                Note = "Alternative low-bit interpretations depend on producer configuration; neither mode is selected here."
            };
        }
        if (!Definitions.TryGetValue(name, out var definition))
        {
            return null;
        }
        uint documented = 0;
        var setFlags = new List<string>();
        foreach (string flag in definition.Split(';'))
        {
            string[] parts = flag.Split('=');
            string[] address = parts[0].Split('.');
            int byteIndex = int.Parse(address[0]), bit = int.Parse(address[1]);
            uint mask = 1u << ((3 - byteIndex) * 8 + bit);
            documented |= mask;
            if ((raw & mask) != 0)
            {
                setFlags.Add(parts[1]);
            }
        }
        return new
        {
            Hex = raw.ToString("X8"),
            SetDocumentedFlags = setFlags,
            UnassignedSetBits = (raw & ~documented).ToString("X8"),
            Note = name == "leg_ind" ? "Byte 1 bit 7 distinguishes boarding adjustment ETD/STD; raw value retained." : "Spare/unused bits are retained without inferred meaning."
        };
    }

    #endregion
}

#endregion
