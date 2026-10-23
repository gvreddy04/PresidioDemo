using PresidioDemo.Feeds.Shared;

namespace PresidioDemo.Feeds.ACI;

#region Enumerations

/// <summary>
/// Identifies the primitive or optional-structure representation of an ACI field.
/// </summary>
public enum AciWireKind
{
    /// <summary>
    /// A signed 32-bit integer encoded in big-endian wire order.
    /// </summary>
    Int32,
    /// <summary>
    /// A counted ASCII string with XDR alignment padding.
    /// </summary>
    String,
    /// <summary>
    /// An optional nested structure guarded by an XDR presence word.
    /// </summary>
    Pointer
}

#endregion

#region Models

/// <summary>
/// Describes one field in the declaration order of an ACI structure.
/// </summary>
/// <param name="Name">The declared field name or reviewed layout identifier.</param>
/// <param name="Kind">The declared primitive, collection or optional-structure representation.</param>
/// <param name="Target">The declared nested structure type for an optional ACI member.</param>
public sealed record AciFieldDefinition(string Name, AciWireKind Kind, string Target = "");

/// <summary>
/// Describes an ACI structure and whether its layout is sample-observed only.
/// </summary>
/// <param name="Name">The declared field name or reviewed layout identifier.</param>
/// <param name="Fields">The ordered decoded fields or reviewed field definitions.</param>
/// <param name="ObservedOnly">Whether this structure is supported by observed samples rather than a confirmed source declaration.</param>
public sealed record AciStructureDefinition(string Name,
    IReadOnlyList<AciFieldDefinition> Fields, bool ObservedOnly = false);

/// <summary>
/// Offsets identify wire locations, never passenger identity.
/// </summary>
/// <param name="Path">The structural field path within the decoded message.</param>
/// <param name="Kind">The declared primitive, collection or optional-structure representation.</param>
/// <param name="Offset">The zero-based payload byte offset of this value or span.</param>
/// <param name="Width">The complete original scalar width in bytes.</param>
/// <param name="Number">The original decoded integer value, when the scalar is numeric.</param>
/// <param name="Text">The original decoded string value, when the scalar is textual.</param>
public sealed record AciScalar(string Path, AciWireKind Kind, int Offset, int Width,
    int? Number = null, string? Text = null)
{

    #region Properties, Indexers

    /// <summary>
    /// Gets the scalar&apos;s value length excluding any counted-string length prefix.
    /// </summary>
    /// <value>The scalar&apos;s value length excluding any counted-string length prefix.</value>
    public int ValueLength => Kind == AciWireKind.String ? Text!.Length : 4;

    /// <summary>
    /// Gets the byte offset of the scalar&apos;s value after any counted-string length prefix.
    /// </summary>
    /// <value>The byte offset of the scalar&apos;s value after any counted-string length prefix.</value>
    public int ValueOffset => Kind == AciWireKind.String ? Offset + 4 : Offset;

    #endregion
}

/// <summary>
/// Associates an ACI field declaration with its scalar or nested structure value.
/// </summary>
/// <param name="Definition">The reviewed declaration associated with this decoded value.</param>
/// <param name="Scalar">The decoded scalar containing the protection field&apos;s text span.</param>
/// <param name="Presence">The original XDR optional-structure presence value.</param>
/// <param name="Child">The optional nested ACI structure value.</param>
public sealed record AciMember(AciFieldDefinition Definition, AciScalar? Scalar = null,
    AciScalar? Presence = null, AciNode? Child = null);

/// <summary>
/// Represents one decoded ACI structure and its ordered members.
/// </summary>
/// <param name="Type">The source-declared ACI structure name.</param>
/// <param name="Path">The structural field path within the decoded message.</param>
/// <param name="Members">The ordered member definitions or decoded values within this structure.</param>
public sealed record AciNode(string Type, string Path, IReadOnlyList<AciMember> Members)
{

    #region Methods

    /// <summary>
    /// Returns the nested structure associated with the requested member name.
    /// </summary>
    /// <param name="name">The declared member name, configuration setting or fixture filename.</param>
    /// <returns>The requested nested structure, or null when its optional value is absent.</returns>
    public AciNode? Child(string name) => Members.Single(member => member.Definition.Name == name).Child;

    /// <summary>
    /// Returns the scalar member with the requested declared field name.
    /// </summary>
    /// <param name="name">The declared member name, configuration setting or fixture filename.</param>
    /// <returns>The requested decoded scalar; a missing scalar is rejected.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    public AciScalar Scalar(string name) => Members.Single(member => member.Definition.Name == name).Scalar
            ?? throw new InvalidDataException("ACI member is not a scalar.");

    #endregion
}

/// <summary>
/// Contains the decoded ACI record discriminator and top-level structure lists.
/// </summary>
/// <param name="Utype">The original ACI record discriminator.</param>
/// <param name="FlightList">The decoded ACI flight-list head, when present.</param>
/// <param name="PassengerList">The decoded ACI passenger-list head, when present.</param>
/// <param name="ErrorText">The decoded error-record text, when the ACI record represents an error.</param>
/// <param name="Scalars">The ordered decoded scalars and their original wire spans.</param>
/// <param name="PayloadLength">The complete original payload length in bytes.</param>
public sealed record AciDocument(int Utype, AciNode? FlightList, AciNode? PassengerList,
    string? ErrorText, IReadOnlyList<AciScalar> Scalars, int PayloadLength);

/// <summary>
/// Associates a protection field with an exact substring of a decoded ACI scalar.
/// </summary>
/// <param name="Field">The parsed protection field associated with this binding.</param>
/// <param name="Scalar">The decoded scalar containing the protection field&apos;s text span.</param>
/// <param name="Start">The starting character offset within the containing decoded string.</param>
internal sealed record AciTextBinding(FeedField Field, AciScalar Scalar, int Start);

/// <summary>
/// Retains ACI input, transport framing, decoded structures and protection bindings.
/// </summary>
/// <param name="Message">The complete delivery metadata and original or protected message bytes.</param>
/// <param name="Envelope">The original payload and transport framing.</param>
/// <param name="Document">The decoded ACI document containing original values and wire locations.</param>
/// <param name="Bindings">The reviewed associations between protection fields and decoded text spans.</param>
internal sealed record AciParsedLayout(FeedMessage Message, FeedEnvelope Envelope,
    AciDocument Document, IReadOnlyList<AciTextBinding> Bindings);

#endregion
