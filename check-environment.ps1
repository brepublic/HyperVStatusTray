[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'
$ServiceName = 'HyperVStatusTrayBroker'
$ConfigPath = Join-Path $env:ProgramData 'HyperVStatusTray\config.json'
$Failed = $false

function Test-IsAdministrator {
    $Identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $Principal = [Security.Principal.WindowsPrincipal]::new($Identity)
    return $Principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-Check {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [bool]$Passed,
        [Parameter(Mandatory)] [string]$Detail
    )

    $Prefix = if ($Passed) { '[OK]  ' } else { '[FAIL]' }
    Write-Host "$Prefix $Name - $Detail" -ForegroundColor $(if ($Passed) { 'Green' } else { 'Red' })
    if (-not $Passed) {
        $script:Failed = $true
    }
}

Write-Host 'HyperVStatusTray environment check' -ForegroundColor Cyan
Write-Host

$Dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $Dotnet) {
    Write-Check '.NET 10 SDK' $false 'dotnet command was not found.'
}
else {
    $Sdks = @(& dotnet --list-sdks 2>$null)
    $HasDotnet10 = $Sdks | Where-Object { $_ -match '^10\.' }
    Write-Check '.NET 10 SDK' ([bool]$HasDotnet10) $(if ($HasDotnet10) { $HasDotnet10 -join '; ' } else { 'No 10.x SDK was found.' })
}

if (Test-IsAdministrator) {
    $HyperVFeature = Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All -ErrorAction SilentlyContinue
    if ($null -eq $HyperVFeature) {
        Write-Host '[INFO] Hyper-V feature - Optional-feature state could not be queried.' -ForegroundColor Yellow
    }
    else {
        Write-Check 'Hyper-V feature' ($HyperVFeature.State -eq 'Enabled') "State=$($HyperVFeature.State)"
    }
}
else {
    Write-Host '[INFO] Hyper-V feature - Optional-feature state could not be queried without elevation.' -ForegroundColor Yellow
}

$Service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -eq $Service) {
    Write-Host "[INFO] Broker service - $ServiceName is not installed yet." -ForegroundColor Yellow
}
else {
    Write-Check 'Broker service' ($Service.Status -eq 'Running') "Status=$($Service.Status)"
}

if (Test-Path $ConfigPath) {
    Write-Check 'Machine config' $true $ConfigPath
}
else {
    Write-Host "[INFO] Machine config - not created yet: $ConfigPath" -ForegroundColor Yellow
}

try {
    $HyperVGroup = Get-LocalGroup -SID 'S-1-5-32-578' -ErrorAction Stop
    $Members = @(Get-LocalGroupMember -Group $HyperVGroup -ErrorAction Stop)
    $BrokerMember = $Members | Where-Object { $_.Name -ieq "NT SERVICE\$ServiceName" } | Select-Object -First 1
    if ($Service) {
        Write-Check 'Broker Hyper-V permission' ($null -ne $BrokerMember) $(if ($BrokerMember) { 'Service account is in Hyper-V Administrators.' } else { 'Service account is not in Hyper-V Administrators.' })
    }
}
catch {
    Write-Host "[INFO] Broker Hyper-V permission - $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Host
if ($Failed) {
    Write-Host 'One or more checks failed. Review the messages above before installing or running.' -ForegroundColor Yellow
    exit 1
}

Write-Host 'Required checks passed, or installation-only checks are not applicable yet.' -ForegroundColor Green
