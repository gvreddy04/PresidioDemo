using PresidioDemo.Feeds.Shared;

namespace PresidioDemo.Feeds.ETKT;

#region Enumerations

/// <summary>
/// Identifies the wire representation of a decoded ETKT scalar.
/// </summary>
public enum EtktWireKind
{
    /// <summary>
    /// A signed 32-bit integer encoded in big-endian wire order.
    /// </summary>
    Int32,
    /// <summary>
    /// A character represented by one 32-bit XDR word.
    /// </summary>
    Character,
    /// <summary>
    /// A counted ASCII string with XDR alignment padding.
    /// </summary>
    String
}

#endregion

#region Models

/// <summary>
/// Describes one ETKT scalar location, source name and structure group.
/// </summary>
/// <param name="Path">The structural field path within the decoded message.</param>
/// <param name="SourceName">The field name transcribed from the reviewed source declaration.</param>
/// <param name="Group">The source structure group associated with this declaration.</param>
/// <param name="Kind">The declared primitive, collection or optional-structure representation.</param>
public sealed record EtktFieldDefinition(string Path, string SourceName, string Group, EtktWireKind Kind);

/// <summary>
/// Locations describe this message's wire layout, never passenger identity.
/// </summary>
/// <param name="Definition">The reviewed declaration associated with this decoded value.</param>
/// <param name="Offset">The zero-based payload byte offset of this value or span.</param>
/// <param name="Width">The complete original scalar width in bytes.</param>
/// <param name="Number">The original decoded integer value, when the scalar is numeric.</param>
/// <param name="Text">The original decoded string value, when the scalar is textual.</param>
public sealed record EtktScalar(EtktFieldDefinition Definition, int Offset, int Width,
    int? Number = null, string? Text = null)
{

    #region Properties, Indexers

    /// <summary>
    /// Gets the decoded field&apos;s wire representation.
    /// </summary>
    /// <value>The decoded field&apos;s wire representation.</value>
    public EtktWireKind Kind => Definition.Kind;

    /// <summary>
    /// Gets the decoded scalar&apos;s structural field path.
    /// </summary>
    /// <value>The decoded scalar&apos;s structural field path.</value>
    public string Path => Definition.Path;

    /// <summary>
    /// Gets the scalar&apos;s value length excluding any counted-string length prefix.
    /// </summary>
    /// <value>The scalar&apos;s value length excluding any counted-string length prefix.</value>
    public int ValueLength => Kind == EtktWireKind.String ? Text!.Length : 4;

    /// <summary>
    /// Gets the byte offset of the scalar&apos;s value after any counted-string length prefix.
    /// </summary>
    /// <value>The byte offset of the scalar&apos;s value after any counted-string length prefix.</value>
    public int ValueOffset => Kind == EtktWireKind.String ? Offset + 4 : Offset;

    #endregion
}

/// <summary>
/// Observed type-5 framing with a body aligned to the documented history order.
/// </summary>
/// <param name="Scalars">The ordered decoded scalars and their original wire spans.</param>
/// <param name="PayloadLength">The complete original payload length in bytes.</param>
public sealed record EtktDocument(IReadOnlyList<EtktScalar> Scalars, int PayloadLength)
{

    #region Methods

    /// <summary>
    /// Returns the decoded scalar at the requested structure path.
    /// </summary>
    /// <param name="path">The exact structural field path within the decoded message.</param>
    /// <returns>The scalar at the exact requested structural path.</returns>
    public EtktScalar Field(string path) => Scalars.Single(scalar => scalar.Path == path);

    #endregion

    #region Properties, Indexers

    /// <summary>
    /// Gets the original discriminator word from the observed ETKT message framing.
    /// </summary>
    /// <value>The original discriminator word from the observed ETKT message framing.</value>
    public int SampleType => Scalars[0].Number!.Value;

    #endregion
}

/// <summary>
/// Associates a protection field with an exact substring of a decoded ETKT scalar.
/// </summary>
/// <param name="Field">The parsed protection field associated with this binding.</param>
/// <param name="Scalar">The decoded scalar containing the protection field&apos;s text span.</param>
/// <param name="Start">The starting character offset within the containing decoded string.</param>
internal sealed record EtktTextBinding(FeedField Field, EtktScalar Scalar, int Start);

/// <summary>
/// Retains ETKT input, transport framing, decoded scalars and protection bindings.
/// </summary>
/// <param name="Message">The complete delivery metadata and original or protected message bytes.</param>
/// <param name="Envelope">The original payload and transport framing.</param>
/// <param name="Document">The decoded ETKT document containing original values and wire locations.</param>
/// <param name="Bindings">The reviewed associations between protection fields and decoded text spans.</param>
internal sealed record EtktParsedLayout(FeedMessage Message, FeedEnvelope Envelope,
    EtktDocument Document, IReadOnlyList<EtktTextBinding> Bindings);

#endregion
