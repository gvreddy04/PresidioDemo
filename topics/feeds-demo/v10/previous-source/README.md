# Presidio Feed Protection Demo

The five routes share one processing flow:

```text
Input feed -> FolderFeedListener -> feed parser -> ConfiguredPiiIdentifier
           -> local Presidio anonymization -> feed serializer -> validation -> output feed
```

.NET owns transport, parsing, policy selection, serialization, validation and acknowledgment. Python.NET embeds CPython and Presidio in the worker process. Engines and the installed NLP model initialize once. Processing makes no HTTP/REST calls and does not download models.

## Actual input results

The actual files under `D:/Bala_Support/Feeds` were run through the listener. Their outer representation is an IBM MQ text export: `A` metadata records plus `X` hexadecimal payload records. Original source files remain unchanged.

| Route | Actual source | Payload | Actual result |
| --- | --- | --- | --- |
| Seats | SEATS.txt | Complete UTF-8 JSON | Protected, validated, published and acknowledged |
| PNR-Linking | PNR_LINKING.txt | Incomplete UTF-8 JSON | Retained; input ends inside a string at payload byte 1,523 |
| ACI | ACI.txt | Binary with strong XDR evidence | Retained; airline field definitions unavailable |
| PNR | PNR.txt | Binary with strong XDR evidence | Retained; 1,523 bytes is not aligned as a complete standalone XDR stream; schema also unavailable |
| ETKT | TKT.txt | Binary with strong XDR evidence | Retained; airline field definitions unavailable |

The actual-file run is **not five successful protected deliveries**. All five routes have parser/serializer integration and terminal outcome reporting, but the unavailable field contracts and incomplete input prevent four real-file outputs. A separate test proves all five routes can publish using explicitly test-only XDR schemas and complete fictional JSON fixtures. Test schemas are never registered for the actual feeds.

The project uses **ETKT** throughout, as confirmed. The supplied `TKT.txt` filename is an explicit source alias for ETKT. Routing inside the listener uses its feed folder. Historical examples remain preserved.

See the [current implementation and actual-run report](topics/feeds-demo/v9/all-feed-implementation-v9.md) and [format analysis](docs/feed-analysis.md).

## Run all actual files once

From the project root:

```powershell
dotnet build
dotnet run -- --process-folder D:/Bala_Support/Feeds
```

This reads the actual source folder, copies recognized `.txt`/`.bin` files into `docs/input-feeds/<route>`, and runs those copies through the normal listener. It preserves originals. Each import has a unique run identifier. It exits after every import reaches a published/acknowledged or retained-failure outcome, or after a two-minute outcome timeout. A pending result does not count as success.

- Protected output: `docs/output-feeds/<route>/`
- Failed copies and `.error.txt` reports: `docs/failed-feeds/<route>/`
- Retained original deliveries: `docs/.processing/<route>/`
- Per-source outcome report with checksums and output/error paths: `docs/batch-runs/feed-batch-<run-id>.json`

The application returns zero only when every batch item is published. A non-success batch returns application exit code 2; startup failures return 1. Invoke `dotnet bin/Debug/net10.0/PresidioDemo.dll` directly when a caller needs the application exit code rather than the `dotnet run` wrapper's exit behavior.

To isolate a run:

```powershell
dotnet run -- --process-folder D:/Bala_Support/Feeds --docs-root D:/Bala_Support/PresidioFeedSandbox/docs
```

Recognized source stems are `ACI`, `PNR`, `PNR_LINKING`/`PNR-Linking`, `SEATS`, and `ETKT` (with the confirmed `TKT` source alias). Unknown TXT/BIN filenames reject batch startup. Other file extensions are ignored.

## Listen continuously

```powershell
dotnet run
```

After `READY`, copy a source into the corresponding `docs/input-feeds/<route>` folder. For a complete fictional two-passenger example:

```powershell
Copy-Item docs/sample-docs/Seats/seats-mq-export.txt docs/input-feeds/Seats/
```

Press Enter or Ctrl+C to stop. Samples are never automatically consumed. JSON is supported inside configured MQ/raw payloads; standalone `.json`/`.xml` filenames and nested input folders are ignored. Legacy AMQSBCG diagnostic dumps remain readable, but rewriting that representation is unsupported.

## Technology stack

| Technology | Version | Responsibility |
| --- | --- | --- |
| C# / .NET | Target net10.0; verified SDK 10.0.401 | Listener, orchestration, codecs, validation and delivery |
| Python.NET NuGet package (`pythonnet`) | 3.2.0 | Embed CPython and acquire Py.GIL() for calls |
| CPython | 3.12.10 | Local runtime under src/.venv |
| presidio-analyzer | 2.2.364 | Known spans and configured local detection |
| presidio-anonymizer | 2.2.364 | Replace, Mask and Redact |
| spaCy | 3.8.16 | Local NLP engine |
| en_core_web_lg | 3.8.0 | Installed English NLP model |
| System.Text.Json | Framework library | Strict JSON parsing and scalar-span serialization |
| System.Buffers.Binary | Framework library | Big-endian XDR primitives and counted-string serialization |
| System.Xml.Linq | Framework library | Strict local XML policies and XDR definitions |
| FileSystemWatcher / file I/O | Framework libraries | Folder transport, atomic publication and batch import |
| Python unittest / .NET harness | Standard library / .NET | Rule, codec, batch and listener checks |

The current host integration targets Windows. PythonHost resolves the local CPython DLL from `src/.venv/pyvenv.cfg`. AWS deployment remains a future packaging step. Folder transport simulates broker delivery; no live broker or database provider is selected. Future broker/database integration must use native clients.

The existing environment is ready. To recreate it during setup, install Python 3.12, run `py -3.12 -m venv src/.venv`, then `src/.venv/Scripts/python.exe -m pip install -r requirements.txt`. Package the installed NLP model before deployment; processing never downloads it. Use `python -m pip` rather than relocated console-script launchers.

## Project layout

```text
PresidioDemo/
  PresidioDemo.csproj
  README.md
  requirements.txt
  src/
    config/
      feed-policies.xml
      xdr-layouts.xml
    python/
      presidio_demo.py
    feeds/
      FeedContracts.cs
      FeedFormats.cs
      JsonFeedCodec.cs
      XdrFeedCodec.cs
      FeedBatchProcessor.cs
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
      test_seats_feed.py
      test_all_feed_batch.py
      test_feed_listener.py
    FeedPipelineChecks/      .NET integration support
  docs/
    sample-docs/
    input-feeds/
    output-feeds/
    feed-analysis.md
  topics/feeds-demo/          Immutable prior revisions and verification
```

Runtime `failed-feeds`, `.processing` and `batch-runs` folders are created under the selected docs root. Historical `docs/input` and `docs/solace-feeds` examples remain preserved and are not watched.

## JSON codecs and configured PII

JsonFeedCodec is registered for Seats and PNR-Linking. It requires a complete object for Seats and a complete array for PNR-Linking, rejects duplicate keys and trailing content, and preserves unchanged payload bytes, property order, grouping, scalar types and MQ metadata. Changed string spans are serialized back into the original MQ hex representation or raw BIN payload. A no-op round trip is byte-for-byte identical.

ConfiguredPiiIdentifier resolves the entire policy plan before anonymization. JSON policies use exact, case-sensitive JSON Pointer paths with `*` for array elements. Runtime numeric indices only locate fields within one message; they are never passenger identities. Unknown scalar paths reject the entire message. Policy changes take effect on restart.

```xml
<field name="/OrderChangeNotif/Old/Customers/*/UARecloc"
       identification="None" action="Keep" />
<field name="/OrderChangeNotif/Old/Customers/*/FirstName"
       identification="Known" entity="PERSON"
       action="Replace" replacement="[PASSENGER_NAME]" />
```

| Identification | Behavior |
| --- | --- |
| None | Keep the original value; bypass Python |
| Known | Protect the full non-empty value, even when NLP would miss it |
| Analyze | Run local detection for configured entities and threshold |

Actions are Keep, Replace, Mask and Redact. An Analyze field with no detections is unchanged; use Known when the field is guaranteed to contain PII. PNR aliases such as UARecloc, UaRecordLocator and PNROwner must remain unchanged. Null/empty fields remain empty. Unsupported non-string transformations reject the message.

Seats verification covers all 59 supplied scalar values across 39 configured paths: 29 replacements and 30 unchanged values. Names, PaxId, OrderId, EmpId and sensitive passenger attribute entries are replaced. PNR, seats, flights and operational metadata remain unchanged. The supplied sample has customers in Old and an empty New group; different shapes require reviewed policies. Downstream consumers must accept configured replacement labels. See the [field-by-field SEATS verification](topics/feeds-demo/v8/seats-completion-v8.md).

The demo uses irreversible replacement labels. It does not issue stable passenger tokens or provide recovery mappings. PNR remains linkable. Reversible tokenization would need a separately implemented tenant + PNR + stable source passenger ID mapping service and storage.

## Schema-driven XDR codecs

XdrFeedCodec replaces the binary placeholder. It is registered for ACI, PNR and ETKT. `src/config/xdr-layouts.xml` explicitly marks these producer contracts as unavailable. A binary sample is not a field definition, so no airline layout is guessed from names or offsets.

The parser supports bounded ASCII strings, signed/unsigned 32/64-bit integers, bit-preserved float32/float64, Booleans, fixed/variable opaque data, structures, fixed/counted arrays, optionals and signed-int32-discriminated unions. Layout files declare exact field order, bounds and optional expected integer constants. Every scalar receives an exact path requiring its own protection policy. Padding, bounds, constants, discriminators, total payload consumption and output structure are validated.

The serializer recalculates changed string lengths and zero padding. It preserves array counts, optional/union selection, untouched fields, opaque bytes, numeric bits, MQ metadata and PNR. Non-ASCII strings, unsupported primitive protection, unknown types, incomplete/trailing bytes and schema mismatches reject the entire message. This is a supported subset of XDR; producer-specific encodings, framing or additional types require an explicit adapter. Opaque or numeric PII cannot silently bypass a protection action.

To enable a real binary feed, obtain its field contract or generated parser/serializer from the system that created the message. Convert the definitions to the supported layout format, configure every exact field policy, and verify producer round-trip fixtures. No export application needs to be implemented by the user. Complete PNR-Linking source data must come from the original message; the missing tail cannot be reconstructed from this file.

Override configuration paths as needed:

```powershell
dotnet run -- --process-folder D:/Bala_Support/Feeds --policy-file src/config/feed-policies.xml --xdr-layout-file src/config/xdr-layouts.xml
```

## Delivery behavior

One active delivery per feed permits independent progress. Shared Python calls are guarded between fields. Shutdown drains workers before releasing the transport and Python runtime.

Producers should close a `.tmp` file before renaming it to `.txt` or `.bin`. The listener waits for stable, exclusively readable files, claims them into `.processing`, runs the full pipeline, flushes output to `.pending`, atomically renames it, then acknowledges the working input. A single-consumer lock protects each docs root. Retrying the same claimed delivery reuses its output destination; a new batch import is a new delivery.

Failures retain the complete original delivery and save a failure copy plus text report. Completed rejections do not automatically retry. Raw payloads are never published as a fallback. After correcting the cause, submit a new delivery. Logs contain stages and counts without decoded personal values.

## Verify

```powershell
dotnet build
src/.venv/Scripts/python.exe -m unittest discover -s tests/unit-tests -p test_*.py -v
src/.venv/Scripts/python.exe tests/unit-tests/test_feed_listener.py
dotnet run --project tests/FeedPipelineChecks
```

Verification separates actual-file results from synthetic success fixtures. Tests cover all-five-route success with explicit test-only contracts, actual source outcomes and checksums, local Presidio operators, field-by-field Seats protection, JSON/XDR round trips, malformed data, PNR preservation, failure retries, restart behavior, feed independence and shutdown draining. Test-only schemas are never production defaults.
