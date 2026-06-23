[CmdletBinding()]
param(
    [switch]$SelfContained,
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$TuiModulePath = Join-Path $PSScriptRoot 'PowerShellTui.psm1'
if (Test-Path $TuiModulePath) {
    Import-Module $TuiModulePath -Force
}

if ($PSBoundParameters.Count -eq 0 -and (Get-Command Test-TuiHost -ErrorAction SilentlyContinue) -and (Test-TuiHost)) {
    $RuntimeChoice = Show-TuiMenu `
        -Title 'HyperVStatusTray Build' `
        -Subtitle 'Choose the Windows runtime target.' `
        -Items @(
            [pscustomobject]@{
                Label = 'Windows x64'
                Description = 'Build for standard 64-bit Intel/AMD Windows PCs.'
                Value = 'win-x64'
            },
            [pscustomobject]@{
                Label = 'Windows ARM64'
                Description = 'Build for ARM-based Windows PCs.'
                Value = 'win-arm64'
            }
        )
    if ($null -eq $RuntimeChoice) {
        Write-Host 'Build cancelled.'
        return
    }

    $Runtime = $RuntimeChoice.Value

    $PublishChoice = Show-TuiMenu `
        -Title 'HyperVStatusTray Build' `
        -Subtitle 'Choose how the app should be published.' `
        -Items @(
            [pscustomobject]@{
                Label = 'Self-contained single-file build'
                Description = 'Best for installation on PCs without a matching .NET Desktop Runtime.'
                SelfContained = $true
            },
            [pscustomobject]@{
                Label = 'Framework-dependent single-file build'
                Description = 'Smaller output; requires .NET 10 Desktop Runtime on the target PC.'
                SelfContained = $false
            }
        )
    if ($null -eq $PublishChoice) {
        Write-Host 'Build cancelled.'
        return
    }

    $SelfContained = $PublishChoice.SelfContained

    if (-not (Read-TuiConfirm -Title 'HyperVStatusTray Build' -Prompt "Publish $Runtime now?" -DefaultYes $true)) {
        Write-Host 'Build cancelled.'
        return
    }

    Clear-Host
    Write-TuiTitle -Title 'HyperVStatusTray Build' -Subtitle "Publishing $Runtime..."
}

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
