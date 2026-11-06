# Presidio Feed Protection Demo

Process airline feeds from a message broker by decoding each message generically and detecting personal data with local Presidio and per-feed recognizer formats. PNR stays unchanged. Output is validated and confirmed by the broker before the input is acknowledged; rejected input goes to the feed's restricted dead-letter queue.

## Solution organization

The solution separates feed processing from messaging. Broker-specific code lives only in its adapter project, so another broker (for example Kafka or IBM MQ) is added without touching the feed code.

```text
PresidioDemo.slnx
├── src/
│   ├── PresidioDemo.Core/              Feeds, generic decoders, detection, pipeline (no broker code)
│   │   ├── Configuration/recognizers/  aci.xml, etkt.xml, pnr.xml, pnr-linking.xml, seats.xml
│   │   ├── FeedCodecs/ Models/ Parser/ Serializer/ Logging/   Shared/ plus the JSON feed folders
│   │   ├── Protection/                 recognizer configuration, detecting protector, python/presidio_demo.py
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
│   ├── FeedPipelineChecks/ FeedRoundTripChecks/ JsonFeeds/ GenericCodecChecks/   feed and codec checks
│   ├── DetectionSpike/                 parity spike, recognizer checks, detection-log run
│   ├── fixtures/                       value-free parity baseline, approved exceptions, spike summary
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
2. **Process** with `FeedMessageProcessor` (Core): decode, detect and protect with local Presidio, rebuild and validate.
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

- **Shared settings** at the top: `Messaging` (transport and delivery limits), `Presidio` (`RecognizerDirectory`, `DetectionLog` and an optional `PythonEnvironment` folder) and `Logging`.
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
- an invalid or missing recognizer file (`Presidio:RecognizerDirectory`)

`Solace:ProvisionQueues` creates the input and dead-letter queues and is enabled only for `Local`; other environments use queues provisioned by the broker team.

**One feed per service.** `Messaging:Feed` (for example `Messaging__Feed=PNR` in a task definition) makes the process consume only that feed's route, so each feed can run as its own ECS service and scale on its own. When it is empty, the process runs every routed feed. A value without a route stops startup.

**Parallel consumers.** `Messaging:ConsumersPerTask` (1 by default, at most 32) opens that many consumers on each routed input queue. All of them share the task's one broker connection. Each consumer reads one message at a time, so broker round trips overlap while Python calls stay serialized. On a partitioned queue, the broker assigns partitions to each consumer, which keeps the order for each partition key. On an exclusive queue, such as the queues the Local broker provisions, the extra consumers wait as hot standby and add no throughput. Set it per task definition, for example `Messaging__ConsumersPerTask=8`, once the feed's queue is partitioned.

**Unique client names.** The broker disconnects an existing connection that reuses a client name. `Solace:UniqueClientName` (on in `Dev`) appends the host name and a random part to `Solace:ClientName`, such as `piidemo-worker-dev-ip-10-0-1-23-ec2-internal-3f9a1c2e`. This lets several tasks of one service connect at the same time, and the name stays within the broker's 160-character limit.

## Architecture diagrams

- [PII infrastructure on AWS](docs/diagrams/aws-pii-infrastructure.svg): proposed dev environment with a Solace broker, one exclusive input queue per feed and acknowledgment after publish.
- [AWS deployment, one ECS service per feed](docs/diagrams/aws-solace-ecs-deployment.svg): a generic view of the Harness build and deploy, the Solace broker in AWS, input queues and DLQs, per-feed workers with parallel consumers and auto scaling, protected topics with output queues for downstream consumers, and the failed-message and broker backstop paths. Specific values are in [docs/aws-deployment-plan.md](docs/aws-deployment-plan.md). Earlier versions are in `docs/diagrams/old`: [v1](docs/diagrams/old/aws-solace-ecs-deployment-v1.svg) (detailed), [v2](docs/diagrams/old/aws-solace-ecs-deployment-v2.svg) (simple), [v3](docs/diagrams/old/aws-solace-ecs-deployment-v3.svg) (first per-feed version), [v4](docs/diagrams/old/aws-solace-ecs-deployment-v4.svg) (topics and connection details), [v5](docs/diagrams/old/aws-solace-ecs-deployment-v5.svg) (parallel consumers and pre-scaling), [v6](docs/diagrams/old/aws-solace-ecs-deployment-v6.svg) (all open items confirmed, with broker details), [v7](docs/diagrams/old/aws-solace-ecs-deployment-v7.svg) (generic broker header), [v8](docs/diagrams/old/aws-solace-ecs-deployment-v8.svg) (generic wording throughout).
- [Feed protection workflow](docs/diagrams/presidio-feed-workflow.svg): Solace input queues, the .NET worker (receive, parse, configured identification, local Presidio, rebuild and validate, publish then acknowledge; it shows the earlier field-policy identification and is due for revision), protected topics and restricted dead-letter queues.

## C# conventions

Types and members include XML summaries. Parameters, return values and property values are documented where applicable, including positional record values and non-public helpers. The application build generates XML documentation.

Declarations use regions for fields and constants, constructors, methods, properties and indexers, and nested types. A file with only one top-level class does not need an outer `Classes` region; member regions inside that class still apply. Methods and properties are ordered by accessibility and then name; overloads stay together. Fields without runtime initializers are sorted by name, while initialized fields retain their construction order. Enum declarations retain their existing numeric and wire order. Control-flow and scoped resource statements use explicit blocks. Documentation does not add runtime annotation attributes.

The root [.editorconfig](.editorconfig) shares C# formatting preferences with Visual Studio. See [C# formatting and cleanup](docs/code-cleanup.md) for the cleanup profile, terminal commands and rules that still require review.

## Decoded-text detection

Every feed is decoded generically; there is no per-feed schema or field list, so a template change does not stop protection.

- **Binary feeds (ACI, ETKT, PNR).** `GenericXdrFeedCodec` reads the IBM MQ export or raw payload and `XdrTextScanner` scans the big-endian XDR words for counted strings of four or more printable ASCII characters. Shorter spans stay raw words and are never changed, because words such as `00000001 31000000` cannot be told apart from a one-character string (WR-015). A counted string whose text is not printable ASCII but is mostly letters, digits or spaces (Latin-1, EBCDIC, or text with a control character) cannot be checked, so the message is dead-lettered. Integers, framing and padding are never decoded or changed. Protected values are written back at the same width, and validation rejects any change outside the scanned strings.
- **JSON feeds (PNR-Linking, Seats).** Every string value is a candidate, with its property name as context. Numbers, booleans and nulls never change; a number or boolean under a personal-data key dead-letters the message, because it cannot be protected in its type.

Each feed has one recognizer file in [src/PresidioDemo.Core/Configuration/recognizers](src/PresidioDemo.Core/Configuration/recognizers). For every decoded string, in this order:

1. **PNR anchor** (WR-017): kept unchanged. JSON names the PNR keys. Binary feeds name the PNR-shaped string next to a neighbouring format, such as the string just before the first passenger name in ACI, never a fixed position; or a PNR embedded in a longer string, as the `pnr` group of a pattern inside a named keep (the ACI record reference). A missing required anchor dead-letters the message. So does a malformed PNR: an optional anchor position (ETKT divide-event PNRs) must hold a PNR-shaped value or another declared value.
2. **JSON key format**: the value is replaced with the format's token, such as `[PASSENGER_NAME]`. Keys match regardless of case.
3. **JSON key keep**: published unchanged; with `scan="true"` (reason texts) personal-data spans inside are masked.
4. **A declared format that matches the whole string**: protected by that format. It wins over a pattern keep, so a keep never publishes a value the feed's own formats identify.
5. **Pattern keep**: published unchanged. With `scan="true"` (records such as DOCS, CTCE, CTCM and TKNE) the personal-data spans the formats find inside it are still masked.
6. **A built-in or NER match of the whole string**: protected by that built-in.
7. **Anything else** is masked across its full width, keeping literal `*` separators (WR-014).

Local Presidio runs once per message as a batch through Python.NET: the XML formats as pattern recognizers, the listed built-ins, and spaCy NER for `PERSON`. Python returns spans only; every decision is made in .NET. A message is dead-lettered only for a structural failure: undecodable framing, binary text that is not printable ASCII, a missing or malformed PNR anchor, a number or boolean anywhere under a personal-data JSON key, a recognizer pattern that times out (the bridge sets Presidio's limit to one second and treats a skipped pattern as a failure), or a rebuild that differs outside the protected strings (WR-016). A value that detection misses is published, and shows up in the detection log.

A format, with fictional data. `DOCSDAHK1/////12MAR85/F//MILLER/JORDAN-1MILLER/JORDAN` publishes as `DOCSDAHK1/////*******/*//*************-1*************`:

```xml
<format name="dateOfBirth" entity="DATE_OF_BIRTH" score="0.9" maskCharacter="*">
  <pattern>(?&lt;=/)\d{2}[A-Z]{3}\d{2}(?=/[MFUX]I?/)</pattern>
  <context>DOCS</context>
</format>
<keep name="docsRecord" scan="true"><pattern>DOCS[A-Z0-9]{2}HK\d/.*</pattern></keep>
```

Patterns use syntax shared by .NET and the Python `regex` module and match case-insensitively. Loyalty, sensitive-attribute and financial formats use `#` so literal `*` separators stay distinguishable; other masks use `*`.

### Changing a format

1. Edit the feed's file in `Configuration/recognizers/`. The loader rejects a bad pattern, a duplicate name, a missing anchor or an unknown element at startup.
2. Check parity: `src/.venv/Scripts/python.exe tests/run_sample_verification.py`. Every value in [tests/fixtures/parity-baseline.json](tests/fixtures/parity-baseline.json) must stay protected or be listed in [parity-exceptions.json](tests/fixtures/parity-exceptions.json), and every PNR must stay unchanged (WR-019). The baseline is value-free: sample, location, length and entity. It was frozen from the field policies this design replaced and cannot be regenerated.
3. Open a pull request. The recognizer files ship only inside the image built from it (PR-005), so a change reaches an environment by deploying that image.

### Detection log

`Presidio:DetectionLog` (off by default; for example `Presidio__DetectionLog=true` in one feed's task definition, read at startup) writes one value-free line per decoded string (WR-018):

```text
PNR 3f9a… DETECT slot=12 key=- len=13 shape=AAAAAA/AAAAAA format=passengerName entity=PERSON score=0.90 decision=masked
```

Decisions are `masked`, `replaced`, `kept`, `masked-inside`, `anchor`, `masked-by-default` and `missed-below-threshold` (a kept value with a weak detection inside it, worth a format). Lines never contain a value. `key=` shows a JSON property name only when the feed's recognizer file declares it, and `?` otherwise, because a producer could use a value as a key. Original values appear only in the local harness reports under `.test-runs/`.

### Results on the supplied samples

All 20 samples run through the real workers and local Presidio: 18 are published, and `PNR_LINKING_03_Negative_Testing` (a date of birth sent as a JSON number) and `SEATS_02_Negative_Testing` (an MQ export without CCSID 1208) are rejected. Of the 211 values the former field policies protected, 190 are protected and 21 are approved exceptions: 15 binary strings of one to three characters and 6 ETKT sign-in numbers that match the four-digit time keep. All 17 PNR occurrences stay unchanged. `PNR_LINKING_02_Negative_Testing`, formerly rejected for an unlisted field, is now published with that field masked. The value-free spike summary is in [tests/fixtures/spike-summary.json](tests/fixtures/spike-summary.json).

Compared byte for byte with the output of the former field policies, 8 of the 17 published samples are identical. The rest differ only in the approved exceptions, the 7 listed false positives (PNR fare and mileage remarks and a six-digit number, now masked), and the mask character of standalone PNR names and identifiers, which the default mask writes as `#` where the former policies wrote `*`.

### Known limits

- A JSON number or boolean under a key that no recognizer file declares is published unchanged; only strings are detected.
- Binary strings of one to three characters are published unchanged (WR-015), for example seat numbers and three-digit phone area codes.
- In binary feeds, a value that matches a keep is published even if it is personal data with the same shape; for example, an ACI eight-digit date of birth matches the `date` keep. Binary strings carry no labels, so a keep cannot tell the two apart.
- A value that detection misses is published (WR-016); the detection log shows `masked-by-default`, `missed-below-threshold` and the formats used, so a format can be added.
- JSON property names are structure and are never changed, even if a producer uses a value as a key; the detection log shows such undeclared keys as `?`.
- Binary text that contains NUL bytes, such as UTF-16, is not recognised as text and is published unchanged. Producers send ASCII; the supplied exports use CCSID 437 and 500.
- In ETKT, a six-character value of letters and digits at a divide-event PNR position is kept as a PNR.
- A Presidio regex timeout is detected through Presidio's "timed out" warning. Recheck `test_presidio_rules` when Presidio is upgraded.

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

It writes `published/<feed>/`, `dead-letter/<feed>/` (with a `.reason.txt` per file), full before/after logs in `logs/<feed>/` and `report.json`. The exit code is 0 when every file is published and 2 when any file is rejected. Files are routed by their feed folder, or by name (`ACI_03.txt`, `SEATS_02_Negative_Testing.txt`). `--recognizer-directory` selects another recognizer folder, `--detection-log on` prints the value-free detection log, and `--fail-publishes <n>` simulates a broker outage.

## Samples and configuration files

- `docs/sample-docs/<feed>/`: immutable source files for review and testing, one folder per feed (`ACI`, `ETKT`, `PNR`, `PNR-Linking`, `Seats`). They aren't needed at runtime. Files ending in `_Negative_Testing` are rejected by design.
- `src/PresidioDemo.Core/Configuration/recognizers/`: one recognizer file per feed, copied next to the worker at build time and into the image.

The harness logs have separate `BEFORE ANONYMIZATION` and `AFTER ANONYMIZATION` headings with divider lines:
- **Before, for MQ input:** the complete original feed data, the decoded payload, a plain-text payload view, then the decoded strings (`location | slot or path | value`).
- **After:** the decoded anonymized payload, then the complete rebuilt and validated feed data.
- **Binary data:** shown as a hex dump with a readable character column.
- **Rejected messages:** the complete input and the reason.

These logs contain original personal data and are written only under `.test-runs/`, which is excluded from Git.

## Tests

| Command | What it checks | Needs |
| --- | --- | --- |
| `dotnet run --project tests/WorkerChecks` | Environment selection, settings validation, ack-after-publish, outage retry, redelivery, max deliveries, dead-lettering, header filtering | nothing |
| `dotnet run --project tests/Messaging.ContractTests` | The broker contract, on the in-memory broker; also on Solace when `SOLACE_CONTRACT_TESTS=1` and the local broker runs | nothing (Solace: local broker) |
| `dotnet run --project tests/FeedPipelineChecks` | Detection with real Presidio on every sample, PNR anchors, fail-closed cases, parity regression, per-feed scheduling and dead-lettering through `FeedWorker` | Python venv |
| `dotnet run --project tests/FeedRoundTripChecks` | Byte-exact round trips without anonymization for every feed | nothing |
| `dotnet run --project tests/JsonFeeds`, `dotnet run --project tests/GenericCodecChecks` | The JSON codec; the generic XDR scanner and codec on all binary samples | nothing |
| `dotnet run --project tests/DetectionSpike -- config-checks` / `-- spike` | Recognizer file validation; parity, PNRs, rebuilds, false positives and throughput against the frozen baseline | nothing / Python venv |
| `src/.venv/Scripts/python.exe -m unittest discover -s tests/unit-tests -p test_*.py` | All feeds through the harness against the baseline, template changes, recognizer patterns, the detection bridge and the value-free detection log | built solution, Python venv |
| `src/.venv/Scripts/python.exe tests/run_sample_verification.py` | All samples end to end, including the negative samples | built solution, Python venv |

The round-trip checks load the normal codecs and workflows with a keep-everything protector, and compare complete input and output bytes, decoded payloads and field values over three cycles per example. Results go to a fresh `.test-runs/round-trip-*` folder. The same checks also run in `FeedPipelineChecks`.

[Feed format analysis](docs/feed-analysis.md) · [Earlier implementation notes](../../topics/feeds-demo/v14/feed-implementation-reference-v14.md) (workspace folder outside this repository; describes the earlier folder-based version)
