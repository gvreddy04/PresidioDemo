using System.Buffers.Binary;
using System.Text;

namespace PresidioDemo.Feeds.ACI;

/// <summary>Bounded big-endian XDR primitives. No transport or business-field logic.</summary>
public sealed class AciXdrReader(ReadOnlyMemory<byte> payload)
{
    public int Offset { get; private set; }
    public int Length => payload.Length;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Length - Offset)
        {
            throw new InvalidDataException($"Incomplete ACI XDR field at byte {Offset}.");
        }
        var result = payload.Span.Slice(Offset, count);
        Offset += count;
        return result;
    }

    public int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));

    public int ReadPresence()
    {
        int present = ReadInt32();
        if (present is not (0 or 1))
        {
            throw new InvalidDataException($"Invalid ACI pointer presence at byte {Offset - 4}.");
        }
        return present;
    }

    public string ReadString()
    {
        int length = ReadInt32();
        if (length is < 0 or > 4096)
        {
            throw new InvalidDataException("ACI string exceeds its reviewed length limit.");
        }
        var value = Take(length);
        foreach (byte character in value)
        {
            if (character is < 32 or > 126)
            {
                throw new InvalidDataException("ACI text requires printable ASCII.");
            }
        }
        string text = Encoding.ASCII.GetString(value);
        foreach (byte padding in Take((-length) & 3))
        {
            if (padding != 0)
            {
                throw new InvalidDataException("ACI XDR padding must contain zero bytes.");
            }
        }
        return text;
    }
}
