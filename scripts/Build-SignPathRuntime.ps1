param([string]$Runtime = "win-x64")

$ErrorActionPreference = "Stop"
$repository = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repository "artifacts\signpath-runtime"
$publish = Join-Path $repository "artifacts\signpath-runtime-publish"
$api = Join-Path $output "api"
$bridge = Join-Path $output "bridge"
New-Item -ItemType Directory -Path $api -Force | Out-Null
New-Item -ItemType Directory -Path $bridge -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $publish "api") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $publish "bridge") -Force | Out-Null

dotnet publish (Join-Path $repository "src\HyperMemory.Api\HyperMemory.Api.csproj") -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $publish "api")
if ($LASTEXITCODE -ne 0) { throw "API publication failed." }
dotnet publish (Join-Path $repository "src\HyperMemory.Bridge\HyperMemory.Bridge.csproj") -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $publish "bridge")
if ($LASTEXITCODE -ne 0) { throw "Bridge publication failed." }

Copy-Item -LiteralPath (Join-Path $publish "api\HyperMemory.Api.exe") -Destination (Join-Path $api "HyperMemory.Api.exe")
Copy-Item -LiteralPath (Join-Path $publish "bridge\HyperMemory.Bridge.exe") -Destination (Join-Path $bridge "HyperMemory.Bridge.exe")
Write-Output $output
