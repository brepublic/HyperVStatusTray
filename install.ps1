[CmdletBinding()]
param(
    [switch]$FrameworkDependent,
    [switch]$DoNotStart,
    [ValidateSet('English', 'SimplifiedChinese', 'TraditionalChinese')]
    [string]$Language = 'SimplifiedChinese',
    [Parameter(DontShow = $true)]
    [switch]$UseInstalledFiles,
    [Parameter(DontShow = $true)]
    [switch]$ElevatedResume
)

$ErrorActionPreference = 'Stop'
$TuiModulePath = Join-Path $PSScriptRoot 'PowerShellTui.psm1'
if (Test-Path $TuiModulePath) {
    Import-Module $TuiModulePath -Force
}

if (-not $UseInstalledFiles -and -not $ElevatedResume -and $PSBoundParameters.Count -eq 0 -and (Get-Command Test-TuiHost -ErrorAction SilentlyContinue) -and (Test-TuiHost)) {
    $PublishChoice = Show-TuiMenu `
        -Title 'HyperVStatusTray Install' `
        -Subtitle 'Choose the build type to install.' `
        -Items @(
            [pscustomobject]@{
                Label = 'Self-contained install'
                Description = 'Recommended for most PCs; bundles the needed runtime pieces.'
                FrameworkDependent = $false
            },
            [pscustomobject]@{
                Label = 'Framework-dependent install'
                Description = 'Smaller install; requires .NET 10 Desktop Runtime on this PC.'
                FrameworkDependent = $true
            }
        )
    if ($null -eq $PublishChoice) {
        Write-Host 'Install cancelled.'
        return
    }

    $FrameworkDependent = $PublishChoice.FrameworkDependent

    $LaunchChoice = Show-TuiMenu `
        -Title 'HyperVStatusTray Install' `
        -Subtitle 'Choose what happens after installation.' `
        -Items @(
            [pscustomobject]@{
                Label = 'Start tray app after install'
                Description = 'Install the service and immediately launch the tray application.'
                DoNotStart = $false
            },
            [pscustomobject]@{
                Label = 'Do not start tray app'
                Description = 'Install everything and leave startup for the next sign-in or manual launch.'
                DoNotStart = $true
            }
        )
    if ($null -eq $LaunchChoice) {
        Write-Host 'Install cancelled.'
        return
    }

    $DoNotStart = $LaunchChoice.DoNotStart

    if (-not (Read-TuiConfirm -Title 'HyperVStatusTray Install' -Prompt 'Build and install HyperVStatusTray now? This requires administrator permission.' -DefaultYes $true)) {
        Write-Host 'Install cancelled.'
        return
    }

    Clear-Host
    Write-TuiTitle -Title 'HyperVStatusTray Install' -Subtitle 'Preparing installation...'
}

$ServiceName = 'HyperVStatusTrayBroker'
$InstallDirectory = Join-Path $env:ProgramFiles 'HyperVStatusTray'
$DataDirectory = Join-Path $env:ProgramData 'HyperVStatusTray'
$Runtime = 'win-x64'

function Test-IsAdministrator {
    $Identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $Principal = [Security.Principal.WindowsPrincipal]::new($Identity)
    return $Principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function ConvertTo-PowerShellSingleQuotedString {
    param([AllowEmptyString()] [Parameter(Mandatory)] [string]$Value)

    return "'" + ($Value -replace "'", "''") + "'"
}

function Start-ElevatedScript {
    param([Parameter(Mandatory)] [hashtable]$ScriptParameters)

    $ScriptPathLiteral = ConvertTo-PowerShellSingleQuotedString -Value $PSCommandPath
    $ParameterEntries = @($ScriptParameters.GetEnumerator() |
        Sort-Object Key |
        ForEach-Object {
            $NameLiteral = ConvertTo-PowerShellSingleQuotedString -Value $_.Key
            $ValueLiteral = if ($_.Value -is [bool]) {
                if ($_.Value) { '$true' } else { '$false' }
            }
            else {
                ConvertTo-PowerShellSingleQuotedString -Value ([string]$_.Value)
            }
            "$NameLiteral = $ValueLiteral"
        })
    $ScriptParameterHashtableLiteral = '@{' + ($ParameterEntries -join '; ') + '}'
    $Command = @"
`$ErrorActionPreference = 'Stop'
`$ScriptExitCode = 0

function Wait-BeforeClose {
    param([int]`$Seconds = 20)

    Write-Host ''
    Write-Host "Press any key to close this window, or wait `$Seconds seconds..." -ForegroundColor Yellow

    for (`$Remaining = `$Seconds; `$Remaining -gt 0; `$Remaining--) {
        `$HasKey = `$false
        try {
            `$HasKey = [Console]::KeyAvailable
        }
        catch {
            `$HasKey = `$false
        }

        if (`$HasKey) {
            try {
                [void][Console]::ReadKey(`$true)
            }
            catch {
            }

            break
        }

        Write-Host -NoNewline (([char]13) + ("Closing in {0} second(s)... " -f `$Remaining))
        Start-Sleep -Seconds 1
    }

    Write-Host ''
}

try {
    `$ScriptParameters = $ScriptParameterHashtableLiteral
    & $ScriptPathLiteral @ScriptParameters
}
catch {
    `$ScriptExitCode = 1
    Write-Host ''
    Write-Host 'Error:' -ForegroundColor Red
    Write-Host `$_.Exception.Message -ForegroundColor Red
    if (`$_.InvocationInfo -and `$_.InvocationInfo.PositionMessage) {
        Write-Host `$_.InvocationInfo.PositionMessage -ForegroundColor DarkGray
    }
}
finally {
    Write-Host ''
    if (`$ScriptExitCode -eq 0) {
        Write-Host 'Completed successfully.' -ForegroundColor Green
    }
    else {
        Write-Host 'Completed with errors.' -ForegroundColor Red
    }

    Wait-BeforeClose -Seconds 20
    exit `$ScriptExitCode
}
"@
    $EncodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Command))
    $ProcessArguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-EncodedCommand', $EncodedCommand
    )
    Start-Process powershell.exe -Verb RunAs -ArgumentList $ProcessArguments
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
    $ScriptParameters = @{ ElevatedResume = $true }
    if ($FrameworkDependent) { $ScriptParameters.FrameworkDependent = $true }
    if ($DoNotStart) { $ScriptParameters.DoNotStart = $true }
    if ($UseInstalledFiles) { $ScriptParameters.UseInstalledFiles = $true }
    if ($Language -ne 'SimplifiedChinese') { $ScriptParameters.Language = $Language }
    Start-ElevatedScript -ScriptParameters $ScriptParameters
    return
}

if ($UseInstalledFiles) {
    $ResolvedScriptRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
    $ResolvedInstallDirectory = if (Test-Path -LiteralPath $InstallDirectory) {
        (Resolve-Path -LiteralPath $InstallDirectory).Path
    }
    else {
        $InstallDirectory
    }

    if ($ResolvedScriptRoot -ine $ResolvedInstallDirectory) {
        throw "-UseInstalledFiles must be run from the installation directory: $InstallDirectory"
    }
}
else {
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
}

$PublishDirectory = if ($UseInstalledFiles) { $PSScriptRoot } else { Join-Path $PSScriptRoot "publish\$Runtime" }
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
if (-not $UseInstalledFiles) {
    Copy-Item (Join-Path $PublishDirectory '*') $InstallDirectory -Recurse -Force
}

$InstalledTray = Join-Path $InstallDirectory 'HyperVStatusTray.exe'
$InstalledBroker = Join-Path $InstallDirectory 'HyperVStatusTrayBroker.exe'
$ConfigPath = Join-Path $DataDirectory 'config.json'
$SecurityPath = Join-Path $DataDirectory 'broker-security.json'
$CurrentUserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$ConfigureScript = Join-Path $PSScriptRoot 'configure-vms.ps1'

if (-not (Test-Path $ConfigureScript)) {
    throw "Configuration script not found: $ConfigureScript"
}

& $ConfigureScript -ConfigPath $ConfigPath -Language $Language

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
