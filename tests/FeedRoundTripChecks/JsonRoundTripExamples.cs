using System.Text;
using System.Text.Json;

/// <summary>
/// Builds independent JSON lexical variations for exact-byte round-trip checks.
/// </summary>
internal static class JsonRoundTripExamples
{

    #region Methods

    /// <summary>
    /// Builds independent JSON lexical variations from one complete source payload.
    /// </summary>
    /// <param name="source">The original fixture or source bytes retained for comparison.</param>
    /// <returns>The complete independently generated fixture collection.</returns>
    public static IReadOnlyList<(string Name, byte[] Bytes)> Create(byte[] source)
    {
        using (var document = JsonDocument.Parse(source))
        {
            var cases = new List<(string, byte[])>
        {
            ("json-minified", JsonSerializer.SerializeToUtf8Bytes(document.RootElement)),
            ("json-indented", JsonSerializer.SerializeToUtf8Bytes(document.RootElement, new JsonSerializerOptions { WriteIndented = true })),
            ("json-surrounding-whitespace", Encoding.UTF8.GetBytes(" \t\r\n" + Encoding.UTF8.GetString(source) + "\r\n\t ")),
            ("json-all-strings-escaped", Rewrite(source, escapeAllStrings: true)),
            ("json-emily-carter", Rewrite(source, person: ("Emily", "Carter"))),
            ("json-michael-brooks", Rewrite(source, person: ("Michael", "Brooks"))),
            ("json-first-name-empty", Rewrite(source, scalarProperty: "FirstName", scalarLiteral: "\"\"")),
            ("json-first-name-null", Rewrite(source, scalarProperty: "FirstName", scalarLiteral: "null")),
            ("json-first-name-escaped", Rewrite(source, scalarProperty: "FirstName", scalarLiteral: "\"Em\\u0069ly\""))
        };
            string indented = Encoding.UTF8.GetString(cases[1].Item2).Replace("\r\n", "\n", StringComparison.Ordinal);
            cases.Add(("json-indented-crlf", Encoding.UTF8.GetBytes(indented.Replace("\n", "\r\n", StringComparison.Ordinal))));
            foreach (string number in new[] { "-0", "1.00", "1e+02", "9007199254740993", "-9223372036854775808", "1.234567890123456789" })
            {
                cases.Add(("json-number-" + number, Rewrite(source, firstNumber: number)));
            }
            using (var stream = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(stream))
                {
                    ReverseObjects(document.RootElement, writer);
                }
                cases.Add(("json-reversed-property-order", stream.ToArray()));
                return cases.AsReadOnly();
            }
        }
    }

    /// <summary>
    /// Rewrites fixture object properties in reverse order without changing their JSON values.
    /// </summary>
    /// <param name="element">The XML declaration or JSON node being processed.</param>
    /// <param name="writer">The JSON writer receiving independently generated fixture content.</param>
    private static void ReverseObjects(JsonElement element, Utf8JsonWriter writer)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().Reverse())
            {
                writer.WritePropertyName(property.Name);
                ReverseObjects(property.Value, writer);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray())
            {
                ReverseObjects(item, writer);
            }
            writer.WriteEndArray();
        }
        else
        {
            element.WriteTo(writer);
        }
    }

    // Independent lexical fixture construction. Production codecs are never used to make input examples.
    /// <summary>
    /// Builds an independent JSON fixture with the requested lexical or sample-value variation.
    /// </summary>
    /// <param name="source">The original fixture or source bytes retained for comparison.</param>
    /// <param name="escapeAllStrings">Whether every fixture property name and string uses Unicode escape spelling.</param>
    /// <param name="firstNumber">The optional raw spelling substituted for the fixture&apos;s first numeric token.</param>
    /// <param name="person">The optional fictional first and last names used to vary fixture string values.</param>
    /// <param name="scalarProperty">The property whose first string value is varied in the fixture.</param>
    /// <param name="scalarLiteral">The raw JSON literal substituted for one selected fixture scalar.</param>
    /// <returns>The complete independently rebuilt fixture bytes.</returns>
    /// <exception cref="Exception">A regression assertion fails or an expected rejection does not occur.</exception>
    private static byte[] Rewrite(byte[] source, bool escapeAllStrings = false, string? firstNumber = null,
            (string First, string Last)? person = null, string? scalarProperty = null, string? scalarLiteral = null)
    {
        var reader = new Utf8JsonReader(source);
        using (var output = new MemoryStream())
        {
            int cursor = 0;
            string property = "";
            bool numberChanged = false, scalarChanged = false;
            while (reader.Read())
            {
                string? replacement = null;
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    property = reader.GetString()!;
                }
                if (escapeAllStrings && reader.TokenType is JsonTokenType.PropertyName or JsonTokenType.String)
                {
                    replacement = "\"" + string.Concat(reader.GetString()!.Select(character => "\\u" + ((int)character).ToString("X4"))) + "\"";
                }
                else if (firstNumber is not null && !numberChanged && reader.TokenType == JsonTokenType.Number)
                {
                    replacement = firstNumber;
                    numberChanged = true;
                }
                else if (person is not null && reader.TokenType == JsonTokenType.String)
                {
                    replacement = property switch
                    {
                        "FirstName" => JsonSerializer.Serialize(person.Value.First),
                        "LastName" => JsonSerializer.Serialize(person.Value.Last),
                        "BookedAsName" => JsonSerializer.Serialize(person.Value.Last + "/" + person.Value.First),
                        _ => null
                    };
                }
                else if (scalarProperty == property && !scalarChanged && reader.TokenType == JsonTokenType.String)
                {
                    replacement = scalarLiteral;
                    scalarChanged = true;
                }
                if (replacement is not null)
                {
                    int start = checked((int)reader.TokenStartIndex);
                    output.Write(source.AsSpan(cursor, start - cursor));
                    output.Write(Encoding.UTF8.GetBytes(replacement));
                    cursor = reader.TokenType == JsonTokenType.PropertyName
                        ? start + reader.ValueSpan.Length + 2 : checked((int)reader.BytesConsumed);
                }
            }
            if (firstNumber is not null && !numberChanged || scalarProperty is not null && !scalarChanged)
            {
                throw new Exception("JSON fixture did not contain its intended token.");
            }
            output.Write(source.AsSpan(cursor));
            return output.ToArray();
        }
    }

    #endregion
}
