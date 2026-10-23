using System.Text.RegularExpressions;
using PresidioDemo.Feeds.Shared;

namespace PresidioDemo.Feeds.ACI;

#region Classes

/// <summary>
/// Checks passenger links and binds configured protection fields to decoded strings.
/// </summary>
public sealed class AciParser
{

    #region Methods

    /// <summary>
    /// Enumerates decoded linked-list nodes in their original structure order.
    /// </summary>
    /// <param name="head">The optional decoded linked-list head.</param>
    /// <returns>The linked-list nodes in their original decoded order.</returns>
    public static IEnumerable<AciNode> EnumerateList(AciNode? head)
    {
        for (var current = head; current is not null; current = current.Child("next"))
        {
            yield return current;
        }
    }

    /// <summary>
    /// Parses the complete input, validates its supported shape and retains the layout required for reconstruction.
    /// </summary>
    /// <param name="payload">The complete decoded payload bytes without transport metadata.</param>
    /// <returns>The validated ACI document with its complete original scalar and structure values.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
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

    /// <summary>
    /// Matches every reviewed protection field to exactly one decoded text span and verifies coverage.
    /// </summary>
    /// <param name="document">The decoded ACI document containing original values and wire locations.</param>
    /// <param name="fields">The complete extracted field set and its retained codec-specific serialization layout.</param>
    /// <param name="locations">The exact reviewed payload offsets and lengths indexed by protection-field identifier.</param>
    /// <returns>The validated bindings between reviewed protection fields and decoded text spans.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
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

    #endregion
}

#endregion
