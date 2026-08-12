param(
    [string]$Runtime = "win-x64",
    [string]$Version = "1.7.0",
    [string]$CertificateThumbprint = "",
    [ValidateSet("CurrentUser", "LocalMachine")][string]$CertificateStore = "CurrentUser",
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$repository = Split-Path -Parent $PSScriptRoot
$buildId = [DateTime]::UtcNow.ToString("yyyyMMddTHHmmssfffZ")
$staging = Join-Path $repository (Join-Path "artifacts\installer-staging" $buildId)
$output = Join-Path $repository (Join-Path "artifacts\installers" ("HyperMemory-{0}-{1}-{2}" -f $Version, $Runtime, $buildId))
$payload = Join-Path $repository "src\HyperMemory.Installer\payload.zip"

function Get-SignTool {
    $roots = @("C:\Program Files (x86)\Windows Kits\10\bin", "C:\Program Files\Windows Kits\10\bin")
    $tool = Get-ChildItem -Path $roots -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object FullName -Descending | Select-Object -First 1
    if ($null -eq $tool) { throw "SignTool was not found in the Windows SDK." }
    return $tool.FullName
}

function Invoke-AuthenticodeSign([string]$FilePath) {
    if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) { return }
    $arguments = @("sign", "/sha1", $CertificateThumbprint.Replace(" ", ""), "/s", "My", "/fd", "SHA256",
        "/tr", $TimestampUrl, "/td", "SHA256", "/d", "HyperMemory para Hermes")
    if ($CertificateStore -eq "LocalMachine") { $arguments += "/sm" }
    $arguments += $FilePath
    & $script:SignToolPath @arguments
    if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed for $FilePath" }
    & $script:SignToolPath verify /pa /all /tw $FilePath
    if ($LASTEXITCODE -ne 0) { throw "Authenticode verification failed for $FilePath" }
}

if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    $storePath = "Cert:\$CertificateStore\My\$($CertificateThumbprint.Replace(' ', ''))"
    $certificate = Get-Item -LiteralPath $storePath -ErrorAction Stop
    if (-not $certificate.HasPrivateKey) { throw "The selected certificate has no accessible private key." }
    if ($certificate.NotAfter -le [DateTime]::Now) { throw "The selected certificate has expired." }
    $codeSigningOid = "1.3.6.1.5.5.7.3.3"
    if (-not ($certificate.EnhancedKeyUsageList.ObjectId.Value -contains $codeSigningOid)) {
        throw "The selected certificate is not valid for code signing."
    }
    $script:SignToolPath = Get-SignTool
}
New-Item -ItemType Directory -Path (Join-Path $staging "api") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $staging "bridge") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $staging "skill") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $staging "plugin") -Force | Out-Null
New-Item -ItemType Directory -Path $output -Force | Out-Null

dotnet publish (Join-Path $repository "src\HyperMemory.Api\HyperMemory.Api.csproj") -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $staging "api")
if ($LASTEXITCODE -ne 0) { throw "API publication failed." }
dotnet publish (Join-Path $repository "src\HyperMemory.Bridge\HyperMemory.Bridge.csproj") -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $staging "bridge")
if ($LASTEXITCODE -ne 0) { throw "Bridge publication failed." }
Invoke-AuthenticodeSign (Join-Path $staging "api\HyperMemory.Api.exe")
Invoke-AuthenticodeSign (Join-Path $staging "bridge\HyperMemory.Bridge.exe")
Copy-Item -LiteralPath (Join-Path $repository "src\HyperMemory.Bridge\hermes-skill\SKILL.md") -Destination (Join-Path $staging "skill\SKILL.md")
Copy-Item -LiteralPath (Join-Path $repository "src\HyperMemory.Bridge\hermes-plugin\__init__.py") -Destination (Join-Path $staging "plugin\__init__.py")
Copy-Item -LiteralPath (Join-Path $repository "src\HyperMemory.Bridge\hermes-plugin\plugin.yaml") -Destination (Join-Path $staging "plugin\plugin.yaml")
Copy-Item -LiteralPath (Join-Path $repository "src\HyperMemory.Bridge\hermes-plugin\README.md") -Destination (Join-Path $staging "plugin\README.md")

Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $payload -CompressionLevel Optimal -Force
dotnet publish (Join-Path $repository "src\HyperMemory.Installer\HyperMemory.Installer.csproj") -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $output
if ($LASTEXITCODE -ne 0) { throw "Installer publication failed." }

$installer = Join-Path $output "HyperMemorySetup.exe"
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw "Installer executable was not generated." }
Invoke-AuthenticodeSign $installer
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
$signature = (Get-AuthenticodeSignature -LiteralPath $installer).Status.ToString()
$details = @"
HyperMemory $Version for Windows x64

Interactive installation: double-click HyperMemorySetup.exe
Silent installation: HyperMemorySetup.exe --silent --storage-root "D:\"
Silent mode starts HyperMemory at the next Windows sign-in.
Silent uninstall is registered automatically in Windows Apps.
Silent uninstall preserves memory by default. Permanent erasure additionally requires
--erase-memory --confirm-storage-root "<exact Hyper_Memory path>".

SHA256: $hash
Authenticode: $signature
Built: $buildId UTC
"@
[System.IO.File]::WriteAllText((Join-Path $output "INSTALL.txt"), $details)
Write-Output "Installer: $installer"
Write-Output "SHA256: $hash"
