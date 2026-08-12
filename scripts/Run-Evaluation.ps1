param(
    [string]$Output = "",
    [string]$Dataset = "",
    [ValidateSet('', 'locomo', 'longmemeval')]
    [string]$Format = "",
    [int]$Limit = 0,
    [ValidateRange(1, 100)]
    [int]$TopK = 5
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$arguments = @('run', '--project', (Join-Path $repository 'tests\HyperMemory.Evaluation'), '--configuration', 'Release')
$programArguments = @()
if (-not [string]::IsNullOrWhiteSpace($Dataset)) {
    $programArguments += @('--dataset', [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Dataset)))
    if (-not [string]::IsNullOrWhiteSpace($Format)) { $programArguments += @('--format', $Format) }
    if ($Limit -gt 0) { $programArguments += @('--limit', $Limit) }
    $programArguments += @('--top-k', $TopK)
}
if (-not [string]::IsNullOrWhiteSpace($Output)) {
    $resolvedOutput = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Output))
    $programArguments += @('--output', $resolvedOutput)
}
if ($programArguments.Count -gt 0) { $arguments += @('--') + $programArguments }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "HyperMemory evaluation failed with exit code $LASTEXITCODE." }
