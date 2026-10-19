# Presidio Feed Protection Demo

Process airline feeds using configured field rules and local Presidio. PNR stays unchanged. Output is validated before publication; failed input is retained.

![Feed protection workflow](topics/feeds-demo/v14/presidio-feed-workflow-v3.svg)

[Editable Excalidraw diagram](topics/feeds-demo/v14/presidio-feed-workflow-v3.excalidraw)

## Current feed status

| Feed | Actual input result |
| --- | --- |
| ACI | All six samples use one sequential XML definition, including four optional groups |
| ETKT | All six samples use one 136-slot definition; empty strings account for missing values |
| Seats | Complete JSON with 39 existing policy field paths |
| PNR | One reviewed sequential definition; 76 masked fields and 30 kept fields; structural tags preserved |
| PNR-Linking | Complete JSON with 49 existing policy field paths |

The three original XML formats remain: [reference mappings](src/config/reference-layouts.xml), [feed policies](src/config/feed-policies.xml), and [XDR structures](src/config/xdr-layouts.xml). Each binary feed has one common structural definition. Optional extensions enable variable widths, optional groups, named capture mappings and 14 shared masking rules without replacing the original roots or feed sections. New structures use reviewed XML changes and a restart. Unsupported structures are retained without publication. `TKT.txt` routes to ETKT.

XDR raw-word blocks use `<words name="..." count="..." />`, a compact alias for the original fixed array of `uint32` values. The loader supports both forms and expands the alias before processing; byte widths, field paths and validation remain unchanged.

All 15 supplied samples pass actual local Presidio processing and independent byte checks: 19 identified PNR occurrences, source bytes and binary framing stay unchanged. The former ETKT `Text0132` mapping is corrected to two preserved raw words. See [verification results](docs/feed-analysis.md#reviewed-samples-and-results).

## Technology stack

| Technology | Version |
| --- | --- |
| C# / .NET | .NET 10 |
| Python.NET | 3.2.0 |
| CPython | 3.12.10 |
| Presidio Analyzer / Anonymizer | 2.2.364 |
| spaCy / en_core_web_lg | 3.8.16 / 3.8.0 |

Python runs inside the .NET process. Processing makes no HTTP calls or model downloads.

## Setup and run

Requires Windows, the .NET 10 SDK and Python 3.12. Run these steps in PowerShell from the project root.

### 1. Create the Python virtual environment

Do this once during initial setup:

```powershell
py -3.12 -m venv src/.venv
```

### 2. Activate the Python virtual environment

Activate it whenever you open a new PowerShell terminal:

```powershell
.\src\.venv\Scripts\Activate.ps1
```

If PowerShell reports that running scripts is disabled, allow locally created scripts for this terminal session, then retry activation:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned
.\src\.venv\Scripts\Activate.ps1
```

If prompted, enter `Y`. The policy change lasts only until this PowerShell terminal closes; administrator access is not required. See [PowerShell execution policies](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_execution_policies).

If your organization enforces a policy that still blocks activation, use the Python launcher to install packages for its selected interpreter:

```powershell
py -m pip install -r requirements.txt
```

With an active virtual environment and no explicit Python version, `py` uses that environment. Without activation, `py` uses its default interpreter and may install packages outside `src/.venv`. If activation remains blocked, confirm the selected interpreter with `py -c "import sys; print(sys.executable)"` before installing; it should end in `src\.venv\Scripts\python.exe` for this project.

The prompt normally shows `(.venv)` after activation. Confirm Python uses this environment:

```powershell
python -c "import sys; print(sys.executable)"
```

The path should end in `src\.venv\Scripts\python.exe`.

### 3. Install the Python packages

With the virtual environment active, install the required packages:

```powershell
python -m pip install -r requirements.txt
```

### 4. Deactivate the Python virtual environment

After package installation finishes, deactivate it:

```powershell
deactivate
```

### Build and run the .NET demo

```powershell
dotnet build
```

Process the actual feeds once, preserving the source files:

```powershell
dotnet run -- --process-folder D:/Bala_Support/Feeds
```

For continuous listening, run `dotnet run`, wait for `READY`, then copy a `.txt` MQ export or `.bin` payload into `docs/input-feeds/<feed>`. Press Enter or Ctrl+C to stop. Use the folders `ACI`, `ETKT`, `PNR`, `PNR-Linking` and `Seats`.

A batch succeeds only when every input is published and acknowledged. Batch filenames accept numeric suffixes such as `ACI_03.txt` and `ETKT_05.txt`; the listener routes by feed folder and accepts arbitrary filenames. Samples are review/test inputs and are not required at runtime. The schemas describe the structures supported by the supplied examples; a producer contract is still needed to confirm unknown binary meanings and general passenger associations.

## Folders and configuration

- `src/`: C# worker, `feeds/`, `python/`, `config/` and `.venv/`.
- `tests/unit-tests/`: Python tests; `tests/FeedPipelineChecks/`: .NET checks.
- `docs/sample-docs/`: immutable source TXT files for review and testing; not required at runtime.
- `docs/input-feeds/`, `output-feeds/`, `failed-feeds/`: one subfolder per feed.
- `docs/feed-logs/`: one text file per transaction, named `<feed>.<message-id>.txt`.
- `docs/feed-processing/`: active input files, with one subfolder per feed.
- `.runtime/docs/`: listener lock and batch reports.

The listener moves each ready file from `docs/input-feeds/<feed>` into `docs/feed-processing/<feed>/<original-stem>.<unique-id>.<extension>`. This removes it from the input folder before processing starts. The same ID follows the transaction through its log and output. Once output is saved, the processing file is deleted. Rejected input is saved in `docs/failed-feeds/<feed>` with an error report before the processing file is deleted. Interrupted transactions and temporary storage failures stay in `feed-processing` for retry after restart. Older `.runtime/docs/processing` deliveries are migrated on startup while preserving their IDs and bytes.

Each transaction log has separate `BEFORE ANONYMIZATION` and `AFTER ANONYMIZATION` headings with divider lines. For MQ input, the before section shows the complete original feed data, decoded payload, a plain-text payload view, then parsed original fields (`location | policy field | value`). The plain-text view preserves readable UTF-8 text and whitespace; binary controls and invalid UTF-8 are shown as spaces. The after section shows the decoded anonymized payload followed by the complete rebuilt and validated feed data. Byte counts reflect the actual payload and feed sizes. Raw input or malformed MQ envelopes show complete input data and its plain-text view without a decoded payload. Binary data is shown as a hex dump with a readable character column. Rejected transactions retain the complete input data and any decoded payload and explain why no validated anonymized output is available. Retries reuse the same log file. If a log cannot be saved, input remains pending and is retried before publication or rejection.

These logs contain original personal data and are excluded from Git. They are written under the selected `--docs-root` when a custom folder is used.

All three XML files are loaded and validated together before input is claimed. Reference mappings retain `name`, `offset` and `length`; with `schemaDriven="true"`, these numbers document the reviewed baseline while the XDR structure determines actual input positions and widths. Reference sample filenames and hashes are provenance only in this mode. Original reference configurations without this flag retain their existing sample-based behavior. Binary fields use Keep or Known full-width ASCII masks. PNRs and confirmed operational fields stay unchanged. Unknown readable identifiers are masked, including the two additional ETKT codes. The three catalog rules for loyalty, sensitive attributes and financial information use `#` to preserve `*` record separators; other masks use `*`. JSON retains its original inline policies, replacement behavior, nulls, types and grouping.

Restart after changes. The existing switches `--reference-layout-file <xml>`, `--xdr-layout-file <xml>` and `--policy-file <xml>` select alternate files. See the [feed analysis](docs/feed-analysis.md) for commented XML examples, empty/optional values, per-sample results and verification commands.

## Tests

```powershell
src/.venv/Scripts/python.exe -m unittest discover -s tests/unit-tests -p test_*.py -v
src/.venv/Scripts/python.exe tests/unit-tests/test_feed_listener.py
dotnet run --project tests/FeedPipelineChecks
src/.venv/Scripts/python.exe tests/run_sample_verification.py
```

[Implementation details](topics/feeds-demo/v14/feed-implementation-reference-v14.md) · [Feed format analysis](docs/feed-analysis.md)
