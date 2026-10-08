using System.Text.Json;
using System.Text.Json.Nodes;

namespace PresidioDemo.Feeds;

public interface IFeedProtector { JsonObject Protect(JsonObject input); }

public sealed class PresidioFeedProtector(DemoTokenVault vault) : IFeedProtector
{
    private static readonly HashSet<string> PnrFields = new(StringComparer.OrdinalIgnoreCase)
        { "pnr", "linkedPnr", "oldPnr", "newPnr" };
    private static readonly HashSet<string> KnownSensitive = new(StringComparer.OrdinalIgnoreCase)
        { "name", "fullName", "passengerName", "firstName", "lastName", "surname", "givenName", "email", "emailAddress", "phone", "phoneNumber", "mobile", "creditCard", "cardNumber", "passport", "passportNumber", "address", "dateOfBirth", "dob", "ticketNumber" };

    public JsonObject Protect(JsonObject input)
    {
        ValidateSensitiveTypes(input, null);
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        CollectTokens(input, "", null, null, tokens);
        string result;
        try
        {
            result = PythonHost.Call("protect_feed_json", input.ToJsonString(new JsonSerializerOptions { MaxDepth = 256 }), JsonSerializer.Serialize(tokens));
        }
        catch (Exception)
        {
            // Python exceptions may contain sensitive values. Retain the raw input only in quarantine.
            throw new InvalidDataException("Local Presidio processing failed; no output was published.");
        }
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(result)
            ?? throw new InvalidDataException("Presidio returned an invalid field result.");
        var output = (JsonObject)input.DeepClone();
        var remaining = new HashSet<string>(values.Keys, StringComparer.Ordinal);
        ApplyProtectedStrings(output, "", values, remaining);
        if (remaining.Count != 0) throw new InvalidDataException("Presidio returned unexpected fields.");
        Validate(input, output, "", null, tokens);
        return output;
    }

    private static void ValidateSensitiveTypes(JsonNode? node, string? field)
    {
        if (field is not null && KnownSensitive.Contains(field) && node is not null &&
            (node is not JsonValue value || !value.TryGetValue<string>(out _)))
            throw new InvalidDataException("Known sensitive fields must be strings or null.");
        if (node is JsonObject obj)
            foreach (var property in obj) ValidateSensitiveTypes(property.Value, property.Key);
        else if (node is JsonArray array)
            foreach (var item in array) ValidateSensitiveTypes(item, field);
    }

    // Presidio returns text values only. Numbers and other non-text values never make
    // a Python JSON round trip, so high-precision numeric literals remain intact.
    private static void ApplyProtectedStrings(JsonNode node, string path, Dictionary<string, string> values, HashSet<string> remaining)
    {
        string Take(string key)
        {
            if (!remaining.Remove(key) || !values.TryGetValue(key, out var result) || result is null)
                throw new InvalidDataException("Presidio did not return every text field.");
            return result;
        }
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
            {
                string childPath = path + "/" + Escape(property.Key);
                if (property.Value is JsonValue value && value.TryGetValue<string>(out _)) obj[property.Key] = Take(childPath);
                else if (property.Value is not null) ApplyProtectedStrings(property.Value, childPath, values, remaining);
            }
        }
        else if (node is JsonArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                string childPath = path + "/" + i;
                if (array[i] is JsonValue value && value.TryGetValue<string>(out _)) array[i] = Take(childPath);
                else if (array[i] is not null) ApplyProtectedStrings(array[i]!, childPath, values, remaining);
            }
        }
    }
    private void CollectTokens(JsonNode? node, string path, string? tenant, string? pnr, Dictionary<string, string> tokens)
    {
        if (node is JsonObject obj)
        {
            tenant = ReadString(obj, "tenant") ?? tenant;
            pnr = ReadString(obj, "pnr") ?? pnr;
            foreach (var property in obj)
            {
                string childPath = path + "/" + Escape(property.Key);
                if (property.Key.Equals("sourcePassengerId", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value is not JsonValue value || !value.TryGetValue<string>(out var id) || string.IsNullOrWhiteSpace(id))
                        throw new InvalidDataException("sourcePassengerId must be a nonempty string.");
                    if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(pnr))
                        throw new InvalidDataException("Passenger tokenization requires tenant and PNR in the passenger scope.");
                    tokens.Add(childPath, vault.Resolve(tenant, pnr, id));
                }
                else CollectTokens(property.Value, childPath, tenant, pnr, tokens);
            }
        }
        else if (node is JsonArray array)
            for (int i = 0; i < array.Count; i++) CollectTokens(array[i], path + "/" + i, tenant, pnr, tokens);
    }

    private static string? ReadString(JsonObject obj, string field)
    {
        var matching = obj.Where(p => p.Key.Equals(field, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matching.Length > 1) throw new InvalidDataException("Ambiguous passenger scope fields.");
        if (matching.Length == 0) return null;
        if (matching[0].Value is not JsonValue value || !value.TryGetValue<string>(out var result))
            throw new InvalidDataException("Passenger scope fields must be strings.");
        return result;
    }

    private static string Escape(string key) => key.Replace("~", "~0").Replace("/", "~1");

    private static void Validate(JsonNode? before, JsonNode? after, string path, string? field, Dictionary<string, string> tokens)
    {
        if (before is JsonObject obj)
        {
            if (after is not JsonObject output || obj.Count != output.Count || obj.Any(p => !output.ContainsKey(p.Key)))
                throw new InvalidDataException("Protection changed the feed structure.");
            foreach (var property in obj) Validate(property.Value, output[property.Key], path + "/" + Escape(property.Key), property.Key, tokens);
        }
        else if (before is JsonArray array)
        {
            if (after is not JsonArray output || array.Count != output.Count)
                throw new InvalidDataException("Protection changed passenger grouping.");
            for (int i = 0; i < array.Count; i++) Validate(array[i], output[i], path + "/" + i, field, tokens);
        }
        else if (before is JsonValue value && value.TryGetValue<string>(out var original))
        {
            if (after is not JsonValue changed || !changed.TryGetValue<string>(out var protectedValue))
                throw new InvalidDataException("Protection changed a field type.");
            if (field is not null && PnrFields.Contains(field) && original != protectedValue)
                throw new InvalidDataException("Protection changed a PNR.");
            if (tokens.TryGetValue(path, out var token) && protectedValue != token)
                throw new InvalidDataException("Passenger token validation failed.");
            if (field is not null && KnownSensitive.Contains(field) && original.Length > 0 && original == protectedValue)
                throw new InvalidDataException("A known sensitive field was not protected.");
        }
        else if (field is not null && KnownSensitive.Contains(field) && before is not null)
            throw new InvalidDataException("Known sensitive fields must be strings or null.");
        else if (!JsonNode.DeepEquals(before, after))
            throw new InvalidDataException("Protection changed a non-text field.");
    }
}