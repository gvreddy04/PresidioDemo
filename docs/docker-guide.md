# Run the Feed Worker in Docker

How to build the worker image and run it with Docker Desktop: Part A uses a local broker in a container, Part B uses the Dev broker, and Part C publishes the image to a registry.

## Where to run the commands

Run every command in **PowerShell**, from the **project root**: the folder that contains `PresidioDemo.slnx`, `Dockerfile` and the `deploy` folder. On the development machine this is `D:\Bala_Support\Projects\PresidioDemo`; use your own clone's path if it differs.

Every new PowerShell window starts somewhere else, so begin each one with:

```powershell
Set-Location D:\Bala_Support\Projects\PresidioDemo
```

Check that you are in the right folder; this prints `True`:

```powershell
Test-Path .\PresidioDemo.slnx
```

All paths in this guide, such as `deploy/docker-compose.yml`, are relative to the project root. The compose files read `deploy/.env`, `deploy/.env.dev` and `deploy/certs/` relative to the `deploy` folder, so they work as long as you pass `-f deploy/...` from the project root.

## Before you start

Run these checks in PowerShell from the project root.

| Requirement | Check |
|---|---|
| Docker Desktop with the WSL2 backend, running | `docker info --format '{{.ServerVersion}}'` prints a version |
| At least 10 GB free on C (Docker keeps its data there) | `[math]::Round((Get-PSDrive C).Free/1GB,1)` |
| .NET 10 SDK, only to send sample files from the host | `dotnet --version` |
| The latest `main` | `git pull` |

## Step 1: Build the image

**Run in:** project root. The trailing `.` tells Docker to use the project root as the build context, where the `Dockerfile` is.

```powershell
Set-Location D:\Bala_Support\Projects\PresidioDemo
docker build -t presidio-worker:dev .
```

The first build takes about 3 minutes; later builds reuse the cache. The image (about 1.8 GB) contains the .NET worker, CPython 3.12, Presidio and the spaCy model. It exposes no ports and makes no HTTP calls at runtime.

Check the image (any folder):

```powershell
docker images presidio-worker
```

## Part A: Local broker

### Step 2: Set the broker admin password

**File:** `deploy\.env` under the project root, for example `D:\Bala_Support\Projects\PresidioDemo\deploy\.env`. It is git-ignored. Create it once, if it does not exist, with a password of your choice:

```text
SOLACE_ADMIN_PASSWORD=<choose one>
```

### Step 3: Start the broker and the worker

**Run in:** project root.

```powershell
Set-Location D:\Bala_Support\Projects\PresidioDemo
docker compose -f deploy/docker-compose.yml --profile worker up -d
```

The broker starts first; the worker starts once the broker reports healthy, after about 30 to 45 seconds. Compose reuses the image from Step 1; add `--build` to rebuild after code changes.

### Step 4: Wait for READY

**Run in:** project root (same window as Step 3).

```powershell
docker compose -f deploy/docker-compose.yml logs -f worker
```

Expected output:

```text
Connected to Solace tcp://solace:55555 (VPN default) as presidio-worker-container.
Initializing embedded local Presidio and the bundled NLP model...
ACI worker reading q.aci.raw; output airline/aci/protected; dead letter q.aci.dlq.
...
READY: 5 feed workers on Solace.
```

Press Ctrl+C to stop following the log; the worker keeps running.

### Step 5: Send the sample files

**Run in:** project root, in a **second** PowerShell window. The sender reads `docs/sample-docs` and `src/PresidioDemo.Worker/appsettings.json` from the project. The local broker accepts any password.

```powershell
Set-Location D:\Bala_Support\Projects\PresidioDemo
$env:SOLACE_PASSWORD = "local-only"
dotnet run --project tools/FeedSampleSender
```

The sender connects as `presidio-worker-local-sender`, so it does not disconnect the worker container.

### Step 6: Check the results

**Run in:** any folder (the command uses the container name).

```powershell
docker logs presidio-worker-local
```

Expect 17 `PUBLISHED` and 3 `DEAD-LETTERED` lines; the three dead-lettered files are the `_Negative_Testing` samples. To inspect protected messages, open PubSub+ Manager at http://localhost:8080 (user `admin`) and create a queue subscribed to `airline/>` before sending the samples; a topic keeps nothing without a subscriber.

### Step 7: Stop when you are done

**Run in:** project root. The worker holds about 1.5 GB of memory, so do not leave it running.

```powershell
Set-Location D:\Bala_Support\Projects\PresidioDemo

# Stop both containers and keep the broker's queues
docker compose -f deploy/docker-compose.yml --profile worker stop

# Or stop and remove them, including the queues
docker compose -f deploy/docker-compose.yml --profile worker down
```

## Part B: Dev broker

### Step 8: Create the Dev settings file

**Run in:** project root. This creates `deploy\.env.dev` from the committed template.

```powershell
Set-Location D:\Bala_Support\Projects\PresidioDemo
Copy-Item deploy/.env.dev.example deploy/.env.dev
notepad deploy/.env.dev
```

In `deploy\.env.dev`, replace the DemoAir stand-ins with the real Dev values:

- `SOLACE_PASSWORD`, `Solace__Host`, `Solace__Vpn` and `Solace__Username`.
- `Solace__ClientName`: a name of your own. The broker disconnects an existing connection that uses the same client name.
- The input queue, output topic and dead-letter queue for each route: 0 is ACI, 1 is ETKT (the TKT queue) and 2 is PNR.

`deploy\.env.dev` is git-ignored. Never commit it, and never copy real values into a committed file.

The Dev queues must already exist, because the worker does not create queues outside the Local environment. The Dev user must be allowed to consume the input queues and publish to the output topics.

### Step 9: Add the broker's CA certificate

**Folder:** `deploy\certs` under the project root (git-ignored). The container reads it as `/app/certs`.

**Run in:** project root. Replace the source path with where you saved the certificate.

```powershell
Set-Location D:\Bala_Support\Projects\PresidioDemo
New-Item -ItemType Directory -Force deploy/certs | Out-Null
Copy-Item <path-to-the-broker-ca.pem> deploy/certs/
```

### Step 10: Start the worker

**Run in:** project root.

```powershell
Set-Location D:\Bala_Support\Projects\PresidioDemo
docker compose -f deploy/docker-compose.dev.yml up -d
docker compose -f deploy/docker-compose.dev.yml logs -f worker
```

Wait for `READY: 3 feed workers on Solace.`. The log also reports that PNR-Linking and Seats have no route and are not consumed; this is expected for Dev.

### Step 11: Send samples (only with the broker owner's approval)

**Run in:** project root, in a **new** PowerShell window, so the Dev values do not stay in a window you use for other work. These commands load `deploy\.env.dev` into this window only, then run the sender, which skips feeds that Dev does not route.

```powershell
Set-Location D:\Bala_Support\Projects\PresidioDemo
Get-Content deploy/.env.dev | Where-Object { $_ -match '^[A-Za-z_]+=' } | ForEach-Object { $name, $value = $_ -split '=', 2; Set-Item "env:$name" $value }
$env:DOTNET_ENVIRONMENT = "Dev"
$env:Solace__TrustStoreFolder = "$PWD\deploy\certs"
dotnet run --project tools/FeedSampleSender
```

Close this window when you are done, so the password does not stay in memory.

### Step 12: Stop the worker

**Run in:** project root.

```powershell
Set-Location D:\Bala_Support\Projects\PresidioDemo
docker compose -f deploy/docker-compose.dev.yml down
```

The worker finishes the current messages first. Unacknowledged messages stay on the broker and are delivered again on the next start.

## Part C: Publish the image

**Script:** `deploy\publish-image.ps1` under the project root. It finds the project from its own location, so it builds the right folder from anywhere; the commands below assume the project root so the `./deploy/...` path works.

It builds the image for `linux/amd64`, tags it with the current commit and pushes it. It refuses uncommitted changes unless you pass `-AllowDirty`.

**Run in:** project root.

```powershell
Set-Location D:\Bala_Support\Projects\PresidioDemo

# Allow local scripts in this window only, if PowerShell blocks them
Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned

# Preview the commands without building or pushing
./deploy/publish-image.ps1 -Registry localhost:5000 -DryRun

# Amazon ECR: signs in through the AWS CLI with your AWS profile; the repository must already exist
./deploy/publish-image.ps1 -Registry <account>.dkr.ecr.<region>.amazonaws.com -ExtraTag dev

# Any other registry: sign in first
docker login
./deploy/publish-image.ps1 -Registry docker.io/<user>
```

The script prints the pushed digest. Reference the digest or the commit tag in the ECS task definition, because moving tags such as `dev` can change.

## Troubleshooting

| What you see | Cause and fix |
|---|---|
| `compose file "...\deploy\docker-compose.yml" is invalid: open ...: The system cannot find the path specified` or `failed to read dockerfile: open Dockerfile: no such file or directory` | You are not in the project root. Run `Set-Location D:\Bala_Support\Projects\PresidioDemo` and repeat. |
| `publish-image.ps1 cannot be loaded because running scripts is disabled` | Run `Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned` in that window, then repeat. |
| `Worker could not start: Solace password environment variable SOLACE_PASSWORD is not set` | `SOLACE_PASSWORD` is missing from `deploy\.env.dev`, or from the window that runs the sender. |
| `host name could not be resolved` | Docker cannot reach the Dev host. Check `Solace__Host`, and that the VPN and DNS work from Docker Desktop. |
| TLS or certificate errors | The CA certificate is missing from `deploy\certs` or is the wrong one. |
| The worker keeps disconnecting | Another connection uses the same client name. Change `Solace__ClientName`. |
| `env file ... .env.dev not found` | Do Step 8 first, from the project root. |
| `Solace:TrustStoreFolder does not exist` (sender) | Do Step 9, then run Step 11 from the project root so `$PWD\deploy\certs` points at the right folder. |
| Docker crashes, or the build fails with no space left | C is full. Free space, or restart Windows if the page file has grown, then repeat the step. |
| The container restarts repeatedly | The settings are invalid or the broker is unreachable; see `docker logs presidio-worker-dev`. The Dev container gives up after 5 restarts. |

## Reference

| Item | Local | Dev |
|---|---|---|
| Run commands from | Project root | Project root |
| Compose file | `deploy/docker-compose.yml` with `--profile worker` | `deploy/docker-compose.dev.yml` |
| Secrets file | `deploy\.env` (broker admin password) | `deploy\.env.dev` (Dev values and password) |
| Certificates | None | `deploy\certs` |
| Container | `presidio-worker-local` | `presidio-worker-dev` |
| Settings | `Environments:Local` in `src/PresidioDemo.Worker/appsettings.json`, plus compose variables | `Environments:Dev`, overridden by `deploy\.env.dev` |
| Feeds | ACI, ETKT, PNR, PNR-Linking, Seats | ACI, ETKT, PNR |
| Queues | Created by the worker on startup | Must already exist |
| Broker connection | `tcp://solace:55555`, no TLS | `tcps://`, CA from `deploy\certs` |
| Memory limit | 3 GB | 3 GB |
| Stop grace period | 40 seconds | 40 seconds |
