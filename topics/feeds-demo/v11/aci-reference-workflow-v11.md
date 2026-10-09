# ACI reference-format workflow

The actual supplied ACI.txt now completes the folder listener workflow: read the MQ export, decode the binary payload, validate the reviewed reference frame, select configured field actions, call local Presidio anonymization, restore the original MQ format, validate, publish and acknowledge the input.

The configuration maps field locations and actions ahead of processing. Known fields use direct anonymization; they do not require NLP detection. Rules match field paths rather than literal values. The user selected protection of the apparent passenger name and ambiguous identifier-like text, while retaining PNR.

## Configuration and output

- `src/config/reference-layouts.xml` pins the original ACI file checksum and explicitly declares 22 counted ASCII strings, split into 23 fields.
- `src/config/feed-policies.xml` defines six Known/Mask actions and 17 None/Keep actions for those exact field paths.
- `/PassengerName`, `/Text0144`, `/Text0152`, `/Text0160`, `/Text0840` and `/RecordReference/Prefix` are masked completely.
- `/pnr` and `/RecordReference/pnr` remain unchanged. The two copies must agree. The composite reference is split so its ambiguous prefix can be protected independently of the PNR suffix.
- Other text paths have explicit Keep rules. Neutral TextNNNN names identify original counted-string offsets; unconfirmed business meanings have not been invented.

[Protected original-format ACI output](aci-protected-reference-v11.txt) is an immutable copy of the published delivery. [Actual-run verification](actual-aci-verification.json) records its live output path and checksums. The [listener log](actual-aci-run.log) shows PARSED, POLICY, 23 fields / 17 kept / six known / zero analyzed, SERIALIZED, VALIDATED, PUBLISHED and CONSUMED.

The 888-byte payload remains the same size. Only bytes belonging to the six configured protected fields change. Count prefixes, padding, every unclassified binary byte, both PNR copies, MQ headers and the original container representation are preserved. Both the external source and finalized sample remain byte-identical to the originals. The successful working input was acknowledged; historical rejected deliveries remain intact.

Configuration can switch a field between Keep and Mask without a code change. A separate end-to-end test changes `/Text0152` to Keep and verifies five masks / 18 Keeps while the other protections continue. Another test uses different fictional names, identifiers and PNR values of the same lengths to prove policies do not match literal source values. Changes take effect on restart. Use `--reference-layout-file` and `--policy-file` for explicit overrides.

## Supported boundary

This is the original-reference implementation approved for this task, not a recovered general airline ACI schema. Fixed-offset access is enabled only after the original reference checksum and complete immutable frame have been verified. There is no runtime scan that guesses string or passenger boundaries.

Unknown numeric and binary regions must remain exactly equal to the reference. They are not semantically identified as operational or personal data. Different counts, flags, string lengths, padding, extra passengers, binary versions, truncation or trailing bytes are rejected before publication. Current direct Mask actions retain ASCII byte lengths; length-changing Replace/Redact actions are unsupported in this profile. A configured producer XDR schema takes precedence when a complete reviewed contract becomes available.

The supplied sample supports these concrete locations and a safe exact-frame edit. It cannot establish every possible airline field or guarantee complete PII coverage for other messages. Downstream consumer business validation has not been exercised; protection of ambiguous text fields is the explicitly selected policy. XDR counted-string encoding and padding are described in [RFC 4506](https://www.rfc-editor.org/rfc/rfc4506.html#section-4.11), which does not supply airline field meanings.

The current actual-feed batch tests now expect ACI and Seats to publish. PNR and ETKT still require complete reviewed definitions; PNR-Linking remains truncated. Synthetic five-route tests continue to use explicitly test-only XDR definitions.

## Validation

- Application build: zero warnings and errors.
- Python suite: 16 tests passed, including actual ACI protection, raw binary input, configuration override and unsupported-frame retention.
- Listener regression: all checks passed, including successful ACI publication/acknowledgment and unchanged recovery behavior.
- .NET harness: all checks passed, including reference round trips, exact allowed byte changes, PNR/header preservation, missing fields, invalid lengths/encoding, frame mutations, configuration bounds, checksum pinning, DTD rejection and producer-schema precedence.
- Original finalized samples and earlier source snapshots retain their verified checksums.
- `.gitattributes` preserves exact finalized MQ/fixture bytes across Git checkouts.

Full evidence: [test results](test-results.json), [verification summary](verification-summary.json), [Python test log](python-unit-tests.log), [listener test log](listener-integration.log), [.NET test log](dotnet-pipeline-checks.log), [pre-change manifest](previous-source-sha256.json) and [final source manifest](source-sha256.json).

The preceding source is preserved in previous-source/. The finalized source, configuration, tests and references are preserved in source/. Earlier delivered revisions remain untouched.
