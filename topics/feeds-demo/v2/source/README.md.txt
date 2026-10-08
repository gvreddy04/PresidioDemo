# Presidio Feed Processing Demo

The console demo processes files through four stages:

**Parse the incoming feed -> local Presidio processing -> rebuild the original format -> publish to the output feed.**

The five feed routes are ETKT, PNR, PNR Linking, Seats and ACI. Local folders mimic
incoming and outgoing MQ queues. There is no live broker connection in this project.

## Run the demo

The project uses .NET 10 and the existing Python 3.12 virtual environment, including
`pythonnet`, `presidio-analyzer`, `presidio-anonymizer` and the installed local spaCy
`en_core_web_lg` model. Presidio runs inside the .NET process through Python.NET.
No HTTP calls or runtime model downloads are used. Install dependencies and the
model before starting the listener; missing dependencies fail startup.

```powershell
dotnet run
```

Startup loads the local NLP engines, then displays `READY`. Copy a fictional JSON
sample into its matching input feed:

```powershell
Copy-Item docs/sample-docs/ACI/passengers-checked-in.json docs/solace-feeds/ACI/
```

Or deliver all five JSON samples:

```powershell
foreach ($feed in @('ETKT', 'PNR', 'PNR-Linking', 'Seats', 'ACI')) {
    Copy-Item "docs/sample-docs/$feed/*.json" "docs/solace-feeds/$feed/"
}
```

The console shows `RECEIVED`, `WORKFLOW`, `PARSED`, `PROTECTED`, `SERIALIZED`,
`VALIDATED`, `PUBLISHED` and `CONSUMED`. Payload values are not printed. Open the
matching `docs/output-feeds/<feed>/` folder to inspect protected output.
Press Enter or Ctrl+C to stop gracefully.

For an isolated demo:

```powershell
dotnet run -- --docs-root D:/Bala_Support/feeds-demo-sandbox/docs
```

## Processing behavior

| Stage | Responsibility |
|---|---|
| Parse | .NET validates UTF-8 JSON, rejects duplicate keys and parses a JSON object |
| Resolve identity | .NET resolves a stable token scoped by tenant + PNR + source passenger ID |
| Protect | Embedded Presidio anonymizes values and inserts the resolved ID token through a custom operator |
| Rebuild | .NET serializes protected JSON and retains the input's UTF-8 BOM policy |
| Validate | Preserve PNRs, field types, property names, arrays and passenger grouping; check known sensitive fields and tokens |
| Publish | Atomically commit the protected file to its output feed, then acknowledge the input |
| Reject | Preserve the complete original input in the failed feed; never copy raw content to the output as fallback |

Names, email addresses, phone numbers, ticket numbers and other known sensitive
fields are replaced in full even when statistical detection misses them. Other
text fields, such as remarks, are analyzed by Presidio. Structured PNR fields
(`pnr`, `linkedPnr`, `oldPnr`, `newPnr`) stay unchanged. Identified operational
fields, such as tenant, message ID, flight, status and seat, retain their values.
Known sensitive values must be strings or null; an unsupported type rejects the
whole message. Free-text detection remains model-dependent and is not a claim
that every possible personal value will be detected.

For example, the fictional ACI sample has two passengers in PNR `K7QX2M`.
Their names become `[PASSENGER_NAME]`, their emails become `[EMAIL_ADDRESS]`,
and their source passenger IDs become two distinct `PAX-TOKEN-...` values.
Passenger order and booking association remain unchanged. Names and array
positions are never used to resolve passenger identity.

Retries, repeated arrivals and different feed types reuse an ID token within the
same tenant/PNR/passenger scope. A different tenant or PNR creates a different
token. The required tenant and PNR can be inherited from a containing object;
a passenger-level scope overrides the containing scope. Missing identity scope
rejects the message. Rebooking lineage is retained in the linking feed rather
than implying the same token across PNRs.

The JSON samples are illustrative feed contracts, not production airline schemas.
Rebuilding retains JSON structure and meaning apart from protected values; whitespace
and escaping can change. JSON payloads are not converted into an IBM airline binary
layout by this demo.

## IBM MQ binary sample

`docs/sample-docs/ACI/aci-mq-partial.txt` contains the earlier diagnostic sample
with its visible passenger name replaced by the fictional `CARTER/EMMA` in both
the hexadecimal bytes and readable column. Its likely PNR and MQ metadata are
retained. It is a partial name-sanitized fixture, not a fully anonymized feed.

Copy it into the ACI input folder to demonstrate rejection:

```powershell
Copy-Item docs/sample-docs/ACI/aci-mq-partial.txt docs/solace-feeds/ACI/
```

The parser checks dump row continuity and declared length, detects **560 of 920
payload bytes**, and quarantines the input with a clear error. `.bin` inputs and
complete MQ dumps also remain quarantined until the producer's binary schema is
configured. No unprotected or guessed binary output is emitted.

To enable binary round trips, supply the complete capture plus the producer's
schema or serializer/deserializer source. The future adapter must decode the
confirmed fields into the protection model, then rebuild the original byte order,
string byte lengths, padding, nested lengths and counts after anonymization.
XDR is a candidate based on the sample, not a confirmed schema. MQMD metadata
and application payload must be handled separately by a native broker transport.

## Transport and reliability

```text
docs/
  sample-docs/       Reusable fictional samples; never auto-consumed
  solace-feeds/      Inputs: ETKT, PNR, PNR-Linking, Seats, ACI
  output-feeds/      Protected outgoing feeds with the same five folders
  failed-feeds/      Original failed payloads and separate .error.json reports
  .processing/      Claimed inputs retained until an outcome is committed
  .demo-token-vault/ Encrypted prototype mappings and local key
```

The listener accepts `.json`, `.txt` and `.bin` filenames, including uppercase
extensions. JSON is the implemented processing format; TXT and BIN failures make
unsupported binary processing explicit. XML files and nested folders are ignored.
A delivery retains its original extension in output or quarantine and receives a
unique filename: `<original-stem>.<delivery-id><original-extension>`.

Notifications wake the listener; periodic scans recover startup files and missed
notifications. Files must be stable for one second and available for exclusive
access. Producers should write a `.tmp` file, close it, then rename it to the final
extension. Stability alone does not prove that a producer has finished writing.

One listener owns each docs root. Claimed inputs move into `.processing` and are
deleted only after output or quarantine has been flushed and atomically committed.
Output `.pending` files are hidden from consumers until renamed. Persisted delivery
IDs and stable tokens support restart recovery without creating a second output
for a committed delivery. Raw failures retain their original bytes. Failed writes
or acknowledgments leave processing state available for retry.

The prototype token store is owned by .NET and encrypts mappings with AES-GCM.
It commits a mapping before publishing protected output. Its key is stored locally
alongside the encrypted mappings; keep this folder private. This store is for the
single-listener local demo, not a production token vault, distributed lock or
transactional database/outbox implementation. A customer deployment needs a native
broker adapter and an organization-managed database repository selected for its
measured load and availability needs. No database engine is fixed by this demo.

## Verification

```powershell
dotnet build
.venv/Scripts/python.exe tests/test_feed_listener.py
```

The integration checks run the real .NET listener and embedded Presidio in isolated
folders. They cover all five routes, known-field protection, no original passenger
values in output, distinct and stable scoped tokens, changed PNR/tenant scopes,
JSON structure, BOM handling, restart recovery, acknowledgment, invalid feeds,
locked arrivals, output failures, quarantine retries, duplicate properties,
missing scope, unsupported sensitive types, truncated MQ capture and opaque binary
rejection. Prior source snapshots remain under `topics/feeds-demo/v1/source/`;
this revision's source and verification record are stored under `topics/feeds-demo/v2/`.