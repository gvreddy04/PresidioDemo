using PresidioDemo.Feeds.Shared;

namespace PresidioDemo.Feeds.ETKT;

/// <summary>
/// Complete type-5 reconstruction using the existing XML protection projection.
/// </summary>
public sealed class EtktFeedCodec : IFeedCodec
{

    #region Fields and Constants

    /// <summary>
    /// The reviewed XML reference projection used to select protection fields.
    /// </summary>
    private readonly ReferenceLayoutFeedCodec _projection;

    /// <summary>
    /// The feed-specific structural parser used to decode and bind protection fields.
    /// </summary>
    private readonly EtktParser _parser = new();

    /// <summary>
    /// The feed-specific serializer used to reconstruct and validate protected output.
    /// </summary>
    private readonly EtktSerializer _serializer = new();

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes ETKT structural processing with its reviewed XML protection projection.
    /// </summary>
    /// <param name="configuration">The reviewed feed reference configuration.</param>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    public EtktFeedCodec(ReferenceFeedLayout configuration)
    {
        if (configuration.Feed != FeedType.ETKT || configuration.Schema is null)
        {
            throw new InvalidDataException("ETKT requires its reviewed schema-driven XML configuration.");
        }
        _projection = new(configuration);
    }

    #endregion

    #region Methods

    /// <summary>
    /// Parses the complete input, validates its supported shape and retains the layout required for reconstruction.
    /// </summary>
    /// <param name="message">The complete input delivery and its retained message bytes.</param>
    /// <returns>The decoded document or parsed fields and retained reconstruction layout.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    public ParsedFeed Parse(FeedMessage message)
    {
        if (message.Delivery.Feed != FeedType.ETKT)
        {
            throw new InvalidDataException("ETKT codec and delivery route differ.");
        }
        var envelope = FeedPayloadReader.Read(message);
        var document = _parser.Parse(envelope.Payload);
        var fields = _projection.Parse(message);
        var bindings = EtktParser.Bind(document, fields, ReferenceLayoutFeedCodec.TextLocations(fields));
        return new(fields.Fields, new EtktParsedLayout(message, envelope, document, bindings));
    }

    /// <summary>
    /// Reconstructs the original message representation using the complete protected field set.
    /// </summary>
    /// <param name="original">The complete extracted field set and its retained codec-specific serialization layout.</param>
    /// <param name="anonymizedFields">The complete protected field values indexed by original instance identifier.</param>
    /// <returns>The complete reconstructed message in its original transport representation.</returns>
    public byte[] Serialize(ParsedFeed original, IReadOnlyDictionary<string, string> anonymizedFields) =>
            _serializer.Serialize((EtktParsedLayout)original.Layout, anonymizedFields);

    /// <summary>
    /// Rejects reconstructed data or settings that violate the preserved structure and protection contract.
    /// </summary>
    /// <param name="original">The complete extracted field set and its retained codec-specific serialization layout.</param>
    /// <param name="serialized">The complete reconstructed output bytes being validated.</param>
    public void Validate(ParsedFeed original, ReadOnlyMemory<byte> serialized)
    {
        var before = (EtktParsedLayout)original.Layout;
        var after = (EtktParsedLayout)Parse(before.Message with { Content = serialized }).Layout;
        _serializer.Validate(before, after);
    }

    #endregion
}
