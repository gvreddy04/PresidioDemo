using System.Buffers.Binary;
using System.Text;

namespace PresidioDemo.Feeds.Shared;

#region Models

/// <summary>
/// One counted XDR string found without a schema: the text span inside the payload.
/// </summary>
/// <param name="Slot">The zero-based order of this candidate among all candidates in the payload.</param>
/// <param name="Offset">The payload byte offset of the first text byte (after the length word).</param>
/// <param name="Length">The text length in bytes, excluding the length word and padding.</param>
/// <param name="Value">The decoded ASCII text.</param>
public sealed record XdrTextCandidate(int Slot, int Offset, int Length, string Value);

/// <summary>
/// What a span after a plausible length word holds.
/// </summary>
internal enum SpanKind
{
    /// <summary>
    /// Not a counted string: integers, framing or padding.
    /// </summary>
    RawWords,
    /// <summary>
    /// A counted string of printable ASCII.
    /// </summary>
    Text,
    /// <summary>
    /// A counted string in another encoding or with control characters.
    /// </summary>
    UnsupportedText
}

#endregion

/// <summary>
/// Finds counted strings in a big-endian XDR payload without a per-feed schema (WR-015).
/// </summary>
public static class XdrTextScanner
{

    #region Fields and Constants

    /// <summary>
    /// The longest text accepted as one candidate; longer length words are treated as raw words.
    /// </summary>
    public const int MaximumLength = 65535;

    /// <summary>
    /// The shortest text accepted as a candidate. A 1-3 byte "string" cannot be told apart from two raw words
    /// (for example <c>00000001 31000000</c>), so shorter spans stay raw words and are never masked (WR-015).
    /// </summary>
    public const int MinimumLength = 4;

    #endregion

    #region Methods

    /// <summary>
    /// Scans the payload word by word and returns every counted string of printable ASCII with zero padding.
    /// </summary>
    /// <param name="payload">The complete XDR payload; its length must be a multiple of four.</param>
    /// <returns>The candidates in payload order.</returns>
    /// <exception cref="InvalidDataException">The payload is not word aligned, or it holds text that is not printable
    /// ASCII (WR-016: such text cannot be detected, so the message fails closed instead of publishing it).</exception>
    public static IReadOnlyList<XdrTextCandidate> Scan(ReadOnlySpan<byte> payload)
    {
        if (payload.Length % 4 != 0)
        {
            throw new InvalidDataException($"XDR payload has {payload.Length} bytes, not a multiple of four; no output was published.");
        }

        var candidates = new List<XdrTextCandidate>();
        int offset = 0;
        while (offset + 4 <= payload.Length)
        {
            uint declared = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset, 4));
            int next = offset + 4;
            var kind = declared is >= MinimumLength and <= MaximumLength ? Classify(payload, offset, (int)declared, out next) : SpanKind.RawWords;
            if (kind == SpanKind.UnsupportedText)
            {
                throw new InvalidDataException("A counted string holds non-ASCII or control characters (for example Latin-1 or EBCDIC text) that cannot be checked for personal data; input retained and no output was published.");
            }
            if (kind == SpanKind.Text)
            {
                candidates.Add(new XdrTextCandidate(candidates.Count, offset + 4, (int)declared,
                    Encoding.ASCII.GetString(payload.Slice(offset + 4, (int)declared))));
                offset = next;
                continue;
            }
            offset += 4;
        }
        return candidates.AsReadOnly();
    }

    /// <summary>
    /// Returns the padded width of a counted string's text.
    /// </summary>
    /// <param name="length">The text length.</param>
    /// <returns>The length rounded up to a multiple of four.</returns>
    public static int Padded(int length) => (length + 3) & ~3;

    /// <summary>
    /// Classifies a span that starts with a plausible length word.
    /// </summary>
    /// <param name="payload">The complete payload.</param>
    /// <param name="offset">The offset of the length word.</param>
    /// <param name="length">The declared text length.</param>
    /// <param name="next">The offset after the padding.</param>
    /// <returns><see cref="SpanKind.Text"/> for printable ASCII with zero padding; <see cref="SpanKind.UnsupportedText"/>
    /// for a zero-padded span without NUL bytes that holds a byte outside printable ASCII and is mostly letters, digits or
    /// spaces (text in another encoding); otherwise <see cref="SpanKind.RawWords"/>, because integers almost always contain
    /// NUL bytes or few letter-like bytes.</returns>
    private static SpanKind Classify(ReadOnlySpan<byte> payload, int offset, int length, out int next)
    {
        next = offset + 4 + Padded(length);
        if (next > payload.Length)
        {
            return SpanKind.RawWords;
        }

        foreach (byte value in payload.Slice(offset + 4 + length, Padded(length) - length))
        {
            if (value != 0)
            {
                return SpanKind.RawWords;
            }
        }

        var text = payload.Slice(offset + 4, length);
        bool printable = true;
        int textLike = 0;
        foreach (byte value in text)
        {
            if (value == 0)
            {
                return SpanKind.RawWords;
            }
            printable &= value is >= 0x20 and <= 0x7E;
            // ASCII letters, digits and space, or 0xC0-0xFF (Latin-1 letters, EBCDIC letters and digits).
            textLike += value is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)' ' or >= 0xC0 ? 1 : 0;
        }
        if (printable)
        {
            return SpanKind.Text;
        }
        // Text in another encoding is mostly letters; integer or timestamp bytes are not, so they stay raw words.
        return textLike * 2 >= length ? SpanKind.UnsupportedText : SpanKind.RawWords;
    }

    #endregion
}
