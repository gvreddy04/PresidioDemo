# Presidio Feed Protection Demo

Process airline feeds from a message broker using configured field rules and local Presidio. PNR stays unchanged. Output is validated and confirmed by the broker before the input is acknowledged; rejected input goes to the feed's restricted dead-letter queue.

## Solution organization

The solution separates feed processing from messaging. Broker-specific code lives only in its adapter project, so another broker (for example Kafka or IBM MQ) is added without touching the feed code.

```text
PresidioDemo.slnx
├── src/
│   ├── PresidioDemo.Core/              Feeds, parsers, serializers, protection, pipeline (no broker code)
│   │   ├── Configuration/              feed-policies.xml, xdr-layouts.xml, reference-layouts.xml
│   │   ├── FeedCodecs/ Models/ Parser/ Serializer/ Reporting/ Logging/   one subfolder per feed
│   │   ├── Protection/                 field policies, embedded Presidio bridge, python/presidio_demo.py
│   │   └── Pipeline/                   workflows, codec registry, FeedMessageProcessor
│   ├── PresidioDemo.Messaging/         broker-neutral ports and models (no broker package)
│   │   ├── Abstractions/               IMessagingConnection, IMessageConsumer, IMessagePublisher
│   │   └── Models/                     InboundMessage, OutboundMessage, MessagingOptions, MessageRoute
│   ├── PresidioDemo.Messaging.Solace/  the only project that references the Solace .NET client
│   │   ├── Connection/                 SolaceOptions, SolaceConnection (session, TLS, reconnect, confirmations)
│   │   ├── Listener/                   SolaceMessageConsumer (queue flow, client acknowledgment)
│   │   ├── Publisher/                  SolaceMessagePublisher (persistent publish, waits for confirmation)
│   │   ├── Models/                     SolaceMessageMapper (native message <-> broker-neutral records)
│   │   └── SolaceMessaging.cs          AddSolaceMessaging registration
│   └── PresidioDemo.Worker/            the deployable: Generic Host, appsettings.json, one FeedWorker per feed
│       ├── Hosting/                    EnvironmentConfiguration, MessagingTransports, PresidioOptions
│       └── Pipeline/                   FeedWorker, FeedWorkerService
├── tests/
│   ├── PresidioDemo.Messaging.Testing/ in-memory broker (tests only, never shipped)
│   ├── Messaging.ContractTests/        the behaviour every broker adapter must pass
│   ├── WorkerChecks/                   environments, settings validation, delivery rules (no Python)
│   ├── FeedPipelineHarness/            runs files through the real workers and Presidio
│   ├── FeedPipelineChecks/ FeedRoundTripChecks/ JsonFeeds/ ACI/ ETKT/ PNR/   feed and codec checks
│   └── unit-tests/                     Python tests, driven through the harness
├── tools/FeedSampleSender/             sends sample files to a running worker's input queues
├── Dockerfile                          the worker image (Linux, embedded CPython, Presidio, spaCy model)
└── deploy/
    ├── docker-compose.yml              local Solace broker; with --profile worker, the worker container too
    ├── docker-compose.dev.yml          the worker container against the Dev broker
    └── .env.dev.example                Dev values to copy into the git-ignored deploy/.env.dev
```

Project references point inward only: Worker → Core, Messaging and the adapters; adapters → Messaging. Core never references messaging, and Messaging never references a broker. Feed-code namespaces are unchanged (`PresidioDemo.Feeds.*`, `PresidioDemo.Pipeline`, `PresidioDemo.Protection`); new code uses `PresidioDemo.Messaging`, `PresidioDemo.Messaging.Solace` and `PresidioDemo.Worker`. The Python environment stays at `src/.venv`; the container image uses `/opt/venv` through `Presidio:PythonEnvironment`.

![Feed protection workflow](docs/diagrams/presidio-feed-workflow.svg)

## Messaging design

The worker runs one `FeedWorker` per feed route. Each message goes through the same steps on every broker:

1. **Receive** from the feed's input queue (`IMessageConsumer`). The broker sends the next message only after this one is acknowledged, so each feed has one active message.
2. **Process** with `FeedMessageProcessor` (Core): parse, protect with local Presidio, rebuild and validate.
3. **Publish** (`IMessagePublisher`) and wait for the broker's confirmation:
   - valid: the feed's output topic, with a stable transaction ID, the input correlation ID and only the headers listed in `Messaging:CopyHeaders`;
   - invalid: the feed's restricted dead-letter queue, with the original bytes and the reason in the `presidio-reject-reason` header.
4. **Acknowledge** the input (`InboundMessage.CompleteAsync`).

A crash at any step means the broker redelivers the message, so nothing is lost. The transaction ID is derived from the broker's stable message ID, so a redelivered message produces output with the same ID and consumers can drop duplicates (at-least-once delivery). While the broker is unavailable, publishing is retried and the input stays unacknowledged. A message delivered more than `Messaging:MaxDeliveries` times goes to the dead-letter queue without processing. Producers send the feed bytes as the message body; the header `presidio-payload-format` is `raw` (default) or `mq-export` for an IBM MQ export with `A`/`X` lines.

Rejected input and the full before/after comparison contain personal data. In production, rejected input goes only to the dead-letter queue (restrict it with broker access rules), and logs contain only the feed, transaction ID, outcome, field counts and timings. Full before/after logs are written only by the test harness.

### Adding another broker (for example Kafka)

Kafka is not implemented. To add it, or any other broker:

1. Create `src/PresidioDemo.Messaging.Kafka/` with the same folders as the Solace adapter (`Connection/`, `Listener/`, `Publisher/`, `Models/`). Reference only `PresidioDemo.Messaging` and the broker's native client (for Kafka, `Confluent.Kafka`; don't use Schema Registry, which is HTTP).
2. Implement the three ports:

   | Port | Solace (implemented) | Kafka (to implement) |
   | --- | --- | --- |
   | `IMessagingConnection.ConnectAsync` | session over tcp:// or tcps:// | consumer and idempotent producer over SASL/TLS |
   | `IMessageConsumer.ReadAsync` | queue flow, window 1, client acknowledgment | consumer group, `EnableAutoCommit=false`, one message at a time per partition |
   | `InboundMessage.StableId` | replication-group message ID | `topic-partition-offset` |
   | `InboundMessage.DeliveryCount` | broker delivery count | a retry-count header, or 1 |
   | `InboundMessage.CompleteAsync` | `flow.Ack(id)` | `Commit(offset)` in partition order |
   | `IMessagePublisher.PublishAsync` | persistent message, waits for the broker acknowledgment | `ProduceAsync` with `Acks=All` and `EnableIdempotence=true` |
   | Dead-letter destination | Solace queue | dead-letter topic |

3. Add `AddKafkaMessaging(...)` and one case in [MessagingTransports.cs](src/PresidioDemo.Worker/Hosting/MessagingTransports.cs), plus a `Kafka` section in the environments that use it, and set `Messaging:Transport` to `Kafka`.
4. Add the adapter to the list in [tests/Messaging.ContractTests/Program.cs](tests/Messaging.ContractTests/Program.cs). It must pass the same contract as the in-memory broker and Solace.

Nothing in Core, the feed code, the workers or the routes changes.

## Configuration and environments

All settings are in one file, [src/PresidioDemo.Worker/appsettings.json](src/PresidioDemo.Worker/appsettings.json):

- **Shared settings** at the top: `Messaging` (transport and delivery limits), `Presidio` (the three XML files and an optional `PythonEnvironment` folder) and `Logging`.
- **`Environments`**: one section per environment, holding only what differs, including its `Messaging:Routes` (`Feed`, `Input`, `Output` and `DeadLetter`). An environment routes only the feeds its broker carries; the worker does not consume the others. Today these are `Local` (local broker, all five feeds) and `Dev` (ACI, ETKT and PNR). Adding `QA`, `UAT` or `Prod` means adding one more block.
- **`CurrentEnvironment`**: the default when nothing else selects one (`Local`, for developer machines).

The `DOTNET_ENVIRONMENT` variable selects the environment; if it isn't set, `CurrentEnvironment` in the file applies. On AWS, each environment's ECS task definition (or EKS pod spec) sets `DOTNET_ENVIRONMENT` (for example `QA`), so the same image runs everywhere. Values are applied in this order, with later ones winning: shared settings, then the selected environment's section, then environment variables with double underscores (for example `Solace__Host` or `Messaging__Routes__2__Input`). The `Dev` section holds DemoAir stand-in names only; the real broker, VPN, user and queue names are supplied through environment variables at deploy time and are never committed.

Secrets are never in the file. `Solace:PasswordEnvironmentVariable` names the variable holding the password (`SOLACE_PASSWORD`). On AWS, the task definition's `secrets` field injects it from Secrets Manager when the task starts, so the application makes no AWS calls.

Startup stops with one clear message for any of these:
- an environment without a section
- unknown sections or keys
- a password in the file
- an HTTP host, or `tcps://` without certificate validation
- a route for an unknown feed, a feed with more than one route, or shared or wildcard destinations
- a `Presidio:PythonEnvironment` folder without `pyvenv.cfg`
- invalid XML configuration

`Solace:ProvisionQueues` creates the input and dead-letter queues and is enabled only for `Local`; other environments use queues provisioned by the broker team.

**One feed per service.** `Messaging:Feed` (for example `Messaging__Feed=PNR` in a task definition) makes the process consume only that feed's route, so each feed can run as its own ECS service and scale on its own. When it is empty, the process runs every routed feed. A value without a route stops startup.

**Unique client names.** The broker disconnects an existing connection that reuses a client name. `Solace:UniqueClientName` (on in `Dev`) appends the host name and a random part to `Solace:ClientName`, such as `piidemo-worker-dev-ip-10-0-1-23-ec2-internal-3f9a1c2e`. This lets several tasks of one service connect at the same time, and the name stays within the broker's 160-character limit.

## Architecture diagrams

- [PII infrastructure on AWS](docs/diagrams/aws-pii-infrastructure.svg): proposed dev environment with a Solace broker, one exclusive input queue per feed and acknowledgment after publish.
- [AWS deployment, one ECS service per feed](docs/diagrams/aws-solace-ecs-deployment-v3.svg): Harness build and deploy, partitioned Solace input queues, per-feed workers scaling on CPU, output queues, and worker and broker dead-letter paths. Earlier versions: [v1](docs/diagrams/aws-solace-ecs-deployment-v1.svg) (detailed), [v2](docs/diagrams/aws-solace-ecs-deployment-v2.svg) (simple).
- [Feed protection workflow](docs/diagrams/presidio-feed-workflow.svg): Solace input queues, the .NET worker (receive, parse, configured identification, local Presidio, rebuild and validate, publish then acknowledge), protected topics and restricted dead-letter queues.

## C# conventions

Types and members include XML summaries. Parameters, return values and property values are documented where applicable, including positional record values and non-public helpers. The application build generates XML documentation.

Declarations use regions for fields and constants, constructors, methods, properties and indexers, and nested types. A file with only one top-level class does not need an outer `Classes` region; member regions inside that class still apply. Methods and properties are ordered by accessibility and then name; overloads stay together. Fields without runtime initializers are sorted by name, while initialized fields retain their construction order. Enum declarations retain their existing numeric and wire order. Control-flow and scoped resource statements use explicit blocks. Documentation does not add runtime annotation attributes.

The root [.editorconfig](.editorconfig) shares C# formatting preferences with Visual Studio. See [C# formatting and cleanup](docs/code-cleanup.md) for the cleanup profile, terminal commands and rules that still require review.

## Current feed status

| Feed | Actual input result |
| --- | --- |
| ACI | Dedicated module decodes the documented structures and rebuilds all six samples; reviewed XML controls protection-field coverage |
| ETKT | All six samples use one 136-slot definition; all 69 documented strings are mapped and have policies, so populated optional values are protected rather than rejected |
| Seats | Feed-specific codec with shared System.Text.Json processing; 68 policy paths covering SEATS.txt, SEATS_03.txt and the New-section mirrors of reviewed Old-section rules |
| PNR | Section structs and linked lists for repeating sections; 53 string mappings and 92 policy fields; PNR.txt has 80 masked and 74 kept values, PNR_02.txt 24 and 33 |
| PNR-Linking | Feed-specific codec with shared System.Text.Json processing; 49 existing policy paths |

The three original XML formats remain: [reference mappings](src/PresidioDemo.Core/Configuration/reference-layouts.xml), [feed policies](src/PresidioDemo.Core/Configuration/feed-policies.xml), and [XDR structures](src/PresidioDemo.Core/Configuration/xdr-layouts.xml). Each binary feed has one common structural definition. Optional extensions enable variable widths, optional groups, named capture mappings and 14 shared masking rules without replacing the original roots or feed sections. New structures use reviewed XML changes and a restart. Unsupported structures go to the feed's dead-letter queue without publication. `TKT.txt` routes to ETKT.

XDR raw-word blocks use `<words name="..." count="..." />`, a compact alias for the original fixed array of `uint32` values. The loader supports both forms and expands the alias before processing; byte widths, field paths and validation remain unchanged.

All 20 samples run through the real workers and local Presidio in the sample verification: 17 are published and the 3 `_Negative_Testing` samples are rejected with their expected reasons. Independent byte checks confirm that the 33 identified PNR occurrences, source bytes and binary framing stay unchanged. The former ETKT `Text0132` mapping is corrected to two preserved raw words. See [verification results](docs/feed-analysis.md#reviewed-samples-and-results).

## ACI module and structure review

The ACI module is split across the Core concern folders: `Parser/ACI` (XDR reader, decoder, parser), `Serializer/ACI` (XDR writer, encoder, serializer), `Models/ACI` (models, structure definitions, indicators), `Reporting/ACI` (report formatter), `Logging/ACI` and `FeedCodecs/ACI` (feed codec), all under [src/PresidioDemo.Core](src/PresidioDemo.Core). The normal ACI route uses this module through `aciStructures="true"` on its schema-driven reference entry. Older custom configurations without this opt-in retain their existing codec. The decoder follows the supplied C-structure declaration order; the encoder rebuilds the complete payload. Existing XML remains the reviewed policy projection for the supplied sample family. Every populated string must have policy coverage, and unreviewed processing shapes are rejected.

The structure review corrects `seatNumber` to `departureGate`, `origin` to `terminatingCity`, and offset-based names to documented fields. Operational equipment, aircraft, gate, sector and process values are kept. Passenger names, actual passenger seat assignments, loyalty numbers and unresolved identifiers remain masked. PNR and numeric passenger IDs stay unchanged; the parser checks the `paxl_id` / `pax_id` association. This rewrite does not introduce tokenization.

An extra block after transaction times appears in all six samples but is not defined in the available structure declarations. It is explicitly named `unresolved_extension`, with observed layout and preserved masks. Its business meaning is unconfirmed. Some documented branches have no populated example; transcribing them does not establish a complete producer contract. Multiple-passenger decoding has a synthetic identity-link check; production processing still needs a reviewed XML projection for that shape.

Run focused ACI checks and produce decoded JSON with field locations:

```powershell
dotnet run --project tests/ACI/AciChecks.csproj -- .test-runs/aci-checks
```

Decoded analysis files intentionally contain original supplied values. The immutable workspace review, transcription, verification evidence are in `D:\Bala_Support\topics\aci\v2`.

## Dedicated ETKT module

The ETKT module uses the same layout under `src/PresidioDemo.Core`: `Parser/ETKT` (XDR reader, decoder, parser), `Serializer/ETKT` (XDR writer, encoder, serializer), `Models/ETKT` (models, structure definitions, indicators), `Reporting/ETKT` (readable report) and `FeedCodecs/ETKT` (route adapter). The normal ETKT route selects this module through `etktStructures="true"` on its schema-driven reference entry. Custom configurations without that setting retain their existing codec.

All six supplied messages begin with the observed framing words `5, 1, 0`. The screenshot enum declares only error=1, issue=2 and update=3. The implementation preserves the observed sample type 5 without assigning it an undocumented business meaning. Its body aligns with all 136 scalar slots in the screenshot history order. The encoder reconstructs the complete payload and original MQ envelope; XML remains the reviewed protection projection.

ETKT field names follow the screenshot history declarations. `agentTerminalAddress` (`h_aaa_lniata`, formerly the legacy `pnr` alias) is shown as **Agent terminal address — provisional mapping** and stays unchanged under a serializer guard. `ticketNumber` is now the 15-digit `h_tkt_nbr` (formerly `Text0088`) with the ticket-reference mask, and `historySequenceNumber` is `glob_tkt_hist_seq_nbr` (formerly `ticketNumber`). `agentCityCode` and `agentSignInNumber` replace `Text0044` and `Text0072`. The former `Flight1`/`Flight2` blocks are `new*`/`previous*` flight-adjustment fields, and the operating flight numbers replace the `NumberRepeat` names. These are provisional meanings for the observed type-5 family. The divide-event `newPnr` and `oldPnr` fields are kept, with serializer guards against changes; for example, ETKT_05 retains `FWGXKN` and `FWDZRF`. Masks for the six samples are unchanged: their protected output is byte-identical to the previous release. No passenger identity or tokenization is inferred from these records.

The issue, exchange, refund, name-adjustment, purge, FIM, ATO, commission and settlement strings are empty in all six samples. They are now declared and mapped individually, not hidden in raw-word blocks, so a populated value is protected instead of sent to the dead-letter queue:

| Fields | Treatment |
| --- | --- |
| `passengerName`, `newPassengerName`, `previousPassengerName` | Person mask |
| `formOfPayment`, `creditCardNumber` | Payment-reference mask |
| `exchangeDocument`, `newTicketNumber`, `oldTicketNumber`, `fim1Coupon1`–`fim2Coupon4` | Ticket-reference mask |
| `totalFare`, `reservationCommissionAmount`, `commissionPenaltyAmount`, `penaltyAmount` | Financial mask (`#`) |
| `exchangeComment`, `newRefundComment`, `previousRefundComment`, the 14 `*Spare3`/`*Spare4` strings | Sensitive-attribute mask (`#`) |
| `settlementAuthorizationCode` | Unclassified-identifier mask |
| `createDate`, `newPurgeDate`, `newUnflownDate`, `oldPurgeDate`, `oldUnflownDate` | Keep |

Character and integer slots, such as the carrier, duty code, sine, coupon flags and the previous flight's carrier and class, stay raw words; they are unchanged and appear in the decoded report.

Focused checks generate readable decoded JSON and exact scalar locations:

```powershell
dotnet run --project tests/ETKT/EtktChecks.csproj -- .test-runs/etkt-checks
```

Status reports include provisional, most-significant-bit-first labels alongside the original signed number and hexadecimal word. For example, `0x00002000` is Checked in (provisional), and `0x00004000` is Divided ETKT (provisional). Reserved set bits remain visible. The report shows exchange destination only when Exchanged is set; displaying labels never changes payload values or protection rules. The `h_indicator` word uses the same convention: document bits 0–2 hold the ID type (001 = ETKT), and documented bits such as 3 (ETKT was in PNR in AAA), 7 (Actioned all subsequent coupons) and 12 (Action for OA) are labelled. For example, ETKT_04's `0x34080000` shows ID type 1, bits 3 and 12, and unassigned bit 5 (`0x04000000`). Other words, such as the coupon actioned flags and status switch, have no documented meanings and remain raw.

The immutable ETKT review and evidence are under `D:\Bala_Support\topics\etkt\v4`. The searchable reference covers the documented input, history, coupon, payment, tax, exchange, fare, passenger and project structures, union arms and indicator comments. It records the repeated `etkt_inds_2` declaration in the issue branch as a source conflict. Populated issue/error branches and other unreviewed shapes still require producer evidence.

Checks cover exact binary and MQ-export round trips, agreement with an independent reader, malformed inputs, policy coverage, kept-field integrity and compatibility of the route setting. The comparison with prior protection rules allows differences only in the approved divide-event PNR spans; status labels introduce no payload changes.

## PNR-Linking and Seats JSON processing

`src/PresidioDemo.Core/FeedCodecs/PNRLinking/PnrLinkingFeedCodec.cs` and `src/PresidioDemo.Core/FeedCodecs/Seats/SeatsFeedCodec.cs` keep the per-feed folders and own each feed's route and root contract: PNR-Linking requires a JSON array, and Seats requires a JSON object. Both inherit the common `src/PresidioDemo.Core/Parser/Shared/JsonFeedCodec.cs`. JSON processing uses only `System.Text.Json`; no additional JSON package is required.

The shared implementation reads named JSON fields, resolves their existing wildcard XML policy paths, replaces selected string values and validates the rebuilt message. JSON supplies its own structure, so these feeds do not need separate structure definitions, domain models, decoders, encoders, parsers, serializers or report formatters. Four former JSON helper files are folded into the same shared class. Eighteen redundant source files were removed from the active project.

The implementation keeps the original UTF-8 JSON and scalar byte locations internally. It copies unchanged bytes and edits selected string values only, preserving property order, escapes, number spelling, nulls and empty containers. Serialization preserves the MQ envelope; validation checks PNRs, JSON types and every unselected payload byte, including empty-container structure. Parsed layouts cannot cross feed routes. Unknown populated paths still reject the whole message through policy resolution. Protection settings, replacement values and passenger grouping are unchanged.

Run the JSON checks and generate reports with the decoded payload and field paths:

```powershell
dotnet run --project tests/JsonFeeds
```

Reports include original supplied values and are saved under `.test-runs/json-feeds`. Immutable source snapshots, the removed-file inventory and verification evidence are in `D:\Bala_Support\topics\pnr-linking\v2` and `D:\Bala_Support\topics\seats\v2`. Earlier delivered folders retain the previous implementation.

## Technology stack

| Technology | Version |
| --- | --- |
| C# / .NET | .NET 10 |
| Python.NET | 3.2.0 |
| CPython | 3.12.10 |
| Presidio Analyzer / Anonymizer | 2.2.364 |
| spaCy / en_core_web_lg | 3.8.16 / 3.8.0 |

Python runs inside the .NET worker process. Processing makes no HTTP calls or model downloads; the broker connection uses the native Solace protocol.

## Setup and run

Running from source requires Windows, the .NET 10 SDK and Python 3.12; running against a local broker also needs Docker or Podman. Run these steps in PowerShell from the project root. The [container image](#run-the-worker-in-docker) needs only Docker.

### 1. Create the Python virtual environment

Do this once during initial setup:

```powershell
py -3.12 -m venv src/.venv
```

### 2. Activate the Python virtual environment

Activate it whenever you open a new PowerShell terminal:

```powershell
.\src\.venv\Scripts\Activate.ps1
```

If PowerShell reports that running scripts is disabled, allow locally created scripts for this terminal session, then retry activation:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned
.\src\.venv\Scripts\Activate.ps1
```

If prompted, enter `Y`. The policy change lasts only until this PowerShell terminal closes; administrator access is not required. See [PowerShell execution policies](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_execution_policies).

If your organization enforces a policy that still blocks activation, use the Python launcher to install packages for its selected interpreter:

```powershell
py -m pip install -r requirements.txt
```

With an active virtual environment and no explicit Python version, `py` uses that environment. Without activation, `py` uses its default interpreter and may install packages outside `src/.venv`. If activation remains blocked, confirm the selected interpreter with `py -c "import sys; print(sys.executable)"` before installing; it should end in `src\.venv\Scripts\python.exe` for this project.

The prompt normally shows `(.venv)` after activation. Confirm Python uses this environment:

```powershell
python -c "import sys; print(sys.executable)"
```

The path should end in `src\.venv\Scripts\python.exe`.

### 3. Install the Python packages

With the virtual environment active, install the required packages:

```powershell
python -m pip install -r requirements.txt
```

### 4. Deactivate the Python virtual environment

After package installation finishes, deactivate it:

```powershell
deactivate
```

### Build

```powershell
dotnet build PresidioDemo.slnx
```

### Run locally against a Solace broker

The `Local` environment expects a Solace broker on `tcp://localhost:55555` and creates its queues on startup. Start one with Docker Desktop (WSL2 backend), then run the worker. The broker admin password comes from `$env:SOLACE_ADMIN_PASSWORD` or from a git-ignored `deploy/.env` file containing `SOLACE_ADMIN_PASSWORD=<choose one>`:

```powershell
docker compose -f deploy/docker-compose.yml up -d   # first start takes about 30 seconds
$env:SOLACE_PASSWORD = "<client password; any value on the default local broker>"
dotnet run --project src/PresidioDemo.Worker
```

Wait for `READY`, then send the samples from another terminal. The sender connects as `<ClientName>-sender`, because the broker allows one connection per client name and would otherwise disconnect the running worker:

```powershell
$env:SOLACE_PASSWORD = "<same value>"
dotnet run --project tools/FeedSampleSender
```

Protected messages are published to the `airline/<feed>/protected` topics and rejected ones go to the `q.<feed>.dlq` queues. A topic keeps nothing without a subscriber, so to inspect protected output locally, create a queue in the PubSub+ Manager (http://localhost:8080, user `admin`) with the subscription `airline/>` before sending. Ctrl+C stops the worker after the current messages; unacknowledged messages are redelivered on the next start. `docker compose -f deploy/docker-compose.yml down` removes the broker and its queues.

Verified locally with Docker Desktop and broker 10.26.0.9163: the 20 sample files produced 17 protected messages and 3 dead-lettered negative-test messages, and all input queues drained.

To run a deployed environment, set `DOTNET_ENVIRONMENT` (for example `Dev`) and `SOLACE_PASSWORD`, then start `PresidioDemo.Worker`. It exits with code 1 and one message if the settings are invalid or the broker is unreachable.

### Run the worker in Docker

For a step-by-step walkthrough with expected output and troubleshooting, see the [Docker guide](docs/docker-guide.md).

The [Dockerfile](Dockerfile) builds a Linux image of the worker on the .NET 10 runtime (Ubuntu 24.04) with CPython 3.12, Presidio and the spaCy model in `/opt/venv`. The model is downloaded while the image is built, and the image sets `TLDEXTRACT_PUBLIC_SUFFIX_LIST_URLS` empty so Presidio's email recognizer uses its bundled suffix list; the running worker makes no HTTP calls. The image is about 2 GB, the container needs about 1.5 GB of memory, and startup takes about 30 seconds while the model loads.

**Local broker.** Run the broker and the worker container together; the worker starts once the broker is healthy and connects as `presidio-worker-container`:

```powershell
docker compose -f deploy/docker-compose.yml --profile worker up -d --build
docker compose -f deploy/docker-compose.yml logs -f worker   # wait for READY
```

Then send the samples from the host as above (`dotnet run --project tools/FeedSampleSender`). `docker compose -f deploy/docker-compose.yml --profile worker down` stops both.

Verified with Docker Desktop: the worker container (1.8 GB image) was ready about 4 seconds after connecting, processed the 20 samples into 17 protected and 3 dead-lettered messages, and stopped cleanly with exit code 0 on SIGTERM.

**Dev broker.** Copy [deploy/.env.dev.example](deploy/.env.dev.example) to `deploy/.env.dev` and replace the DemoAir stand-ins with the real Dev host, VPN, user, password, queues and topics. Put the broker's CA certificate (PEM) in `deploy/certs/`. Both are git-ignored; never commit real values. Give `Solace__ClientName` a value of your own, because the broker disconnects an existing connection that uses the same client name.

```powershell
docker compose -f deploy/docker-compose.dev.yml up -d --build
docker compose -f deploy/docker-compose.dev.yml logs -f worker
```

`Dev` routes ACI, ETKT (the `TKT` queue) and PNR; its other queues are not consumed. `ProvisionQueues` is off, so the input and dead-letter queues must already exist and the user must be allowed to publish to the output topics. Before sending samples into Dev queues, confirm with the broker owner that this is allowed. To send them, load the same variables into the terminal, then run the sender, which skips feeds that `Dev` does not route:

```powershell
Get-Content deploy/.env.dev | Where-Object { $_ -match '^[A-Za-z_]+=' } | ForEach-Object { $name, $value = $_ -split '=', 2; Set-Item "env:$name" $value }
$env:DOTNET_ENVIRONMENT = "Dev"
$env:Solace__TrustStoreFolder = "$PWD/deploy/certs"
dotnet run --project tools/FeedSampleSender
```

`docker compose -f deploy/docker-compose.dev.yml down` stops the worker after the current messages; unacknowledged messages stay on the broker.

### Publish the image

[deploy/publish-image.ps1](deploy/publish-image.ps1) builds the image for `linux/amd64`, tags it with the current commit and pushes it. It refuses uncommitted changes unless you pass `-AllowDirty`, and `-DryRun` prints the commands without running them. For Amazon ECR it signs in through the AWS CLI with your AWS profile; the ECR repository must already exist. For any other registry, run `docker login` first.

```powershell
./deploy/publish-image.ps1 -Registry <account>.dkr.ecr.<region>.amazonaws.com -ExtraTag dev
./deploy/publish-image.ps1 -Registry docker.io/<user>   # after docker login
```

The script prints the pushed digest. Reference the digest or the commit tag in the task definition, because moving tags such as `dev` can change.

### Process files without a broker

The test harness sends `.txt` (IBM MQ export) and `.bin` (raw payload) files through the real workers and Presidio over the in-memory broker. It replaces the former `--process-folder` batch:

```powershell
dotnet run --project tests/FeedPipelineHarness -- --input docs/sample-docs --output .test-runs/samples
```

It writes `published/<feed>/`, `dead-letter/<feed>/` (with a `.reason.txt` per file), full before/after logs in `logs/<feed>/` and `report.json`. The exit code is 0 when every file is published and 2 when any file is rejected. Files are routed by their feed folder, or by name (`ACI_03.txt`, `SEATS_02_Negative_Testing.txt`). The `--policy-file`, `--xdr-layout-file` and `--reference-layout-file` switches select alternate XML files; `--fail-publishes <n>` simulates a broker outage.

## Samples and configuration files

- `docs/sample-docs/<feed>/`: immutable source files for review and testing, one folder per feed (`ACI`, `ETKT`, `PNR`, `PNR-Linking`, `Seats`). They aren't needed at runtime. Files ending in `_Negative_Testing` are rejected by design.
- `src/PresidioDemo.Core/Configuration/`: the three XML files, copied next to the worker at build time.

The harness logs have separate `BEFORE ANONYMIZATION` and `AFTER ANONYMIZATION` headings with divider lines:
- **Before, for MQ input:** the complete original feed data, the decoded payload, a plain-text payload view, then the parsed original fields (`location | policy field | value`).
- **After:** the decoded anonymized payload, then the complete rebuilt and validated feed data.
- **Binary data:** shown as a hex dump with a readable character column.
- **Rejected messages:** the complete input and the reason.

These logs contain original personal data and are written only under `.test-runs/`, which is excluded from Git.

All three XML files are loaded and validated together before the worker connects. Reference mappings keep `name`, `offset` and `length`. With `schemaDriven="true"`, these numbers document the reviewed baseline, while the XDR structure determines the actual input positions and widths; reference sample filenames and hashes are provenance only. Field protection rules:
- **Binary fields** use Keep or Known full-width ASCII masks.
- **PNRs and confirmed operational fields** stay unchanged.
- **Unknown readable identifiers** are masked, including the two additional ETKT codes.
- **Mask characters:** the three catalog rules for loyalty, sensitive attributes and financial information use `#` to preserve `*` record separators; other masks use `*`.
- **JSON** keeps its original inline policies, replacement behavior, nulls, types and grouping.

Restart the worker after changes. See the [feed analysis](docs/feed-analysis.md) for commented XML examples, empty/optional values, per-sample results and verification commands.

## Tests

| Command | What it checks | Needs |
| --- | --- | --- |
| `dotnet run --project tests/WorkerChecks` | Environment selection, settings validation, ack-after-publish, outage retry, redelivery, max deliveries, dead-lettering, header filtering | nothing |
| `dotnet run --project tests/Messaging.ContractTests` | The broker contract, on the in-memory broker; also on Solace when `SOLACE_CONTRACT_TESTS=1` and the local broker runs | nothing (Solace: local broker) |
| `dotnet run --project tests/FeedPipelineChecks` | Policies, codecs, real Presidio, per-feed scheduling and dead-lettering through `FeedWorker` | Python venv |
| `dotnet run --project tests/FeedRoundTripChecks` | Byte-exact round trips without anonymization for every feed | nothing |
| `dotnet run --project tests/JsonFeeds`, `tests/ACI/AciChecks.csproj`, `tests/ETKT/EtktChecks.csproj`, `tests/PNR/PnrChecks.csproj -- <report-folder>` | Feed-specific codecs | nothing |
| `src/.venv/Scripts/python.exe -m unittest discover -s tests/unit-tests -p test_*.py` | Per-feed protection, byte preservation and samples, through the harness | built solution, Python venv |
| `src/.venv/Scripts/python.exe tests/run_sample_verification.py` | All samples end to end, including the negative samples | built solution, Python venv |

The round-trip checks load the normal codecs and workflows with an in-memory All-Keep policy, and compare complete input and output bytes, decoded payloads and field values over three cycles per example. Results go to a fresh `.test-runs/round-trip-*` folder. The same checks also run in `FeedPipelineChecks`.

[Feed format analysis](docs/feed-analysis.md) · [Earlier implementation notes](../../topics/feeds-demo/v14/feed-implementation-reference-v14.md) (workspace folder outside this repository; describes the earlier folder-based version)

## PNR sample mapping

The PNR route lives in `src/PresidioDemo.Core/FeedCodecs/PNR`. The XDR schema follows the P2 PNR Record Layout sections (keys 01, 10, 20, 30, 40, 43, 50, 60, 80, C0, D0, D2, D8). Repeating sections are rpcgen linked lists: a `1` word precedes each element and a `0` word ends the list. The spec describes the MQ/RTA P2 record and excludes the real-time C-structure feed, so it supplies field meanings, not wire order; element shapes are fitted to PNR.txt and PNR_02.txt, and lists that are empty in both samples reject any content. Mixed record texts (SSR/OSI facts, frequent-flyer accounts, remarks and fare references) use ordered variants keyed by record code, such as DOCS, CTCE, CTCM and TKNE: the first matching pattern masks the personal values, the record syntax stays unchanged, and a catch-all masks unrecognized records whole. Neither the spec nor the samples carry a stable passenger ID. Run `dotnet run --project tests/PNR/PnrChecks.csproj -- <report-folder>` to write a decoded report for each PNR sample; reports contain original sample values.
