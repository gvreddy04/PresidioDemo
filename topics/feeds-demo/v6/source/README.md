# Presidio Feed Protection Demo

A local .NET listener applies configurable PII protection and writes messages back in their original feed representation.

```text
Input feed -> FolderFeedListener -> feed parser -> ConfiguredPiiIdentifier
           -> local Presidio anonymization -> feed serializer -> validation -> output feed
```

.NET owns transport, parsing, policy selection, serialization, validation and acknowledgment. Python.NET embeds CPython and Presidio in the worker process. Engines and the installed NLP model initialize once. There are no HTTP/REST PII calls, remote recognizers or runtime model downloads.

## Feed analysis and implemented support

The finalized files are from `D:/Bala_Support/Feeds`. Each contains one IBM MQ export: `A` records contain MQ metadata and `X` records contain hexadecimal application payload bytes. `MQSTR` in metadata does not establish the payload's application schema. MQ numeric encoding and CCSID are retained; proprietary binary records are never decoded as ordinary text.

| Project route | Supplied filename | Payload bytes | Observed payload | Current behavior |
| --- | --- | ---: | --- | --- |
| ETKT | TKT.txt | 672 | Proprietary binary | Retained; producer schema and serializer required |
| PNR | PNR.txt | 1,523 | Proprietary binary | Retained; producer schema and serializer required |
| ACI | ACI.txt | 888 | Proprietary binary | Retained; producer schema and serializer required |
| Seats | SEATS.txt | 1,362 | Complete UTF-8 JSON object | Parsed, protected, serialized and validated |
| PNR-Linking | PNR_LINKING.txt | 1,523 | UTF-8 JSON array cut off inside a string | Retained with an incomplete/malformed JSON report |

The agreed project name is **ETKT** everywhere. The supplied `TKT.txt` is assigned to the ETKT route; its preserved project copy is named `ETKT.txt`. Routing uses the input folder, not a filename guess. Existing archived revisions remain unchanged.

`JsonFeedCodec` is registered for Seats and PNR-Linking. A complete PNR-Linking message can run when all its scalar paths match configuration; paths currently reflect only the visible part of the supplied export and must be reviewed against the full message. ACI, PNR and ETKT use `UnconfiguredBinaryFeedCodec`, which retains the input without publishing. A working producer codec must implement `IFeedCodec.Parse`, `Serialize` and `Validate`; it must consume every record and update binary lengths, padding and counts correctly. No byte-offset or printable-string replacement is used as a substitute for a schema.

See [feed analysis](docs/feed-analysis.md) for checksums, completeness limitations and outstanding producer requirements.

## Technology stack

| Technology | Version | Responsibility |
| --- | --- | --- |
| C# / .NET | Target `net10.0`; verified SDK `10.0.401` | Listener, orchestration, parsing, policy validation, serialization and delivery |
| Python.NET NuGet package (`pythonnet`) | `3.2.0` | Embed CPython in .NET; acquire `Py.GIL()` for calls |
| CPython | Installed `3.12.10` | Local runtime under `src/.venv` |
| presidio-analyzer | `2.2.364` | Known spans and configured local free-text detection |
| presidio-anonymizer | `2.2.364` | Replace, Mask and Redact operators |
| spaCy | `3.8.16` | Local NLP engine |
| en_core_web_lg | `3.8.0` | Preinstalled English NLP model |
| System.Text.Json | Framework library | Strict JSON parsing and scalar-span serialization |
| System.Xml.Linq | Framework library | Validated local XML policy configuration |
| FileSystemWatcher / file I/O | Framework libraries | Folder transport simulation and atomic output delivery |
| Python unittest / .NET check harness | Standard library / .NET | Python rule tests, listener checks and codec integration checks |

This is a Windows demo; `PythonHost` resolves the local CPython DLL from `src/.venv/pyvenv.cfg`. AWS customer hosting remains a future deployment step. The folder adapter simulates broker delivery; no live broker or database provider has been selected. Any future broker or organization database adapter must use a native client.

## Project layout

```text
PresidioDemo/
  PresidioDemo.csproj
  README.md
  requirements.txt
  src/
    config/
      feed-policies.xml
    python/
      presidio_demo.py
    feeds/
      FeedContracts.cs
      FeedFormats.cs
      JsonFeedCodec.cs
      FeedPolicies.cs
      FeedProtection.cs
      FeedWorkflows.cs
      FolderFeedListener.cs
      FolderFeedTransport.cs
    .venv/
    Program.cs
    PythonHost.cs
  tests/
    unit-tests/
      test_presidio_rules.py
      test_feed_listener.py
    FeedPipelineChecks/      .NET integration support for codec and delivery checks
  docs/
    sample-docs/
      finalized-feeds/       Unmodified supplied bytes; TKT named ETKT in the project
      Seats/seats-mq-export.txt
    input-feeds/
      ETKT/ PNR/ PNR-Linking/ Seats/ ACI/
    output-feeds/
      ETKT/ PNR/ PNR-Linking/ Seats/ ACI/
      Seats/seats-protected.txt
    feed-analysis.md
  topics/feeds-demo/          Preserved historical source revisions
```

Historical `docs/input` and `docs/solace-feeds` examples remain preserved and are not watched. Runtime failures and in-flight deliveries use `docs/failed-feeds` and `docs/.processing`; these are created on demand. The existing virtual environment has moved to `src/.venv`; use its Python executable and `python -m pip` rather than old console-script launchers.

## Run

The existing environment is ready. To recreate it on a new Windows machine, install Python 3.12, then run `py -3.12 -m venv src/.venv` and `src/.venv/Scripts/python.exe -m pip install -r requirements.txt` during setup. Package the installed NLP model before deployment; processing never downloads it.

From the project root:

```powershell
dotnet build
dotnet run
```

Wait for `READY`, then copy the fictional two-passenger MQ export into the Seats input folder:

```powershell
Copy-Item docs/sample-docs/Seats/seats-mq-export.txt docs/input-feeds/Seats/
```

The listener writes `docs/output-feeds/Seats/seats-mq-export.<delivery-id>.txt`. It deletes the claimed input only after successful output publication. `docs/output-feeds/Seats/seats-protected.txt` is a verified example output for review. Sample files are never enqueued automatically. Press Enter or Ctrl+C to stop.

The supplied complete SEATS file can also be tested:

```powershell
Copy-Item docs/sample-docs/finalized-feeds/SEATS.txt docs/input-feeds/Seats/
```

For isolated runs and a custom policy file:

```powershell
dotnet run -- --docs-root D:/Bala_Support/PresidioFeedSandbox/docs --policy-file src/config/feed-policies.xml
```

Only `.txt` MQ exports/diagnostic dumps and `.bin` payloads are accepted by the folder listener. JSON is supported **inside** the two configured feed payloads. Standalone `.json`/`.xml` input files and nested input folders are ignored. Legacy AMQSBCG diagnostic dumps remain readable; rewriting that diagnostic representation is not implemented. Raw `.bin` JSON inputs produce raw `.bin` JSON outputs.

## Configured PII identification and anonymization

`ConfiguredPiiIdentifier` resolves the complete field plan before any anonymization. `PresidioFeedProtector` invokes the installed local Presidio engines. Policy changes take effect on restart.

For JSON, field names are exact, case-sensitive JSON Pointer paths with `*` for array elements. Runtime locations use numeric array indices only to find values within the current message; these are never passenger identities. Repeated field names at different paths require separate configuration. Unknown scalar paths reject the entire message. Legacy named-field policies are retained for producer-codec integration and the existing binary regression fixture; they do not establish binary schemas.

```xml
<field name="/OrderChangeNotif/Old/Customers/*/UARecloc"
       identification="None" action="Keep" />
<field name="/OrderChangeNotif/Old/Customers/*/FirstName"
       identification="Known" entity="PERSON"
       action="Replace" replacement="[PASSENGER_NAME]" />
```

| Identification mode | Behavior | Example |
| --- | --- | --- |
| None | Keep the original value; bypass Python | PNR, flight, seat and operational timestamps |
| Known | Protect the full non-empty field using a configured entity span | Names, passenger IDs and employee IDs |
| Analyze | Run local detection for only the configured entities and threshold | Free text containing email addresses |

| Action | Result |
| --- | --- |
| Keep | Original value |
| Replace | Explicit configured label |
| Mask | Configured character count, masking character and direction |
| Redact | Remove the identified value/span |

Known sensitive fields are protected even when NLP would miss them. An `Analyze` field with no detections is unchanged; use `Known` for fields guaranteed to hold PII. All PNR aliases, including `UARecloc`, `UaRecordLocator` and `PNROwner`, must remain unchanged. JSON numbers, booleans and nulls preserve their original types; non-string transformations are rejected. Null and empty known fields remain empty.

Seats rules replace names, `PaxId`, `OrderId` and `EmpId`. Passenger `Attributes`, `IATAAttributes` and `TTYAttributes` can reveal health, age or assistance information, so their entries use configured replacement labels. Flight, seat and PNR fields remain visible. JSON string replacements may grow or shrink; serialization rewrites only changed scalar spans and rebuilds MQ hex rows while preserving MQ metadata, newline style and final-newline presence. No-op serialization is byte-for-byte identical.

The active demo performs irreversible anonymization with shared replacement labels. It does not issue distinct stable passenger tokens, provide recovery mappings or implement a token vault. Passenger grouping is retained by the original record structure. PNR remains linkable. Reversible scoped tokenization would require a separately configured tenant + PNR + stable source passenger ID mapping service and database design.

## Delivery and validation

One active delivery per feed allows independent progress across the five routes. Shared Python engines are guarded between fields; this is not parallel NLP inference. Shutdown drains active workers before releasing the transport or Python runtime.

The producer should write a `.tmp` file, close it, then rename it to `.txt` or `.bin`. The listener waits for stable, exclusively readable files, claims them into `.processing`, parses the complete payload, resolves configured fields, protects them, serializes and validates the result. Outputs are flushed to `.pending` then renamed before input acknowledgment. A single-consumer lock protects each docs root. Delivery IDs make retries of the same claimed input reuse its destination; resubmitting a new file is a new delivery.

Any parse, identification, anonymization, serialization, validation or publishing failure retains the complete source message and records a failure copy plus `.error.txt` report. No raw payload is published as a fallback. Completed rejections are not retried automatically; copy the source back to the input folder after correcting the cause. Logs record stages and counts, without decoded field values.

## Verify

```powershell
dotnet build
src/.venv/Scripts/python.exe -m unittest discover -s tests/unit-tests -p test_presidio_rules.py -v
src/.venv/Scripts/python.exe tests/unit-tests/test_feed_listener.py
dotnet run --project tests/FeedPipelineChecks
```

Checks cover real embedded Presidio operators, strict policies, exact MQ/JSON round trips, metadata and PNR preservation, two-passenger grouping, type preservation, complete supplied SEATS processing, malformed and truncated payload rejection, unsupported binary retention, failure-report retries, restart behavior, feed independence and shutdown draining. The .NET binary regression fixture is explicitly test-only and is never registered as an airline codec.
