# Remaining feed workflows

ETKT now uses the same explicit, configuration-driven reference approach as ACI. Complete PNR-Linking delivery has dedicated end-to-end verification. The actual PNR and PNR-Linking source files still cannot produce validated outputs because their full boundaries are unavailable.

## Actual file outcomes

| Feed | Actual result | Verified behavior |
| --- | --- | --- |
| ACI | Published and acknowledged | Six direct Mask actions, 17 Keeps; PNR, 888-byte binary frame and MQ metadata preserved |
| ETKT | Published and acknowledged | Five direct Mask actions, four Keeps; PNR, status text, 672-byte frame and MQ metadata preserved |
| Seats | Published and acknowledged | 29 configured Replace actions, 30 Keeps; JSON shape/types/order, passenger grouping and PNR preserved |
| PNR | Retained without output | Original 1,523 bytes remain unchanged; payload is not a complete standalone XDR stream under the configured interpretation |
| PNR-Linking | Retained without output | Original JSON ends inside a property name; no tail or passenger fields were invented |

All five originals were run through the normal listener. The batch returns application exit code 2 because two actual messages remain rejected. A passing verification suite does not mean five actual messages published. See [the original batch report](actual-feed-batch.json), [field/output verification](actual-feed-verification.json), and [actual listener log](actual-feed-run.log).

Published original-format review copies: [ETKT](ETKT-protected-reference-v12.txt), [ACI](ACI-protected-reference-v12.txt), and [Seats](Seats-protected-reference-v12.txt). Live paths are recorded in the verification JSON.

## ETKT configuration

`src/config/reference-layouts.xml` pins the unchanged ETKT reference SHA-256 and declares eight counted ASCII strings, split into nine exact field paths. `/Text0044`, `/Text0072`, `/Text0088`, `/Text0152`, and `/Text0172/Prefix` use Known/Mask policies. `/pnr`, `/Text0012`, `/Text0024`, and `/Text0172/Remainder` use None/Keep policies.

The composite text's first eight characters are protected separately from its retained status remainder. Neutral TextNNNN names refer to original counted-string offsets because business meanings are unconfirmed. ETKT is the project route; external TKT.txt remains the explicitly confirmed input alias.

Every configured action applies directly without NLP detection. Same-length Mask or Replace actions preserve all counted-string lengths, padding and offsets. Unknown binary words must match the reviewed reference exactly; they have not been semantically identified as operational or personal fields. Different layouts, versions, counts, lengths, flags or truncation are rejected. This is a restricted reference-frame adapter, not a general airline ETKT producer schema. Downstream business acceptance has not been exercised, and complete PII coverage for other messages is not claimed.

The implementation reuses ReferenceLayoutFeedCodec rather than introducing another binary parser. Shared Python test helpers now verify allowed byte changes for both ACI and ETKT. General configured XDR producer schemas still take precedence over the reference adapter.

## Complete PNR-Linking workflow

PNR-Linking already uses JsonFeedCodec and exact field policies. Dedicated tests now prove a complete two-passenger MQ export passes parsing, configured protection, original-format serialization, validation, publication and acknowledgment. They also verify that unconfigured sensitive fields and the original truncated export remain retained without output.

A clearly fictional complete input is preserved in fictional-inputs/PNR_LINKING.txt. Its [protected review output](PNR-Linking-fictional-protected-v12.txt), [verification](fictional-linking-verification.json), and [listener log](fictional-linking-run.log) demonstrate 22 scalar values under 15 Replace policies and seven Keep policies. Null personal fields remain null. PNR and passenger grouping remain unchanged. This fixture is not presented as a recovered or repaired actual source message.

## Input still required

The actual PNR payload contains 380 complete four-byte units and three trailing zero bytes. The last recognized counted string finishes at payload byte 1,520, after which only three bytes remain. Under a standalone XDR interpretation this is incomplete; custom framing is another unconfirmed possibility. Adding a zero byte would not establish message completeness or the remaining field definitions.

The actual PNR-Linking message ends inside the second customer's property name `UaRecordLoca`, whose opening quote is at payload byte 1,510. Closing quotes/brackets or inventing missing values would manufacture data. [Incomplete-source evidence](incomplete-source-evidence.json) records the exact boundaries and source checksums without decoded passenger values.

A single clarification was requested for the location of complete original PNR and PNR-Linking messages. Until those inputs or confirmed PNR framing are available, successful actual-file output for these two feeds remains unfinished. Existing original files, rejection copies, error reports and historical revisions remain intact.

## Verification

- Build succeeded with zero warnings and zero errors.
- All 23 Python tests passed, including new ETKT and complete/truncated PNR-Linking coverage.
- All listener regression checks passed, including actual ACI, ETKT and Seats publication and unchanged failure/restart behavior.
- All .NET pipeline checks passed, including ETKT exact round trips, allowed byte changes, PNR preservation, length constraints, flags and schema precedence.
- Full original-feed run: three validated publications and two intact retained failures.
- Separate complete fictional PNR-Linking run: published and acknowledged successfully.
- Finalized sample bytes still match the original external feeds, and the agreed four docs folders remain intact.

Evidence: [test results](test-results.json), [summary](verification-summary.json), [Python log](python-unit-tests.log), [listener log](listener-integration.log), and [.NET log](dotnet-pipeline-checks.log).

Prior source is preserved in previous-source/ and verified by previous-source-sha256.json. Final source/configuration/tests/references are preserved in source/ and verified by source-sha256.json. Earlier delivered versions were not modified.
