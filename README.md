# Solace Feed Listener Demo

A console POC that mimics incoming and outgoing Solace queues with local folders.
Run the app, copy JSON samples into the feed folders, and watch each delivery move
through its matching workflow. This phase validates JSON syntax and delivers the
original bytes unchanged. Feed parsing and local Presidio protection are the next phase.

## Run the stakeholder demo

Requires the .NET 10 SDK. Python, spaCy and a running Solace broker are not needed
for this listener phase. The existing Python.NET bridge is preserved for the future phase.

From the console project directory:

```powershell
dotnet run
```

The listener starts directly, creates missing queue folders, and displays `READY`.
There is no demo menu. Press Enter or Ctrl+C to stop gracefully.

In a second terminal, deliver a ticket message:

```powershell
Copy-Item docs/sample-docs/ETKT/ticket-issued.json docs/solace-feeds/ETKT/
```

Or copy all five samples into their matching feeds:

```powershell
foreach ($feed in @('ETKT', 'PNR', 'PNR-Linking', 'Seats', 'ACI')) {
    Copy-Item "docs/sample-docs/$feed/*.json" "docs/solace-feeds/$feed/"
}
```

The console shows `RECEIVED`, the workflow name, `VALIDATED`, `PUBLISHED`, and
`CONSUMED`. It displays filenames and delivery IDs, without printing payloads.
Open the matching output folder to see the copied JSON.

## Finalized requirements

| Requirement | Agreed behavior |
|---|---|
| Incoming feeds | ETKT, PNR, PNR Linking, Seats, ACI |
| Input layout | `docs/solace-feeds/<feed>/`; PNR Linking uses `PNR-Linking` |
| Trigger | Copy a `.json` file into a feed folder while the listener runs |
| Startup backlog | Also consume JSON already waiting when the app starts |
| Routing | The containing folder selects one of five separate workflow strategies |
| Current processing | Validate JSON syntax, then copy the original bytes unchanged |
| Outputs | `docs/output-feeds/<feed>/<original-stem>.<delivery-id>.json` |
| Repeated filenames | Each new arrival gets a unique output; existing outputs are preserved |
| Successful consumption | Delete the working input only after output has been fully saved |
| Failures | Preserve the file in `docs/failed-feeds/<feed>/` with a separate `.error.json` report |
| Samples | Reusable fictional examples in `docs/sample-docs/<feed>/`; never auto-consumed |
| App startup | Start the listener directly; the earlier demo menu is not exposed |
| Future processing | Feed parsing -> local Presidio through Python.NET -> outgoing feed delivery |

## Folder layout

```text
docs/
  sample-docs/       Reusable source samples, one folder per feed
  solace-feeds/      Incoming queues: ETKT, PNR, PNR-Linking, Seats, ACI
  output-feeds/      Outgoing queues with the same five folders
  failed-feeds/      Failed messages and error reports, with the same five folders
  .processing/      Claimed inputs retained until delivery or rejection completes
```

The sample payloads are illustrative airline messages, not production Solace schemas.
All five relate to the fictional booking `K7QX2M` and two distinct stable source
passenger IDs. PNR and all passenger values remain unchanged in this phase.
`feedType` in a sample is informative; routing always uses the containing folder.
The generated delivery ID identifies a file arrival, not a passenger. Recopying a
sample with the same payload `messageId` intentionally creates a new delivery.

## Design and reliability

- **Strategy:** `IFeedWorkflow` has ETKT, PNR, PNR Linking, Seats and ACI implementations.
  Their initial behavior is shared JSON validation; later each can implement feed-specific parsing. Each returns an output payload, which is unchanged in this phase and can become protected JSON in the next phase.
- **Router and constructor injection:** workflows are registered once at startup and selected by feed.
- **Transport adapter:** `IFeedTransport` separates delivery operations from workflow behavior.
  `FolderFeedTransport` implements the local queue mimic and can later be replaced by native Solace transport.
- **Listener orchestration:** `FolderFeedListener` coordinates receive, workflow, publish/reject, and acknowledge.

[FileSystemWatcher](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher?view=net-10.0)
notifications can repeat or be lost. The listener treats them as wake-up hints and
rescans every 500 ms, including at startup. A file must remain unchanged for at least
one second and be available for exclusive access before it is claimed. For a producer
that closes between chunks or pauses during copying, write with a `.tmp` extension
and rename to `.json` when complete; metadata stability alone cannot prove completion.
Files without a `.json` extension and nested input subfolders are ignored.
UTF-8 JSON with or without a BOM is accepted; validation has a maximum depth of 256.

A claimed file moves into `.processing/<feed>/<delivery-id>/`, where it remains until
its output or failed copy is committed. Outputs are written as `.pending` files and
renamed to `.json` only when complete. Persisted delivery IDs allow restart recovery
to reuse an already written output, including after a crash between publishing and
input deletion. This is a single-consumer POC, not a distributed queue or an exactly-once
business processing guarantee. A per-docs-root lock prevents two listener instances.
No global ordering across the five feeds is promised. Files are handled sequentially.

Processing errors, including invalid JSON or output-write failure, go to the failed
feed. Locked input files wait rather than being rejected. If saving a failure or
acknowledging a completed delivery fails, the input remains in `.processing` and retries
at five-second intervals. Fix the issue before stopping or restarting.

To show failure handling, save `{ invalid json` as a `.json` file and copy it into any
feed. Its content is preserved in that feed's failed folder, alongside an error report.
To retry a failed message, copy only its payload `.json` back into the input folder;
do not copy its `.error.json` report as a message.

For an isolated demo or test:

```powershell
dotnet run -- --docs-root D:/Bala_Support/feeds-demo-sandbox/docs
```

Runtime input/output/failure files and processing state are excluded from Git. Five
input folder placeholders and all samples are tracked. Output and failed folders are
created automatically.

## Verification

```powershell
dotnet build
py -3.12 tests/test_feed_listener.py
```

The end-to-end checks cover all five workflow routes, startup backlog, exact-byte
copying, input deletion, invalid JSON, repeated filenames, renamed and locked arrivals,
output-write failures, restart recovery, and single-consumer ownership. They use an
isolated temporary docs root and do not consume the stakeholder's samples or queues.

The earlier entry point and documentation are preserved under
`topics/feeds-demo/v1/source/`. Existing Python processing and historical review artifacts
are retained for the next phase.
