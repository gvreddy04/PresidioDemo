using System.Text.RegularExpressions;
using PresidioDemo.Feeds.Shared;

namespace PresidioDemo.Feeds.ACI;

/// <summary>Checks passenger links and binds configured protection fields to decoded strings.</summary>
public sealed class AciParser
{
    public AciDocument Parse(ReadOnlyMemory<byte> payload)
    {
        var document = new AciDecoder().Decode(payload);
        if (document.Utype != 2 || document.PassengerList is null)
        {
            throw new InvalidDataException("ACI processing requires DATA with a passenger list; error records are retained.");
        }
        foreach (var passenger in EnumerateList(document.PassengerList))
        {
            string pnr = passenger.Scalar("rec_loc").Text!;
            if (!Regex.IsMatch(pnr, "\\A[A-Z0-9]{6}\\z", RegexOptions.CultureInvariant))
            {
                throw new InvalidDataException("ACI passenger record locator must contain six uppercase letters or digits.");
            }
            foreach (var flight in EnumerateList(passenger.Child("paxflt_items")))
            {
                if (passenger.Scalar("paxl_id").Number != flight.Scalar("pax_id").Number)
                {
                    throw new InvalidDataException("ACI paxl_id and pax_id passenger association differs.");
                }
                foreach (var extension in EnumerateList(flight.Child("unresolved_extension")))
                {
                    string reference = extension.Scalar("unresolvedRecordReference").Text!;
                    if (reference.Length > 0 && !Regex.IsMatch(reference, "\\A[0-9*]*" + Regex.Escape(pnr) + "\\z", RegexOptions.CultureInvariant))
                    {
                        throw new InvalidDataException("ACI unresolved extension PNR does not match its owning passenger.");
                    }
                }
            }
        }
        return document;
    }

    public static IEnumerable<AciNode> EnumerateList(AciNode? head)
    {
        for (var current = head; current is not null; current = current.Child("next"))
        {
            yield return current;
        }
    }

    internal static IReadOnlyList<AciTextBinding> Bind(AciDocument document, ParsedFeed fields,
        IReadOnlyDictionary<string, (int Offset, int Length)> locations)
    {
        var bindings = new List<AciTextBinding>();
        var coverage = new bool[document.PayloadLength];
        foreach (var field in fields.Fields)
        {
            if (!locations.TryGetValue(field.Id, out var span))
            {
                throw new InvalidDataException("ACI policy field has no XDR string location.");
            }
            var candidates = document.Scalars.Where(scalar => scalar.Kind == AciWireKind.String &&
                span.Offset >= scalar.ValueOffset && span.Offset + span.Length <= scalar.ValueOffset + scalar.ValueLength).ToArray();
            if (candidates.Length != 1 || span.Length != field.Value.Length)
            {
                throw new InvalidDataException("ACI configured field does not match exactly one decoded string.");
            }
            var scalar = candidates[0];
            int start = span.Offset - scalar.ValueOffset;
            if (scalar.Text!.Substring(start, span.Length) != field.Value)
            {
                throw new InvalidDataException("ACI configured field and semantic decoder disagree.");
            }
            for (int offset = span.Offset; offset < span.Offset + span.Length; offset++)
            {
                if (coverage[offset])
                {
                    throw new InvalidDataException("ACI protection fields overlap.");
                }
                coverage[offset] = true;
            }
            bindings.Add(new(field, scalar, start));
        }
        foreach (var scalar in document.Scalars.Where(scalar => scalar.Kind == AciWireKind.String))
        {
            for (int offset = scalar.ValueOffset; offset < scalar.ValueOffset + scalar.ValueLength; offset++)
            {
                if (!coverage[offset])
                {
                    throw new InvalidDataException($"Nonempty ACI text at {scalar.Path} lacks a reviewed field policy.");
                }
            }
        }
        return bindings.AsReadOnly();
    }
}
