using System.Buffers.Binary;
using System.Text;

namespace PresidioDemo.Feeds.ACI;

/// <summary>Writes XDR primitives, counted strings and four-byte zero padding.</summary>
public sealed class AciXdrWriter : IDisposable
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
            throw new InvalidDataException("ACI output string exceeds its ASCII or length contract.");
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
