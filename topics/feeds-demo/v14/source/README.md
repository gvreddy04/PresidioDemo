# Presidio Feed Protection Demo

Process airline feeds using configured field rules and local Presidio. PNR stays unchanged. Output is validated before publication; failed input is retained.

![Feed protection workflow](topics/feeds-demo/v14/presidio-feed-workflow-v3.svg)

[Editable Excalidraw diagram](topics/feeds-demo/v14/presidio-feed-workflow-v3.excalidraw)

## Current feed status

| Feed | Actual input result |
| --- | --- |
| ACI | Published using the reviewed 888-byte reference layout |
| ETKT | Published using the reviewed 672-byte reference layout |
| Seats | Published using complete JSON |
| PNR | Retained; complete binary framing and layout need confirmation |
| PNR-Linking | Retained; supplied JSON ends inside a property name |

ACI and ETKT support the reviewed reference frames, rather than arbitrary producer messages. The external filename `TKT.txt` routes to ETKT. Protection uses irreversible masks or replacement labels; stable tokens are not implemented.

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

Requires Windows, the .NET 10 SDK and Python 3.12. From the project root, perform initial setup:

```powershell
py -3.12 -m venv src/.venv
src/.venv/Scripts/python.exe -m pip install -r requirements.txt
dotnet build
```

Process the actual feeds once, preserving the source files:

```powershell
dotnet run -- --process-folder D:/Bala_Support/Feeds
```

For continuous listening, run `dotnet run`, wait for `READY`, then copy a `.txt` MQ export or `.bin` payload into `docs/input-feeds/<feed>`. Press Enter or Ctrl+C to stop. Use the folders `ACI`, `ETKT`, `PNR`, `PNR-Linking` and `Seats`.

A batch succeeds only when every input is published and acknowledged. The supplied PNR and PNR-Linking files currently prevent a fully successful actual-file batch.

## Folders and configuration

- `src/`: C# worker, `feeds/`, `python/`, `config/` and `.venv/`.
- `tests/unit-tests/`: Python tests; `tests/FeedPipelineChecks/`: .NET checks.
- `docs/sample-docs/`: finalized reference TXT files.
- `docs/input-feeds/`, `output-feeds/`, `failed-feeds/`: one subfolder per feed.
- `.runtime/docs/`: retained processing input, listener lock and batch reports.

Edit [field policies](src/config/feed-policies.xml) to Keep, Mask, Replace or Redact fields. `Known` protects the entire value; `Analyze` uses local NLP. Reviewed binary profiles are in [reference layouts](src/config/reference-layouts.xml); general producer definitions belong in [XDR layouts](src/config/xdr-layouts.xml). Restart after configuration changes.

## Tests

```powershell
src/.venv/Scripts/python.exe -m unittest discover -s tests/unit-tests -p test_*.py -v
src/.venv/Scripts/python.exe tests/unit-tests/test_feed_listener.py
dotnet run --project tests/FeedPipelineChecks
```

[Implementation details](topics/feeds-demo/v14/feed-implementation-reference-v14.md) · [Feed format analysis](docs/feed-analysis.md)
