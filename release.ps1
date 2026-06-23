[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+([-.+][0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [switch]$FrameworkDependent,

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot 'dist'
}

function Find-InnoSetupCompiler {
    $Command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($null -ne $Command) {
        return $Command.Source
    }

    $CandidatePaths = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )

    foreach ($Path in $CandidatePaths) {
        if ($Path -and (Test-Path -LiteralPath $Path)) {
            return $Path
        }
    }

    throw 'Inno Setup compiler (ISCC.exe) was not found. Install Inno Setup 6 and rerun this script.'
}

function ConvertTo-InnoDefineValue {
    param([Parameter(Mandatory)] [string]$Value)

    return $Value.Replace('"', '\"')
}

$BuildScript = Join-Path $PSScriptRoot 'build.ps1'
$InstallerScript = Join-Path $PSScriptRoot 'installer\HyperVStatusTray.iss'
$PublishDirectory = Join-Path $PSScriptRoot "publish\$Runtime"
$ReleaseDirectory = Join-Path $OutputDirectory $Version
$SetupBaseName = "HyperVStatusTray-$Version-$Runtime-setup"

if (-not (Test-Path -LiteralPath $BuildScript)) {
    throw "Build script not found: $BuildScript"
}

if (-not (Test-Path -LiteralPath $InstallerScript)) {
    throw "Inno Setup script not found: $InstallerScript"
}

$BuildArguments = @(
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', $BuildScript,
    '-Runtime', $Runtime
)

if (-not $FrameworkDependent) {
    $BuildArguments += '-SelfContained'
}

Write-Host "Publishing $Runtime..."
& powershell.exe @BuildArguments
if ($LASTEXITCODE -ne 0) {
    throw "build.ps1 failed with exit code $LASTEXITCODE"
}

$RequiredPublishFiles = @(
    'HyperVStatusTray.exe',
    'HyperVStatusTrayBroker.exe',
    'configure-vms.ps1',
    'PowerShellTui.psm1'
)

foreach ($FileName in $RequiredPublishFiles) {
    $Path = Join-Path $PublishDirectory $FileName
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Required publish output was not found: $Path"
    }
}

$Iscc = Find-InnoSetupCompiler
New-Item -Path $ReleaseDirectory -ItemType Directory -Force | Out-Null

$SourceRoot = ConvertTo-InnoDefineValue -Value $PSScriptRoot
$InnoOutputDirectory = ConvertTo-InnoDefineValue -Value $ReleaseDirectory
$InnoArguments = @(
    "/DAppVersion=$Version",
    "/DRuntime=$Runtime",
    "/DSourceRoot=$SourceRoot",
    "/DOutputDir=$InnoOutputDirectory",
    "/DSetupBaseName=$SetupBaseName",
    $InstallerScript
)

Write-Host "Building installer with Inno Setup..."
& $Iscc @InnoArguments
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE"
}

$SetupPath = Join-Path $ReleaseDirectory "$SetupBaseName.exe"
if (-not (Test-Path -LiteralPath $SetupPath)) {
    throw "Installer was not created: $SetupPath"
}

$ChecksumPath = Join-Path $ReleaseDirectory 'SHA256SUMS.txt'
$Hash = Get-FileHash -Algorithm SHA256 -LiteralPath $SetupPath
"$($Hash.Hash.ToLowerInvariant())  $(Split-Path -Leaf $SetupPath)" | Set-Content -Path $ChecksumPath -Encoding ASCII

Write-Host ''
Write-Host "Release created:"
Write-Host "  Installer: $SetupPath"
Write-Host "  SHA256:    $ChecksumPath"
