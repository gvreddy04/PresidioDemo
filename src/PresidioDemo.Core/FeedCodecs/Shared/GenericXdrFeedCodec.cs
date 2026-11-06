using System.Globalization;
using System.Text;

namespace PresidioDemo.Feeds.Shared;

/// <summary>
/// Decodes any XDR feed without a per-feed schema: every counted string of four or more printable characters becomes a
/// field, and protected values are written back at the same width so the framing stays valid (WR-004, WR-015).
/// </summary>
/// <param name="feed">The binary feed route this codec serves.</param>
public sealed class GenericXdrFeedCodec(FeedType feed) : IFeedCodec
{

    #region Methods

    /// <summary>
    /// Returns the field identifier used for one candidate slot.
    /// </summary>
    /// <param name="slot">The zero-based candidate slot.</param>
    /// <returns>The slot identifier, such as <c>/s/12</c>.</returns>
    public static string SlotId(int slot) => "/s/" + slot.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Returns the scanned candidates retained by a parse from this codec.
    /// </summary>
    /// <param name="parsed">A parse result from <see cref="Parse"/>.</param>
    /// <returns>The candidates in payload order.</returns>
    /// <exception cref="InvalidDataException">The parse came from another codec.</exception>
    public static IReadOnlyList<XdrTextCandidate> Candidates(ParsedFeed parsed) =>
        parsed.Layout is Layout layout ? layout.Candidates : throw new InvalidDataException("The parse did not come from the generic XDR codec.");

    /// <summary>
    /// Reads the transport envelope and scans the payload for candidate strings.
    /// </summary>
    /// <param name="message">The complete input delivery.</param>
    /// <returns>One field per candidate, with the layout needed to rebuild the message.</returns>
    /// <exception cref="InvalidDataException">The route differs or the framing cannot be decoded.</exception>
    public ParsedFeed Parse(FeedMessage message)
    {
        if (message.Delivery.Feed != feed)
        {
            throw new InvalidDataException("Generic XDR codec and delivery route differ.");
        }

        var envelope = FeedPayloadReader.Read(message);
        var candidates = XdrTextScanner.Scan(envelope.Payload);
        var fields = candidates.Select(c => new FeedField(SlotId(c.Slot), "slot" + c.Slot.ToString(CultureInfo.InvariantCulture), c.Value)).ToArray();
        return new ParsedFeed(fields, new Layout(message, envelope, candidates));
    }

    /// <summary>
    /// Writes each protected value over its candidate span and rebuilds the original transport representation.
    /// </summary>
    /// <param name="original">The parse result.</param>
    /// <param name="anonymizedFields">The protected value for every field, by identifier.</param>
    /// <returns>The complete rebuilt message.</returns>
    /// <exception cref="InvalidDataException">A value is missing, changed width or is not printable ASCII.</exception>
    public byte[] Serialize(ParsedFeed original, IReadOnlyDictionary<string, string> anonymizedFields)
    {
        var layout = Require(original);
        if (anonymizedFields.Count != layout.Candidates.Count)
        {
            throw new InvalidDataException("Protected field set does not match the scanned strings.");
        }

        byte[] payload = layout.Envelope.Payload.ToArray();
        foreach (var candidate in layout.Candidates)
        {
            if (!anonymizedFields.TryGetValue(SlotId(candidate.Slot), out string? value) || value.Length != candidate.Length ||
                value.Any(c => c is < ' ' or > '~'))
            {
                throw new InvalidDataException("A protected binary string is missing, changed width or is not printable ASCII.");
            }
            Encoding.ASCII.GetBytes(value, payload.AsSpan(candidate.Offset, candidate.Length));
        }
        return layout.Envelope.Serialize(payload);
    }

    /// <summary>
    /// Rejects output whose framing, length, candidate layout or unselected bytes differ from the input.
    /// </summary>
    /// <param name="original">The parse result.</param>
    /// <param name="serialized">The rebuilt message.</param>
    /// <exception cref="InvalidDataException">The rebuild differs outside the candidate text spans.</exception>
    public void Validate(ParsedFeed original, ReadOnlyMemory<byte> serialized)
    {
        var layout = Require(original);
        var rebuilt = FeedPayloadReader.Read(new FeedMessage(layout.Message.Delivery, serialized));
        byte[] before = layout.Envelope.Payload;
        byte[] after = rebuilt.Payload;
        if (before.Length != after.Length || layout.Envelope.Header != rebuilt.Header || layout.Envelope.Format != rebuilt.Format ||
            serialized.Length != layout.Envelope.Original.Length)
        {
            throw new InvalidDataException("Rebuilt binary message changed its framing or length.");
        }

        var mutable = new bool[before.Length];
        foreach (var candidate in layout.Candidates)
        {
            mutable.AsSpan(candidate.Offset, candidate.Length).Fill(true);
        }
        for (int i = 0; i < before.Length; i++)
        {
            if (!mutable[i] && before[i] != after[i])
            {
                throw new InvalidDataException("Rebuilt binary message changed bytes outside the protected strings.");
            }
        }

        var rescanned = XdrTextScanner.Scan(after);
        if (rescanned.Count != layout.Candidates.Count ||
            rescanned.Zip(layout.Candidates).Any(pair => pair.First.Offset != pair.Second.Offset || pair.First.Length != pair.Second.Length))
        {
            throw new InvalidDataException("Rebuilt binary message changed its string layout.");
        }
    }

    /// <summary>
    /// Returns this codec's layout from a parse result.
    /// </summary>
    /// <param name="parsed">The parse result.</param>
    /// <returns>The retained layout.</returns>
    /// <exception cref="InvalidDataException">The parse came from another codec or route.</exception>
    private Layout Require(ParsedFeed parsed) =>
        parsed.Layout is Layout layout && layout.Message.Delivery.Feed == feed
            ? layout
            : throw new InvalidDataException("Generic XDR codec requires its own parse of the same route.");

    #endregion

    #region Nested Types

    /// <summary>
    /// Retains the input, its envelope and the scanned candidates.
    /// </summary>
    /// <param name="Message">The complete input delivery.</param>
    /// <param name="Envelope">The transport envelope and payload.</param>
    /// <param name="Candidates">The scanned candidate strings.</param>
    private sealed record Layout(FeedMessage Message, FeedEnvelope Envelope, IReadOnlyList<XdrTextCandidate> Candidates);

    #endregion
}
