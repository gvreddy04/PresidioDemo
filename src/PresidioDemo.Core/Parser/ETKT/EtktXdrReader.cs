using System.Buffers.Binary;
using System.Text;

namespace PresidioDemo.Feeds.ETKT;

/// <summary>
/// Bounded local XDR primitives, independent of transport and field policies.
/// </summary>
/// <param name="payload">The complete decoded payload bytes without transport metadata.</param>
public sealed class EtktXdrReader(ReadOnlyMemory<byte> payload)
{

    #region Methods

    /// <summary>
    /// Reads one signed 32-bit integer in big-endian wire order.
    /// </summary>
    /// <returns>The original signed 32-bit value.</returns>
    public int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));

    /// <summary>
    /// Reads a counted ASCII string and validates its length, character range and alignment padding.
    /// </summary>
    /// <returns>The decoded ASCII string without its length prefix or padding.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    public string ReadString()
    {
        int length = ReadInt32();
        if (length is < 0 or > 4096)
        {
            throw new InvalidDataException("ETKT string exceeds its reviewed length limit.");
        }
        var value = Take(length);
        foreach (byte character in value)
        {
            if (character is < 32 or > 126)
            {
                throw new InvalidDataException("ETKT text requires printable ASCII.");
            }
        }
        string text = Encoding.ASCII.GetString(value);
        foreach (byte padding in Take((-length) & 3))
        {
            if (padding != 0)
            {
                throw new InvalidDataException("ETKT XDR padding must contain zero bytes.");
            }
        }
        return text;
    }

    /// <summary>
    /// Consumes an exact payload slice and rejects truncated or out-of-range reads.
    /// </summary>
    /// <param name="count">The exact number of bytes or elements to consume.</param>
    /// <returns>The requested payload slice; the reader offset advances by its length.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Length - Offset)
        {
            throw new InvalidDataException($"Incomplete XDR field in ETKT at byte {Offset}.");
        }
        var result = payload.Span.Slice(Offset, count);
        Offset += count;
        return result;
    }

    #endregion

    #region Properties, Indexers

    /// <summary>
    /// Gets the complete input payload length in bytes.
    /// </summary>
    /// <value>The complete input payload length in bytes.</value>
    public int Length => payload.Length;

    /// <summary>
    /// Gets the current byte offset within the decoded payload.
    /// </summary>
    /// <value>The current byte offset within the decoded payload.</value>
    public int Offset { get; private set; }

    #endregion
}
