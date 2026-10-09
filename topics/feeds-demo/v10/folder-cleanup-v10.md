# Feed folder cleanup

The active docs tree now contains only the three exchange roots, the flat finalized sample collection, and feed-analysis.md.

```text
docs/
  input-feeds/      ACI, ETKT, PNR, PNR-Linking, Seats
  output-feeds/     ACI, ETKT, PNR, PNR-Linking, Seats
  failed-feeds/     ACI, ETKT, PNR, PNR-Linking, Seats
  sample-docs/      ACI.txt, ETKT.txt, PNR.txt, PNR_LINKING.txt, SEATS.txt
  feed-analysis.md
```

All three exchange roots have the same five immediate route folders, with tracked placeholders. Existing real output and failure files remain. The external source folder was not modified; every finalized sample remains byte-identical to its source. ETKT.txt retains the confirmed project naming for the external TKT.txt.

| Previous location | Action and reason |
| --- | --- |
| docs/input | Removed obsolete JSON/XML examples after preservation |
| docs/output | Removed obsolete generated JSON output after preservation |
| docs/solace-feeds | Removed unused alternate inbox tree after preservation |
| docs/sample-docs/<route> and finalized-feeds | Removed nested directories; retained only the five finalized TXT files directly in sample-docs |
| Fictional Seats export and partial ACI diagnostic dump | Moved regression fixtures into tests/unit-tests/fixtures |
| docs/output-feeds/Seats/seats-protected.txt | Removed obsolete fictional demo output after preservation; kept the actual protected Seats delivery |
| docs/.processing | Migrated all retained deliveries and rejection markers to .runtime/docs/processing |
| docs/batch-runs | Migrated reports to .runtime/docs/reports and updated live report paths |
| docs/.feed-listener.lock | Replaced with .runtime/docs/listener.lock |

FeedWorkspacePaths centralizes paths used by the listener and batch runner. A custom docs root <parent>/<name> uses <parent>/.runtime/<name>. State and test workspaces are ignored by Git and excluded from Solution Explorer. The transport rejects startup when legacy .processing deliveries remain, so upgrading a different workspace cannot silently abandon them.

The current migration acquired both listener locks exclusively. Deletion and move targets were checked to remain inside the project. Each removed file was compared with its preservation copy before removal. All 78 files in the pre-cleanup snapshot were subsequently verified against before-cleanup-sha256.json. Existing revisions were left intact.

Retained folders: src and its Python environment, tests and their fixtures/.NET harness, docs, version history in topics, generated bin/obj, Visual Studio state in .vs, temporary test support in .test-runs, internal delivery state in .runtime, and repository metadata in .git. These folders serve active development, recovery or preservation purposes.

## Verification

- Application build succeeded with zero warnings and zero errors.
- Python unittest discovery: 12 tests passed, including flat samples and exact route layout checks.
- Listener regression suite: all checks passed, covering restart, complete-message retention, retry, output acknowledgment and actual supplied inputs.
- .NET pipeline harness: all checks passed, including exclusive locking, independent runtime roots, legacy-state protection, JSON/XDR codecs and local Presidio.
- All five samples match D:/Bala_Support/Feeds exactly, with TKT.txt mapped to ETKT.txt.
- Migrated reports resolve to existing retained originals, error reports and successful output.

Full evidence: [test results](test-results.json), [verification summary](verification-summary.json), [Python tests](python-unit-tests.log), [listener checks](listener-integration.log), and [.NET checks](dotnet-pipeline-checks.log).

These passing tests do not imply that all five actual feeds can publish. Actual Seats succeeds; ACI and ETKT still require producer schemas, PNR has an incomplete/unframed XDR candidate and no schema, and PNR-Linking is truncated. Those four messages are deliberately retained without successful output. Synthetic all-route success tests use explicitly test-only schemas and complete fictional messages.

The prior source and removed content are preserved in previous-source/. The finalized active source is preserved in source/; checksum manifests accompany both revisions.
