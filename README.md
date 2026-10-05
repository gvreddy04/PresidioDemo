# Presidio Demo (.NET 10 + Python.NET)

A small console app that runs [Microsoft Presidio](https://presidio.dataprivacystack.org/) **locally**
from C#. Python is embedded in the .NET process with [Python.NET](https://pythonnet.github.io/),
so there is no HTTP service and no network call during PII processing.

The menu has four demos, all using fictional airline-style sample text:

| Option | What it shows |
|---|---|
| 1. Analyze | Built-in recognizers find names, emails, phone numbers and card numbers |
| 2. Anonymize | The same entities transformed with `replace`, `redact`, `mask` or `hash` |
| 3. Custom recognizers | Two `PatternRecognizer`s added for `PNR_LOCATOR` and `PASSENGER_ID` |
| 4. Reversible tokenization | A custom operator swaps values for tokens such as `<PERSON_0>`, keeps the PNR unchanged, then restores the original from the mapping |
| 5. Run all | Runs every demo on the sample text |

For options 1 to 4 you can press Enter to use the sample text or type your own.

## How it fits together

```
Program.cs (menu)  ->  PythonHost.cs (Python.NET)  ->  python/presidio_demo.py  ->  Presidio + spaCy
     C#                  embeds CPython 3.12              plain Python functions      inside .venv
```

C# passes strings in and gets strings (JSON) back, so no Python objects leak into the C# code.

## Prerequisites

- **Windows 10/11 x64 only.** `PythonHost.cs` expects the Windows Python layout
  (`python312.dll`, `Lib\site-packages`), so macOS and Linux won't work as-is.
- **.NET 10 SDK**
- **Python 3.12 x64** from python.org or winget (not the Microsoft Store version).
  It can sit next to other Python versions and doesn't need to be on `PATH`.
  This project is tested with 3.12; other versions are untested.
- **About 1.5 GB of free disk space.** The finished `.venv` is about 700 MB.
- **Internet access to NuGet, PyPI and GitHub** for the first setup. The spaCy model
  (~400 MB) downloads from GitHub, which is the step most likely to be blocked by a company proxy
  or firewall. After setup, the app runs fully offline.

### Install the tools

**Using the terminal:**

```powershell
winget install --id Microsoft.DotNet.SDK.10
winget install --id Python.Python.3.12 --scope user
```

**Or manually:**

1. **.NET 10 SDK:** download the x64 SDK installer from
   https://dotnet.microsoft.com/download/dotnet/10.0 and run it. If you install
   Visual Studio 2026 with the **.NET desktop development** workload, the SDK comes with it.
2. **Python 3.12:** open https://www.python.org/downloads/release/python-31210/, download
   **Windows installer (64-bit)** and run it. Leave **Use admin privileges when installing py.exe**
   as it is. You don't need **Add python.exe to PATH**. Click **Install Now**.

### Check the tools

**Using the terminal** (open a new terminal after installing):

```powershell
dotnet --list-sdks     # should list a 10.0.x SDK
py -0p                 # should list -V:3.12 and its path
```

**Or manually:** open Windows **Settings > Apps > Installed apps** and look for
**Microsoft .NET SDK 10.0.x (x64)** and **Python 3.12.x (64-bit)**.

## Quick start

There are two one-time setup actions after cloning (create `.venv` and install the packages),
then you run the app. `.venv` is not in git, so every teammate does the setup once.

**Using the terminal:** open PowerShell in the project folder (the folder that contains
`PresidioDemo.csproj`), then run:

```powershell
py -3.12 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
dotnet run
```

After the first time, `dotnet run` is enough.

**Or manually, with VS Code and Visual Studio:**

1. **Create `.venv` in VS Code:** choose **File > Open Folder** and open the project folder.
   Press `Ctrl+Shift+P`, run **Python: Create Environment**, choose **Venv**, then choose
   **Python 3.12**. When it asks which dependencies to install, tick **requirements.txt** and click
   **OK**. VS Code creates `.venv` in the project folder and installs the packages. If it doesn't
   ask about dependencies, open **Terminal > New Terminal** and run the pip line above.
2. **Run in Visual Studio:** double-click `PresidioDemo.csproj` to open it in Visual Studio 2026,
   then press `Ctrl+F5` (**Debug > Start Without Debugging**). The menu opens in a console window.

Things to know:

- Create the venv with `py -3.12 -m venv` or VS Code, **not** `uv venv`. A uv venv has no pip,
  so the install line fails.
- The terminal commands call the venv's `python.exe` directly, so you don't need to activate the
  venv (activation can be blocked by the PowerShell execution policy).
- The first install takes several minutes, mostly for the spaCy model.
- Startup takes a few seconds while spaCy loads the model. Then choose `5` to see every demo.
- The app finds `.venv` by searching upward from its build output folder, so it works the same from
  the terminal, Visual Studio or VS Code.

---

## Build it from scratch, step by step

Use this section to understand how the project was made, or to recreate it in a new folder.
It assumes the prerequisites above are installed.

Each step that creates or edits a file names it in a **File:** line above the code. Paths are
relative to the project folder, the folder that contains `PresidioDemo.csproj`.
Steps with terminal commands also show a manual way to do the same thing.

To create files in Visual Studio instead of a text editor: in **Solution Explorer**, right-click the
project and choose **Add > New Folder** or **Add > New Item…**, then type the exact file name.

### Step 1 – Create the console project and add Python.NET

**Using the terminal:** pick any folder, for example:

```powershell
mkdir PresidioDemo
cd PresidioDemo
dotnet new console -n PresidioDemo -o . --framework net10.0
dotnet add package pythonnet --version 3.2.0
```

**Or manually, in Visual Studio 2026:**

1. Choose **File > New > Project**, select **Console App** (the C# one), and click **Next**.
2. Set **Project name** to `PresidioDemo`, choose a **Location**, and tick
   **Place solution and project in the same directory**. This keeps `.venv`, `python/` and
   `PresidioDemo.csproj` in one folder, which is where the app looks for them. Click **Next**.
3. Set **Framework** to **.NET 10.0**. Leave **Do not use top-level statements** unticked,
   because `Program.cs` in step 7 uses top-level statements. Click **Create**.
4. In **Solution Explorer**, right-click the project and choose **Manage NuGet Packages…**.
   On the **Browse** tab, search for `pythonnet`, select version **3.2.0**, and click **Install**.

`pythonnet` is the NuGet package that provides the `Python.Runtime` namespace.

### Step 2 – Create `requirements.txt`

Pinning exact versions means everyone gets the same Presidio, spaCy and model that this demo was
tested with. The last line installs the spaCy model straight from its GitHub release, so no separate
`spacy download` step is needed.

**File:** `requirements.txt` (new file)

```text
presidio-analyzer==2.2.364
presidio-anonymizer==2.2.364
spacy==3.8.16
en_core_web_lg @ https://github.com/explosion/spacy-models/releases/download/en_core_web_lg-3.8.0/en_core_web_lg-3.8.0-py3-none-any.whl#sha256=293e9547a655b25499198ab15a525b05b9407a75f10255e405e8c3854329ab63
```

### Step 3 – Keep `.venv` out of the .NET build and out of git

`.venv` contains thousands of files, so tell MSBuild to skip it by adding the
`DefaultItemExcludes` line. The rest of the file was generated in step 1. Do this before
creating `.venv` in step 4, otherwise Visual Studio's Solution Explorer tries to load every file in it.

In Visual Studio, double-click the project name in **Solution Explorer** to open the `.csproj` for editing.

**File:** `PresidioDemo.csproj` (edit the existing file)

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <DefaultItemExcludes>$(DefaultItemExcludes);.venv/**</DefaultItemExcludes>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="pythonnet" Version="3.2.0" />
  </ItemGroup>

</Project>
```

Then tell git to ignore the venv and build output. The file name starts with a dot and has no
extension.

**File:** `.gitignore` (new file)

```text
.venv/
bin/
obj/
```

### Step 4 – Create the `.venv` virtual environment and install the packages

The folder must be named exactly `.venv` and sit in the project folder.

**Using the terminal:**

```powershell
py -3.12 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
```

**Or manually, in VS Code:** choose **File > Open Folder** and open the project folder. Press
`Ctrl+Shift+P`, run **Python: Create Environment**, choose **Venv**, then **Python 3.12**, and tick
**requirements.txt** when asked which dependencies to install. If it doesn't ask, open
**Terminal > New Terminal** and run the pip line above.

Optional quick check that Presidio works on its own (terminal only):

```powershell
.\.venv\Scripts\python.exe -c "from presidio_analyzer import AnalyzerEngine; print(AnalyzerEngine().analyze(text='Call Emily Carter at 212-555-0147', language='en'))"
```

### Step 5 – Add the Python module

Create a folder named `python` and add the file below. It holds all the Presidio logic, written
much like the [Presidio samples](https://presidio.dataprivacystack.org/samples/). The engines are
created once when C# imports the module. The file name matters: C# imports it as `presidio_demo`.

**File:** `python/presidio_demo.py` (new folder and file)

```python
"""Presidio demo functions called from .NET through Python.NET.
Inputs and outputs are plain strings (JSON) to keep the C# side simple."""

import json
import re

from presidio_analyzer import AnalyzerEngine, Pattern, PatternRecognizer
from presidio_anonymizer import AnonymizerEngine
from presidio_anonymizer.entities import OperatorConfig
from presidio_anonymizer.operators import Operator, OperatorType

# Engines are created once and reused (loading en_core_web_lg is slow).
analyzer = AnalyzerEngine()  # default NLP engine: spaCy en_core_web_lg
anonymizer = AnonymizerEngine()

# Custom airline recognizers, registered on a second analyzer so option 1 stays "built-in only".
custom_analyzer = AnalyzerEngine()
custom_analyzer.registry.add_recognizer(PatternRecognizer(
    supported_entity="PNR_LOCATOR",
    patterns=[Pattern("pnr", r"\b[A-Z][A-Z0-9]{5}\b", 0.4)],
    context=["pnr", "booking", "reservation", "locator"],
    global_regex_flags=re.MULTILINE,  # default includes IGNORECASE, which would match "Carter"
))
custom_analyzer.registry.add_recognizer(PatternRecognizer(
    supported_entity="PASSENGER_ID",
    patterns=[Pattern("pax_id", r"\bPAX-\d{4}\b", 0.9)],
))


def _entities(text, results):
    return json.dumps([
        {"entity_type": r.entity_type, "score": round(r.score, 2), "text": text[r.start:r.end]}
        for r in sorted(results, key=lambda r: r.start)
    ])


def analyze(text):
    return _entities(text, analyzer.analyze(text=text, language="en"))


def analyze_custom(text):
    return _entities(text, custom_analyzer.analyze(text=text, language="en"))


OPERATORS = {
    "replace": OperatorConfig("replace"),
    "redact": OperatorConfig("redact"),
    "mask": OperatorConfig("mask", {"masking_char": "*", "chars_to_mask": 100, "from_end": False}),
    "hash": OperatorConfig("hash", {"hash_type": "sha256"}),
}


def anonymize(text, mode):
    results = analyzer.analyze(text=text, language="en")
    result = anonymizer.anonymize(text=text, analyzer_results=results,
                                  operators={"DEFAULT": OPERATORS[mode]})
    return result.text


class TokenOperator(Operator):
    """Custom operator: replaces each distinct value with <ENTITY_TYPE_n> and records it
    in a mapping (adapted from the Presidio 'pseudonymization' sample)."""

    def operate(self, text, params=None):
        per_type = params["mapping"].setdefault(params["entity_type"], {})
        if text not in per_type:
            per_type[text] = f"<{params['entity_type']}_{len(per_type)}>"
        return per_type[text]

    def validate(self, params=None):
        pass

    def operator_name(self):
        return "token"

    def operator_type(self):
        return OperatorType.Anonymize


anonymizer.add_anonymizer(TokenOperator)


def tokenize(text):
    """Returns {"text": tokenized text, "mapping": {entity: {original: token}}}.
    The PNR is detected but kept visible and unchanged."""
    mapping = {}
    results = custom_analyzer.analyze(text=text, language="en")
    result = anonymizer.anonymize(text=text, analyzer_results=results, operators={
        "DEFAULT": OperatorConfig("token", {"mapping": mapping}),
        "PNR_LOCATOR": OperatorConfig("keep"),
    })
    return json.dumps({"text": result.text, "mapping": mapping})


def detokenize(tokenized_json):
    """Restores originals from the mapping. Demo only; a real system uses a token vault."""
    data = json.loads(tokenized_json)
    text = data["text"]
    for per_type in data["mapping"].values():
        for original, token in per_type.items():
            text = text.replace(token, original)
    return text
```

### Step 6 – Add the Python.NET bridge

This class embeds CPython. Key Python.NET points:

- `Runtime.PythonDLL` must point to the base install's `python312.dll`. A venv has no DLL of its
  own, so the path is read from `.venv\pyvenv.cfg` (`home = ...`).
- `PythonEngine.PythonPath` replaces the module search path, so it must list the standard library
  (`Lib`, `DLLs`), the venv's `site-packages`, and our `python` folder.
- Call `PythonEngine.Initialize()` once, then `BeginAllowThreads()`. Wrap every Python call
  in `using (Py.GIL())`.

**File:** `PythonHost.cs` (new file)

```csharp
using Python.Runtime;

namespace PresidioDemo;

/// <summary>Starts embedded CPython using the project's .venv and calls python/presidio_demo.py.</summary>
public static class PythonHost
{
    private static PyObject? _module;

    public static void Start()
    {
        string projectDir = FindProjectDir();
        string venvDir = Path.Combine(projectDir, ".venv");

        // pyvenv.cfg records the base Python install ("home") and its version.
        var cfg = File.ReadAllLines(Path.Combine(venvDir, "pyvenv.cfg"))
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim());

        string pythonHome = cfg["home"];
        string[] version = (cfg.GetValueOrDefault("version") ?? cfg["version_info"]).Split('.'); // uv writes version_info

        string pythonDll = Path.Combine(pythonHome, $"python{version[0]}{version[1]}.dll");
        if (!File.Exists(pythonDll))
            throw new FileNotFoundException(
                $"{pythonDll} not found. Was Python 3.12 uninstalled? Recreate .venv (README: Quick start).");

        Runtime.PythonDLL = pythonDll;
        PythonEngine.PythonHome = pythonHome;
        PythonEngine.PythonPath = string.Join(Path.PathSeparator,
            Path.Combine(pythonHome, "Lib"),
            Path.Combine(pythonHome, "DLLs"),
            Path.Combine(venvDir, "Lib", "site-packages"),
            Path.Combine(projectDir, "python"));

        PythonEngine.Initialize();
        PythonEngine.BeginAllowThreads();

        using (Py.GIL())
        {
            _module = Py.Import("presidio_demo");
        }
    }

    public static string Call(string function, params string[] args)
    {
        using (Py.GIL())
        {
            PyObject[] pyArgs = args.Select(a => new PyString(a)).ToArray<PyObject>();
            using PyObject result = _module!.InvokeMethod(function, pyArgs);
            return result.As<string>();
        }
    }

    public static void Stop()
    {
        try { PythonEngine.Shutdown(); } catch { }
    }

    // Walk up from bin/Debug/net10.0 until the folder that contains .venv.
    private static string FindProjectDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, ".venv")))
                return dir.FullName;

        throw new DirectoryNotFoundException(
            "No .venv folder found in the project folder. Create it first (README: Quick start).");
    }
}
```

### Step 7 – Add the console menu

Replace everything in the `Program.cs` that step 1 created.

**File:** `Program.cs` (replace the existing file)

```csharp
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
```

### Step 8 – Build and run

**Using the terminal:**

```powershell
dotnet build
dotnet run
```

**Or manually, in Visual Studio:** choose **Build > Build Solution** (`Ctrl+Shift+B`), then
**Debug > Start Without Debugging** (`Ctrl+F5`). Use `F5` instead if you want to hit breakpoints in
the C# code. Breakpoints in the Python file are not hit, because Python runs inside the .NET process.

The final folder looks like this:

```
PresidioDemo/
├── .venv/                  Python 3.12 virtual environment (not in git)
├── python/
│   └── presidio_demo.py    Presidio logic
├── PythonHost.cs           Python.NET bridge
├── Program.cs              Console menu
├── PresidioDemo.csproj
├── requirements.txt        Pinned Python packages and spaCy model
├── .gitignore              Ignores .venv, bin and obj
└── README.md
```

---

## Sample output (option 4)

```
Tokenized: Booking PNR K7QX2M for passengers <PERSON_2> (<PASSENGER_ID_1>) and <PERSON_1> (<PASSENGER_ID_0>). ...
Mapping:
  <PERSON_2>           = Emily Carter
  <PASSENGER_ID_1>     = PAX-1001
  ...
Restored : Booking PNR K7QX2M for passengers Emily Carter (PAX-1001) and Michael Brooks (PAX-1002). ...
```

Token numbers can look reversed because Presidio replaces entities from the end of the text
backwards.

## Things worth knowing for the demo

- **Detection is model-based.** For example, "Emily" on its own after "Contact" is not detected
  in the sample, while "Michael" is. This is how spaCy behaves, not a bug in the app.
- **URL hits inside emails** (`emily.car`, `example.com`) appear in the analyze list. The anonymizer
  resolves the overlap in favour of the higher-scoring `EMAIL_ADDRESS`, so the output is clean.
- **The token mapping is in memory only.** It shows the concept; a real system would store
  mappings in a secured token database.
- **Piping input from PowerShell** (`"5" | dotnet run`) can add an invisible byte-order mark that
  makes the first menu choice show "Unknown option". Typing interactively works normally.

## Troubleshooting

| Problem | Fix |
|---|---|
| `Startup failed: No .venv folder found` | Run the Quick start's first two lines in the project folder. |
| `Startup failed: ...python312.dll not found` | Python 3.12 was moved or uninstalled. Reinstall it, delete `.venv` and recreate it. |
| `Startup failed: No module named 'presidio_analyzer'` | Packages went to the wrong Python. Run `.\.venv\Scripts\python.exe -m pip install -r requirements.txt`. |
| `No module named pip` | The venv was made with `uv venv`. Delete `.venv` and recreate it with `py -3.12 -m venv .venv`. |
| `py` is not recognized | Open a new terminal after installing Python, or reinstall Python 3.12 with the "py launcher" option. |
| Download of `en_core_web_lg` times out or is blocked | Your network blocks GitHub downloads. Ask IT for access to `github.com`, or set `HTTPS_PROXY` before running pip. |
| Install fails building spaCy or numpy wheels | You are on a Python version without prebuilt wheels. Use 3.12. |

## References

- Presidio samples: https://presidio.dataprivacystack.org/samples/
- Presidio custom recognizers: https://presidio.dataprivacystack.org/analyzer/adding_recognizers/
- Presidio anonymizer operators: https://presidio.dataprivacystack.org/anonymizer/adding_operators/
- Python.NET: https://pythonnet.github.io/
