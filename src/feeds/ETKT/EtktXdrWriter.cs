using System.Buffers.Binary;
using System.Text;

namespace PresidioDemo.Feeds.ETKT;

#region Classes

/// <summary>
/// Writes counted ASCII strings and big-endian scalar slots.
/// </summary>
public sealed class EtktXdrWriter : IDisposable
{

    #region Fields and Constants

    /// <summary>
    /// The owned buffer receiving encoded XDR payload bytes.
    /// </summary>
    private readonly MemoryStream _output = new();

    #endregion

    #region Methods

    /// <summary>
    /// Releases the owned streams, file watchers, synchronization objects or workspace lock.
    /// </summary>
    public void Dispose() => _output.Dispose();

    /// <summary>
    /// Returns the complete encoded bytes written to the owned payload buffer.
    /// </summary>
    /// <returns>A byte array containing all bytes written to the owned buffer.</returns>
    public byte[] ToArray() => _output.ToArray();

    /// <summary>
    /// Writes one signed 32-bit integer in big-endian wire order.
    /// </summary>
    /// <param name="value">The signed 32-bit value to write in big-endian order.</param>
    public void WriteInt32(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        _output.Write(bytes);
    }

    /// <summary>
    /// Writes a counted ASCII string with its byte-length prefix and zero alignment padding.
    /// </summary>
    /// <param name="value">The ASCII string to write with its counted length and zero padding.</param>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
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

    #endregion
}

#endregion
