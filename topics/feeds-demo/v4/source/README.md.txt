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
routes remain ETKT, PNR, PNR Linking, Seats and ACI. Local folders simulate MQ.

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

## Local anonymization

.NET passes extracted text values and field names directly to Python.NET.
There is no JSON document conversion in the Presidio bridge. Presidio replaces
known sensitive fields in full, even when statistical detection misses them.
Names become `[PASSENGER_NAME]`, emails become `[EMAIL_ADDRESS]`, and personal
passenger IDs become `[PASSENGER_ID]`. These are irreversible shared replacement
labels with no token vault or recovery map. Other free-text fields are analyzed
locally; detection of unknown free text remains model-dependent.

PNR fields remain visible and unchanged. Non-text binary data stays under the
codec's control rather than passing through a text serializer. Replacement values
can have different encoded lengths, so a confirmed binary serializer is required.

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
restart behavior. The separate pipeline checks use a clearly labeled test-only
binary codec to exercise actual embedded Presidio, rebuilding, publishing and
successful-delivery deletion; that fixture codec is not registered by the app and
is not an airline schema.

Prior revisions are unchanged under `topics/feeds-demo/v1/`, `v2/` and `v3/`.
This revision's source and verification record are under `topics/feeds-demo/v4/`.