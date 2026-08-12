param(
    [Parameter(Mandatory = $true)]
    [string]$StorageBasePath,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$base = [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($StorageBasePath))
$root = if ([System.IO.Path]::GetFileName($base.TrimEnd('\', '/')) -ieq "Hyper_Memory") { $base.TrimEnd('\', '/') } else { Join-Path $base "Hyper_Memory" }
$root = [System.IO.Path]::GetFullPath($root)
if ([System.IO.Path]::GetFileName($root) -ine "Hyper_Memory") { throw "Resolved folder must be named Hyper_Memory." }
if (Test-Path -LiteralPath $root -PathType Leaf) { throw "Target is a file: $root" }
New-Item -ItemType Directory -Path $root -Force | Out-Null
$rootInfo = Get-Item -LiteralPath $root
if (($rootInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Hyper_Memory cannot be a link or junction." }

$releaseName = [DateTime]::UtcNow.ToString("yyyyMMddTHHmmssfffZ")
$release = Join-Path $root (Join-Path "app\releases" $releaseName)
if (Test-Path -LiteralPath $release) { throw "Release folder unexpectedly exists: $release" }
New-Item -ItemType Directory -Path $release | Out-Null

$repository = Split-Path -Parent $PSScriptRoot
dotnet publish (Join-Path $repository "src\HyperMemory.Api\HyperMemory.Api.csproj") -c $Configuration -o (Join-Path $release "api") --no-self-contained
if ($LASTEXITCODE -ne 0) { throw "API publication failed. The unique release folder is preserved for diagnosis: $release" }
dotnet publish (Join-Path $repository "src\HyperMemory.Bridge\HyperMemory.Bridge.csproj") -c $Configuration -o (Join-Path $release "bridge") --no-self-contained
if ($LASTEXITCODE -ne 0) { throw "Bridge publication failed. The unique release folder is preserved for diagnosis: $release" }

$launcher = @"
`$env:HYPERMEMORY_STORAGE = '$($root.Replace("'", "''"))'
& '$((Join-Path $release "api\HyperMemory.Api.exe").Replace("'", "''"))'
"@
[System.IO.File]::WriteAllText((Join-Path $release "Start-HyperMemory.ps1"), $launcher)
Write-Output "Installed immutable release: $release"
Write-Output "Storage root: $root"
Write-Output "Start with: powershell -File `"$(Join-Path $release 'Start-HyperMemory.ps1')`""
