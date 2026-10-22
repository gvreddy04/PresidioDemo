using System.Buffers.Binary;
using System.Text;

namespace PresidioDemo.Feeds.ETKT;

/// <summary>Bounded local XDR primitives, independent of transport and field policies.</summary>
public sealed class EtktXdrReader(ReadOnlyMemory<byte> payload)
{
    public int Offset { get; private set; }
    public int Length => payload.Length;

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

    public int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));

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
}
