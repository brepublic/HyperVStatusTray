[CmdletBinding()]
param(
    [switch]$PurgeData
)

$ErrorActionPreference = 'Stop'
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
    $Args = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', "`"$PSCommandPath`""
    )
    if ($PurgeData) { $Args += '-PurgeData' }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $Args
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
