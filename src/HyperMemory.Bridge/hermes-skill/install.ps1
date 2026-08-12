param(
    [Parameter(Mandatory = $true)][string]$PublishedBridgeDirectory,
    [string]$HermesSkillsRoot = ""
)
$ErrorActionPreference = "Stop"
$hermesRoot = $env:HERMES_HOME
if ([string]::IsNullOrWhiteSpace($HermesSkillsRoot)) {
    if (-not [string]::IsNullOrWhiteSpace($hermesRoot)) {
        $HermesSkillsRoot = Join-Path $hermesRoot "skills"
    } elseif ($IsWindows) {
        $desktopRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "hermes"
        if (Test-Path -LiteralPath $desktopRoot -PathType Container) {
            $HermesSkillsRoot = Join-Path $desktopRoot "skills"
        }
    }
    if ([string]::IsNullOrWhiteSpace($HermesSkillsRoot)) {
        $userRoot = [Environment]::GetFolderPath('UserProfile')
        $HermesSkillsRoot = Join-Path (Join-Path $userRoot ".hermes") "skills"
    }
}
$source = (Resolve-Path -LiteralPath $PublishedBridgeDirectory).Path
$target = Join-Path $HermesSkillsRoot "hyper-memory"
if (Test-Path -LiteralPath $target) {
    throw "Refusing to overwrite existing skill directory: $target"
}
New-Item -ItemType Directory -Path (Join-Path $target "bin") -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "SKILL.md") -Destination $target
Copy-Item -Path (Join-Path $source "*") -Destination (Join-Path $target "bin") -Recurse
Write-Output "Installed append-only HyperMemory skill at $target"
