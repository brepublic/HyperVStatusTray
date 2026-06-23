[CmdletBinding()]
param(
    [switch]$FrameworkDependent,
    [switch]$DoNotStart
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'HyperVStatusTrayBroker'
$InstallDirectory = Join-Path $env:ProgramFiles 'HyperVStatusTray'
$DataDirectory = Join-Path $env:ProgramData 'HyperVStatusTray'
$Runtime = 'win-x64'

function Test-IsAdministrator {
    $Identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $Principal = [Security.Principal.WindowsPrincipal]::new($Identity)
    return $Principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-Sc {
    param(
        [Parameter(Mandatory)] [string[]]$Arguments,
        [Parameter(Mandatory)] [string]$FailureMessage
    )

    $Output = & sc.exe @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage`n$($Output -join [Environment]::NewLine)"
    }

    return $Output
}

if (-not (Test-IsAdministrator)) {
    $Args = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', "`"$PSCommandPath`""
    )
    if ($FrameworkDependent) { $Args += '-FrameworkDependent' }
    if ($DoNotStart) { $Args += '-DoNotStart' }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $Args
    return
}

$BuildScript = Join-Path $PSScriptRoot 'build.ps1'
$BuildArguments = @(
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', $BuildScript,
    '-Runtime', $Runtime
)
if (-not $FrameworkDependent) {
    $BuildArguments += '-SelfContained'
}

& powershell.exe @BuildArguments
if ($LASTEXITCODE -ne 0) {
    throw "build.ps1 failed with exit code $LASTEXITCODE"
}

$PublishDirectory = Join-Path $PSScriptRoot "publish\$Runtime"
$TrayExecutable = Join-Path $PublishDirectory 'HyperVStatusTray.exe'
$BrokerExecutable = Join-Path $PublishDirectory 'HyperVStatusTrayBroker.exe'
if (-not (Test-Path $TrayExecutable)) {
    throw "Published tray executable not found: $TrayExecutable"
}
if (-not (Test-Path $BrokerExecutable)) {
    throw "Published broker executable not found: $BrokerExecutable"
}

Get-Process HyperVStatusTray -ErrorAction SilentlyContinue | Stop-Process -Force
if ($ExistingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    if ($ExistingService.Status -ne 'Stopped') {
        Invoke-Sc -Arguments @('stop', $ServiceName) -FailureMessage "Failed to stop existing service $ServiceName." | Out-Null
        Start-Sleep -Seconds 2
    }

    Invoke-Sc -Arguments @('delete', $ServiceName) -FailureMessage "Failed to delete existing service $ServiceName." | Out-Null
    Start-Sleep -Seconds 2
}

New-Item $InstallDirectory -ItemType Directory -Force | Out-Null
New-Item $DataDirectory -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $PublishDirectory '*') $InstallDirectory -Recurse -Force

$InstalledTray = Join-Path $InstallDirectory 'HyperVStatusTray.exe'
$InstalledBroker = Join-Path $InstallDirectory 'HyperVStatusTrayBroker.exe'
$ConfigPath = Join-Path $DataDirectory 'config.json'
$SecurityPath = Join-Path $DataDirectory 'broker-security.json'
$CurrentUserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$ConfigureScript = Join-Path $PSScriptRoot 'configure-vms.ps1'

if (-not (Test-Path $ConfigureScript)) {
    throw "Configuration script not found: $ConfigureScript"
}

& $ConfigureScript -ConfigPath $ConfigPath

$SecurityConfig = [ordered]@{
    AllowedUserSid = $CurrentUserSid
    AllowedClientPath = $InstalledTray
}
$SecurityConfig | ConvertTo-Json -Depth 4 | Set-Content -Path $SecurityPath -Encoding UTF8

$CreateArgs = @(
    'create', $ServiceName,
    'binPath=', ('"' + $InstalledBroker + '"'),
    'start=', 'auto',
    'DisplayName=', 'HyperVStatusTray Broker'
)
Invoke-Sc -Arguments $CreateArgs -FailureMessage "Failed to create service $ServiceName." | Out-Null

Invoke-Sc -Arguments @('sidtype', $ServiceName, 'unrestricted') -FailureMessage "Failed to set service SID type for $ServiceName." | Out-Null
Invoke-Sc -Arguments @('config', $ServiceName, 'obj=', "NT SERVICE\$ServiceName") -FailureMessage "Failed to configure virtual service account for $ServiceName." | Out-Null

$HyperVGroup = Get-LocalGroup -SID 'S-1-5-32-578'
$ServiceAccount = "NT SERVICE\$ServiceName"
$ExistingHyperVMember = Get-LocalGroupMember -Group $HyperVGroup -ErrorAction Stop |
    Where-Object { $_.Name -ieq $ServiceAccount } |
    Select-Object -First 1
if ($null -eq $ExistingHyperVMember) {
    Add-LocalGroupMember -Group $HyperVGroup -Member "NT SERVICE\$ServiceName" -ErrorAction Stop
}

& icacls.exe $DataDirectory '/inheritance:r' | Out-Null
$SystemAcl = '*S-1-5-18:(OI)(CI)F'
$AdministratorsAcl = '*S-1-5-32-544:(OI)(CI)F'
$ServiceAcl = "NT SERVICE\${ServiceName}:(OI)(CI)M"
$UserAcl = "*${CurrentUserSid}:(OI)(CI)RX"
& icacls.exe $DataDirectory '/grant:r' $SystemAcl $AdministratorsAcl $ServiceAcl $UserAcl | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Failed to set ACL on $DataDirectory."
}

Invoke-Sc -Arguments @('start', $ServiceName) -FailureMessage "Failed to start service $ServiceName." | Out-Null

$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
New-Item $RunKey -Force | Out-Null
New-ItemProperty -Path $RunKey -Name 'HyperVStatusTray' -Value "`"$InstalledTray`"" -PropertyType String -Force | Out-Null

if (-not $DoNotStart) {
    Start-Process $InstalledTray
}

Write-Host "Installed to: $InstallDirectory"
Write-Host "Machine configuration: $ConfigPath"
Write-Host "Broker service: $ServiceName"
Write-Host 'The tray application is configured to start when this user signs in.'
