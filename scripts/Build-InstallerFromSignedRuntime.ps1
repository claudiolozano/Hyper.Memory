param(
    [Parameter(Mandatory = $true)][string]$SignedRuntimeDirectory,
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$repository = Split-Path -Parent $PSScriptRoot
$source = [IO.Path]::GetFullPath($SignedRuntimeDirectory)
$api = Join-Path $source "api\HyperMemory.Api.exe"
$bridge = Join-Path $source "bridge\HyperMemory.Bridge.exe"
if (-not (Test-Path -LiteralPath $api -PathType Leaf)) { throw "Signed API is missing." }
if (-not (Test-Path -LiteralPath $bridge -PathType Leaf)) { throw "Signed Bridge is missing." }
if ((Get-AuthenticodeSignature -LiteralPath $api).Status -ne "Valid") { throw "API signature is not valid." }
if ((Get-AuthenticodeSignature -LiteralPath $bridge).Status -ne "Valid") { throw "Bridge signature is not valid." }

$payloadRoot = Join-Path $repository "artifacts\signpath-payload"
$installerOutput = Join-Path $repository "artifacts\signpath-installer"
$payload = Join-Path $repository "src\HyperMemory.Installer\payload.zip"
New-Item -ItemType Directory -Path (Join-Path $payloadRoot "api") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $payloadRoot "bridge") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $payloadRoot "skill") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $payloadRoot "plugin") -Force | Out-Null
New-Item -ItemType Directory -Path $installerOutput -Force | Out-Null
Copy-Item -LiteralPath $api -Destination (Join-Path $payloadRoot "api\HyperMemory.Api.exe")
Copy-Item -LiteralPath $bridge -Destination (Join-Path $payloadRoot "bridge\HyperMemory.Bridge.exe")
Copy-Item -LiteralPath (Join-Path $repository "src\HyperMemory.Bridge\hermes-skill\SKILL.md") -Destination (Join-Path $payloadRoot "skill\SKILL.md")
Copy-Item -LiteralPath (Join-Path $repository "src\HyperMemory.Bridge\hermes-plugin\__init__.py") -Destination (Join-Path $payloadRoot "plugin\__init__.py")
Copy-Item -LiteralPath (Join-Path $repository "src\HyperMemory.Bridge\hermes-plugin\plugin.yaml") -Destination (Join-Path $payloadRoot "plugin\plugin.yaml")
Copy-Item -LiteralPath (Join-Path $repository "src\HyperMemory.Bridge\hermes-plugin\README.md") -Destination (Join-Path $payloadRoot "plugin\README.md")
Compress-Archive -Path (Join-Path $payloadRoot "*") -DestinationPath $payload -CompressionLevel Optimal -Force

dotnet publish (Join-Path $repository "src\HyperMemory.Installer\HyperMemory.Installer.csproj") -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $installerOutput
if ($LASTEXITCODE -ne 0) { throw "Installer publication failed." }
Write-Output $installerOutput
