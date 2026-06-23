[CmdletBinding()]
param(
    [switch]$PurgeData,
    [Parameter(DontShow = $true)]
    [switch]$ElevatedResume
)

$ErrorActionPreference = 'Stop'
$TuiModulePath = Join-Path $PSScriptRoot 'PowerShellTui.psm1'
if (Test-Path $TuiModulePath) {
    Import-Module $TuiModulePath -Force
}

if (-not $ElevatedResume -and $PSBoundParameters.Count -eq 0 -and (Get-Command Test-TuiHost -ErrorAction SilentlyContinue) -and (Test-TuiHost)) {
    $Choice = Show-TuiMenu `
        -Title 'HyperVStatusTray Uninstall' `
        -Subtitle 'Choose how much to remove.' `
        -Items @(
            [pscustomobject]@{
                Label = 'Uninstall and keep data'
                Description = 'Remove the app, service, startup entry, and service permissions. Keep config and logs.'
                PurgeData = $false
            },
            [pscustomobject]@{
                Label = 'Uninstall and purge all data'
                Description = 'Also remove machine config, broker logs, current-user data, and legacy install data.'
                PurgeData = $true
            }
        )
    if ($null -eq $Choice) {
        Write-Host 'Uninstall cancelled.'
        return
    }

    $PurgeData = $Choice.PurgeData
    $Prompt = if ($PurgeData) {
        'Remove HyperVStatusTray and purge all app data now?'
    }
    else {
        'Remove HyperVStatusTray while keeping config and logs now?'
    }

    if (-not (Read-TuiConfirm -Title 'HyperVStatusTray Uninstall' -Prompt $Prompt -DefaultYes $false)) {
        Write-Host 'Uninstall cancelled.'
        return
    }

    Clear-Host
    Write-TuiTitle -Title 'HyperVStatusTray Uninstall' -Subtitle 'Removing installed components...'
}

$ServiceName = 'HyperVStatusTrayBroker'
$InstallDirectory = Join-Path $env:ProgramFiles 'HyperVStatusTray'
$DataDirectory = Join-Path $env:ProgramData 'HyperVStatusTray'
$LegacyInstallDirectory = Join-Path $env:LOCALAPPDATA 'Programs\HyperVStatusTray'
$UserDataDirectory = Join-Path $env:LOCALAPPDATA 'HyperVStatusTray'
$RunSubKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$RunValueName = 'HyperVStatusTray'

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
            $ValueLiteral = if ($_.Value) { '$true' } else { '$false' }
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

function Remove-RunValue {
    param([Parameter(Mandatory)] [string]$RegistryPath)

    try {
        Remove-ItemProperty -Path $RegistryPath -Name $RunValueName -ErrorAction SilentlyContinue
    }
    catch {
        Write-Warning "Failed to remove startup entry from ${RegistryPath}: $($_.Exception.Message)"
    }
}

function Remove-StartupEntries {
    Remove-RunValue -RegistryPath "HKCU:\$RunSubKey"

    Get-ChildItem 'Registry::HKEY_USERS' -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -notmatch '_Classes$' } |
        ForEach-Object {
            $Path = "Registry::$($_.Name)\$RunSubKey"
            if (Test-Path $Path) {
                Remove-RunValue -RegistryPath $Path
            }
        }
}

function Get-ServiceSid {
    param([Parameter(Mandatory)] [string]$Name)

    $Output = & sc.exe showsid $Name 2>$null
    foreach ($Line in $Output) {
        if ($Line -match 'S-1-5-80-[0-9-]+') {
            return $Matches[0]
        }
    }

    return $null
}

function Remove-ServiceFromHyperVAdministrators {
    try {
        $HyperVGroup = Get-LocalGroup -SID 'S-1-5-32-578' -ErrorAction Stop
        $Members = @(Get-LocalGroupMember -Group $HyperVGroup -ErrorAction Stop)
        $ServiceAccount = "NT SERVICE\$ServiceName"
        $ServiceSid = Get-ServiceSid -Name $ServiceName

        foreach ($Member in $Members) {
            $MatchesService =
                $Member.Name -ieq $ServiceAccount -or
                ($ServiceSid -and $Member.SID.Value -eq $ServiceSid)

            if ($MatchesService) {
                Remove-LocalGroupMember -Group $HyperVGroup -Member $Member -ErrorAction SilentlyContinue
            }
        }
    }
    catch {
        Write-Warning $_.Exception.Message
    }
}

function Remove-KnownDirectory {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$ExpectedLeaf
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $Resolved = (Resolve-Path -LiteralPath $Path).Path
    if ((Split-Path -Leaf $Resolved) -ne $ExpectedLeaf) {
        throw "Refusing to remove unexpected path: $Resolved"
    }

    Remove-Item -LiteralPath $Resolved -Recurse -Force -ErrorAction SilentlyContinue
}

if (-not (Test-IsAdministrator)) {
    $ScriptParameters = @{ ElevatedResume = $true }
    if ($PurgeData) { $ScriptParameters.PurgeData = $true }
    Start-ElevatedScript -ScriptParameters $ScriptParameters
    return
}

Get-Process HyperVStatusTray -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-StartupEntries

$Service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -ne $Service) {
    & sc.exe stop $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

Remove-ServiceFromHyperVAdministrators

if ($null -ne $Service) {
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

Remove-KnownDirectory -Path $InstallDirectory -ExpectedLeaf 'HyperVStatusTray'

if ($PurgeData) {
    Remove-KnownDirectory -Path $DataDirectory -ExpectedLeaf 'HyperVStatusTray'
    Remove-KnownDirectory -Path $UserDataDirectory -ExpectedLeaf 'HyperVStatusTray'
    Remove-KnownDirectory -Path $LegacyInstallDirectory -ExpectedLeaf 'HyperVStatusTray'
}

Write-Host 'HyperVStatusTray was removed.'
if ($PurgeData) {
    Write-Host 'Program files, service registration, startup entries, service Hyper-V group membership, machine data, and current-user data were removed.'
}
else {
    Write-Host "Machine configuration and broker logs were retained at: $DataDirectory"
    Write-Host "Current-user tray logs were retained at: $UserDataDirectory"
}
