using System.Buffers.Binary;
using System.Text;

namespace PresidioDemo.Feeds.ETKT;

/// <summary>Writes counted ASCII strings and big-endian scalar slots.</summary>
public sealed class EtktXdrWriter : IDisposable
{
    private readonly MemoryStream _output = new();

    public void WriteInt32(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        _output.Write(bytes);
    }

    public void WriteString(string value)
    {
        if (value.Length > 4096 || value.Any(character => character is < ' ' or > '~'))
        {
            throw new InvalidDataException("ETKT output string exceeds its ASCII or length contract.");
        }
        WriteInt32(value.Length);
        _output.Write(Encoding.ASCII.GetBytes(value));
        for (int index = 0; index < ((-value.Length) & 3); index++)
        {
            _output.WriteByte(0);
        }
    }

    public byte[] ToArray() => _output.ToArray();
    public void Dispose() => _output.Dispose();
}
