[CmdletBinding()]
param(
    [switch]$SelfContained,
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$TrayProject = Join-Path $PSScriptRoot 'src\HyperVStatusTray.csproj'
$BrokerProject = Join-Path $PSScriptRoot 'broker\HyperVStatusTray.Broker.csproj'
$Output = Join-Path $PSScriptRoot "publish\$Runtime"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK was not found. Install the .NET 10 SDK first.'
}

Remove-Item $Output -Recurse -Force -ErrorAction SilentlyContinue
New-Item $Output -ItemType Directory -Force | Out-Null

function Publish-Project {
    param(
        [Parameter(Mandatory)] [string]$Project
    )

    $Arguments = @(
        'publish', $Project,
        '-c', 'Release',
        '-r', $Runtime,
        '--self-contained', $(if ($SelfContained) { 'true' } else { 'false' }),
        '-p:PublishSingleFile=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-p:EnableCompressionInSingleFile=true',
        '-o', $Output
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }
}

Publish-Project -Project $TrayProject
Publish-Project -Project $BrokerProject

Write-Host "Published tray and broker to: $Output"
if (-not $SelfContained) {
    Write-Host 'This build requires the .NET 10 Desktop Runtime on the target PC.'
}
