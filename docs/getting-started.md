# Getting started: set up and test PresidioDemo on Windows

This guide takes a new developer from a clean Windows PC to a running feed worker that reads airline feed messages
from a local Solace broker in Docker, protects personal data with Presidio, and publishes the protected copy. It
ends with testing the queue messages.

Every step is a PowerShell block to copy and run, followed by **You should see**, so you can confirm it worked
before going on. The blocks work in Windows PowerShell 5.1 (the built-in one) and in PowerShell 7.

Everything runs on your own PC against a **local** broker. No shared environment and no real credentials are needed.

## What you are setting up

```
 docs/sample-docs/<Feed>/*.txt
            │  tools/FeedSampleSender
            ▼
 ┌────────────── Local Solace broker (Docker container) ──────────────┐
 │  queue  q.<feed>.raw              original messages                 │
 │  topic  airline/<feed>/protected  protected messages ─► q.protected.watch
 │  queue  q.<feed>.dlq              rejected originals (restricted)   │
 └─────────────────────────────────────────────────────────────────────┘
            ▲  native SMF protocol, port 55555
            ▼
 ┌──────── PresidioDemo.Worker (.NET 10) ────────┐
 │  parse feed ─► protect fields ─► rebuild feed │
 │        embedded CPython 3.12 (Python.NET)     │
 │        Presidio + spaCy en_core_web_lg        │
 └───────────────────────────────────────────────┘
```

- Feeds: **ACI**, **ETKT**, **PNR**, **PNR-Linking**, **Seats**. Each has its own input queue, output topic and
  dead-letter queue (DLQ), set in `src/PresidioDemo.Worker/appsettings.json` under `Environments:Local`.
- Presidio runs **inside** the worker process. The worker makes no HTTP calls; it opens only the native broker
  connection.
- A valid message is published to `airline/<feed>/protected`. A message that cannot be protected safely goes,
  unchanged, to `q.<feed>.dlq` with a reason.

## Steps at a glance

| Part | Steps | Done once or each time |
|---|---|---|
| A. Install tools | 1–4: WSL 2, Git, .NET 10, Python 3.12, Docker Desktop | Once |
| B. Set up the project | 5–8: clone, Python environment, build, offline tests | Once (tests any time) |
| C. Solace in Docker | 9–11: pull image, start broker, open Manager | Once, then start each day |
| D. Run and test queue messages | 12–20: worker, watch queue, Try-Me, send, check | Each time |

---

## Part A. Install the tools (once)

### What the PC needs

| Item | Requirement | Why |
|---|---|---|
| Windows | 10 (22H2) or 11, 64-bit, with **winget** (App Installer) | Install commands below use winget |
| CPU virtualization | Enabled in BIOS/UEFI | Docker Desktop runs Linux containers in WSL 2 |
| Memory | 8 GB RAM or more | The spaCy model needs about 1.5 GB loaded; the broker reserves 1 GB shared memory |
| Disk | About 6 GB free | Solace image about 2.5 GB, Python environment about 0.7 GB, plus tools |
| Admin rights | For steps 1–2 | Installing WSL and Docker Desktop |
| Internet | During setup only | Tools, NuGet and Python packages, the spaCy model (from GitHub), Docker images |

| Tool | Version | Used for |
|---|---|---|
| Git | any recent | Cloning the repository |
| .NET SDK | **10.0** (verified with 10.0.403) | Every project targets `net10.0` |
| Python | **3.12, 64-bit**, python.org build (verified with 3.12.10) | The worker embeds `python312.dll` from this install |
| Docker Desktop | current (verified with Docker Engine 29.9.2) | Runs the Solace broker container |

Docker Desktop is free for personal use and small companies, but larger organizations need a paid subscription.
Ask your team lead which licence to use before installing.

### Step 1. Turn on WSL 2

Open **PowerShell as Administrator** (Start → type `PowerShell` → **Run as administrator**):

```powershell
wsl --install --no-distribution
```

Restart Windows when asked. Then, in a normal PowerShell window:

```powershell
wsl --status
```

**You should see:** `Default Version: 2`. If it says WSL is not installed, or that virtualization is off, enable
virtualization (Intel VT-x or AMD-V) in the BIOS/UEFI and run step 1 again.

### Step 2. Install Git, .NET 10 SDK, Python 3.12 and Docker Desktop

In **PowerShell as Administrator**:

```powershell
winget install -e --id Git.Git                  --accept-package-agreements --accept-source-agreements
winget install -e --id Microsoft.DotNet.SDK.10  --accept-package-agreements --accept-source-agreements
winget install -e --id Python.Python.3.12       --accept-package-agreements --accept-source-agreements
winget install -e --id Docker.DockerDesktop     --accept-package-agreements --accept-source-agreements
```

Then **sign out of Windows and sign back in** (or restart). The new `PATH` entries and Docker's `docker-users` group
apply only after that.

Notes:

- Already have a tool? winget reports it as installed. Just make sure the versions match the table above.
- Python must stay where the installer puts it. The worker reads the `home` folder from `src\.venv\pyvenv.cfg` and
  loads `python312.dll` from there. Other Python versions can stay installed; the commands below pick 3.12 with
  `py -3.12`.

### Step 3. Start Docker Desktop for the first time

1. Start **Docker Desktop** from the Start menu.
2. Accept the Docker Subscription Service Agreement. You can skip signing in.
3. Keep the default **Use the WSL 2 based engine** (Settings → General) and Linux containers.
4. Optional: Settings → General → **Start Docker Desktop when you sign in to your computer**.
5. Wait until the bottom-left status says **Engine running**.

Docker Desktop must be running whenever you use the broker (Part C onward).

### Step 4. Check every prerequisite

Open a **new, normal** PowerShell window:

```powershell
git --version
dotnet --list-sdks
py -0
docker --version
docker info --format "Docker engine {{.ServerVersion}} on {{.OSType}}"
docker run --rm hello-world
```

**You should see:**

- `git version 2.x`
- a line starting with `10.0.`
- `-V:3.12` in the `py -0` list
- `Docker version 2x.x`
- `Docker engine 2x.x on linux`
- `Hello from Docker!`

If `docker` says it cannot connect to the Docker API, Docker Desktop is not running (step 3).

---

## Part B. Set up the project (once)

### Step 5. Get the code

Ask the team lead for the repository URL. Then, in a normal PowerShell window, from the folder where you keep
projects:

```powershell
git clone <repository-url> PresidioDemo
cd PresidioDemo
git checkout main
```

Run **every later command from this `PresidioDemo` folder** (the one that contains `PresidioDemo.slnx`).

### Step 6. Create the Python environment

The worker looks for the virtual environment at exactly `src\.venv`.

```powershell
py -3.12 -m venv src\.venv
src\.venv\Scripts\python -m pip install --upgrade pip
src\.venv\Scripts\python -m pip install -r requirements.txt
src\.venv\Scripts\python -c "import presidio_analyzer, spacy; spacy.load('en_core_web_lg'); print('Python environment OK')"
```

`requirements.txt` pins `presidio-analyzer`, `presidio-anonymizer`, `spacy` and the `en_core_web_lg` model. The
install takes a few minutes, because the model download is large.

**You should see:** `Python environment OK` on the last line.

### Step 7. Build the solution

```powershell
dotnet build PresidioDemo.slnx
```

**You should see:** `Build succeeded.` and `0 Error(s)`. This also builds the test harness that the Python tests
use.

### Step 8. Run the tests that need no broker

These tests use an in-memory broker, so Docker is not needed yet. Run the three blocks in order.

**.NET checks.** Each test project is a console program that prints `PASS ...` lines:

```powershell
$failed = @()
foreach ($p in 'WorkerChecks','Messaging.ContractTests','FeedPipelineChecks','FeedRoundTripChecks','JsonFeeds','ACI','ETKT','PNR') {
    Write-Host "== $p" -ForegroundColor Cyan
    dotnet run --project "tests/$p" --no-build
    if ($LASTEXITCODE -ne 0) { $failed += $p }
}
if ($failed.Count -eq 0) { Write-Host "All .NET checks passed." -ForegroundColor Green } else { Write-Host "FAILED: $($failed -join ', ')" -ForegroundColor Red }
```

**You should see:** `All .NET checks passed.` `Messaging.ContractTests` prints `SKIP Solace contract ...` here; it
runs against the broker in step 20.

**Python unit tests** (54 tests, about 1–2 minutes; they load Presidio):

```powershell
src\.venv\Scripts\python -m unittest discover -s tests/unit-tests
```

**You should see:** `Ran 54 tests` and `OK`.

**End-to-end sample check.** This sends all 20 files in `docs/sample-docs` through the real feed workers and
embedded Presidio, over the in-memory broker:

```powershell
src\.venv\Scripts\python tests/run_sample_verification.py
```

**You should see:** `"Published": 17`, `"Rejected": 3` and `"all_checks_passed": true`. The 3 rejected files are
the deliberate negative samples (see step 17).

Test output goes to `.test-runs\` (git-ignored). Some files there contain the original, unprotected sample data;
never copy them elsewhere.

---

## Part C. Run the Solace broker in Docker

The broker is `solace/solace-pubsub-standard`, pinned in `deploy/docker-compose.yml`. The container is named
`presidio-solace` and publishes three ports:

| Port | Used by | Purpose |
|---|---|---|
| 55555 | The worker and the sample sender | Native messaging (SMF) |
| 8080 | You, in a browser | PubSub+ Manager web UI and its admin API |
| 8008 | Your browser's Try-Me! page | Web messaging for Try-Me only |

### Step 9. Pull the Solace image (once)

Docker Desktop must be running.

```powershell
docker pull solace/solace-pubsub-standard:10.26.0.9163
docker image ls solace/solace-pubsub-standard
```

**You should see:** the `10.26.0.9163` tag, about 2.5 GB. The download takes a while.

### Step 10. Start the broker

Choose a password for the broker's `admin` user. It protects only your local broker's web UI and admin API, so
make one up. Set it, with the client password the worker uses, in the PowerShell window:

```powershell
$env:SOLACE_ADMIN_PASSWORD = "<choose-a-local-admin-password>"
$env:SOLACE_PASSWORD = "local-only"
docker compose -f deploy/docker-compose.yml up -d --wait
docker ps --filter name=presidio-solace --format "{{.Names}} | {{.Status}} | {{.Ports}}"
```

**You should see:** `Container presidio-solace Healthy`, then
`presidio-solace | Up ... (healthy) | ...8008->8008/tcp, ...8080->8080/tcp, ...55555->55555/tcp`.
The broker takes about 30 seconds to become healthy.

To avoid typing the admin password each time, put `SOLACE_ADMIN_PASSWORD=<your-password>` in `deploy\.env`. Compose
reads it from there. The file is git-ignored; never commit it.

If it doesn't become healthy, look at the broker log:

```powershell
docker logs presidio-solace --tail 30
```

The broker has no data volume. `docker compose ... stop` keeps queues and messages, but `docker compose ... down`
deletes them (step 21).

### Step 11. Open PubSub+ Manager

Open <http://localhost:8080> in a browser and sign in as user `admin` with your admin password. Click the
**default** Message VPN.

**You should see:** the `default` VPN summary page, with **Queues** and **Try Me!** in the left menu.

---

## Part D. Run the worker and test queue messages

You will use three PowerShell windows, all in the `PresidioDemo` folder:

| Window | Runs |
|---|---|
| 1 | The worker (stays open) |
| 2 | The sample sender and the queue scripts |
| 3 | Optional: the broker commands from Part C |

### Step 12. Set the variables in each new window

Variables last only for the window they are set in. Paste this into **every** new PowerShell window first:

```powershell
cd <path-to>\PresidioDemo
$env:SOLACE_PASSWORD = "local-only"
$env:SOLACE_ADMIN_PASSWORD = "<your-local-admin-password>"
```

- `SOLACE_PASSWORD` is the client password. The Local broker's `default` VPN accepts any password, but the worker
  and sender refuse to start unless the variable is set.
- `SOLACE_ADMIN_PASSWORD` is needed only by the queue scripts (steps 14 and 17) and by Docker Compose.

### Step 13. Start the worker (window 1)

```powershell
dotnet run --project src/PresidioDemo.Worker
```

- `DOTNET_ENVIRONMENT` is not set, so the worker uses `"CurrentEnvironment": "Local"` from `appsettings.json`.
- With `"ProvisionQueues": true` (Local only), the worker creates its 10 queues (5 input, 5 DLQ) on first connect.

**You should see**, within about 10 seconds:

```
PRESIDIO FEED WORKER | environment Local | Ctrl+C or SIGTERM stops after the current messages.
info: ...SolaceConnection[0] Provisioned 10 Solace queues on the local broker.
info: ...SolaceConnection[0] Connected to Solace tcp://localhost:55555 (VPN default) as presidio-worker-local.
info: ...FeedWorkerService[0] Initializing embedded local Presidio and the bundled NLP model...
info: ...Worker.ACI[0] ACI worker reading q.aci.raw; output airline/aci/protected; dead letter q.aci.dlq.
  (one line each for ETKT, PNR, PNR-Linking and Seats)
info: ...FeedWorkerService[0] READY: 5 feed workers on Solace.
```

Leave this window open. **Ctrl+C** stops the worker after the current messages.

The worker can also run as a container instead:
`docker compose -f deploy/docker-compose.yml --profile worker up -d --build`. Run only one worker at a time: the
input queues are exclusive, so a second worker just waits as a standby.

### Step 14. Create the watch queue for protected output (window 2)

The worker publishes protected messages to **topics**. The broker delivers a topic message only to subscribers that
exist at that moment, and drops it otherwise. So create a queue that subscribes to all output topics **before**
sending anything. This script calls the local broker's admin API (SEMP) on port 8080:

```powershell
$cred = New-Object System.Management.Automation.PSCredential('admin', (ConvertTo-SecureString $env:SOLACE_ADMIN_PASSWORD -AsPlainText -Force))
$semp = 'http://localhost:8080/SEMP/v2'
$queue = @{ queueName = 'q.protected.watch'; accessType = 'exclusive'; permission = 'consume'; ingressEnabled = $true; egressEnabled = $true } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "$semp/config/msgVpns/default/queues" -Credential $cred -ContentType 'application/json' -Body $queue | Out-Null
$subscription = @{ subscriptionTopic = 'airline/*/protected' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "$semp/config/msgVpns/default/queues/q.protected.watch/subscriptions" -Credential $cred -ContentType 'application/json' -Body $subscription | Out-Null
"q.protected.watch is subscribed to airline/*/protected"
```

**You should see:** `q.protected.watch is subscribed to airline/*/protected`. An `already exists` error means it was
created earlier, which is fine. Run the script again after a `docker compose ... down`, which deletes the queue.

The same thing in PubSub+ Manager: **Queues** → **+ Queue** → name `q.protected.watch` → **Create** → **Apply**.
Then open the queue → **Subscriptions** → **+ Subscription** → `airline/*/protected` → **Create**.

### Step 15. Optional: watch the output live with Try-Me!

**Try-Me!** is a small publish/subscribe client built into PubSub+ Manager. It runs in your browser and connects to
the broker on port 8008. It shows message payloads, which the queue list doesn't.

1. In PubSub+ Manager, open **Try Me!** in the left menu. The page has a **Publisher** panel (left) and a
   **Subscriber** panel (right).
2. In **Subscriber**, click **Connect** and keep the defaults: Broker URL `ws://localhost:8008`, Message VPN
   `default`, Client Username `default`, empty password. The status changes to **Connected**.
3. In **Topic Subscriber**, replace the suggested `flight/>` with `airline/>` and click **Subscribe**.

**You should see:** `airline/>` under **Subscribed Topics**. After step 16, messages appear under
**Messages (Most Recent 20)**. Each shows the topic, delivery mode, **Correlation Id** (the sample file name, such as
`SEATS_03.txt`) and the payload.

Try-Me keeps only the last 20 messages and loses them when you reload the page. The watch queue (step 14) keeps
everything.

If the subscriber stays on **Connecting**, port 8008 is not published. Check `docker ps` (step 10).

### Step 16. Send the sample messages (window 2)

```powershell
dotnet run --project tools/FeedSampleSender
```

The sender reads the same `appsettings.json` and environment as the worker. It connects with its own client name
(`presidio-worker-local-sender`) and publishes every `.txt` and `.bin` file under `docs/sample-docs` to its feed's
input queue. The feed comes from the folder name (`ACI`, `ETKT`, `PNR`, `PNR-Linking`, `Seats`) or else from the
file name (`PNR_02.txt` → PNR). `.txt` files are sent as IBM MQ exports and `.bin` files as raw payloads.

**You should see:** `Sending 20 files to Solace (Local).` and then 20 lines like:

```
SENT ACI          ACI.txt -> queue:q.aci.raw
SENT Seats        SEATS_03.txt -> queue:q.seats.raw
```

To send only some files:

```powershell
dotnet run --project tools/FeedSampleSender -- --input docs/sample-docs/PNR
dotnet run --project tools/FeedSampleSender -- --input docs/sample-docs/Seats/SEATS_03.txt
```

### Step 17. Check the results

**Worker window (1).** About one message per second, each with one summary line, plus value-free stage lines
(`PARSED`, `POLICY`, `FIELDS`, `SERIALIZED`) with counts and timings:

```
info: ...Worker.ACI[0] ACI 6cf9...9106 PUBLISHED to topic:airline/aci/protected | 2604 bytes | ... fields protected | delivery 1 | ... ms
warn: ...Worker.Seats[0] Seats c9a4...b851 DEAD-LETTERED to queue:q.seats.dlq | JSON MQ exports require UTF-8 CCSID 1208; ...
```

**Queue status script (window 2).** Wait about 30 seconds after sending, then run:

```powershell
$cred = New-Object System.Management.Automation.PSCredential('admin', (ConvertTo-SecureString $env:SOLACE_ADMIN_PASSWORD -AsPlainText -Force))
$semp = 'http://localhost:8080/SEMP/v2'
$list = Invoke-RestMethod -Uri "$semp/monitor/msgVpns/default/queues?count=100" -Credential $cred
$rows = for ($i = 0; $i -lt $list.data.Count; $i++) {
    $name = $list.data[$i].queueName
    $flows = Invoke-RestMethod -Uri "$semp/monitor/msgVpns/default/queues/$([uri]::EscapeDataString($name))/txFlows" -Credential $cred
    [pscustomobject]@{ Queue = $name; Messages = $list.collections[$i].msgs.count; Consumers = @($flows.data).Count }
}
$rows | Format-Table -AutoSize
```

**You should see**, after sending all 20 samples once:

```
Queue             Messages Consumers
-----             -------- ---------
q.aci.dlq                0         0
q.aci.raw                0         1
q.etkt.dlq               0         0
q.etkt.raw               0         1
q.pnr-linking.dlq        2         0
q.pnr-linking.raw        0         1
q.pnr.dlq                0         0
q.pnr.raw                0         1
q.protected.watch       17         0
q.seats.dlq              1         0
q.seats.raw              0         1
```

| What | Meaning |
|---|---|
| `.raw` queues: 0 messages, 1 consumer | The worker consumed everything and is still connected |
| `q.protected.watch`: 17 | 17 samples were protected and published |
| `q.pnr-linking.dlq`: 2, `q.seats.dlq`: 1 | The 3 negative samples were rejected, as intended |

The same numbers are in PubSub+ Manager → **Queues** (click **Refresh Data**). Running the sender again adds 17
and 3 more.

The three dead-lettered samples test the rejection path:

| Sample | DLQ | Reason in the worker log |
|---|---|---|
| `PNR_LINKING_02_Negative_Testing.txt` | `q.pnr-linking.dlq` | The feed codec returned a field without a configured protection policy. |
| `PNR_LINKING_03_Negative_Testing.txt` | `q.pnr-linking.dlq` | Non-string JSON fields must remain unchanged. |
| `SEATS_02_Negative_Testing.txt` | `q.seats.dlq` | JSON MQ exports require UTF-8 CCSID 1208; no implicit code-page conversion is performed. |

A DLQ holds the **original, unprotected** message, with the reason in the `presidio-reject-reason` header. Treat
DLQ contents as personal data.

### Step 18. Read the content of queued messages with Try-Me!

The Manager queue page (**Queues → queue → Messages Queued**) lists only message ID, time and size, not the
payload. To read payloads, use Try-Me's queue consumer:

1. **Try Me!** → **Subscriber** → **Connect** (if not already connected).
2. Expand **Bind to an endpoint to receive guaranteed messages**.
3. Select **Queue**, replace `try-me-queue` with the queue name (`q.protected.watch`, or a DLQ such as
   `q.seats.dlq`), and click **Start Consume**.
4. The messages appear under **Messages**, each with its Correlation Id (the sample file name) and payload. Click
   **Stop Consume** when done.

**Start Consume removes the messages from the queue**, because Try-Me acknowledges each one. Afterwards the queue
shows 0 messages; send the samples again (step 16) if you need them back. Try-Me doesn't show message headers, so
read reject reasons from the worker log.

### Step 19. Publish your own test message with Try-Me!

Use the Try-Me **Publisher** to put a hand-typed message on an input queue, for example to see the dead-letter path:

1. **Try Me!** → **Publisher** → **Connect** (same defaults as the subscriber).
2. Select **Queue**, enter `q.seats.raw`, and select **Persistent**.
3. In **Message Content** enter `{"hello":"world"}` and click **Publish**.

**You should see**, in the worker window:

```
warn: ...Worker.Seats[0] Seats 19de...bb49 DEAD-LETTERED to queue:q.seats.dlq | The feed codec returned a field without a configured protection policy. | delivery 1 | ... ms
```

The worker treats a Try-Me message as a **raw** payload, because Try-Me sends no `presidio-payload-format` header.
`{"hello":"world"}` is not a valid Seats message, so it is rejected. If Try-Me is consuming `q.seats.dlq`
(step 18), the rejected message appears there straight away.

Limits:

- Try-Me message content is capped at **1024 characters**. The real samples are larger, and ACI, ETKT and PNR are
  binary, so send real feed messages with `FeedSampleSender` (step 16).
- Don't publish to the `airline/...` topics yourself; only the worker publishes protected output there.

### Step 20. Optional: run the Solace contract tests against the broker (window 2)

This runs the messaging contract from step 8 against the real broker:

```powershell
$env:SOLACE_CONTRACT_TESTS = "1"
dotnet run --project tests/Messaging.ContractTests
Remove-Item Env:SOLACE_CONTRACT_TESTS
```

**You should see:** four `PASS Solace: ...` lines and `All messaging contract checks passed.` The test creates
`contract.<id>` and `contract.<id>.dlq` queues and leaves them on the broker; `docker compose ... down` removes them.

### Step 21. Stop or reset

```powershell
# Worker (window 1): press Ctrl+C

# Stop the broker but keep queues and messages:
docker compose -f deploy/docker-compose.yml stop

# Start it again later (queues and messages are still there):
docker compose -f deploy/docker-compose.yml start

# Reset: delete the broker container with all queues and messages:
docker compose -f deploy/docker-compose.yml down
```

After a `down`, the next `up` (step 10) starts an empty broker. The worker recreates its 10 queues, but you must run
step 14 again for the watch queue.

---

## Daily quick start (after the first setup)

1. Start Docker Desktop.
2. Window 1:

   ```powershell
   cd <path-to>\PresidioDemo
   $env:SOLACE_PASSWORD = "local-only"
   $env:SOLACE_ADMIN_PASSWORD = "<your-local-admin-password>"
   docker compose -f deploy/docker-compose.yml up -d --wait
   dotnet run --project src/PresidioDemo.Worker
   ```

3. Window 2: set the variables (step 12), run step 14 if the broker was reset, then steps 16–17.

## Useful settings

Settings come from `src/PresidioDemo.Worker/appsettings.json`: the shared values first, then the selected
environment's section, then environment variables (`__` separates levels). Set any of these in window 1 before
`dotnet run`:

| Variable | Effect |
|---|---|
| `$env:Messaging__Feed = "PNR"` | The worker consumes only the PNR feed |
| `$env:Messaging__ConsumersPerTask = "4"` | 4 consumers per input queue (1–32) |
| `$env:Logging__LogLevel__Default = "Warning"` | Only warnings and errors, such as dead letters |

The feed protection rules are in `src/PresidioDemo.Core/Configuration/` (`feed-policies.xml`, `xdr-layouts.xml`,
`reference-layouts.xml`). The build copies them next to the worker.

## Troubleshooting

| Message or symptom | Cause | Fix |
|---|---|---|
| `winget` is not recognized | App Installer missing | Install **App Installer** from the Microsoft Store, then step 2 |
| `docker: ... cannot connect to the Docker API` / `dockerDesktopLinuxEngine` not found | Docker Desktop not running | Start it and wait for **Engine running** (step 3) |
| `Set SOLACE_ADMIN_PASSWORD for the broker admin user` | No admin password for Compose | `$env:SOLACE_ADMIN_PASSWORD = "..."` or `deploy\.env` (step 10) |
| Broker never becomes healthy | Not enough memory, or the port is in use | `docker logs presidio-solace --tail 30`; free ports 55555, 8080, 8008 |
| `Worker could not start: Solace password environment variable SOLACE_PASSWORD is not set.` | Variable missing in this window | Step 12 |
| `No src/.venv folder found in the project folder.` | Step 6 not done, or the venv is elsewhere | Create it at `src\.venv` (step 6) |
| `...python312.dll not found. Was Python 3.12 uninstalled?` | The base Python 3.12 moved or was removed | Reinstall Python 3.12, delete `src\.venv`, repeat step 6 |
| `Install the bundled en_core_web_lg model before starting the listener.` | Model not in the venv | `src\.venv\Scripts\python -m pip install -r requirements.txt` |
| `Worker stopped: ...` and exit code 1 | Broker unreachable or stopped | Start the broker (step 10); unacknowledged messages stay on the queue |
| `Build tests/FeedPipelineHarness first; missing ...` | Python tests run before a build | Step 7 |
| Queue script: `(401) Unauthorized` | Wrong `SOLACE_ADMIN_PASSWORD` in this window | Use the password the broker was started with |
| Queue script: `Unable to connect to the remote server` | Broker not running | Step 10 |
| Sender prints `SKIP ... (no route in Local)` | The file's feed has no route in the selected environment | Check `Environments:Local:Messaging:Routes` |
| Watch queue stays at 0 while the worker logs `PUBLISHED` | Watch queue created after sending, or a typo in `airline/*/protected` | Step 14, then send again |
| `.raw` queue shows 0 consumers | Worker not running or not READY | Check window 1 (step 13) |
| Try-Me subscriber stays on **Connecting** | Port 8008 not published | `docker ps` must list 8008 (step 10) |
| Try-Me: `The number of characters cannot exceed 1024.` | Try-Me size limit | Use `FeedSampleSender` for real samples |
