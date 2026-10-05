# Presidio Demo (.NET 10 + Python.NET)

A small console app that runs [Microsoft Presidio](https://presidio.dataprivacystack.org/) **locally**
from C#. Python is embedded in the .NET process with [Python.NET](https://pythonnet.github.io/),
so there is no HTTP service and no network call during PII processing.

The menu has four demos:

| Option | What it shows |
|---|---|
| 1. Analyze | Built-in recognizers find names, emails, phone numbers and card numbers |
| 2. Anonymize | The same entities transformed with `replace`, `redact`, `mask` or `hash` |
| 3. Custom recognizers | Two `PatternRecognizer`s added for `PNR_LOCATOR` and `PASSENGER_ID` |
| 4. Reversible tokenization | A custom operator swaps values for tokens such as `<PERSON_0>`, keeps the PNR unchanged, then restores the original from the mapping |
| 5. Run all | Runs every demo on the built-in sample text |

When the app starts, its first question is whether to use the **default**: anonymize the first
JSON file in `docs/input` with `mask`. Press Enter (or `y`) to run it, or `n` to open the menu above.

Options 1 to 4 can read **user input** (text, JSON or XML typed or pasted into the console) or a
**local file** (JSON or XML placed in `docs/input`). See [Using the app](#using-the-app).
All sample data is fictional.

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
- Startup takes a few seconds while spaCy loads the model. Then answer `n` and choose `5` to see
  every demo.
- The app finds `.venv` by searching upward from its build output folder, so it works the same from
  the terminal, Visual Studio or VS Code.

## Using the app

Start the app (`dotnet run`, or `Ctrl+F5` in Visual Studio). The first question is always:

```
Use default: Anonymize the first JSON file in docs/input with mask? [Y/n]:
```

- **Enter or `y`:** runs the default with no further questions. It takes the first `.json` file in
  `docs/input` in alphabetical order (`booking-sample.json` unless you add others), masks it, prints
  the result and saves it to `docs/output`.
- **`n`:** shows the full menu:

```
1. Analyze (built-in recognizers)
2. Anonymize (replace / redact / mask / hash)
3. Custom recognizers (PNR + passenger ID)
4. Reversible tokenization
5. Run all with sample text
0. Exit
Choose:
```

After each run the app asks the default question again. To exit, answer `n`, then `0`.

For options **1** to **4**, the app then asks where the input comes from:

```
Input source:
  1. User input
  2. Local file (docs/input)
```

**1. User input:** choose the format (**1** Text, **2** JSON, **3** XML), then type or paste the
input and press **Enter on an empty line** to finish. Press Enter straight away to use the sample
for that format.

**2. Local file:** put your file in `docs/input` first. The app shows the folder's full path, asks
for the format (**1** JSON, **2** XML), then lists the matching files by number (Enter = 1).
Two samples are already there:

| File | Contents |
|---|---|
| `docs/input/booking-sample.json` | PNR `R4TZ8N` with passengers Sarah Mitchell and David Turner |
| `docs/input/booking-sample.xml` | PNR `M2VK9D` with passengers Jessica Hayes and Ryan Cooper |

For option **2** (Anonymize), the app finally asks for the operator: `replace`, `redact`, `mask` or
`hash` (Enter = `replace`).

**How results are shown:** every demo prints the input and the output in separate blocks, so it's
easy to compare them:

```
===== INPUT [json, booking-sample.json] ================================
...the input...
===== OUTPUT: Anonymize (mask) =========================================
...the result...
========================================================================
```

The label after `INPUT` shows the format and, for local files, the file name. Option 4 splits its
output further into `----- Tokenized`, `----- Mapping` and `----- Restored` sections.

**Where results go:** every result is printed in the console. Results from a local file are also
saved to `docs/output` as `<file name>.<demo>.<extension>`:

| Demo | Saved file for `booking-sample.json` |
|---|---|
| 1. Analyze | `booking-sample.analyze.json` (list of findings) |
| Default / 2. Anonymize | `booking-sample.anonymize-mask.json` (the operator is in the name) |
| 3. Custom recognizers | `booking-sample.custom-analyze.json` (list of findings) |
| 4. Reversible tokenization | `booking-sample.tokenized.json` |

**JSON and XML are processed value by value.** Presidio checks each JSON string value and each
XML element text and attribute separately, and the field name (for example `email` or `pnr`) is
passed to Presidio as a context hint. Keys, tags and nesting are never changed, so the output is
still valid JSON or XML. In option 4, one token mapping covers the whole document, so the same
name gets the same token everywhere in it.

### Example 1 – Analyze text you type

Keys: `n` → `1` (Analyze) → `1` (User input) → `1` (Text) → type the text → Enter on an empty line.

```
===== INPUT [text] =====================================================
Please call Kevin Price at 646-555-0110 or email kevin.price@example.com
===== OUTPUT: Analyze ==================================================
-              PERSON           0.85  Kevin Price
-              PHONE_NUMBER     0.40  646-555-0110
-              EMAIL_ADDRESS    1.00  kevin.price@example.com
-              URL              0.50  kevin.pr
-              URL              0.50  example.com
========================================================================
```

The first column is the field name; it shows `-` for plain text.

### Example 2 – The default: mask the first JSON file

Keys: Enter.

```
Using the first JSON file in docs/input: booking-sample.json

===== INPUT [json, booking-sample.json] ================================
{
  "pnr": "R4TZ8N",
  ...
}
===== OUTPUT: Anonymize (mask) =========================================
{
  "pnr": "R4TZ8N",
  "messageType": "booking",
  "passengers": [
    {
      "passengerId": "PAX-3001",
      "name": "**************",
      "email": "**************************",
      "phone": "************"
    },
    ...
  ],
  "remarks": "************** requests an aisle seat. Call ************ if the flight changes.",
  "payment": {
    "cardNumber": "*******************"
  }
}
Saved: docs/output/booking-sample.anonymize-mask.json
========================================================================
```

### Example 3 – Mask JSON you paste

Keys: `n` → `2` (Anonymize) → `1` (User input) → `2` (JSON) → paste → Enter on an empty line → `mask`.

```
===== INPUT [json] =====================================================
{"pnr": "Q8WN3B",
 "passenger": {"passengerId": "PAX-5001", "name": "Laura Bennett", "email": "laura.bennett@example.com"}}
===== OUTPUT: Anonymize (mask) =========================================
{
  "pnr": "******",
  "passenger": {
    "passengerId": "PAX-5001",
    "name": "*************",
    "email": "*************************"
  }
}
========================================================================
```

The built-in model mistakes the PNR for a person's name and masks it, and it doesn't know
passenger IDs. Options 3 and 4 add the custom recognizers that fix both.

### Example 4 – Custom recognizers on the JSON sample file

Keys: `n` → `3` (Custom recognizers) → `2` (Local file) → `1` (JSON) → `1` (`booking-sample.json`).

```
===== INPUT [json, booking-sample.json] ================================
{
  "pnr": "R4TZ8N",
  "messageType": "booking",
  ...
}
===== OUTPUT: Analyze with custom recognizers ==========================
pnr            PNR_LOCATOR      0.75  R4TZ8N
passengerId    PASSENGER_ID     0.90  PAX-3001
name           PERSON           0.85  Sarah Mitchell
email          EMAIL_ADDRESS    1.00  sarah.mitchell@example.com
phone          PHONE_NUMBER     0.75  312-555-0182
...
cardNumber     CREDIT_CARD      1.00  4111 1111 1111 1111
Saved: docs/output/booking-sample.custom-analyze.json
========================================================================
```

### Example 5 – Tokenize the XML sample file

Keys: `n` → `4` (Reversible tokenization) → `2` (Local file) → `2` (XML) → `1` (`booking-sample.xml`).

```
===== INPUT [xml, booking-sample.xml] ==================================
<?xml version="1.0" encoding="utf-8"?>
<booking pnr="M2VK9D" messageType="booking">
  ...
  <remarks>Ryan Cooper requests a window seat. Contact Jessica Hayes at 415-555-0123.</remarks>
</booking>
===== OUTPUT: Reversible tokenization (PNR kept) =======================
----- Tokenized --------------------------------------------------------
<booking pnr="M2VK9D" messageType="booking">
  <passenger id="&lt;PASSENGER_ID_0&gt;">
    <name>&lt;PERSON_0&gt;</name>
    <email>&lt;EMAIL_ADDRESS_0&gt;</email>
    <phone>&lt;PHONE_NUMBER_0&gt;</phone>
  </passenger>
  ...
  <remarks>&lt;PERSON_1&gt; requests a window seat. Contact &lt;PERSON_0&gt; at &lt;PHONE_NUMBER_0&gt;.</remarks>
</booking>
----- Mapping ----------------------------------------------------------
  <PASSENGER_ID_0>     = PAX-4001
  <PASSENGER_ID_1>     = PAX-4002
  <PERSON_0>           = Jessica Hayes
  <PERSON_1>           = Ryan Cooper
  ...
----- Restored ---------------------------------------------------------
<booking pnr="M2VK9D" messageType="booking">
  <passenger id="PAX-4001">
    <name>Jessica Hayes</name>
  ...
Saved: docs/output/booking-sample.tokenized.xml
========================================================================
```

The PNR stays unchanged, and Jessica Hayes is `<PERSON_0>` both in her `<name>` element and in the
remarks. In XML, `<` and `>` inside a value are written as `&lt;` and `&gt;`; that is what keeps
the output valid XML.

### Use your own file

1. Copy a `.json` or `.xml` file into `docs/input`.
2. Either press Enter for the default (it masks the first JSON file by name), or answer `n`, pick a
   demo, then **2** (Local file), the format, and your file's number.
3. Open the result in `docs/output`.

`docs/output` is in `.gitignore`, so generated results are never committed.

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

Then tell git to ignore the venv, the build output, the Python cache and the generated results. The file name starts
with a dot and has no extension.

**File:** `.gitignore` (new file)

```text
.venv/
bin/
obj/
__pycache__/
docs/output/
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
Inputs and outputs are plain strings (JSON) to keep the C# side simple.

Every function takes `fmt`: "text", "json" or "xml". For JSON and XML, Presidio runs on each
value separately (JSON string values, XML element text and attributes), so keys, tags and
nesting are never changed and the output stays valid JSON/XML."""

import json
import re
import xml.etree.ElementTree as ET

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


def _map_values(content, fmt, fn):
    """Calls fn(value, field) for every text value and returns the rebuilt document.
    `field` is the JSON key, XML tag or XML attribute name (None for plain text)."""
    if fmt == "text":
        return fn(content, None)

    if fmt == "json":
        def walk(node, key):
            if isinstance(node, dict):
                return {k: walk(v, k) for k, v in node.items()}
            if isinstance(node, list):
                return [walk(v, key) for v in node]
            if isinstance(node, str):
                return fn(node, key)
            return node  # numbers, booleans and null are left as they are
        return json.dumps(walk(json.loads(content), None), indent=2, ensure_ascii=False)

    if fmt == "xml":
        root = ET.fromstring(content)
        for element in root.iter():
            if element.text and element.text.strip():
                element.text = fn(element.text, element.tag)
            for name, value in element.attrib.items():
                element.attrib[name] = fn(value, name)
        return ET.tostring(root, encoding="unicode")

    raise ValueError(f"Unknown format: {fmt}")


def _analyze_value(engine, value, field):
    # The field name (for example "email" or "pnr") is passed as context to help detection.
    return engine.analyze(text=value, language="en", context=[field] if field else None)


def _find(engine, content, fmt):
    found = []

    def collect(value, field):
        for r in sorted(_analyze_value(engine, value, field), key=lambda r: r.start):
            found.append({"field": field or "", "entity_type": r.entity_type,
                          "score": round(r.score, 2), "text": value[r.start:r.end]})
        return value

    _map_values(content, fmt, collect)
    return json.dumps(found, indent=2)


def analyze(content, fmt):
    return _find(analyzer, content, fmt)


def analyze_custom(content, fmt):
    return _find(custom_analyzer, content, fmt)


OPERATORS = {
    "replace": OperatorConfig("replace"),
    "redact": OperatorConfig("redact"),
    "mask": OperatorConfig("mask", {"masking_char": "*", "chars_to_mask": 100, "from_end": False}),
    "hash": OperatorConfig("hash", {"hash_type": "sha256"}),
}


def anonymize(content, fmt, mode):
    def protect(value, field):
        results = _analyze_value(analyzer, value, field)
        return anonymizer.anonymize(text=value, analyzer_results=results,
                                    operators={"DEFAULT": OPERATORS[mode]}).text

    return _map_values(content, fmt, protect)


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


def tokenize(content, fmt):
    """Returns {"text": tokenized document, "mapping": {entity: {original: token}}}.
    One mapping is shared by the whole document, so a repeated value gets the same token.
    The PNR is detected but kept visible and unchanged."""
    mapping = {}
    operators = {
        "DEFAULT": OperatorConfig("token", {"mapping": mapping}),
        "PNR_LOCATOR": OperatorConfig("keep"),
    }

    def protect(value, field):
        results = _analyze_value(custom_analyzer, value, field)
        # spaCy sometimes also labels a PNR as PERSON with a higher score; the PNR must win.
        pnrs = [r for r in results if r.entity_type == "PNR_LOCATOR"]
        results = [r for r in results if r.entity_type == "PNR_LOCATOR"
                   or not any(r.start < p.end and p.start < r.end for p in pnrs)]
        return anonymizer.anonymize(text=value, analyzer_results=results, operators=operators).text

    document = _map_values(content, fmt, protect)
    return json.dumps({"text": document, "mapping": mapping})


def detokenize(tokenized_json, fmt):
    """Restores originals from the mapping. Demo only; a real system uses a token vault."""
    data = json.loads(tokenized_json)

    def restore(value, field):
        for per_type in data["mapping"].values():
            for original, token in per_type.items():
                value = value.replace(token, original)
        return value

    return _map_values(data["text"], fmt, restore)
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

    /// <summary>The folder that contains .venv, python/ and docs/.</summary>
    public static string ProjectDir { get; private set; } = "";

    public static void Start()
    {
        string projectDir = ProjectDir = FindProjectDir();
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
        Console.Write("Use default: Anonymize the first JSON file in docs/input with mask? [Y/n]: ");
        string? answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (answer is null) break;

        string? choice;
        if (answer is "" or "y" or "yes")
        {
            choice = "default";
        }
        else if (answer is "n" or "no")
        {
            Console.WriteLine("1. Analyze (built-in recognizers)");
            Console.WriteLine("2. Anonymize (replace / redact / mask / hash)");
            Console.WriteLine("3. Custom recognizers (PNR + passenger ID)");
            Console.WriteLine("4. Reversible tokenization");
            Console.WriteLine("5. Run all with sample text");
            Console.WriteLine("0. Exit");
            Console.Write("Choose: ");

            choice = Console.ReadLine()?.Trim();
            if (choice is null or "0") break;
        }
        else
        {
            Console.WriteLine("Please answer y or n.");
            continue;
        }

        try
        {
            switch (choice)
            {
                case "default":
                    string firstJson = InputFiles("json")[0];
                    Console.WriteLine($"Using the first JSON file in docs/input: {Path.GetFileName(firstJson)}");
                    Anonymize(new Input(File.ReadAllText(firstJson), "json", firstJson), "mask");
                    break;
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

// .json or .xml files in docs/input, sorted by name.
string[] InputFiles(string format)
{
    string[] files = Directory.Exists(inputDir)
        ? Directory.GetFiles(inputDir, $"*.{format}").Order().ToArray()
        : [];
    return files.Length > 0
        ? files
        : throw new FileNotFoundException($"No .{format} files found in {inputDir}");
}

Input ReadLocalFile()
{
    Directory.CreateDirectory(inputDir);
    Console.WriteLine($"\nInput folder: {inputDir}");
    Console.WriteLine("File format:");
    Console.WriteLine("  1. JSON");
    Console.WriteLine("  2. XML");
    string format = Ask("Choose (Enter = 1): ") == "2" ? "xml" : "json";

    string[] files = InputFiles(format);
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
```

### Step 8 – Add the sample input files

Create the folders `docs` and `docs/input`, then add the two samples. The app lists every `.json`
or `.xml` file in `docs/input`, and uses these two files when you press Enter for a JSON or XML
sample. The `docs/output` folder is created automatically the first time a result is saved.

**File:** `docs/input/booking-sample.json` (new folders and file)

```json
{
  "pnr": "R4TZ8N",
  "messageType": "booking",
  "passengers": [
    {
      "passengerId": "PAX-3001",
      "name": "Sarah Mitchell",
      "email": "sarah.mitchell@example.com",
      "phone": "312-555-0182"
    },
    {
      "passengerId": "PAX-3002",
      "name": "David Turner",
      "email": "david.turner@example.com",
      "phone": "312-555-0164"
    }
  ],
  "remarks": "Sarah Mitchell requests an aisle seat. Call 312-555-0182 if the flight changes.",
  "payment": {
    "cardNumber": "4111 1111 1111 1111"
  }
}
```

**File:** `docs/input/booking-sample.xml` (new file)

```xml
<?xml version="1.0" encoding="utf-8"?>
<booking pnr="M2VK9D" messageType="booking">
  <passenger id="PAX-4001">
    <name>Jessica Hayes</name>
    <email>jessica.hayes@example.com</email>
    <phone>415-555-0123</phone>
  </passenger>
  <passenger id="PAX-4002">
    <name>Ryan Cooper</name>
    <email>ryan.cooper@example.com</email>
    <phone>415-555-0198</phone>
  </passenger>
  <remarks>Ryan Cooper requests a window seat. Contact Jessica Hayes at 415-555-0123.</remarks>
</booking>
```

### Step 9 – Build and run

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
├── .venv/                       Python 3.12 virtual environment (not in git)
├── docs/
│   ├── input/
│   │   ├── booking-sample.json  JSON sample
│   │   └── booking-sample.xml   XML sample
│   └── output/                  Saved results (created when needed, not in git)
├── python/
│   └── presidio_demo.py         Presidio logic
├── PythonHost.cs                Python.NET bridge
├── Program.cs                   Console menu and input handling
├── PresidioDemo.csproj
├── requirements.txt             Pinned Python packages and spaCy model
├── .gitignore                   Ignores .venv, bin, obj, Python cache, docs/output
└── README.md
```

---

## Things worth knowing for the demo

- **Detection is model-based.** For example, "Emily" on its own after "Contact" is not detected
  in the sample, while "Michael" is. This is how spaCy behaves, not a bug in the app.
- **URL hits inside emails** (`emily.car`, `example.com`) appear in the analyze list. The anonymizer
  resolves the overlap in favour of the higher-scoring `EMAIL_ADDRESS`, so the output is clean.
- **Short values on their own are harder for the model.** In the JSON and XML samples, spaCy labels
  the bare value `PAX-3002` as a PERSON and `PAX-4002` as a LOCATION. The built-in demos
  (options 1 and 2) therefore treat one passenger ID differently from the other. With the custom
  recognizers (options 3 and 4), the higher-scoring `PASSENGER_ID` wins.
- **The PNR always stays unchanged in option 4,** even when spaCy also labels it as a name.
- **Token numbers can look reversed** in plain text, because Presidio replaces entities from the end
  of the text backwards.
- **`hash` output changes between runs,** because Presidio adds a random salt to each hash.
- **Only text values are checked.** JSON numbers and booleans, XML comments and the XML declaration
  are not analyzed, and the saved XML leaves out the `<?xml ...?>` declaration and comments.
- **Pasted input ends at the first empty line.** Remove blank lines from JSON or XML before pasting,
  or use a local file instead.
- **The token mapping is in memory only.** It shows the concept; a real system would store
  mappings in a secured token database.
- **Piping input from PowerShell** (`"n" | dotnet run`) can add an invisible byte-order mark that
  makes the first answer show "Please answer y or n.". Typing interactively works normally.

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
| `Error: No .json files found in ...` (or `.xml`) | Put the file in the `docs/input` folder the app shows, with a `.json` or `.xml` extension, and pick the matching format. |
| `Error: Expecting ',' delimiter...` or a similar JSON message | The JSON is invalid. Check it, or check that a pasted JSON didn't stop at a blank line. |
| `Error: mismatched tag...` or `not well-formed` | The XML is invalid. Check that every tag is closed. |
| `Error: Invalid file number` / `Unknown operator` | Enter one of the numbers or operator names shown in the prompt. |

## References

- Presidio samples: https://presidio.dataprivacystack.org/samples/
- Presidio custom recognizers: https://presidio.dataprivacystack.org/analyzer/adding_recognizers/
- Presidio anonymizer operators: https://presidio.dataprivacystack.org/anonymizer/adding_operators/
- Python.NET: https://pythonnet.github.io/
