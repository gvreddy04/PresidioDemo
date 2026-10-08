# Presidio Feed Processing Demo

## Finalized workflow

1. Receive the file from the input feed.
2. Parse the entire file.
3. Use local Presidio to anonymize personal details.
4. Rebuild the file in its original format with anonymized data.
5. Copy the file to the output feed.
6. Delete the input only after successful delivery.

There is no tokenization in this feed workflow. Personal values are replaced
irreversibly; no unique passenger tokens, identity mappings or token vault are used.
PNR remains visible and unchanged. The five feed routes are ETKT, PNR, PNR Linking,
Seats and ACI. Local folders simulate the input and output MQ feeds.

## Run the demo

The project uses .NET 10 and its existing Python 3.12 environment with `pythonnet`,
`presidio-analyzer`, `presidio-anonymizer` and the installed local spaCy
`en_core_web_lg` model. .NET embeds Python through Python.NET; Presidio runs locally
inside that process. Engines are initialized once and reused. No HTTP calls or
runtime model downloads occur in the processing flow.

```powershell
dotnet run
```

Wait for `READY`, then copy a fictional sample to its matching input folder:

```powershell
Copy-Item docs/sample-docs/ACI/passengers-checked-in.json docs/solace-feeds/ACI/
```

Or deliver all five JSON samples:

```powershell
foreach ($feed in @('ETKT', 'PNR', 'PNR-Linking', 'Seats', 'ACI')) {
    Copy-Item "docs/sample-docs/$feed/*.json" "docs/solace-feeds/$feed/"
}
```

Open `docs/output-feeds/<feed>/` to inspect the anonymized result. The console
shows processing stages, filenames and delivery IDs without printing payloads.
Press Enter or Ctrl+C to stop gracefully.

For an isolated demo:

```powershell
dotnet run -- --docs-root D:/Bala_Support/feeds-demo-sandbox/docs
```

## Parsing, anonymization and rebuilding

.NET reads the entire file and parses a complete UTF-8 JSON object. Duplicate
properties, trailing content and invalid JSON reject the entire delivery. UTF-8
JSON with or without a BOM is accepted; maximum nesting depth is 256.

Presidio processes extracted text values throughout the parsed document. Known
sensitive fields are protected in full even if statistical detection misses them.
For example, names become `[PASSENGER_NAME]`, emails become `[EMAIL_ADDRESS]`,
source passenger IDs become `[PASSENGER_ID]`, and ticket numbers become
`[TICKET_NUMBER]`. These are shared replacement labels, not unique tokens. There
is no recovery mapping. Passenger IDs can be anonymized without tenant/PNR identity
scope. Unsupported types in known sensitive fields reject the entire file.

Other text fields, such as remarks, are analyzed by Presidio. Free-text detection
remains model-dependent; the demo does not claim every possible personal value
will be detected. Structured PNR fields (`pnr`, `linkedPnr`, `oldPnr`, `newPnr`)
and defined operational fields such as flight, status and seat retain their values.

.NET rebuilds the JSON using the anonymized text values while retaining original
non-text values, including high-precision numbers. Field names, types, array
lengths, passenger grouping and BOM policy are validated before copying to output.
Whitespace and escaping can change when the file is rebuilt. The supplied JSON
samples are illustrative contracts, not confirmed production airline schemas.

## Delivery and failure handling

```text
docs/
  sample-docs/   Reusable fictional examples; never consumed automatically
  solace-feeds/  Input feeds: ETKT, PNR, PNR-Linking, Seats, ACI
  output-feeds/  Anonymized output feeds with the same five folders
  failed-feeds/  Copies of failed inputs and separate .error.json reports
  .processing/  Claimed inputs; failed inputs remain retained here
```

The listener accepts `.json`, `.txt` and `.bin` extensions, including uppercase.
JSON is the implemented processing format. TXT and BIN inputs with unconfigured
binary schemas are retained as failures. XML files and nested folders are ignored.
Delivery files keep their original extension and use a unique output name:
`<original-stem>.<delivery-id><original-extension>`.

Notifications trigger scans; periodic scans also recover startup files and missed
notifications. Files must be stable for one second and available exclusively before
claiming. Producers should write a `.tmp` file, close it, then rename it to the final
extension. Only one listener can own a docs root.

Claimed input files move to `.processing`. The output is flushed and atomically
renamed from `.pending` to its final name before the working input is deleted.
A failure never reaches that deletion step. The original input remains in
`.processing`, and a copy plus an error report is saved to `failed-feeds`. Once
that failure outcome is committed, it is skipped on subsequent scans and restarts.
If saving the failure outcome is interrupted, the listener retries while retaining
the input. To retry a quarantined message after correcting the cause, copy its
payload back to the input feed as a new delivery.

Persisted delivery IDs support restart recovery for a committed output whose input
acknowledgment was interrupted. Retained failed inputs are not automatically deleted.
Native broker connectivity is not configured; the current project uses folder-based
MQ simulation. A real broker adapter must acknowledge a successfully published
message according to the broker's delivery contract.

## IBM MQ binary sample

`docs/sample-docs/ACI/aci-mq-partial.txt` is the earlier diagnostic sample with the
visible passenger name replaced by the fictional `CARTER/EMMA` in both its hex bytes
and readable column. Its likely PNR and MQ metadata are retained. It is a partial,
name-sanitized fixture, not a fully anonymized feed.

```powershell
Copy-Item docs/sample-docs/ACI/aci-mq-partial.txt docs/solace-feeds/ACI/
```

The parser validates dump row continuity and detects that only **560 of 920 payload
bytes** are present. The input is retained and never copied to the output feed.
Complete MQ dumps and raw binary inputs also fail until their producer's schema is
configured. Implementing a binary round trip requires the complete capture and
producer schema or serializer/deserializer source. The adapter must parse the whole
message and rebuild its confirmed lengths, byte order, padding and nested records
with anonymized values. XDR remains a candidate, not a confirmed schema.

## Verification and revision history

```powershell
dotnet build
.venv/Scripts/python.exe tests/test_feed_listener.py
```

The integration checks use the actual .NET listener and embedded Presidio in isolated
folders. They cover the five routes, full-file parsing, known-field anonymization,
no minted tokens or vault, passenger grouping, PNR preservation, exact non-text
values, BOM handling, successful-delivery deletion, failed-input retention, output
failures, quarantine retries and restart recovery. The earlier delivered revisions
remain under `topics/feeds-demo/v1/` and `topics/feeds-demo/v2/`. The anonymization-only
revision's source snapshot, example output and verification record are under
`topics/feeds-demo/v3/`.