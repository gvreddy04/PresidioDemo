# Presidio Binary Feed Processing Demo

## Finalized workflow

1. Receive the file from the input feed.
2. Parse the entire file using its confirmed producer layout.
3. Use local Presidio to anonymize personal details.
4. Rebuild the file in its original format with anonymized data.
5. Copy the file to the output feed.
6. Delete the input only after successful delivery.

There is no JSON feed support and no tokenization. The listener accepts IBM MQ
hexadecimal diagnostic dumps (`.txt`) and raw binary payloads (`.bin`). The five
routes remain ETKT, PNR, PNR Linking, Seats and ACI. Local folders simulate MQ. Each feed has its own strategy, codec registration and
field policy; one active delivery per feed allows independent progress.

**The producer-specific binary codec is not configured.** This revision removes
the JSON path and provides the binary codec integration contract. It does not
claim that the airline payload has been decoded, anonymized or rebuilt. Unsupported
or incomplete messages are retained and never copied to an output feed.

## Run

The project uses .NET 10 and the existing local Python 3.12 environment with
Python.NET, `presidio-analyzer`, `presidio-anonymizer` and preinstalled spaCy
`en_core_web_lg`. Presidio is embedded inside the .NET worker. No HTTP calls,
remote processing, runtime model downloads or identity mappings are used.

```powershell
dotnet run
```

After `READY`, copy the name-sanitized sample into the ACI input folder:

```powershell
Copy-Item docs/sample-docs/ACI/aci-mq-partial.txt docs/solace-feeds/ACI/
```

The sample contains only **560 of the declared 920 bytes**. It produces a text
failure report and retained input, with no output delivery. Its visible name uses
the fictional `CARTER/EMMA`; PNR and MQ metadata were retained. It is a partial
name-sanitized diagnostic fixture, not a complete or fully anonymized feed.

Press Enter or Ctrl+C to stop. Use a separate docs root for an isolated run:

```powershell
dotnet run -- --docs-root D:/Bala_Support/binary-feed-sandbox/docs
```

JSON files, XML files and nested input folders are ignored and left untouched.
Earlier JSON sample files remain as historical examples; they are not supported
inputs or suggested demo deliveries.

## Enable the producer-specific codec

Supply the complete raw message or complete MQ dump together with the producer's
schema or serializer/deserializer source. XDR remains a possible representation
based on the sample, not a confirmed schema.

`IBinaryFeedCodec` defines the required integration:

- `Parse`: consume the entire file/payload, decode all records and retain the binary
  layout and opaque data needed to rebuild it. Return extracted text fields with
  field names and unique locations within that message.
- `Rebuild`: write anonymized values back in the original format, recalculating
  encoded byte lengths, padding, counts and enclosing record lengths as required.
- `Validate`: check the rebuilt message using the confirmed schema, including PNR
  preservation, passenger grouping and data that should remain unchanged.

Field locations are transient positions within a parsed file, not passenger
identity tokens. The default `UnconfiguredBinaryFeedCodec` always fails closed.
Replace it with confirmed feed-specific codecs when their layouts are available.
The application does not infer a schema from visible names or byte offsets.

`BinaryFeedInput` reconstructs payload bytes from a single MQ diagnostic dump and
checks offsets and declared length. It reads raw BIN files without text conversion.
Diagnostic metadata and application payload are separate; a configured codec must
preserve/update the diagnostic representation if rebuilding a TXT input, or write
raw payload bytes for a BIN input. An actual MQ adapter must handle MQMD separately
and use native broker clients. No live broker is configured here.

## Feed strategies and field configuration

`FeedWorkflowRouter` selects `EtktWorkflow`, `PnrWorkflow`, `PnrLinkingWorkflow`,
`SeatsWorkflow` or `AciWorkflow`. Each strategy receives its own `IBinaryFeedCodec`
and `FeedPolicy`. All strategies share the six-step safety sequence, while their
producer layouts and field rules can differ. Register the confirmed codec for each
feed through `FeedWorkflowRouter.Create`'s codec factory; the default still rejects
all feeds with `UnconfiguredBinaryFeedCodec`.

Edit `config/feed-policies.xml` to control each feed. It is a **starter policy**, not
an assertion about actual airline fields. Align each rule name with the canonical
field name returned by its producer codec. Multiple passengers can have the same
field name; distinct within-message field locations keep their values separate.
Policies are exact names, compared without case sensitivity; there are no wildcard
or name-guessing fallbacks. An unconfigured field rejects the entire message.

For each field, configure identification separately from the action:

| Identification | Behavior | Typical use |
| --- | --- | --- |
| `None` | Bypass Presidio; requires `Keep` | PNR, flight, seat |
| `Known` | Full-field entity span with score 1; skips NLP detection | Name, email, passport, passenger ID |
| `Analyze` | Local detection restricted to `entities` and `threshold` | Remarks and other free text |

| Action | Settings | Result |
| --- | --- | --- |
| `Keep` | `identification="None"` | Original value |
| `Replace` | Explicit `replacement` | Replacement of the whole known field or each detected span |
| `Mask` | `maskCharacter`, positive `maskCharacters`, `fromEnd` | Mask selected characters in the known field or detected spans |
| `Redact` | No operator settings | Remove the known value or detected spans |

```xml
<field name="pnr" identification="None" action="Keep" />
<field name="name" identification="Known" entity="PERSON"
       action="Replace" replacement="[PASSENGER_NAME]" />
<field name="phone" identification="Known" entity="PHONE_NUMBER"
       action="Mask" maskCharacter="*" maskCharacters="6" fromEnd="true" />
<field name="passport" identification="Known" entity="PASSPORT_NUMBER" action="Redact" />
<field name="remarks" identification="Analyze" entities="PERSON,EMAIL_ADDRESS,PHONE_NUMBER"
       threshold="0.5" action="Replace" replacement="[PERSONAL_DETAIL]" />
```

Known sensitive fields are normally configured with full replacement or redaction.
Masking can leave part of a value visible. An `Analyze` field with no detections
remains unchanged; statistical detection is not a substitute for `Known` when the
field is known to contain personal data. PNR, linked/old/new PNR rules must use
`Keep`; configuration cannot override that requirement. Known personal fields whose
action leaves the original value unchanged are rejected before rebuilding.

Configuration is validated once at startup before inputs are claimed. Missing feed
policies, duplicate field rules, unsupported actions, invalid parameters and XML
external entities are rejected. Changes take effect on restart. A custom file can
be selected alongside the docs root:

```powershell
dotnet run -- --docs-root D:/Bala_Support/binary-feed-sandbox/docs --policy-file config/feed-policies.xml
```

XML is used only for configuration; XML/JSON feed inputs remain unsupported. There
is no JSON conversion, tokenization, token vault or recovery mapping. Names and
emails use fictional labels, and source IDs use `[PASSENGER_ID]` by default.
Replacement labels are shared irreversible values rather than unique tokens.

## Performance and local execution

Kept fields do not enter Python. Known fields call the Presidio anonymizer without
running detection; only `Analyze` fields invoke the analyzer. .NET passes ordinary
strings and policy arguments directly through Python.NET into embedded CPython.
The runtime, model and engines initialize once and are reused without remote calls
or model downloads. PNR stays unchanged; binary data remains under the codec.

The listener allows **one active delivery per feed**, up to five active deliveries
in total. A slow parse, rebuild or output copy in one feed does not hold the whole
listener scan. Files waiting for a busy feed remain on disk. The shared Presidio
engines are protected by a gate released between fields; Python operations are
serialized. A long individual NLP call can delay other Python calls. This is not
parallel NLP inference; sustained heavy detection may require separate worker
processes after measurements. Per-feed logs show field counts, kept/known/analyzed
counts, protection time (including gate wait), and parse/rebuild time without values.
Shutdown drains workers before disposing transport or embedded Python.

## Delivery and failures

```text
docs/
  sample-docs/   Source fixtures; never consumed automatically
  solace-feeds/  Input folders for the five routes
  output-feeds/  Anonymized output folders
  failed-feeds/  Failed input copies and .error.txt reports
  .processing/  Claimed inputs; unsuccessful deliveries remain retained
```

The listener claims stable, exclusively readable TXT/BIN files and preserves their
original extension. A producer should write a `.tmp` file, close it, then rename
it to its final extension. Outputs are flushed to `.pending` and atomically renamed
before the working input is deleted. Only one listener can own a docs root.

If parsing, anonymization, rebuilding, validation or copying fails, the working
input is retained in `.processing`. A copy and text error report are saved to
`failed-feeds`. Completed failure outcomes are skipped on later scans and restarts.
Interrupted failure writes retry while retaining the input. To retry after correcting
the cause, copy the failed payload back into the input feed as a new delivery.
JSON error reports are no longer produced. Existing historical files are preserved.

## Verification and history

```powershell
dotnet build
.venv/Scripts/python.exe tests/test_feed_listener.py
dotnet run --project tests/FeedPipelineChecks
```

Listener checks verify ignored JSON inputs, binary routing, full-dump recovery,
partial/malformed dumps, retained failures, text reports, quarantine retries and
restart behavior. The pipeline checks also verify XML policy rejection, configured field actions,
feed isolation and worker draining. They use a clearly labeled test-only
binary codec to exercise actual embedded Presidio, rebuilding, publishing and
successful-delivery deletion; that fixture codec is not registered by the app and
is not an airline schema.

Prior revisions are unchanged under `topics/feeds-demo/v1/`, `v2/` and `v3/`.
Previous binary-only source and verification remain under `topics/feeds-demo/v4/`.
The configurable feed strategies revision is under `topics/feeds-demo/v5/`.