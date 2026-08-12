param(
    [Parameter(Mandatory = $true)][string]$PublishedBridgeDirectory,
    [string]$HermesSkillsRoot = (Join-Path $HOME ".hermes\skills")
)
$ErrorActionPreference = "Stop"
$source = (Resolve-Path -LiteralPath $PublishedBridgeDirectory).Path
$target = Join-Path $HermesSkillsRoot "hyper-memory"
if (Test-Path -LiteralPath $target) {
    throw "Refusing to overwrite existing skill directory: $target"
}
New-Item -ItemType Directory -Path (Join-Path $target "bin") -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "SKILL.md") -Destination $target
Copy-Item -Path (Join-Path $source "*") -Destination (Join-Path $target "bin") -Recurse
Write-Output "Installed append-only HyperMemory skill at $target"
