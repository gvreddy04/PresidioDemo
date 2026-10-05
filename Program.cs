using System.Text;
using System.Text.Json;
using PresidioDemo;

const string SampleText =
    "Booking PNR K7QX2M for passengers Emily Carter (PAX-1001) and Michael Brooks (PAX-1002). " +
    "Contact Emily at emily.carter@example.com or 212-555-0147. " +
    "Michael can be reached at michael.brooks@example.com. " +
    "Card on file: 4111 1111 1111 1111.";

string[] operators = ["replace", "redact", "mask", "hash"];

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

string inputDir = Path.Combine(PythonHost.ProjectDir, "docs", "input");
string outputDir = Path.Combine(PythonHost.ProjectDir, "docs", "output");

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
        Console.WriteLine("6. Anonymize with mask (default)");
        Console.WriteLine("0. Exit");
        Console.Write("Choose (Enter = 6): ");

        string? choice = Console.ReadLine()?.Trim();
        if (choice is null or "0") break;
        if (choice.Length == 0) choice = "6";

        try
        {
            switch (choice)
            {
                case "1": Analyze(ReadInput()); break;
                case "2": Anonymize(ReadInput(), ReadOperator()); break;
                case "3": AnalyzeCustom(ReadInput()); break;
                case "4": Tokenize(ReadInput()); break;
                case "5":
                    var sample = new Input(SampleText, "text", null);
                    Analyze(sample);
                    foreach (var op in operators) Anonymize(sample, op);
                    AnalyzeCustom(sample);
                    Tokenize(sample);
                    break;
                case "6": Anonymize(ReadInput(), "mask"); break;
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

string Ask(string prompt)
{
    Console.Write(prompt);
    return Console.ReadLine()?.Trim() ?? "";
}

Input ReadInput()
{
    Console.WriteLine("\nInput source:");
    Console.WriteLine("  1. User input");
    Console.WriteLine("  2. Local file (docs/input)");
    return Ask("Choose (Enter = 1): ") == "2" ? ReadLocalFile() : ReadUserInput();
}

Input ReadUserInput()
{
    Console.WriteLine("\nInput format:");
    Console.WriteLine("  1. Text");
    Console.WriteLine("  2. JSON");
    Console.WriteLine("  3. XML");
    string format = Ask("Choose (Enter = 1): ") switch { "2" => "json", "3" => "xml", _ => "text" };

    Console.WriteLine($"Type or paste the {format.ToUpperInvariant()} input, then press Enter on an empty line.");
    Console.WriteLine("Press Enter straight away to use the sample.");
    var lines = new StringBuilder();
    string? line;
    while (!string.IsNullOrEmpty(line = Console.ReadLine()))
        lines.AppendLine(line);

    string content = lines.ToString().Trim();
    if (content.Length == 0)
        content = format == "text"
            ? SampleText
            : File.ReadAllText(Path.Combine(inputDir, $"booking-sample.{format}"));

    return new Input(content, format, null);
}

Input ReadLocalFile()
{
    Directory.CreateDirectory(inputDir);
    Console.WriteLine($"\nPlace your file in: {inputDir}");
    Ask("Press Enter when the file is there...");

    Console.WriteLine("File format:");
    Console.WriteLine("  1. JSON");
    Console.WriteLine("  2. XML");
    string format = Ask("Choose (Enter = 1): ") == "2" ? "xml" : "json";

    string[] files = Directory.GetFiles(inputDir, $"*.{format}").Order().ToArray();
    if (files.Length == 0)
        throw new FileNotFoundException($"No .{format} files found in {inputDir}");

    for (int i = 0; i < files.Length; i++)
        Console.WriteLine($"  {i + 1}. {Path.GetFileName(files[i])}");

    string pick = Ask("Choose a file (Enter = 1): ");
    int index = 0;
    if (pick.Length > 0 && !(int.TryParse(pick, out index) && index >= 1 && index <= files.Length))
        throw new ArgumentException($"Invalid file number: {pick}");

    string path = files[Math.Max(index - 1, 0)];
    return new Input(File.ReadAllText(path), format, path);
}

string ReadOperator()
{
    string op = Ask("Operator [replace|redact|mask|hash] (Enter = replace): ").ToLowerInvariant();
    if (op.Length == 0) return "replace";
    return operators.Contains(op) ? op : throw new ArgumentException($"Unknown operator: {op}");
}

void Analyze(Input input)
{
    Begin("Analyze", input);
    string findings = PythonHost.Call("analyze", input.Content, input.Format);
    PrintEntities(findings);
    Save(input, "analyze", findings, "json");
    End();
}

void AnalyzeCustom(Input input)
{
    Begin("Analyze with custom recognizers", input);
    string findings = PythonHost.Call("analyze_custom", input.Content, input.Format);
    PrintEntities(findings);
    Save(input, "custom-analyze", findings, "json");
    End();
}

void Anonymize(Input input, string op)
{
    Begin($"Anonymize ({op})", input);
    string result = PythonHost.Call("anonymize", input.Content, input.Format, op);
    Console.WriteLine(result);
    Save(input, $"anonymize-{op}", result);
    End();
}

void Tokenize(Input input)
{
    Begin("Reversible tokenization (PNR kept)", input);
    string tokenized = PythonHost.Call("tokenize", input.Content, input.Format);

    using var doc = JsonDocument.Parse(tokenized);
    string? protectedText = doc.RootElement.GetProperty("text").GetString();
    Console.WriteLine(Rule("Tokenized", '-'));
    Console.WriteLine(protectedText);
    Console.WriteLine(Rule("Mapping", '-'));
    foreach (var entity in doc.RootElement.GetProperty("mapping").EnumerateObject())
        foreach (var pair in entity.Value.EnumerateObject())
            Console.WriteLine($"  {pair.Value.GetString(),-20} = {pair.Name}");

    Console.WriteLine(Rule("Restored", '-'));
    Console.WriteLine(PythonHost.Call("detokenize", tokenized, input.Format));
    Save(input, "tokenized", protectedText ?? "");
    End();
}

void PrintEntities(string json)
{
    foreach (var e in JsonDocument.Parse(json).RootElement.EnumerateArray())
    {
        string field = e.GetProperty("field").GetString() is { Length: > 0 } f ? f : "-";
        Console.WriteLine($"{field,-14} {e.GetProperty("entity_type").GetString(),-16} " +
                          $"{e.GetProperty("score").GetDouble():0.00}  " +
                          $"{e.GetProperty("text").GetString()}");
    }
}

// Prints the input between separator lines, then opens the output section.
void Begin(string title, Input input)
{
    string source = input.FilePath is null ? "" : $", {Path.GetFileName(input.FilePath)}";
    Console.WriteLine();
    Console.WriteLine(Rule($"INPUT [{input.Format}{source}]", '='));
    Console.WriteLine(input.Content.TrimEnd());
    Console.WriteLine(Rule($"OUTPUT: {title}", '='));
}

void End() => Console.WriteLine(new string('=', 72));

// "===== LABEL =====...", padded to 72 characters.
string Rule(string label, char c) => $"{new string(c, 5)} {label} ".PadRight(72, c);

// Results are saved only for local-file input, as docs/output/<file>.<demo>.<ext>.
void Save(Input input, string suffix, string content, string? extension = null)
{
    if (input.FilePath is null) return;
    Directory.CreateDirectory(outputDir);
    string name = $"{Path.GetFileNameWithoutExtension(input.FilePath)}.{suffix}.{extension ?? input.Format}";
    File.WriteAllText(Path.Combine(outputDir, name), content);
    Console.WriteLine($"Saved: docs/output/{name}");
}

record Input(string Content, string Format, string? FilePath);
