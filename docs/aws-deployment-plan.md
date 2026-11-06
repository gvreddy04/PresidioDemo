# AWS Deployment Plan (Dev, proposed)

Diagram: [aws-solace-ecs-deployment.svg](diagrams/aws-solace-ecs-deployment.svg) (earlier versions in [diagrams/old](diagrams/old): [v1](diagrams/old/aws-solace-ecs-deployment-v1.svg), [v2](diagrams/old/aws-solace-ecs-deployment-v2.svg), [v3](diagrams/old/aws-solace-ecs-deployment-v3.svg), [v4](diagrams/old/aws-solace-ecs-deployment-v4.svg), [v5](diagrams/old/aws-solace-ecs-deployment-v5.svg), [v6](diagrams/old/aws-solace-ecs-deployment-v6.svg), [v7](diagrams/old/aws-solace-ecs-deployment-v7.svg), [v8](diagrams/old/aws-solace-ecs-deployment-v8.svg)). The diagram keeps its wording generic. Specific values are on this page: connection settings, producers, consumers, queue mapping, sizing numbers, delivery counts and worker setting names. Review this page with the pipeline and infrastructure team before requesting resources. Names use the DemoAir stand-ins; real values are supplied at deploy time only.

## How it works

Harness CI builds the repository `Dockerfile` into one image. A .NET 10 SDK stage publishes the worker, and the runtime stage adds CPython 3.12, `presidio-analyzer` and `presidio-anonymizer` 2.2.364, and the spaCy `en_core_web_lg` model. CI tags the image with the git commit and pushes it to Amazon ECR. Harness CD then registers a task definition for each feed that points at that tag and rolls the five ECS Fargate services onto it. The tasks have no load balancer and no inbound ports. At start-up, ECS reads the Solace password from Secrets Manager into `SOLACE_PASSWORD`, and the rest of the settings come from `appsettings.json` and task environment variables.

Each feed (ACI, ETKT, PNR, Seats and PNR-Linking) runs as its own ECS service from the same image, so each feed scales on its own. A service always keeps at least one task and scales out up to the number of partitions in the feed's input queue (see [Scalability](#scalability)). Input queues are partitioned by PNR, so the messages for one PNR stay in order while tasks run in parallel. Each task reads its queue one message at a time over the native Solace client using TLS. It parses the message with the feed codec, masks known PII fields through Presidio running in the same process via Python.NET, validates and serializes the output, and publishes it to the feed's output. The task acknowledges the input only after the broker confirms the publish. The worker sends a rejected message, or one delivered more than 5 times, to the feed's dead letter queue, never to output. As a backstop, the broker moves a message to the same DLQ after 10 deliveries. The PII path makes no HTTP calls and uses no remote services.

## Resource list

The Existing? column records what we assume; confirm each row with the platform team. Reuse an existing resource wherever one fits.

| # | Resource | Specification | Existing? |
|---|---|---|---|
| 1 | ECR repository | `presidio-worker`, scan on push, immutable tags; image about 1.8 GB, linux/amd64 | New |
| 2 | ECS cluster | Fargate capacity | Reuse if one exists |
| 3 | ECS task definitions, 5 | One per feed, such as `presidio-worker-pnr-dev`, all using the same image; 1 vCPU, 4 GB; `stopTimeout` 40 s; `DOTNET_ENVIRONMENT=Dev`; the feed setting and that feed's route; `Solace__*` environment variables; `secrets` entry for `SOLACE_PASSWORD`; `awslogs` driver | New (Harness registers them) |
| 4 | ECS services, 5 | One per feed; min 1 task; no load balancer; rolling deploy | New |
| 4a | Service auto scaling, 5 | Min sized for the normal peak (1 per feed); scheduled actions raise it before holidays; CPU target tracking at 60 % with parallel consumers; max = the feed queue's partition count | New |
| 5 | Task execution role | `ecr:GetAuthorizationToken` and image pull; `logs:CreateLogStream` and `PutLogEvents`; `secretsmanager:GetSecretValue` on the one secret; `kms:Decrypt` if a customer-managed key is used | New |
| 6 | Task role | Not needed, because the worker calls no AWS API | — |
| 7 | Secrets Manager secret | `piidemo/dev/solace`, JSON key `password`; the broker owner sets the value | New |
| 8 | KMS key | For the secret and the log group | Reuse an existing key, or the AWS-managed key |
| 9 | CloudWatch log groups and alarms | One log group per feed, such as `/ecs/presidio-worker-pnr-dev`, with 30-day retention; CPU alarms for scaling; an alarm when a service's running task count drops below 1 | New |
| 10 | VPC and private subnets | Subnets that can route to the Solace broker | Reuse |
| 11 | Security group | No inbound rules; outbound TCP 55443 to the broker and 443 to the VPC endpoints | New |
| 12 | VPC endpoints, or NAT | `ecr.api`, `ecr.dkr`, S3 gateway, `logs`, `secretsmanager` (platform traffic only) | Reuse if present |
| 13 | Network path to the broker | The broker is in AWS in the same region: VPC peering or Transit Gateway to its account, plus the broker-side firewall rule for the ECS subnet CIDRs | Reuse if a path exists |
| 14 | Harness | CI pipeline (build and push), CD service and environment with an ECS rolling deploy, an AWS connector, and a delegate that can reach AWS | Confirm what exists |
| 15 | Harness deploy role | `ecs:RegisterTaskDefinition`, `UpdateService`, `DescribeServices`; `iam:PassRole` on the execution role; ECR push for CI | New, or extend an existing role |

## Scalability

**Target:** 2,000 messages per second in total across the five feeds. The split between feeds is still to be confirmed.

**Decided:**

- **Parallel consumers per task.** Each task opens several broker flows, so 15–30 tasks cover 2,000 per second and far fewer new tasks ever start. Built: set `Messaging__ConsumersPerTask` in each task definition, such as `8`, once that feed's queue is partitioned. All consumers share the task's one broker connection.
- **Numbers:** 8 consumers per task and 32 partitions per feed, so at most 4 tasks per feed and 20 in total. That covers 2,000 per second only if no single feed carries more than about 400–800 per second. If one feed carries most of the load, raise that feed's partitions; no code change is needed.
- **Start tasks before they're needed.** Each service's minimum is sized for the normal peak (50,000–100,000 messages an hour, about 28 a second in total, so one task per feed). Scheduled scaling raises the minimums before holidays, so slow task start-up rarely matters. CPU target tracking at 60 % handles the rest, up to the partition count.

**What one message costs:** decoded-text detection runs local Presidio on every decoded string, with spaCy NER on. In the detection spike (17 production samples, 20 rounds, one process, no broker) one task handled about 50 messages per second overall: ACI 57, ETKT 69, PNR 14, PNR-Linking about 1,600 and Seats about 2,000 per second. PNR is the slowest because each message holds about 90 strings. Presidio calls are serialized inside a task, so extra consumers per task overlap broker round trips but do not add detection throughput; scale with tasks. If the 2,000 per second peak were shared equally by the five feeds, it would need about 44 tasks: ACI 7, ETKT 6, PNR 29, PNR-Linking 1, Seats 1. Confirm the real split between feeds before sizing. The figures come from `dotnet run --project tests/DetectionSpike -- spike` and are recorded in [tests/fixtures/spike-summary.json](../tests/fixtures/spike-summary.json). In production, the round trip to the broker adds to this.

| | One consumer per task (`ConsumersPerTask=1`) | Several consumers per task (chosen) |
|---|---|---|
| What limits throughput | The wait on the broker; CPU stays low | CPU |
| Messages per second, one 1-vCPU task | 14–65, depending on broker distance; with detection, at most the spike rates above | About 100–200 before detection; with detection, at most the spike rates above |
| Tasks for 2,000 per second, with headroom | About 40 (broker in the same region) to 185 (broker about 30 ms away) | About 15–30 |
| Scaling signal | Queue depth, or a fixed count per feed | CPU target tracking at 60 % |
| Approximate Fargate cost at full scale | About $2–11 per hour | About $1–2 per hour |

`Messaging:ConsumersPerTask` opens several broker flows per task on the feed's partitioned queue. Each flow still reads one message at a time, so the order for each partition key holds. On an exclusive queue, the extra flows only wait as standby.

Limits to confirm before requesting infrastructure:

- **Broker:** about 2,000 persistent messages per second in and the same out; one connection per task, and one flow per consumer on top; enough partitions per feed for its maximum number of consumers.
- **AWS:** a Fargate quota of about 60–80 vCPU, allowing for rolling deploys; about 100 free IP addresses in the subnets.
- **Start-up time:** a new task pulls a 1.8 GB image and loads the spaCy model before it takes messages. Keep each service's minimum task count sized for the normal peak, and use scheduled scaling before holidays.

Not measured yet: CPU per message on Fargate, real message sizes, and task start-up time. A load test in Dev should set the final task counts.

## Solace configuration

Configured in `appsettings.json` (`Environments:Dev`): host `tcps://…:55443`, VPN, username, client name and the password variable name. Routes are configured for ACI, ETKT (the TKT queue) and PNR. Real values are applied as environment variables at deploy time.

Confirmed:

- The broker is in AWS in the same region as the ECS tasks.
- ACI, ETKT and PNR come from the reservation host through IBM MQ and Solace micro-integrations. The TKT queue carries the ETKT feed.
- Seats and PNR-Linking come from the operations apps into the same broker.
- The masked output goes to the partner core system.

Still needed from the broker owner:

- Partitioned input queues, 32 partitions per feed, keyed by PNR for every feed. The publishers must set the key. The Seats and PNR-Linking JSON already carries a record-locator field. For ACI, ETKT and PNR, the micro-integration must read the PNR from the binary host record, or receive it in an MQ header; confirm it can.
- Input queues for Seats and PNR-Linking in Dev, on the same broker as the others.
- For each protected topic: an output queue and its topic subscription, consumed by the partner core system. The partner should confirm it can take 2,000 messages per second, and whether it needs the order kept for each PNR. The order is kept for now as the safe default.
- One dead letter queue per feed, created by the broker team with the proposed `…DLQ.1` names and the same access limits as its input queue, because it holds raw PII.
- Each input queue's dead message queue set to the feed's DLQ, with max redelivery 10, as the broker backstop.
- An ACL profile for `piidemo-app` that can consume the input queues, publish to the protected topics, and send to the DLQs.
- Delivery count enabled on the input queues. Without it, the worker cannot count failed deliveries and move a failing message to its DLQ.
- The broker CA certificate, if the broker does not use a public CA. It can be baked into the image under `/app/certs`, because it is not secret.

## Worker settings for per-feed services

- **One feed per task:** set `Messaging__Feed` in each task definition, such as `Messaging__Feed=PNR`. The process then consumes only that feed's route. When it is empty, the process runs every routed feed.
- **Unique client name per task:** `Solace:UniqueClientName` is on in `Dev`. It appends the host name and a random part to `Solace:ClientName`, so scaled-out tasks don't disconnect each other. The host name on Fargate is the task's private address, so the name is traceable without the ECS metadata endpoint, which is an HTTP call.

## Solace password from Secrets Manager

The worker already reads the password from the `SOLACE_PASSWORD` environment variable, so this needs no code change. The task definition maps the secret into that variable, and ECS fetches it when the task starts:

```json
"secrets": [
  { "name": "SOLACE_PASSWORD", "valueFrom": "arn:aws:secretsmanager:<region>:<account>:secret:piidemo/dev/solace-<suffix>:password::" }
]
```

Access needed: `secretsmanager:GetSecretValue` on that secret for the task execution role, plus `kms:Decrypt` if the secret uses a customer-managed key. To rotate the password, update the secret, then redeploy or restart the task.

## Build and pipeline status

- **Docker image:** the `Dockerfile` and `requirements.txt` in the repository contain the .NET and Python packages listed above. `deploy/publish-image.ps1` builds the image for linux/amd64 and pushes it to ECR. The image was not rebuilt while this page was prepared.
- **To confirm with the pipeline team:** whether Harness CI builds this `Dockerfile` successfully, whether it pushes to ECR, and what remains for the ECS deploy stage (the service, the task definition source, the AWS connector and the delegate).
