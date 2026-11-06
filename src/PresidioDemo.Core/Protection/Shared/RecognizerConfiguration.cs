using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using PresidioDemo.Feeds.Shared;

namespace PresidioDemo.Protection;

#region Enumerations

/// <summary>
/// How a detected value is protected.
/// </summary>
public enum DetectionAction
{
    /// <summary>
    /// Replace every character with the mask character, keeping the width (required in binary feeds).
    /// </summary>
    Mask,
    /// <summary>
    /// Replace the whole value with a fixed token (JSON feeds only).
    /// </summary>
    Replace
}

#endregion

#region Models

/// <summary>
/// A declared personal-data format: an entity found by patterns (through Presidio) or, in JSON feeds, by property key.
/// </summary>
/// <param name="Name">The unique format name used in logs and anchors.</param>
/// <param name="Entity">The entity type, such as <c>PERSON</c>.</param>
/// <param name="Score">The score Presidio assigns to a pattern match.</param>
/// <param name="Action">How a match is protected.</param>
/// <param name="MaskCharacter">The mask character for <see cref="DetectionAction.Mask"/>.</param>
/// <param name="Replacement">The token for <see cref="DetectionAction.Replace"/>.</param>
/// <param name="Patterns">The regular expressions, in the syntax shared by .NET and the Python <c>regex</c> module.</param>
/// <param name="Context">Context words that raise a pattern's score when they appear near the match.</param>
/// <param name="Keys">JSON property names whose whole value is this entity.</param>
/// <param name="Compiled">The patterns compiled for whole-value checks in .NET.</param>
public sealed record DetectionFormat(string Name, string Entity, double Score, DetectionAction Action, char MaskCharacter,
    string Replacement, IReadOnlyList<string> Patterns, IReadOnlyList<string> Context, IReadOnlyList<string> Keys,
    IReadOnlyList<Regex> Compiled)
{

    #region Methods

    /// <summary>
    /// Returns whether one pattern matches the complete value.
    /// </summary>
    /// <param name="value">The decoded string.</param>
    /// <returns>True for a whole-value match.</returns>
    public bool MatchesWhole(string value) => Compiled.Any(regex => regex.IsMatch(value));

    #endregion
}

/// <summary>
/// A declared keep format: an operational value or record that is published unchanged.
/// </summary>
/// <param name="Name">The unique keep name used in logs and anchors.</param>
/// <param name="Patterns">Whole-value regular expressions.</param>
/// <param name="Keys">JSON property names whose value is kept.</param>
/// <param name="Scan">Whether personal-data spans inside a kept value are still masked (records such as DOCS).</param>
/// <param name="Compiled">The compiled whole-value patterns.</param>
public sealed record KeepFormat(string Name, IReadOnlyList<string> Patterns, IReadOnlyList<string> Keys, bool Scan, IReadOnlyList<Regex> Compiled)
{

    #region Methods

    /// <summary>
    /// Returns whether one pattern matches the complete value.
    /// </summary>
    /// <param name="value">The decoded string.</param>
    /// <returns>True for a whole-value match.</returns>
    public bool MatchesWhole(string value) => Compiled.Any(regex => regex.IsMatch(value));

    #endregion
}

/// <summary>
/// A Presidio built-in recognizer enabled for a feed, with its protection.
/// </summary>
/// <param name="Entity">The built-in entity type, such as <c>EMAIL_ADDRESS</c> or <c>PERSON</c> (spaCy NER).</param>
/// <param name="Action">How a match is protected.</param>
/// <param name="MaskCharacter">The mask character.</param>
/// <param name="Replacement">The replacement token.</param>
public sealed record BuiltInEntity(string Entity, DetectionAction Action, char MaskCharacter, string Replacement);

/// <summary>
/// Locates a PNR that must stay unchanged (WR-017): in JSON by property key; in binary feeds as the PNR-shaped candidate
/// next to a named neighbouring format, or as the <c>pnr</c> group of a pattern inside a named keep (a PNR embedded in a
/// longer string). Never a fixed slot.
/// </summary>
/// <param name="After">The format or keep name the PNR follows, or null.</param>
/// <param name="Before">The format or keep name the PNR precedes, or null.</param>
/// <param name="Offset">How many candidates away from the first neighbour match the PNR sits.</param>
/// <param name="Optional">Whether the anchor may be absent, as with ETKT divide-event PNRs. An optional position that
/// holds a value matching no keep or format is a malformed PNR and dead-letters the message.</param>
/// <param name="Keys">JSON property names that hold a PNR.</param>
/// <param name="Within">The keep whose strings embed a PNR, or null.</param>
/// <param name="Capture">For <paramref name="Within"/>: the whole-value pattern whose <c>pnr</c> group is the PNR.</param>
public sealed record PnrAnchor(string? After, string? Before, int Offset, bool Optional, IReadOnlyList<string> Keys,
    string? Within = null, Regex? Capture = null);

/// <summary>
/// The validated recognizer configuration of one feed.
/// </summary>
/// <param name="Feed">The feed route.</param>
/// <param name="Threshold">The minimum Presidio score for a detection.</param>
/// <param name="DefaultMaskCharacter">The character used to mask unmatched strings (WR-014).</param>
/// <param name="Anchors">The PNR anchors.</param>
/// <param name="Formats">The personal-data formats.</param>
/// <param name="Keeps">The keep formats.</param>
/// <param name="BuiltIns">The enabled Presidio built-in recognizers.</param>
public sealed record FeedRecognizerSet(FeedType Feed, double Threshold, char DefaultMaskCharacter, IReadOnlyList<PnrAnchor> Anchors,
    IReadOnlyList<DetectionFormat> Formats, IReadOnlyList<KeepFormat> Keeps, IReadOnlyList<BuiltInEntity> BuiltIns)
{

    #region Fields and Constants

    /// <summary>
    /// The shape every anchored PNR must have.
    /// </summary>
    public static readonly Regex PnrShape = new("\\A[A-Z0-9]{6}\\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    #endregion

    #region Methods

    /// <summary>
    /// Returns the format or keep with this name, or null.
    /// </summary>
    /// <param name="name">The format or keep name.</param>
    /// <returns>A <see cref="DetectionFormat"/>, a <see cref="KeepFormat"/>, or null.</returns>
    public object? Named(string name) => (object?)Formats.FirstOrDefault(f => f.Name == name) ?? Keeps.FirstOrDefault(k => k.Name == name);

    /// <summary>
    /// Returns whether the named format or keep matches the complete value.
    /// </summary>
    /// <param name="name">The format or keep name.</param>
    /// <param name="value">The decoded string.</param>
    /// <returns>True for a whole-value match.</returns>
    public bool MatchesNamed(string name, string value) => Named(name) switch
    {
        DetectionFormat format => format.MatchesWhole(value),
        KeepFormat keep => keep.MatchesWhole(value),
        _ => false
    };

    /// <summary>
    /// Returns the JSON document the Python bridge uses to build this feed's Presidio registry.
    /// </summary>
    /// <returns>Pattern formats and built-in entities, without key-only formats; .NET applies the threshold.</returns>
    public string ToBridgeJson() => JsonSerializer.Serialize(new
    {
        feed = FeedCatalog.Get(Feed).FolderName,
        formats = Formats.Where(f => f.Patterns.Count > 0).Select(f => new { name = f.Name, entity = f.Entity, score = f.Score, patterns = f.Patterns, context = f.Context }),
        builtIns = BuiltIns.Select(b => b.Entity)
    });

    #endregion
}

#endregion

/// <summary>
/// Loads and validates the per-feed recognizer XML files (WR-014, WR-017, PR-005). Any invalid file stops startup.
/// </summary>
public static class RecognizerConfiguration
{

    #region Fields and Constants

    /// <summary>
    /// The Presidio built-in entities a feed may enable.
    /// </summary>
    public static readonly IReadOnlySet<string> SupportedBuiltIns = new HashSet<string>(StringComparer.Ordinal)
    {
        "PERSON", "EMAIL_ADDRESS", "PHONE_NUMBER", "IP_ADDRESS", "CREDIT_CARD", "IBAN_CODE", "URL"
    };

    /// <summary>
    /// The time limit for one .NET pattern match.
    /// </summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    #endregion

    #region Methods

    /// <summary>
    /// Returns the file name of a feed's recognizer XML, such as <c>pnr-linking.xml</c>.
    /// </summary>
    /// <param name="feed">The feed route.</param>
    /// <returns>The file name.</returns>
    public static string FileName(FeedType feed) => FeedCatalog.Get(feed).FolderName.ToLowerInvariant() + ".xml";

    /// <summary>
    /// Loads one recognizer file per supported feed from a directory.
    /// </summary>
    /// <param name="directory">The recognizer directory.</param>
    /// <returns>The validated sets by feed.</returns>
    /// <exception cref="InvalidDataException">A file is missing or invalid; the message names the file but no data value.</exception>
    public static IReadOnlyDictionary<FeedType, FeedRecognizerSet> Load(string directory)
    {
        var sets = new Dictionary<FeedType, FeedRecognizerSet>();
        foreach (var definition in FeedCatalog.All)
        {
            string path = Path.Combine(directory, FileName(definition.Type));
            if (!File.Exists(path))
            {
                throw new InvalidDataException($"Recognizer file {FileName(definition.Type)} is missing.");
            }
            sets.Add(definition.Type, LoadFile(path, definition.Type));
        }
        return sets;
    }

    /// <summary>
    /// Loads and validates one recognizer file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="feed">The feed the file must declare.</param>
    /// <returns>The validated set.</returns>
    /// <exception cref="InvalidDataException">The file is invalid.</exception>
    public static FeedRecognizerSet LoadFile(string path, FeedType feed)
    {
        string file = Path.GetFileName(path);
        try
        {
            XElement root;
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            {
                root = XDocument.Load(reader).Root ?? throw Invalid(file, "has no root element");
            }
            return Parse(root, feed, file);
        }
        catch (XmlException)
        {
            throw Invalid(file, "is not well-formed XML or contains a DTD");
        }
    }

    /// <summary>
    /// Validates a parsed root element.
    /// </summary>
    /// <param name="root">The root element.</param>
    /// <param name="feed">The expected feed.</param>
    /// <param name="file">The file name for messages.</param>
    /// <returns>The validated set.</returns>
    private static FeedRecognizerSet Parse(XElement root, FeedType feed, string file)
    {
        bool binary = feed is FeedType.ACI or FeedType.ETKT or FeedType.PNR;
        Require(root.Name.LocalName == "recognizers", file, "root must be <recognizers>");
        Attributes(root, file, "feed", "threshold", "defaultMaskCharacter");
        Require(root.Attribute("feed")?.Value == FeedCatalog.Get(feed).FolderName, file, "feed attribute must be " + FeedCatalog.Get(feed).FolderName);
        double threshold = Number(root, "threshold", 0.5, file);
        char defaultMask = MaskCharacter(root.Attribute("defaultMaskCharacter")?.Value ?? "*", file, "defaultMaskCharacter");

        var names = new HashSet<string>(StringComparer.Ordinal);
        var formats = new List<DetectionFormat>();
        var keeps = new List<KeepFormat>();
        var builtIns = new List<BuiltInEntity>();
        var anchors = new List<PnrAnchor>();
        foreach (var element in root.Elements())
        {
            switch (element.Name.LocalName)
            {
                case "format":
                    formats.Add(Format(element, binary, names, file));
                    break;
                case "keep":
                    keeps.Add(Keep(element, binary, names, file));
                    break;
                case "builtIn":
                    builtIns.Add(BuiltIn(element, binary, file));
                    break;
                case "pnrAnchor":
                    anchors.Add(Anchor(element, binary, file));
                    break;
                default:
                    throw Invalid(file, $"has an unknown element <{element.Name.LocalName}>");
            }
        }

        Require(anchors.Count > 0, file, "must declare at least one <pnrAnchor>");
        foreach (var anchor in anchors.Where(a => a.Keys.Count == 0 && a.Within is null))
        {
            string neighbour = anchor.After ?? anchor.Before!;
            Require(names.Contains(neighbour), file, $"pnrAnchor neighbour '{neighbour}' is not a declared format or keep");
        }
        foreach (var anchor in anchors.Where(a => a.Within is not null))
        {
            Require(keeps.Any(k => k.Name == anchor.Within), file, $"pnrAnchor within '{anchor.Within}' is not a declared keep");
        }
        Require(builtIns.Select(b => b.Entity).Distinct().Count() == builtIns.Count, file, "declares a builtIn entity twice");
        return new FeedRecognizerSet(feed, threshold, defaultMask, anchors, formats, keeps, builtIns);
    }

    /// <summary>
    /// Reads one &lt;format&gt;.
    /// </summary>
    private static DetectionFormat Format(XElement element, bool binary, HashSet<string> names, string file)
    {
        Attributes(element, file, "name", "entity", "score", "action", "maskCharacter", "replacement");
        string name = Name(element, names, file);
        string entity = element.Attribute("entity")?.Value ?? "";
        Require(Regex.IsMatch(entity, "\\A[A-Z][A-Z_]*\\z", RegexOptions.CultureInvariant), file, $"format '{name}' needs an upper-case entity");
        var (action, mask, replacement) = Protection(element, binary, file, name);
        Children(element, file, "pattern", "context", "key");
        var patterns = Texts(element, "pattern");
        var keys = Texts(element, "key");
        Require(patterns.Count + keys.Count > 0, file, $"format '{name}' needs a <pattern> or <key>");
        Require(!binary || keys.Count == 0, file, $"format '{name}': <key> applies to JSON feeds only");
        return new DetectionFormat(name, entity, Number(element, "score", 0.85, file), action, mask, replacement,
            patterns, Texts(element, "context"), keys, Compile(patterns, file, name));
    }

    /// <summary>
    /// Reads one &lt;keep&gt;.
    /// </summary>
    private static KeepFormat Keep(XElement element, bool binary, HashSet<string> names, string file)
    {
        Attributes(element, file, "name", "scan");
        string name = Name(element, names, file);
        Children(element, file, "pattern", "key");
        var patterns = Texts(element, "pattern");
        var keys = Texts(element, "key");
        Require(patterns.Count + keys.Count > 0, file, $"keep '{name}' needs a <pattern> or <key>");
        Require(!binary || keys.Count == 0, file, $"keep '{name}': <key> applies to JSON feeds only");
        bool scan = element.Attribute("scan")?.Value switch
        {
            null or "false" => false,
            "true" => true,
            _ => throw Invalid(file, $"keep '{name}' scan must be true or false")
        };
        return new KeepFormat(name, patterns, keys, scan, Compile(patterns, file, name));
    }

    /// <summary>
    /// Reads one &lt;builtIn&gt;.
    /// </summary>
    private static BuiltInEntity BuiltIn(XElement element, bool binary, string file)
    {
        Attributes(element, file, "entity", "action", "maskCharacter", "replacement");
        string entity = element.Attribute("entity")?.Value ?? "";
        Require(SupportedBuiltIns.Contains(entity), file, $"builtIn entity '{entity}' is not supported");
        Require(!element.HasElements, file, "builtIn takes no child elements");
        var (action, mask, replacement) = Protection(element, binary, file, entity);
        return new BuiltInEntity(entity, action, mask, replacement);
    }

    /// <summary>
    /// Reads one &lt;pnrAnchor&gt;.
    /// </summary>
    private static PnrAnchor Anchor(XElement element, bool binary, string file)
    {
        Attributes(element, file, "after", "before", "offset", "optional", "within");
        Children(element, file, "key", "pattern");
        var keys = Texts(element, "key");
        string? after = element.Attribute("after")?.Value;
        string? before = element.Attribute("before")?.Value;
        string? within = element.Attribute("within")?.Value;
        bool optional = element.Attribute("optional")?.Value switch
        {
            null or "false" => false,
            "true" => true,
            _ => throw Invalid(file, "pnrAnchor optional must be true or false")
        };
        if (binary && within is not null)
        {
            var patterns = Texts(element, "pattern");
            Require(keys.Count == 0 && after is null && before is null && element.Attribute("offset") is null && patterns.Count == 1, file,
                "an embedded pnrAnchor needs within and exactly one <pattern>");
            var capture = Compile(patterns, file, "pnrAnchor")[0];
            Require(capture.GetGroupNames().Contains("pnr"), file, "an embedded pnrAnchor pattern needs a (?<pnr>...) group");
            return new PnrAnchor(null, null, 0, optional, [], within, capture);
        }
        Require(!element.Elements("pattern").Any(), file, "<pattern> applies to an embedded pnrAnchor only");
        if (binary)
        {
            Require(keys.Count == 0 && (after is null) != (before is null), file, "a binary pnrAnchor needs exactly one of after or before");
            int offset = (int)Number(element, "offset", 1, file);
            Require(offset is >= 1 and <= 8, file, "pnrAnchor offset must be 1 to 8");
            return new PnrAnchor(after, before, offset, optional, []);
        }
        Require(keys.Count > 0 && after is null && before is null && within is null && element.Attribute("offset") is null, file, "a JSON pnrAnchor needs <key> children only");
        return new PnrAnchor(null, null, 0, optional, keys);
    }

    /// <summary>
    /// Reads and checks the protection attributes of a format or built-in.
    /// </summary>
    private static (DetectionAction Action, char Mask, string Replacement) Protection(XElement element, bool binary, string file, string name)
    {
        var action = element.Attribute("action")?.Value switch
        {
            null or "Mask" => DetectionAction.Mask,
            "Replace" => DetectionAction.Replace,
            _ => throw Invalid(file, $"'{name}' action must be Mask or Replace")
        };
        char mask = MaskCharacter(element.Attribute("maskCharacter")?.Value ?? "*", file, name);
        string replacement = element.Attribute("replacement")?.Value ?? "";
        Require(action == DetectionAction.Mask || (!binary && replacement.Length > 0), file,
            $"'{name}': Replace needs a replacement and is allowed in JSON feeds only (binary values keep their width)");
        return (action, mask, replacement);
    }

    /// <summary>
    /// Compiles patterns for .NET whole-value checks; rejects empty or invalid patterns.
    /// </summary>
    private static IReadOnlyList<Regex> Compile(IReadOnlyList<string> patterns, string file, string name)
    {
        var compiled = new List<Regex>();
        foreach (string pattern in patterns)
        {
            Require(pattern.Length > 0, file, $"'{name}' has an empty pattern");
            try
            {
                // .NET only checks complete values (keeps, neighbours, whole-value formats); spans come from Presidio.
                compiled.Add(new Regex("\\A(?:" + pattern + ")\\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, MatchTimeout));
            }
            catch (ArgumentException)
            {
                throw Invalid(file, $"'{name}' has a pattern that does not compile");
            }
        }
        return compiled.AsReadOnly();
    }

    /// <summary>
    /// Reads a unique name.
    /// </summary>
    private static string Name(XElement element, HashSet<string> names, string file)
    {
        string name = element.Attribute("name")?.Value ?? "";
        Require(Regex.IsMatch(name, "\\A[A-Za-z][A-Za-z0-9]*\\z", RegexOptions.CultureInvariant), file, "every format and keep needs a letter-and-digit name");
        Require(names.Add(name), file, $"name '{name}' is declared twice");
        return name;
    }

    /// <summary>
    /// Reads a single printable mask character.
    /// </summary>
    private static char MaskCharacter(string value, string file, string name)
    {
        Require(value.Length == 1 && value[0] is >= '!' and <= '~', file, $"'{name}' mask character must be one printable ASCII character");
        return value[0];
    }

    /// <summary>
    /// Reads an optional invariant number attribute.
    /// </summary>
    private static double Number(XElement element, string attribute, double fallback, string file)
    {
        string? text = element.Attribute(attribute)?.Value;
        if (text is null)
        {
            return fallback;
        }
        Require(double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value >= 0 &&
            (attribute == "offset" || value <= 1), file, $"attribute {attribute} is not a valid number");
        return value;
    }

    /// <summary>
    /// Returns the text of each child element with this name; patterns keep their exact text (a leading space can be
    /// part of a value), keys and context words are trimmed.
    /// </summary>
    private static IReadOnlyList<string> Texts(XElement element, string name) =>
        element.Elements(name).Select(child => name == "pattern" ? child.Value : child.Value.Trim()).ToArray();

    /// <summary>
    /// Rejects unknown attributes.
    /// </summary>
    private static void Attributes(XElement element, string file, params string[] allowed)
    {
        foreach (var attribute in element.Attributes())
        {
            Require(allowed.Contains(attribute.Name.LocalName), file, $"<{element.Name.LocalName}> has an unknown attribute {attribute.Name.LocalName}");
        }
    }

    /// <summary>
    /// Rejects unknown child elements.
    /// </summary>
    private static void Children(XElement element, string file, params string[] allowed)
    {
        foreach (var child in element.Elements())
        {
            Require(allowed.Contains(child.Name.LocalName), file, $"<{element.Name.LocalName}> has an unknown child <{child.Name.LocalName}>");
        }
    }

    /// <summary>
    /// Throws when a condition fails.
    /// </summary>
    private static void Require(bool condition, string file, string problem)
    {
        if (!condition)
        {
            throw Invalid(file, problem);
        }
    }

    /// <summary>
    /// Creates a value-free configuration error.
    /// </summary>
    private static InvalidDataException Invalid(string file, string problem) => new($"Recognizer file {file}: {problem}.");

    #endregion
}
