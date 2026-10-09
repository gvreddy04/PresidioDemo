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
| ACI | ACI.txt | Binary with strong XDR evidence | Protected and published using the approved 888-byte reference-layout profile |
| PNR | PNR.txt | Binary with strong XDR evidence | Retained; 1,523 bytes is not aligned as a complete standalone XDR stream; schema also unavailable |
| ETKT | TKT.txt | Binary with strong XDR evidence | Protected and published using the approved 672-byte reference-layout profile |

The actual-file run now produces **three successful protected deliveries: ACI, ETKT and Seats**. ACI and ETKT use restricted configurations for their supplied original reference frames; these are not general airline producer schemas. The actual PNR and PNR-Linking files still lack complete or confirmed framing needed to publish. A separate test proves all five routes can publish using explicitly test-only XDR schemas and complete fictional JSON fixtures. Test schemas are never registered for the actual feeds.

The project uses **ETKT** throughout, as confirmed. The supplied `TKT.txt` filename is an explicit source alias for ETKT. Routing inside the listener uses its feed folder. Historical examples remain preserved.

See the [current feed implementation and verification](../../../topics/feeds-demo/v12/remaining-feed-workflows-v12.md), [earlier ACI verification](../../../topics/feeds-demo/v11/aci-reference-workflow-v11.md), the [earlier all-feed implementation](../../../topics/feeds-demo/v9/all-feed-implementation-v9.md), and [format analysis](../../../docs/feed-analysis.md).

## Run all actual files once

From the project root:

```powershell
dotnet build
dotnet run -- --process-folder D:/Bala_Support/Feeds
```

This reads the actual source folder, copies recognized `.txt`/`.bin` files into `docs/input-feeds/<route>`, and runs those copies through the normal listener. It preserves originals. Each import has a unique run identifier. It exits after every import reaches a published/acknowledged or retained-failure outcome, or after a two-minute outcome timeout. A pending result does not count as success.

- Protected output: `docs/output-feeds/<route>/`
- Failed copies and `.error.txt` reports: `docs/failed-feeds/<route>/`
- Retained original deliveries: `.runtime/docs/processing/<route>/`
- Per-source outcome report with checksums and output/error paths: `.runtime/docs/reports/feed-batch-<run-id>.json`

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

After `READY`, copy a source into the corresponding `docs/input-feeds/<route>` folder. For the finalized Seats sample:

```powershell
Copy-Item docs/sample-docs/SEATS.txt docs/input-feeds/Seats/
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
| System.Security.Cryptography | Framework library | Pin the original reference file with SHA-256 |
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
      reference-layouts.xml
    python/
      presidio_demo.py
    feeds/
      FeedContracts.cs
      FeedFormats.cs
      JsonFeedCodec.cs
      XdrFeedCodec.cs
      ReferenceLayoutFeedCodec.cs
      FeedBatchProcessor.cs
      FeedWorkspacePaths.cs
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
      test_aci_feed.py
      test_etkt_feed.py
      test_pnr_linking_feed.py
      reference_feed_support.py
      test_presidio_rules.py
      test_seats_feed.py
      test_all_feed_batch.py
      test_feed_listener.py
      test_project_layout.py
      fixtures/             Fictional and malformed regression inputs
    FeedPipelineChecks/      .NET integration support
  docs/
    sample-docs/             Finalized TXT files only; no subfolders
      ACI.txt
      ETKT.txt
      PNR.txt
      PNR_LINKING.txt
      SEATS.txt
    input-feeds/             ACI, ETKT, PNR, PNR-Linking, Seats
    output-feeds/            ACI, ETKT, PNR, PNR-Linking, Seats
    failed-feeds/            ACI, ETKT, PNR, PNR-Linking, Seats
    feed-analysis.md
  .runtime/docs/             Recovery state, listener lock and batch reports
  topics/feeds-demo/          Immutable prior revisions and verification
```

Each exchange folder contains exactly the five route folders shown above. `sample-docs` is a flat reference collection and is never watched. Its ETKT.txt is a byte-identical copy of the external TKT.txt, under the confirmed project name.

For any `--docs-root <parent>/<name>`, internal state lives at `<parent>/.runtime/<name>/`: `processing/<route>/` retains claimed deliveries, `reports/` holds batch reports, and `listener.lock` prevents concurrent consumers. `.runtime` is ignored by Git and excluded from Solution Explorer. Keep this state across restarts. Different docs roots have separate state and locks.

The redundant `docs/input`, `docs/output`, `docs/solace-feeds`, old nested samples and demonstration output were removed from the active tree after checksum-verified preservation in [the cleanup revision](../../../topics/feeds-demo/v10/folder-cleanup-v10.md). Existing real output and failed feeds remain in their route folders. Prior revisions remain intact. Build folders (`bin`, `obj`), the Python environment, Visual Studio state and test support remain in use.

An older workspace still containing `docs/.processing` rejects startup until its retained deliveries are migrated to the matching runtime processing folder; it never silently abandons those files. The default workspace has already been migrated.

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

Seats verification covers all 59 supplied scalar values across 39 configured paths: 29 replacements and 30 unchanged values. Names, PaxId, OrderId, EmpId and sensitive passenger attribute entries are replaced. PNR, seats, flights and operational metadata remain unchanged. The supplied sample has customers in Old and an empty New group; different shapes require reviewed policies. Downstream consumers must accept configured replacement labels. See the [field-by-field SEATS verification](../../../topics/feeds-demo/v8/seats-completion-v8.md).

The demo uses irreversible replacement labels. It does not issue stable passenger tokens or provide recovery mappings. PNR remains linkable. Reversible tokenization would need a separately implemented tenant + PNR + stable source passenger ID mapping service and storage.

## ACI reference-layout workflow

The supplied `docs/sample-docs/ACI.txt` is the original-format reference. `src/config/reference-layouts.xml` pins its file checksum and explicitly describes 22 counted ASCII strings, split into 23 fields. The codec reads the MQ hex container, validates the complete 888-byte frame, extracts the configured fields, applies local Presidio actions, restores the original container, validates the result and publishes before acknowledging input.

`src/config/feed-policies.xml` decides which fields to keep or protect. Rules bind to field paths rather than literal input values. Every extracted field requires a rule. All current ACI rules use None/Keep or Known/Mask: known fields bypass NLP detection. The model still initializes for other routes that use Analyze.

| Configured ACI field | Action | Meaning / constraint |
| --- | --- | --- |
| `/PassengerName` | Mask all 11 characters | Apparent name field in the supplied reference |
| `/Text0144`, `/Text0152`, `/Text0160`, `/Text0840` | Mask the complete field | User-approved protection for ambiguous identifier-like strings; business names remain unconfirmed |
| `/RecordReference/Prefix` | Mask all nine characters | Protect the ambiguous prefix of the composite reference |
| `/pnr`, `/RecordReference/pnr` | Keep | Preserve both six-character PNR copies and require them to agree |
| Other 15 configured text fields | Keep | Explicit reference policy; their business semantics are not claimed as a producer contract |

```xml
<field name="/PassengerName" identification="Known" entity="PERSON"
       action="Mask" maskCharacters="11" maskCharacter="*" />
<field name="/pnr" identification="None" action="Keep" />
```

Finalized MQ references, TXT fixtures and archived revision files are marked `-text` in `.gitattributes` so Git preserves their original bytes and checksums.

The result has six masks and 17 Keeps. Masking retains every field's ASCII byte length, every counted-string length and pad, all unknown binary bytes and MQ metadata. Length-changing replacements/redaction are rejected by this profile. Same-length values can change without changing the configuration; changed counts, flags, unknown words, lengths, padding, extra passengers, truncation, trailing bytes and inconsistent PNR copies are retained for review. This profile supports only the approved reference frame, not arbitrary ACI messages. Numeric and unknown binary regions are frozen to the reference and are not presented as semantically identified or fully classified PII fields.

A real producer schema, when configured, takes precedence over the reference profile. To override reference configuration, pass `--reference-layout-file <xml-file>`. Changes take effect on application restart. Modifying the reference file requires a deliberate checksum/profile review.

To process the original ACI reference through the listener, start `dotnet run`, wait for READY, then copy it from a second terminal:

```powershell
Copy-Item docs/sample-docs/ACI.txt docs/input-feeds/ACI/
```

Previously rejected deliveries remain retained. Submitting a fresh copy creates a new delivery under the new configuration; historical failures are not automatically retried.

## ETKT reference-layout workflow

ETKT now uses the same strict reference codec as ACI. Its configuration pins the unchanged original `docs/sample-docs/ETKT.txt` and declares eight counted strings split into nine fields in the 672-byte payload. The external source name TKT.txt remains the confirmed ETKT input alias.

| ETKT field | Action | Reference policy |
| --- | --- | --- |
| `/Text0044`, `/Text0072`, `/Text0088`, `/Text0152` | Mask | Protect ambiguous identifier-like text without inventing business meanings |
| `/Text0172/Prefix` | Mask | Protect the first eight characters of the composite text |
| `/Text0172/Remainder` | Keep | Retain the remaining status text and its spacing |
| `/pnr` | Keep | Preserve the six-character record locator |
| `/Text0012`, `/Text0024` | Keep | Explicit Keep rules for the observed date/time text |

Five Known/Mask actions and four None/Keep actions bypass NLP detection. Exact-byte tests verify every changed and unchanged byte, PNR, status text, count prefixes, padding, MQ metadata and acknowledgment. Only the reviewed frame is accepted: different unknown words, lengths, counts or versions are retained, and numeric/opaque regions have not been semantically classified. General producer schemas retain precedence.

```powershell
Copy-Item docs/sample-docs/ETKT.txt docs/input-feeds/ETKT/
```

## PNR and PNR-Linking input readiness

The finalized originals remain unchanged. The actual PNR payload contains 1,523 bytes: 380 complete four-byte units and three trailing zero bytes. It is not a complete standalone XDR stream under the current interpretation; incomplete input or producer-specific framing must be resolved before enabling a reviewed full-frame layout. Appending one zero byte would not establish that all records are present.

The actual PNR-Linking payload ends inside the property name `UaRecordLoca` in its second customer object. The JSON message cannot be completed reliably from the file. Its configured parser, direct field actions, serializer, validation, output and acknowledgment work for complete messages, verified using a separate fictional two-passenger MQ export in `tests/unit-tests/fixtures/pnr-linking-mq-export.txt`. That fixture is not represented as a recovered original.

Both actual inputs remain retained with error reports. Their complete original messages are required for successful actual-file verification. The [input evidence](../../../topics/feeds-demo/v12/incomplete-source-evidence.json) records boundaries and checksums without decoded personal values.

## Schema-driven XDR codecs

XdrFeedCodec handles configured producer schemas for ACI, PNR and ETKT. `src/config/xdr-layouts.xml` marks these general producer contracts as unavailable. The approved ACI and ETKT reference profiles are separate and explicitly restricted to the supplied frames; they do not fill in airline-wide schemas.

The parser supports bounded ASCII strings, signed/unsigned 32/64-bit integers, bit-preserved float32/float64, Booleans, fixed/variable opaque data, structures, fixed/counted arrays, optionals and signed-int32-discriminated unions. Layout files declare exact field order, bounds and optional expected integer constants. Every scalar receives an exact path requiring its own protection policy. Padding, bounds, constants, discriminators, total payload consumption and output structure are validated.

The serializer recalculates changed string lengths and zero padding. It preserves array counts, optional/union selection, untouched fields, opaque bytes, numeric bits, MQ metadata and PNR. Non-ASCII strings, unsupported primitive protection, unknown types, incomplete/trailing bytes and schema mismatches reject the entire message. This is a supported subset of XDR; producer-specific encodings, framing or additional types require an explicit adapter. Opaque or numeric PII cannot silently bypass a protection action.

To enable a real binary feed, obtain its field contract or generated parser/serializer from the system that created the message. Convert the definitions to the supported layout format, configure every exact field policy, and verify producer round-trip fixtures. No export application needs to be implemented by the user. Complete PNR-Linking source data must come from the original message; the missing tail cannot be reconstructed from this file.

Override configuration paths as needed:

```powershell
dotnet run -- --process-folder D:/Bala_Support/Feeds --policy-file src/config/feed-policies.xml --xdr-layout-file src/config/xdr-layouts.xml
```

## Delivery behavior

One active delivery per feed permits independent progress. Shared Python calls are guarded between fields. Shutdown drains workers before releasing the transport and Python runtime.

Producers should close a `.tmp` file before renaming it to `.txt` or `.bin`. The listener waits for stable, exclusively readable files, claims them into `.runtime/<docs-name>/processing`, runs the full pipeline, flushes output to `.pending`, atomically renames it, then acknowledges the working input. A single-consumer lock protects each docs root. Retrying the same claimed delivery reuses its output destination; a new batch import is a new delivery.

Failures retain the complete original delivery and save a failure copy plus text report. Completed rejections do not automatically retry. Raw payloads are never published as a fallback. After correcting the cause, submit a new delivery. Logs contain stages and counts without decoded personal values.

## Verify

```powershell
dotnet build
src/.venv/Scripts/python.exe -m unittest discover -s tests/unit-tests -p test_*.py -v
src/.venv/Scripts/python.exe tests/unit-tests/test_feed_listener.py
dotnet run --project tests/FeedPipelineChecks
```

Verification separates actual-file results from synthetic success fixtures. Tests cover ACI/ETKT reference protection, complete PNR-Linking MQ delivery, configurable Keep/Mask decisions, strict reference-layout rejection, all-five-route success with explicit test-only contracts, actual source outcomes and checksums, local Presidio operators, field-by-field Seats protection, JSON/XDR round trips, malformed data, PNR preservation, failure retries, restart behavior, feed independence and shutdown draining. Test-only schemas are never production defaults.
