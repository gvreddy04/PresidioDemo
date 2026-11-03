<#
.SYNOPSIS
Builds the feed worker image and pushes it to a container registry.

.DESCRIPTION
Builds the Dockerfile at the repository root for linux/amd64 and tags the image with the current commit,
so each pushed tag is immutable. -ExtraTag adds moving tags such as dev.

Sign-in: for an Amazon ECR registry (<account>.dkr.ecr.<region>.amazonaws.com) the script signs in with the
AWS CLI using your configured AWS profile; for any other registry, run docker login first. The script never
stores or accepts a password. The ECR repository must already exist.

Run from any folder in Windows PowerShell 5.1 or PowerShell 7.

.EXAMPLE
./deploy/publish-image.ps1 -Registry 123456789012.dkr.ecr.us-east-1.amazonaws.com -ExtraTag dev

.EXAMPLE
docker login
./deploy/publish-image.ps1 -Registry docker.io/your-user

.EXAMPLE
./deploy/publish-image.ps1 -Registry localhost:5000 -DryRun
#>
param(
    # Registry host and optional namespace, such as 123456789012.dkr.ecr.us-east-1.amazonaws.com or docker.io/your-user.
    [Parameter(Mandatory = $true)] [string] $Registry,
    # Repository name inside the registry.
    [string] $Repository = 'presidio-worker',
    # Immutable tag; defaults to the current commit's short hash.
    [string] $Tag,
    # Additional moving tags, such as dev.
    [string[]] $ExtraTag = @(),
    # Target platform; ECS on Fargate runs linux/amd64 unless the task definition selects ARM64.
    [string] $Platform = 'linux/amd64',
    # Publish from a working tree with uncommitted changes; the tag gets a -dirty suffix.
    [switch] $AllowDirty,
    # Print the commands without building, signing in or pushing.
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Runs a native command, echoing it first; stops on a non-zero exit code.
function Invoke-Native([string] $File, [string[]] $Arguments) {
    Write-Host ("> {0} {1}" -f $File, ($Arguments -join ' ')) -ForegroundColor Cyan
    if ($DryRun) { return }
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File exited with code $LASTEXITCODE." }
}

foreach ($tool in @('docker', 'git')) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool is not installed or not on PATH." }
}
if (-not $DryRun) {
    docker info --format '{{.ServerVersion}}' *> $null
    if ($LASTEXITCODE -ne 0) { throw 'The Docker daemon is not running. Start Docker Desktop and retry.' }
}

# The commit identifies exactly what the image contains, so uncommitted changes are refused by default.
$commit = (git -C $root rev-parse HEAD).Trim()
$dirty = [bool](git -C $root status --porcelain)
if ($dirty -and -not $AllowDirty) {
    throw 'The working tree has uncommitted changes. Commit them, or pass -AllowDirty to publish a -dirty tag.'
}
if (-not $Tag) {
    $Tag = $commit.Substring(0, 12)
    if ($dirty) { $Tag += '-dirty' }
}

$registryName = $Registry.TrimEnd('/')
$image = "$registryName/$Repository"
$tags = @($Tag) + @($ExtraTag | Where-Object { $_ -and $_ -ne $Tag })

# Amazon ECR: sign in with a short-lived token from the AWS CLI; the token goes through stdin only.
if ($registryName -match '^(\d{12})\.dkr\.ecr\.([a-z0-9-]+)\.amazonaws\.com$') {
    $region = $Matches[2]
    Write-Host "> aws ecr get-login-password --region $region | docker login --username AWS --password-stdin $registryName" -ForegroundColor Cyan
    if (-not $DryRun) {
        if (-not (Get-Command aws -ErrorAction SilentlyContinue)) { throw 'Amazon ECR sign-in needs the AWS CLI on PATH.' }
        aws ecr describe-repositories --region $region --repository-names $Repository *> $null
        if ($LASTEXITCODE -ne 0) { throw "ECR repository $Repository was not found in $region, or your AWS profile cannot read it." }
        aws ecr get-login-password --region $region | docker login --username AWS --password-stdin $registryName
        if ($LASTEXITCODE -ne 0) { throw 'Amazon ECR sign-in failed.' }
    }
}

$build = @('build', '--platform', $Platform,
    '--label', "org.opencontainers.image.revision=$commit",
    '--label', "org.opencontainers.image.created=$((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'))",
    '--label', 'org.opencontainers.image.title=presidio-worker')
foreach ($t in $tags) { $build += @('--tag', "${image}:$t") }
$build += $root
Invoke-Native docker $build

foreach ($t in $tags) { Invoke-Native docker @('push', "${image}:$t") }

if (-not $DryRun) {
    $digest = docker inspect --format '{{index .RepoDigests 0}}' "${image}:$Tag"
    Write-Host ''
    Write-Host "Published $image with tags: $($tags -join ', ')" -ForegroundColor Green
    Write-Host "Digest: $digest"
    Write-Host 'Reference the digest (or the commit tag) in the ECS task definition; moving tags such as dev can change.'
}
