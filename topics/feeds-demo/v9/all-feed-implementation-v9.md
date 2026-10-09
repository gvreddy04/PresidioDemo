# All-feed implementation and actual-file results

**Actual-file processing remains partially blocked.** One supplied feed publishes successfully; four are retained without output. The code and tests distinguish these real outcomes from successful synthetic verification.

## Implemented

- A shared .NET listener pipeline for all five routes: parser, exact configured field identification, embedded local Presidio anonymization, serializer, validation, publication and acknowledgment.
- JSON codecs for Seats and PNR-Linking, with malformed-data rejection and safe error byte positions.
- Strict schema-driven XDR parsing and string serialization for ACI, PNR and ETKT, replacing the binary placeholder. Exact airline field contracts remain unconfigured because none are present in the supplied files/workspace.
- Source-folder batch processing, original-source checksums, unique imports, per-file outcomes, retained failures and machine-readable reports.
- No HTTP/REST PII processing or runtime model downloads.

## Run the actual folder

```powershell
dotnet run -- --process-folder D:/Bala_Support/Feeds
```

## Actual results

| Route | Outcome | Evidence / remaining requirement |
| --- | --- | --- |
| ACI | Rejected | Airline XDR field schema/parser definition is unavailable. Encoding rules alone do not identify passenger fields. |
| PNR-Linking | Rejected | JSON ends inside a string at payload byte 1,523. Requires complete original message content. |
| PNR | Rejected | 1,523-byte payload is not a complete four-byte-aligned standalone XDR stream. Requires complete source or framing definition, plus airline field schema. |
| Seats | Published | Protected output; all 59 values verified: 29 replaced and 30 unchanged. PNR, metadata, grouping and types preserved. |
| ETKT | Rejected | Airline XDR field schema/parser definition is unavailable. Encoding rules alone do not identify passenger fields. |

All original files in `D:/Bala_Support/Feeds` retain their original checksums. Rejected deliveries are saved byte-for-byte, with no successful output. No missing JSON content or airline schema has been fabricated.

## Verification

- Build passed with zero warnings/errors.
- Python unittest discovery: 10 tests passed.
- Listener regression checks passed.
- .NET JSON/XDR codec and pipeline checks passed.
- Synthetic all-five-route success test passed using explicitly test-only XDR definitions and complete fictional JSON. This proves implementation wiring; it does not establish the unavailable airline contracts.
- Real-file batch: one published, four rejected; every original checksum and retained input verified.

## Review files

- [Actual batch report](actual-feed-batch-v9.json) records source hashes and the live output/retained/error paths.
- [Readable protected SEATS payload](review/SEATS-protected-decoded-v9.json) is a decoded review artifact; runtime output retains the original MQ export format.
- [Protected SEATS MQ export](review/SEATS-protected-v9.txt) and [field verification](seats-field-check-v9.json).

## Information required to finish all actual outputs

The missing binary input is a field contract from the system that created the messages: `.x` definitions, record specifications, or matching generated parser/serializer code. It is not a request for the user to build an export application. PNR-Linking additionally requires the missing source bytes; PNR needs completeness/framing confirmation.
