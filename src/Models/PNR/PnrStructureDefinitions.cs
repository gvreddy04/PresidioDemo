namespace PresidioDemo.Feeds.PNR;
/// <summary>P2 PNR Record Layout meanings for schema paths; the P2 spec defines field content, not this feed's wire order.</summary>
public static class PnrStructureDefinitions
{
    private static readonly IReadOnlyDictionary<string, PnrFieldMeaning> Meanings = Build();
    /// <summary>Returns a supported label for a schema path, or null if the business meaning is unresolved.</summary>
    public static PnrFieldMeaning? Find(string path) => Meanings.GetValueOrDefault(path);
    private static IReadOnlyDictionary<string, PnrFieldMeaning> Build()
    {
        var result = new Dictionary<string, PnrFieldMeaning>(StringComparer.Ordinal);
        void Add(string path, string category, string label, string evidence) => result.Add(path, new(category, label, evidence));
        Add("/general/hostAirline", "General", "Host airline code", "09.jpeg: key 01 P20HALC");
        Add("/general/pnr", "General", "Record locator", "09.jpeg: key 01 P20KRLO");
        Add("/general/creationDate", "General", "PNR creation date", "09.jpeg: key 01 P20KCDT");
        Add("/general/creationTime", "General", "PNR creation time (GMT)", "09.jpeg: key 01 P20KTIM");
        Add("/general/transactionDate", "General", "PNR transaction date", "10.jpeg: key 01 P20TDAT");
        Add("/general/transactionTime", "General", "PNR transaction time (GMT)", "10.jpeg: key 01 P20TTIM");
        Add("/general/p2CreationTime", "General", "P2 creation time (PT, HHMMSSnnn)", "10.jpeg: key 01 P20PTIM");
        Add("/names/*/surname", "Passenger", "Surname", "17.jpeg: key 20 P20PSNM");
        Add("/firstNames/*/givenName", "Passenger", "Given name", "19.jpeg: key 30 P20PNAM");
        Add("/facts/*/itemNumber", "General fact", "Fact item number", "21.jpeg: key 40 P20P4K2");
        Add("/facts/*/factType", "General fact", "Fact type (G = Gen Fax)", "22.jpeg: key 40 P20PFTY");
        Add("/facts/*/factCode", "General fact", "SSR or OSI type", "22.jpeg: key 40 P20PFCD");
        Add("/facts/*/text", "General fact", "SSR or OSI text", "22.jpeg: key 40 P20PFTX");
        Add("/frequentFlyers/*/itemNumber", "Frequent flyer", "Fact item number", "24.jpeg: key 43 P20P432");
        Add("/frequentFlyers/*/action", "Frequent flyer", "Action indicator", "25.jpeg: key 43 P20PFQI");
        Add("/frequentFlyers/*/factCode", "Frequent flyer", "Fact type", "25.jpeg: key 43 P20PFC3");
        Add("/frequentFlyers/*/serviceCode", "Frequent flyer", "Service code", "25.jpeg: key 43 P20PPSC");
        Add("/frequentFlyers/*/carrier", "Frequent flyer", "SSR carrier code", "25.jpeg: key 43 P20PCAR");
        Add("/frequentFlyers/*/account", "Frequent flyer", "FQTV number and tier text", "25-26.jpeg: key 43 P20PFQT, P20ESTX");
        Add("/frequentFlyers/*/holderName", "Frequent flyer", "Name of FQTV holder", "25.jpeg: key 43 P20PNME");
        Add("/itinerary/*/itemNumber", "Itinerary", "Segment item number", "28.jpeg: key 80 P20P8K2");
        Add("/itinerary/*/segmentType", "Itinerary", "Segment type", "29.jpeg: key 80 P20PIT0");
        Add("/itinerary/*/actionCode", "Itinerary", "Current action code", "30.jpeg: key 80 P20PCAC");
        Add("/itinerary/*/departureDate", "Itinerary", "Departure date", "30.jpeg: key 80 P20PDDT");
        Add("/itinerary/*/dayOfWeek", "Itinerary", "Departure day of week", "30.jpeg: key 80 P20PDAY");
        Add("/itinerary/*/partySize", "Itinerary", "Number in party", "30.jpeg: key 80 P20PNIP");
        Add("/itinerary/*/carrier", "Itinerary", "Operational carrier code", "30.jpeg: key 80 P20PCC");
        Add("/itinerary/*/previousActionCode", "Itinerary", "Previous action code", "30.jpeg: key 80 P20PPAC");
        Add("/itinerary/*/boardPoint", "Itinerary", "Board point", "30.jpeg: key 80 P20PBRD");
        Add("/itinerary/*/offPoint", "Itinerary", "Off point", "31.jpeg: key 80 P20POFF");
        Add("/itinerary/*/departureTime", "Itinerary", "Departure time", "31.jpeg: key 80 P20PDTM");
        Add("/itinerary/*/arrivalTime", "Itinerary", "Arrival time", "31.jpeg: key 80 P20PATM");
        Add("/itinerary/*/classOfService", "Itinerary", "Class of service", "31.jpeg: key 80 P20PCOS");
        Add("/itinerary/*/flightNumber", "Itinerary", "Flight number", "31.jpeg: key 80 P20PFL1");
        return result;
    }
}
