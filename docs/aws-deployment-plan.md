# AWS Deployment Plan (Dev, proposed)

Diagram: [aws-solace-ecs-deployment-v3.svg](diagrams/aws-solace-ecs-deployment-v3.svg) (earlier: [v1](diagrams/aws-solace-ecs-deployment-v1.svg), [v2](diagrams/aws-solace-ecs-deployment-v2.svg)). Review this page with the pipeline and infrastructure team before requesting resources. Names use the DemoAir stand-ins; real values are supplied at deploy time only.

## How it works

Harness CI builds the repository `Dockerfile` into one image. A .NET 10 SDK stage publishes the worker, and the runtime stage adds CPython 3.12, `presidio-analyzer` and `presidio-anonymizer` 2.2.364, and the spaCy `en_core_web_lg` model. CI tags the image with the git commit and pushes it to Amazon ECR. Harness CD then registers a task definition for each feed that points at that tag and rolls the five ECS Fargate services onto it. The tasks have no load balancer and no inbound ports. At start-up, ECS reads the Solace password from Secrets Manager into `SOLACE_PASSWORD`, and the rest of the settings come from `appsettings.json` and task environment variables.

Each feed (ACI, ETKT, PNR, Seats and PNR-Linking) runs as its own ECS service from the same image, so each feed scales on its own. A service always keeps at least one task. It adds a task when average CPU passes 60 %, up to the number of partitions in the feed's input queue. Input queues are partitioned by PNR, so the messages for one PNR stay in order while tasks run in parallel. Each task reads its queue one message at a time over the native Solace client using TLS. It parses the message with the feed codec, masks known PII fields through Presidio running in the same process via Python.NET, validates and serializes the output, and publishes it to the feed's output. The task acknowledges the input only after the broker confirms the publish. The worker sends a rejected message, or one delivered more than 5 times, to the feed's dead letter queue, never to output. As a backstop, the broker moves a message to the same DLQ after 10 deliveries. The PII path makes no HTTP calls and uses no remote services.

## Resource list

The Existing? column records what we assume; confirm each row with the platform team. Reuse an existing resource wherever one fits.

| # | Resource | Specification | Existing? |
|---|---|---|---|
| 1 | ECR repository | `presidio-worker`, scan on push, immutable tags; image about 1.8 GB, linux/amd64 | New |
| 2 | ECS cluster | Fargate capacity | Reuse if one exists |
| 3 | ECS task definitions, 5 | One per feed, such as `presidio-worker-pnr-dev`, all using the same image; 1 vCPU, 4 GB; `stopTimeout` 40 s; `DOTNET_ENVIRONMENT=Dev`; the feed setting and that feed's route; `Solace__*` environment variables; `secrets` entry for `SOLACE_PASSWORD`; `awslogs` driver | New (Harness registers them) |
| 4 | ECS services, 5 | One per feed; min 1 task; no load balancer; rolling deploy | New |
| 4a | Service auto scaling, 5 | Target tracking at 60 % average CPU; min 1, max = the feed queue's partition count | New |
| 5 | Task execution role | `ecr:GetAuthorizationToken` and image pull; `logs:CreateLogStream` and `PutLogEvents`; `secretsmanager:GetSecretValue` on the one secret; `kms:Decrypt` if a customer-managed key is used | New |
| 6 | Task role | Not needed, because the worker calls no AWS API | — |
| 7 | Secrets Manager secret | `piidemo/dev/solace`, JSON key `password`; the broker owner sets the value | New |
| 8 | KMS key | For the secret and the log group | Reuse an existing key, or the AWS-managed key |
| 9 | CloudWatch log groups and alarms | One log group per feed, such as `/ecs/presidio-worker-pnr-dev`, with 30-day retention; CPU alarms for scaling; an alarm when a service's running task count drops below 1 | New |
| 10 | VPC and private subnets | Subnets that can route to the Solace broker | Reuse |
| 11 | Security group | No inbound rules; outbound TCP 55443 to the broker and 443 to the VPC endpoints | New |
| 12 | VPC endpoints, or NAT | `ecr.api`, `ecr.dkr`, S3 gateway, `logs`, `secretsmanager` (platform traffic only) | Reuse if present |
| 13 | Network path to the broker | Peering, Transit Gateway, PrivateLink or VPN, plus the broker-side firewall rule for the ECS subnet CIDRs | To confirm |
| 14 | Harness | CI pipeline (build and push), CD service and environment with an ECS rolling deploy, an AWS connector, and a delegate that can reach AWS | Confirm what exists |
| 15 | Harness deploy role | `ecs:RegisterTaskDefinition`, `UpdateService`, `DescribeServices`; `iam:PassRole` on the execution role; ECR push for CI | New, or extend an existing role |

## Solace configuration

Configured in `appsettings.json` (`Environments:Dev`): host `tcps://…:55443`, VPN, username, client name and the password variable name. Routes are configured for ACI, ETKT (the TKT queue) and PNR. Real values are applied as environment variables at deploy time.

Still needed from the broker owner:

- Partitioned input queues, with the partition count per feed. The upstream publisher must set PNR as the partition key; confirm the key for each feed.
- Input queues for Seats and PNR-Linking in Dev. Both are routed locally, but neither has a Dev queue.
- For each protected topic: an output queue, its topic subscription, and the consuming system.
- One dead letter queue per feed, created with the same access limits as its input queue, because it holds raw PII. The current names are proposals.
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
