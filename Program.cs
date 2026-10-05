using System.Text.Json;
using PresidioDemo;

const string Sample =
    "Booking PNR K7QX2M for passengers Emily Carter (PAX-1001) and Michael Brooks (PAX-1002). " +
    "Contact Emily at emily.carter@example.com or 212-555-0147. " +
    "Michael can be reached at michael.brooks@example.com. " +
    "Card on file: 4111 1111 1111 1111.";

Console.WriteLine("Loading Presidio and spaCy en_core_web_lg (takes a few seconds)...");
try
{
    PythonHost.Start();
}
catch (Exception ex)
{
    Console.WriteLine($"Startup failed: {ex.Message}");
    Environment.ExitCode = 1;
    return;
}

try
{
    while (true)
    {
        Console.WriteLine();
        Console.WriteLine("1. Analyze (built-in recognizers)");
        Console.WriteLine("2. Anonymize (replace / redact / mask / hash)");
        Console.WriteLine("3. Custom recognizers (PNR + passenger ID)");
        Console.WriteLine("4. Reversible tokenization");
        Console.WriteLine("5. Run all with sample text");
        Console.WriteLine("0. Exit");
        Console.Write("Choose: ");

        string? choice = Console.ReadLine()?.Trim();
        if (choice is null or "0") break;

        try
        {
            switch (choice)
            {
                case "1": Analyze(ReadText()); break;
                case "2": Anonymize(ReadText(), ReadOperator()); break;
                case "3": AnalyzeCustom(ReadText()); break;
                case "4": Tokenize(ReadText()); break;
                case "5":
                    Analyze(Sample);
                    foreach (var op in new[] { "replace", "redact", "mask", "hash" }) Anonymize(Sample, op);
                    AnalyzeCustom(Sample);
                    Tokenize(Sample);
                    break;
                default: Console.WriteLine("Unknown option."); break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }
}
finally
{
    PythonHost.Stop();
}

string ReadText()
{
    Console.Write("Text (Enter = sample): ");
    string? text = Console.ReadLine();
    return string.IsNullOrWhiteSpace(text) ? Sample : text;
}

string ReadOperator()
{
    Console.Write("Operator [replace|redact|mask|hash] (Enter = replace): ");
    string? op = Console.ReadLine()?.Trim().ToLowerInvariant();
    return string.IsNullOrEmpty(op) ? "replace" : op;
}

void Analyze(string text)
{
    Console.WriteLine("\n--- Analyze ---");
    PrintEntities(PythonHost.Call("analyze", text));
}

void AnalyzeCustom(string text)
{
    Console.WriteLine("\n--- Analyze with custom recognizers ---");
    PrintEntities(PythonHost.Call("analyze_custom", text));
}

void Anonymize(string text, string op)
{
    Console.WriteLine($"\n--- Anonymize ({op}) ---");
    Console.WriteLine(PythonHost.Call("anonymize", text, op));
}

void Tokenize(string text)
{
    Console.WriteLine("\n--- Reversible tokenization (PNR kept) ---");
    string tokenized = PythonHost.Call("tokenize", text);

    using var doc = JsonDocument.Parse(tokenized);
    Console.WriteLine($"Tokenized: {doc.RootElement.GetProperty("text").GetString()}");
    Console.WriteLine("Mapping:");
    foreach (var entity in doc.RootElement.GetProperty("mapping").EnumerateObject())
        foreach (var pair in entity.Value.EnumerateObject())
            Console.WriteLine($"  {pair.Value.GetString(),-20} = {pair.Name}");

    Console.WriteLine($"Restored : {PythonHost.Call("detokenize", tokenized)}");
}

void PrintEntities(string json)
{
    foreach (var e in JsonDocument.Parse(json).RootElement.EnumerateArray())
        Console.WriteLine($"{e.GetProperty("entity_type").GetString(),-16} " +
                          $"{e.GetProperty("score").GetDouble():0.00}  " +
                          $"{e.GetProperty("text").GetString()}");
}
