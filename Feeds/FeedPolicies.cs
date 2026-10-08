using System.Collections.ObjectModel;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace PresidioDemo.Feeds;

public enum FieldIdentification { None, Known, Analyze }
public enum FieldAction { Keep, Replace, Mask, Redact }

public sealed record FieldPolicy(string Name, FieldIdentification Identification, FieldAction Action,
    string Entity = "", string Replacement = "", string Entities = "", double Threshold = 0.5,
    string MaskCharacter = "*", int MaskCharacters = 0, bool FromEnd = true)
{
    public static bool IsPnr(string name) => new[] { "pnr", "linkedPnr", "oldPnr", "newPnr" }.Contains(name, StringComparer.OrdinalIgnoreCase);
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name != Name.Trim() || !Enum.IsDefined(Identification) || !Enum.IsDefined(Action))
            throw new InvalidDataException("Invalid field policy.");
        if ((Action == FieldAction.Keep) != (Identification == FieldIdentification.None))
            throw new InvalidDataException("Keep requires None identification; protection requires Known or Analyze identification.");
        if (IsPnr(Name) && Action != FieldAction.Keep)
            throw new InvalidDataException("PNR fields must be kept unchanged.");
        static bool EntityName(string name) => name.Length > 0 && name.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
        if (Identification == FieldIdentification.Known && !EntityName(Entity))
            throw new InvalidDataException("Known fields require an explicit entity type.");
        if (Identification == FieldIdentification.Analyze && (string.IsNullOrWhiteSpace(Entities) || Entities.Split(',').Any(e => !EntityName(e))))
            throw new InvalidDataException("Analyze fields require a comma-separated entity list.");
        if (!double.IsFinite(Threshold) || Threshold < 0 || Threshold > 1)
            throw new InvalidDataException("Detection threshold must be between zero and one.");
        if (Action == FieldAction.Replace && string.IsNullOrEmpty(Replacement))
            throw new InvalidDataException("Replace requires an explicit replacement value.");
        if (Action == FieldAction.Mask && (MaskCharacter.Length != 1 || MaskCharacters < 1))
            throw new InvalidDataException("Mask requires one masking character and a positive character count.");
        if (Action == FieldAction.Keep && (Entity.Length > 0 || Replacement.Length > 0 || Entities.Length > 0 || MaskCharacters != 0))
            throw new InvalidDataException("Keep cannot include identification or protection settings.");
        if (Identification == FieldIdentification.Known && Entities.Length > 0 || Identification == FieldIdentification.Analyze && Entity.Length > 0)
            throw new InvalidDataException("Use entity for Known identification, entities for Analyze.");
        if (Action != FieldAction.Replace && Replacement.Length > 0 || Action != FieldAction.Mask && MaskCharacters != 0)
            throw new InvalidDataException("Operator settings do not match the selected action.");
    }
}

public sealed class FeedPolicy
{
    public FeedType Feed { get; }
    public IReadOnlyDictionary<string, FieldPolicy> Fields { get; }
    public FeedPolicy(FeedType feed, IEnumerable<FieldPolicy> fields)
    {
        if (!Enum.IsDefined(feed)) throw new InvalidDataException("Unknown feed policy type.");
        Feed = feed;
        var rules = new Dictionary<string, FieldPolicy>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in fields)
        {
            rule.Validate();
            if (!rules.TryAdd(rule.Name, rule)) throw new InvalidDataException("Duplicate field policy.");
        }
        if (rules.Count == 0) throw new InvalidDataException("Every feed requires field policies.");
        Fields = new ReadOnlyDictionary<string, FieldPolicy>(rules);
    }
    public FieldPolicy Get(string name) => Fields.TryGetValue(name, out var rule) ? rule :
        throw new InvalidDataException("The binary codec returned a field without a configured protection policy.");
}

/// <summary>Strict local XML configuration; it does not enable XML or JSON input feeds.</summary>
public static class FeedPolicyConfiguration
{
    public static IReadOnlyDictionary<FeedType, FeedPolicy> Load(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var doc = XDocument.Load(reader);
        var root = doc.Root ?? throw new InvalidDataException("Missing field-policy root.");
        Check(root, "feedPolicies", []);
        var policies = new Dictionary<FeedType, FeedPolicy>();
        foreach (var element in root.Elements())
        {
            Check(element, "feed", ["type"]);
            string type = Required(element, "type");
            var definition = FeedCatalog.All.SingleOrDefault(f => f.FolderName.Equals(type, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("Unknown feed type in policy configuration.");
            var rules = element.Elements().Select(ParseField).ToArray();
            if (!policies.TryAdd(definition.Type, new FeedPolicy(definition.Type, rules)))
                throw new InvalidDataException("Duplicate feed policy.");
        }
        foreach (var feed in FeedCatalog.All)
            if (!policies.ContainsKey(feed.Type)) throw new InvalidDataException("A feed protection policy is missing.");
        return new ReadOnlyDictionary<FeedType, FeedPolicy>(policies);
    }
    private static FieldPolicy ParseField(XElement e)
    {
        Check(e, "field", ["name", "identification", "action", "entity", "replacement", "entities", "threshold", "maskCharacter", "maskCharacters", "fromEnd"]);
        if (e.Elements().Any()) throw new InvalidDataException("Field policies cannot contain nested elements.");
        T Choice<T>(string key) where T : struct, Enum => Enum.GetNames<T>().Contains(Required(e, key)) && Enum.TryParse<T>(Required(e, key), false, out var value)
            ? value : throw new InvalidDataException("Unknown policy identification or action.");
        string Value(string key, string fallback = "") => (string?)e.Attribute(key) ?? fallback;
        var identification = Choice<FieldIdentification>("identification");
        var action = Choice<FieldAction>("action");
        if (identification != FieldIdentification.Analyze && e.Attribute("threshold") is not null ||
            action != FieldAction.Mask && new[] { "maskCharacter", "maskCharacters", "fromEnd" }.Any(key => e.Attribute(key) is not null))
            throw new InvalidDataException("Configuration contains settings unused by the selected policy.");
        try
        {
            return new(Required(e, "name"), identification, action,
                Value("entity"), Value("replacement"), Value("entities"), double.Parse(Value("threshold", "0.5"), CultureInfo.InvariantCulture),
                Value("maskCharacter", "*"), int.Parse(Value("maskCharacters", "0"), CultureInfo.InvariantCulture), bool.Parse(Value("fromEnd", "true")));
        }
        catch (Exception ex) when (ex is FormatException or OverflowException) { throw new InvalidDataException("Invalid numeric or boolean field-policy setting."); }
    }
    private static string Required(XElement e, string key) => !string.IsNullOrWhiteSpace((string?)e.Attribute(key))
        ? e.Attribute(key)!.Value : throw new InvalidDataException("A required field-policy setting is missing.");
    private static void Check(XElement e, string name, string[] allowed)
    {
        if (e.Name != name || e.Attributes().Any(a => !allowed.Contains(a.Name.ToString())) || e.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
            throw new InvalidDataException("Unknown policy element, attribute or text.");
    }
}