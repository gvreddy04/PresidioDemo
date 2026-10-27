namespace PresidioDemo.Feeds.PNR;
/// <summary>Observed sample aliases and related P2 business descriptions, not a producer schema.</summary>
public static class PnrStructureDefinitions
{
    private static readonly IReadOnlyDictionary<string, PnrFieldMeaning> Meanings = Build();
    /// <summary>Returns a supported label, or null if the business meaning is unresolved.</summary>
    public static PnrFieldMeaning? Find(string path) => Meanings.GetValueOrDefault(path);
    private static IReadOnlyDictionary<string, PnrFieldMeaning> Build()
    {
        var result = new Dictionary<string, PnrFieldMeaning>(StringComparer.Ordinal);
        void Add(string names, string category, string label, string evidence)
        {
            foreach (string name in names.Split(' '))
            {
                result.Add("/" + name, new(category, label, evidence));
            }
        }
        Add("RecordTag001", "General", "Airline code", "09.jpeg: key 01");
        Add("pnr", "General", "Record locator", "09.jpeg: key 01");
        Add("passengerSurname", "Passenger", "Surname", "17.jpeg: key 20");
        Add("passengerGivenName", "Passenger", "Given name", "19.jpeg: key 30");
        Add("RecordTag092 RecordTag103 RecordTag114 RecordTag125 RecordTag136 RecordTag147 RecordTag158 RecordTag169 RecordTag180 RecordTag191 RecordTag202 RecordTag213 RecordTag224 RecordTag235 RecordTag246", "General fact", "SSR or OSI type", "22.jpeg: key 40");
        Add("travelDocumentRecord emailContactRecord phoneContactRecord ticketRecord1 ticketRecord2 Remark0592 Remark0696 Remark0796 Remark1052 Remark1140 Remark1228 Remark1316 Remark1388 Remark1488 Remark1572", "General fact", "SSR or OSI text", "22.jpeg: key 40");
        Add("RecordTag259 RecordTag275", "Frequent flyer", "Fact type", "25.jpeg: key 43");
        Add("RecordTag260 RecordTag276", "Frequent flyer", "Service code", "25.jpeg: key 43");
        Add("RecordTag261 RecordTag277", "Frequent flyer", "Carrier code", "25.jpeg: key 43");
        Add("loyaltyRecord1 loyaltyRecord2", "Frequent flyer", "Membership and status text", "25-26.jpeg: key 43");
        Add("passengerNameOccurrence1 passengerNameOccurrence2", "Frequent flyer", "Passenger name text", "25.jpeg: key 43");
        Add("RecordTag338 RecordTag376", "Itinerary", "Current action code", "30.jpeg: key 80");
        Add("TravelDate2444 TravelDate2652", "Itinerary", "Departure date", "30.jpeg: key 80");
        Add("RecordTag340 RecordTag378", "Itinerary", "Departure day of week", "30.jpeg: key 80");
        Add("Airline2468 Airline2676", "Itinerary", "Carrier code", "30.jpeg: key 80");
        Add("RecordTag343 RecordTag381", "Itinerary", "Previous action code", "30.jpeg: key 80");
        Add("Origin2484 Origin2692", "Itinerary", "Board point", "30.jpeg: key 80");
        Add("Destination2492 Destination2700", "Itinerary", "Off point", "31.jpeg: key 80");
        Add("DepartureTime2500 DepartureTime2708", "Itinerary", "Departure time", "31.jpeg: key 80");
        Add("ArrivalTime2508 ArrivalTime2716", "Itinerary", "Arrival time", "31.jpeg: key 80");
        return result;
    }
}
