using System.Collections.ObjectModel;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using PresidioDemo.Feeds.Shared;

namespace PresidioDemo.Protection;

#region Enumerations

/// <summary>
/// Specifies whether a field is kept, treated as known sensitive data or analyzed locally.
/// </summary>
public enum FieldIdentification
{
    /// <summary>
    /// No identification; the field value is kept unchanged.
    /// </summary>
    None,
    /// <summary>
    /// The configured field is known to contain sensitive data and does not require NLP detection.
    /// </summary>
    Known,
    /// <summary>
    /// The field text is analyzed locally for the configured entity types.
    /// </summary>
    Analyze
}

/// <summary>
/// Specifies the configured transformation applied to a protection field.
/// </summary>
public enum FieldAction
{
    /// <summary>
    /// Preserves the original field value.
    /// </summary>
    Keep,
    /// <summary>
    /// Replaces the complete identified value with the configured placeholder.
    /// </summary>
    Replace,
    /// <summary>
    /// Masks the configured character span while preserving the remaining value.
    /// </summary>
    Mask,
    /// <summary>
    /// Removes the identified text without creating a recovery mapping.
    /// </summary>
    Redact
}

#endregion

#region Models

/// <summary>
/// Defines field protection; MaskAll resolves the full width of each input value.
/// </summary>
/// <param name="Name">The declared field name or reviewed layout identifier.</param>
/// <param name="Identification">The configured sensitive-data identification mode.</param>
/// <param name="Action">The configured field transformation.</param>
/// <param name="Entity">The configured entity type for known sensitive data.</param>
/// <param name="Replacement">The configured placeholder used by a replace operation.</param>
/// <param name="Entities">The comma-separated entity types selected for local analysis.</param>
/// <param name="Threshold">The minimum local-analysis detection score accepted by the policy.</param>
/// <param name="MaskCharacter">The single character used to mask identified text.</param>
/// <param name="MaskCharacters">The fixed number of identified characters to mask when full masking is disabled.</param>
/// <param name="FromEnd">Whether masking begins at the end of the identified text.</param>
/// <param name="MaskAll">Whether masking covers the complete identified field value.</param>
public sealed record FieldPolicy(string Name, FieldIdentification Identification, FieldAction Action,
    string Entity = "", string Replacement = "", string Entities = "", double Threshold = 0.5,
    string MaskCharacter = "*", int MaskCharacters = 0, bool FromEnd = true, bool MaskAll = false)
{

    #region Methods

    /// <summary>
    /// Accepts an already masked value without allowing unprotected Known fields through.
    /// </summary>
    /// <param name="value">The scalar value to inspect, format, encode or protect.</param>
    /// <returns>True when every character in the configured mask span is already masked; otherwise, false.</returns>
    public bool IsAlreadyMasked(string value)
    {
        if (Action != FieldAction.Mask)
        {
            return false;
        }

        int count = Math.Min(value.Length, MaskCount(value));
        int start = FromEnd ? value.Length - count : 0;
        return value.AsSpan(start, count).IndexOfAnyExcept(MaskCharacter[0]) < 0;
    }

    /// <summary>
    /// Determines whether the final field-path segment is a preserved PNR alias.
    /// </summary>
    /// <param name="name">The declared member name, configuration setting or fixture filename.</param>
    /// <returns>True when the final path segment matches a preserved PNR alias; otherwise, false.</returns>
    public static bool IsPnr(string name) => new[] { "pnr", "linkedPnr", "oldPnr", "newPnr", "UARecloc", "UaRecordLocator", "PNROwner" }.Contains(name.Split('/')[^1], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves maskCharacters="all" without fixing a sample-specific width.
    /// </summary>
    /// <param name="value">The scalar value to inspect, format, encode or protect.</param>
    /// <returns>The complete value length for full masking, or the configured fixed mask width.</returns>
    public int MaskCount(string value) => MaskAll ? value.Length : MaskCharacters;

    /// <summary>
    /// Validates identification and operator settings and enforces immutable PNR field rules.
    /// </summary>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name != Name.Trim() || !Enum.IsDefined(Identification) || !Enum.IsDefined(Action))
        {
            throw new InvalidDataException("Invalid field policy.");
        }

        if ((Action == FieldAction.Keep) != (Identification == FieldIdentification.None))
        {
            throw new InvalidDataException("Keep requires None identification; protection requires Known or Analyze identification.");
        }

        if (IsPnr(Name) && Action != FieldAction.Keep)
        {
            throw new InvalidDataException("PNR fields must be kept unchanged.");
        }

        static bool EntityName(string name) => name.Length > 0 && name.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
        if (Identification == FieldIdentification.Known && !EntityName(Entity))
        {
            throw new InvalidDataException("Known fields require an explicit entity type.");
        }

        if (Identification == FieldIdentification.Analyze && (string.IsNullOrWhiteSpace(Entities) || Entities.Split(',').Any(e => !EntityName(e))))
        {
            throw new InvalidDataException("Analyze fields require a comma-separated entity list.");
        }

        if (!double.IsFinite(Threshold) || Threshold < 0 || Threshold > 1)
        {
            throw new InvalidDataException("Detection threshold must be between zero and one.");
        }

        if (Action == FieldAction.Replace && string.IsNullOrEmpty(Replacement))
        {
            throw new InvalidDataException("Replace requires an explicit replacement value.");
        }

        bool invalidMaskCount = MaskAll ? MaskCharacters != 0 : MaskCharacters < 1;
        if (Action == FieldAction.Mask && (MaskCharacter.Length != 1 || invalidMaskCount))
        {
            throw new InvalidDataException("Mask requires one masking character and a positive character count or maskCharacters=all.");
        }
        if (Action == FieldAction.Keep && (Entity.Length > 0 || Replacement.Length > 0 || Entities.Length > 0 || MaskCharacters != 0))
        {
            throw new InvalidDataException("Keep cannot include identification or protection settings.");
        }

        if (Identification == FieldIdentification.Known && Entities.Length > 0 || Identification == FieldIdentification.Analyze && Entity.Length > 0)
        {
            throw new InvalidDataException("Use entity for Known identification, entities for Analyze.");
        }

        if (Action != FieldAction.Replace && Replacement.Length > 0 || Action != FieldAction.Mask && (MaskCharacters != 0 || MaskAll))
        {
            throw new InvalidDataException("Operator settings do not match the selected action.");
        }
    }

    #endregion
}

#endregion

#region Classes

/// <summary>
/// Provides validated protection rules for one feed route.
/// </summary>
public sealed class FeedPolicy
{

    #region Constructors

    /// <summary>
    /// Validates and stores the reviewed field policies for one feed route.
    /// </summary>
    /// <param name="feed">The supported feed route.</param>
    /// <param name="fields">The complete ordered parsed field set or reviewed field definitions.</param>
    /// <param name="configured">Whether the feed has reviewed processing rules.</param>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    public FeedPolicy(FeedType feed, IEnumerable<FieldPolicy> fields, bool configured = true)
    {
        if (!Enum.IsDefined(feed))
        {
            throw new InvalidDataException("Unknown feed policy type.");
        }
        Feed = feed;
        IsConfigured = configured;
        var rules = new Dictionary<string, FieldPolicy>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in fields)
        {
            rule.Validate();
            if (!rules.TryAdd(rule.Name, rule))
            {
                throw new InvalidDataException("Duplicate field policy.");
            }
        }
        if (configured && rules.Count == 0)
        {
            throw new InvalidDataException("Configured feeds require field policies.");
        }
        if (!configured && rules.Count != 0)
        {
            throw new InvalidDataException("Unconfigured feeds cannot declare field policies.");
        }
        Fields = new ReadOnlyDictionary<string, FieldPolicy>(rules);
    }

    #endregion

    #region Methods

    /// <summary>
    /// Rejects processing when this feed has no reviewed protection policy.
    /// </summary>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    public void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidDataException("The feed protection policy is not configured; input retained and no output was published.");
        }
    }

    /// <summary>
    /// Returns the exact configured field rule and rejects unreviewed field paths.
    /// </summary>
    /// <param name="name">The declared member name, configuration setting or fixture filename.</param>
    /// <returns>The exact reviewed rule for the requested field path.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    public FieldPolicy Get(string name)
    {
        EnsureConfigured();
        return Fields.TryGetValue(name, out var rule) && (!name.StartsWith('/') || rule.Name == name) ? rule :
            throw new InvalidDataException("The feed codec returned a field without a configured protection policy.");
    }

    #endregion

    #region Properties, Indexers

    /// <summary>
    /// Gets the feed route handled by this definition, policy or workflow.
    /// </summary>
    /// <value>The feed route handled by this definition, policy or workflow.</value>
    public FeedType Feed { get; }

    /// <summary>
    /// Gets the reviewed or parsed fields associated with this feed.
    /// </summary>
    /// <value>The reviewed or parsed fields associated with this feed.</value>
    public IReadOnlyDictionary<string, FieldPolicy> Fields { get; }

    /// <summary>
    /// Gets a value indicating whether this feed has reviewed protection rules.
    /// </summary>
    /// <value>A value indicating whether this feed has reviewed protection rules.</value>
    public bool IsConfigured { get; }

    #endregion
}

/// <summary>
/// Loads reusable protection operators independently of feed layouts and field paths.
/// </summary>
public static class FeedPolicyConfiguration
{

    #region Methods

    /// <summary>
    /// Loads and validates per-feed rules and reusable protection operators from XML.
    /// </summary>
    /// <param name="path">The structural path, source file or workspace location being processed.</param>
    /// <returns>The complete validated configuration indexed by feed route, or its verification bundle.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    public static IReadOnlyDictionary<FeedType, FeedPolicy> Load(string path)
    {
        using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
        {
            var doc = XDocument.Load(reader);
            var root = doc.Root ?? throw new InvalidDataException("Missing field-policy root.");
            Check(root, "feedPolicies", []);
            var policies = new Dictionary<FeedType, FeedPolicy>();
            var catalog = new Dictionary<string, FieldPolicy>(StringComparer.Ordinal);
            var catalogs = root.Elements("catalog").ToArray();
            if (catalogs.Length > 1)
            {
                throw new InvalidDataException("Duplicate shared policy catalog.");
            }

            foreach (var shared in catalogs)
            {
                Check(shared, "catalog", []);
                foreach (var item in shared.Elements())
                {
                    var rule = ParseField(item, shared: true);
                    rule.Validate();
                    if (!rule.Name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') || !catalog.TryAdd(rule.Name, rule))
                    {
                        throw new InvalidDataException("Invalid or duplicate shared policy ID.");
                    }
                }
            }
            foreach (var element in root.Elements().Where(item => item.Name != "catalog"))
            {
                Check(element, "feed", ["type", "configured"]);
                string type = Required(element, "type");
                var definition = FeedCatalog.All.SingleOrDefault(f => f.FolderName.Equals(type, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("Unknown feed type in policy configuration.");
                bool configured = (string?)element.Attribute("configured") switch
                {
                    null or "true" => true,
                    "false" => false,
                    _ => throw new InvalidDataException("Feed policy configured must be true or false.")
                };
                var rules = element.Elements().Select(field =>
                {
                    if (field.Attribute("policy") is not { } reference)
                    {
                        return ParseField(field);
                    }

                    Check(field, "field", ["name", "policy"]);
                    if (field.Elements().Any() || !catalog.TryGetValue(reference.Value, out var shared))
                    {
                        throw new InvalidDataException("Unknown shared field policy.");
                    }

                    return shared with { Name = Required(field, "name") };
                }).ToArray();
                if (!policies.TryAdd(definition.Type, new FeedPolicy(definition.Type, rules, configured)))
                {
                    throw new InvalidDataException("Duplicate feed policy.");
                }
            }
            foreach (var feed in FeedCatalog.All)
            {
                if (!policies.ContainsKey(feed.Type))
                {
                    throw new InvalidDataException("A feed protection policy is missing.");
                }
            }

            return new ReadOnlyDictionary<FeedType, FeedPolicy>(policies);
        }
    }

    /// <summary>
    /// Rejects unexpected XML element names, attributes or text content.
    /// </summary>
    /// <param name="e">The XML element whose settings are being inspected.</param>
    /// <param name="name">The expected XML element or attribute name.</param>
    /// <param name="allowed">The permitted XML attribute names or fixture values.</param>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    private static void Check(XElement e, string name, string[] allowed)
    {
        if (e.Name != name || e.Attributes().Any(a => !allowed.Contains(a.Name.ToString())) || e.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
        {
            throw new InvalidDataException("Unknown policy element, attribute or text.");
        }
    }

    /// <summary>
    /// Resolves and validates one configured field rule or shared operator definition.
    /// </summary>
    /// <param name="e">The XML element whose settings are being inspected.</param>
    /// <returns>The validated field rule or reusable protection-operator definition.</returns>
    private static FieldPolicy ParseField(XElement e) => ParseField(e, shared: false);

    /// <summary>
    /// Resolves and validates one configured field rule or shared operator definition.
    /// </summary>
    /// <param name="e">The XML element whose settings are being inspected.</param>
    /// <param name="shared">Whether this XML element defines a reusable operator rather than a feed field.</param>
    /// <returns>The validated field rule or reusable protection-operator definition.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    private static FieldPolicy ParseField(XElement e, bool shared)
    {
        Check(e, shared ? "policy" : "field", [shared ? "id" : "name", "identification", "action", "entity", "replacement", "entities", "threshold", "maskCharacter", "maskCharacters", "fromEnd"]);
        if (e.Elements().Any())
        {
            throw new InvalidDataException("Field policies cannot contain nested elements.");
        }

        T Choice<T>(string key) where T : struct, Enum => Enum.GetNames<T>().Contains(Required(e, key)) && Enum.TryParse<T>(Required(e, key), false, out var value)
                    ? value : throw new InvalidDataException("Unknown policy identification or action.");
        string Value(string key, string fallback = "") => (string?)e.Attribute(key) ?? fallback;
        var identification = Choice<FieldIdentification>("identification");
        var action = Choice<FieldAction>("action");
        if (identification != FieldIdentification.Analyze && e.Attribute("threshold") is not null ||
            action != FieldAction.Mask && new[] { "maskCharacter", "maskCharacters", "fromEnd" }.Any(key => e.Attribute(key) is not null))
        {
            throw new InvalidDataException("Configuration contains settings unused by the selected policy.");
        }

        try
        {
            string maskCountText = Value("maskCharacters", "0");
            bool maskAll = maskCountText == "all";
            int maskCharacters = maskAll ? 0 : int.Parse(maskCountText, CultureInfo.InvariantCulture);

            return new(Required(e, shared ? "id" : "name"), identification, action,
                Value("entity"), Value("replacement"), Value("entities"), double.Parse(Value("threshold", "0.5"), CultureInfo.InvariantCulture),
                Value("maskCharacter", "*"), maskCharacters, bool.Parse(Value("fromEnd", "true")), maskAll);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException) { throw new InvalidDataException("Invalid numeric or boolean field-policy setting."); }
    }

    /// <summary>
    /// Reads a nonempty mandatory XML attribute and rejects a missing setting.
    /// </summary>
    /// <param name="e">The XML element whose settings are being inspected.</param>
    /// <param name="key">The configured attribute or dictionary key to inspect.</param>
    /// <returns>The validated nonempty configuration value.</returns>
    /// <exception cref="InvalidDataException">The supplied message, field set or configuration violates the reviewed processing contract.</exception>
    private static string Required(XElement e, string key) => !string.IsNullOrWhiteSpace((string?)e.Attribute(key))
            ? e.Attribute(key)!.Value : throw new InvalidDataException("A required field-policy setting is missing.");

    #endregion
}

#endregion
