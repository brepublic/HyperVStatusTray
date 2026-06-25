[CmdletBinding()]
param(
    [string]$ConfigPath = (Join-Path $env:ProgramData 'HyperVStatusTray\config.json'),
    [ValidateSet('English', 'SimplifiedChinese', 'TraditionalChinese')]
    [string]$Language = 'SimplifiedChinese'
)

$ErrorActionPreference = 'Stop'
$TuiModulePath = Join-Path $PSScriptRoot 'PowerShellTui.psm1'
if (Test-Path $TuiModulePath) {
    Import-Module $TuiModulePath -Force
}

function Get-HyperVVirtualMachineNames {
    $GetVmError = $null
    $GetVmCommand = Get-Command Get-VM -ErrorAction SilentlyContinue
    if ($null -eq $GetVmCommand) {
        try {
            Import-Module Hyper-V -ErrorAction Stop
            $GetVmCommand = Get-Command Get-VM -ErrorAction SilentlyContinue
        }
        catch {
            $GetVmCommand = $null
        }
    }

    if ($null -ne $GetVmCommand) {
        try {
            return @(
                Get-VM -ErrorAction Stop |
                    Sort-Object -Property Name |
                    ForEach-Object { $_.Name }
            )
        }
        catch {
            $GetVmError = $_.Exception.Message
        }
    }

    try {
        return @(
            Get-CimInstance -Namespace 'root\virtualization\v2' -ClassName 'Msvm_ComputerSystem' -ErrorAction Stop |
                Where-Object {
                    $_.Caption -eq 'Virtual Machine' -or
                    $_.Description -eq 'Microsoft Virtual Computer System'
                } |
                Sort-Object -Property ElementName |
                ForEach-Object { $_.ElementName }
        )
    }
    catch {
        if (-not [string]::IsNullOrWhiteSpace($GetVmError)) {
            throw "Failed to query Hyper-V VMs. Get-VM: $GetVmError; CIM: $($_.Exception.Message)"
        }

        throw
    }
}

function New-DefaultConfig {
    [ordered]@{
        Language = $Language
        PollIntervalSeconds = 5
        StartupTimeoutSeconds = 180
        SignalLossGraceSeconds = 20
        MonitorFailureThreshold = 2
        VirtualMachines = @()
    }
}

function Read-ExistingConfig {
    param([Parameter(Mandatory)] [string]$Path)

    if (-not (Test-Path $Path)) {
        return New-DefaultConfig
    }

    try {
        $Config = Get-Content -Raw -Encoding UTF8 -Path $Path | ConvertFrom-Json
        return [ordered]@{
            Language = if ($null -ne $Config.Language) { [string]$Config.Language } else { $Language }
            PollIntervalSeconds = if ($null -ne $Config.PollIntervalSeconds) { [int]$Config.PollIntervalSeconds } else { 5 }
            StartupTimeoutSeconds = if ($null -ne $Config.StartupTimeoutSeconds) { [int]$Config.StartupTimeoutSeconds } else { 180 }
            SignalLossGraceSeconds = if ($null -ne $Config.SignalLossGraceSeconds) { [int]$Config.SignalLossGraceSeconds } else { 20 }
            MonitorFailureThreshold = if ($null -ne $Config.MonitorFailureThreshold) { [int]$Config.MonitorFailureThreshold } else { 2 }
            VirtualMachines = @()
        }
    }
    catch {
        Write-Warning "Existing config could not be read. Default timing settings will be used. Reason: $($_.Exception.Message)"
        return New-DefaultConfig
    }
}

function Get-DefaultSelectionText {
    param(
        [Parameter(Mandatory)] [string[]]$VmNames,
        [string]$Path
    )

    if (-not (Test-Path $Path)) {
        if ($VmNames.Count -eq 1) {
            return '1'
        }

        return $null
    }

    try {
        $Config = Get-Content -Raw -Encoding UTF8 -Path $Path | ConvertFrom-Json
        $ExistingNames = @($Config.VirtualMachines | ForEach-Object { $_.Name })
        if ($ExistingNames.Count -lt 1 -or $ExistingNames.Count -gt 2) {
            return $null
        }

        $Indexes = foreach ($ExistingName in $ExistingNames) {
            $Index = -1
            for ($VmIndex = 0; $VmIndex -lt $VmNames.Count; $VmIndex++) {
                if ($VmNames[$VmIndex] -ieq $ExistingName) {
                    $Index = $VmIndex
                    break
                }
            }

            if ($Index -lt 0) {
                return $null
            }

            ($Index + 1).ToString()
        }

        return ($Indexes -join ',')
    }
    catch {
        return $null
    }
}

function Convert-SelectionTextToIndexes {
    param(
        [string]$SelectionText,
        [int]$Count
    )

    if ([string]::IsNullOrWhiteSpace($SelectionText)) {
        return @()
    }

    $Indexes = New-Object System.Collections.Generic.List[int]
    foreach ($Part in @($SelectionText -split '[,\s]+' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
        $Number = 0
        if ([int]::TryParse($Part, [ref]$Number) -and $Number -ge 1 -and $Number -le $Count) {
            $Indexes.Add($Number - 1) | Out-Null
        }
    }

    return $Indexes.ToArray()
}

function Read-VmSelection {
    param(
        [Parameter(Mandatory)] [string[]]$VmNames,
        [string]$DefaultSelection
    )

    if ((Get-Command Select-TuiMultiChoice -ErrorAction SilentlyContinue) -and (Test-TuiHost)) {
        $DefaultSelectedIndexes = @(Convert-SelectionTextToIndexes -SelectionText $DefaultSelection -Count $VmNames.Count)
        $SelectedNames = Select-TuiMultiChoice `
            -Title 'HyperVStatusTray VM Configuration' `
            -Subtitle 'Select one or two Hyper-V virtual machines to monitor.' `
            -Items $VmNames `
            -DefaultSelectedIndexes $DefaultSelectedIndexes `
            -MinimumSelected 1 `
            -MaximumSelected 2

        if ($null -eq $SelectedNames) {
            throw 'VM configuration cancelled.'
        }

        return $SelectedNames
    }

    Write-Host ''
    Write-Host 'Hyper-V virtual machines found on this computer:' -ForegroundColor Cyan
    for ($Index = 0; $Index -lt $VmNames.Count; $Index++) {
        Write-Host ("  {0}. {1}" -f ($Index + 1), $VmNames[$Index])
    }

    while ($true) {
        $Prompt = 'Select one or two VM numbers to monitor, separated by comma or space'
        if (-not [string]::IsNullOrWhiteSpace($DefaultSelection)) {
            $Prompt += " [$DefaultSelection]"
        }

        $InputText = Read-Host $Prompt
        if ([string]::IsNullOrWhiteSpace($InputText) -and -not [string]::IsNullOrWhiteSpace($DefaultSelection)) {
            $InputText = $DefaultSelection
        }

        $Parts = @($InputText -split '[,\s]+' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        if ($Parts.Count -lt 1 -or $Parts.Count -gt 2) {
            Write-Host 'Enter one or two numbers.' -ForegroundColor Yellow
            continue
        }

        $Seen = @{}
        $Selection = New-Object System.Collections.Generic.List[string]
        $Valid = $true
        foreach ($Part in $Parts) {
            $Number = 0
            if (-not [int]::TryParse($Part, [ref]$Number) -or $Number -lt 1 -or $Number -gt $VmNames.Count) {
                Write-Host "Invalid number: $Part" -ForegroundColor Yellow
                $Valid = $false
                break
            }

            if ($Seen.ContainsKey($Number)) {
                Write-Host 'Do not select the same VM more than once.' -ForegroundColor Yellow
                $Valid = $false
                break
            }

            $Seen[$Number] = $true
            $Selection.Add($VmNames[$Number - 1]) | Out-Null
        }

        if ($Valid) {
            return $Selection.ToArray()
        }
    }
}

function New-VmConfig {
    param([Parameter(Mandatory)] [string]$Name)

    [ordered]@{
        Name = $Name
        Label = $Name
        UseHeartbeat = $true
        PingAddress = $null
        PingTimeoutMilliseconds = 800
    }
}

$VmNames = @(Get-HyperVVirtualMachineNames | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($VmNames.Count -eq 0) {
    throw 'No Hyper-V virtual machines were found. Create a VM first, or confirm that Hyper-V is enabled.'
}

$Config = Read-ExistingConfig -Path $ConfigPath
$DefaultSelection = Get-DefaultSelectionText -VmNames $VmNames -Path $ConfigPath
$SelectedNames = @(Read-VmSelection -VmNames $VmNames -DefaultSelection $DefaultSelection)

$VmConfigs = New-Object System.Collections.Generic.List[object]
foreach ($Name in $SelectedNames) {
    $VmConfigs.Add((New-VmConfig -Name $Name)) | Out-Null
}

$Config.VirtualMachines = @($VmConfigs.ToArray())

$ConfigDirectory = Split-Path -Parent $ConfigPath
New-Item -Path $ConfigDirectory -ItemType Directory -Force | Out-Null
$Config | ConvertTo-Json -Depth 8 | Set-Content -Path $ConfigPath -Encoding UTF8

Write-Host ''
Write-Host "Config written: $ConfigPath" -ForegroundColor Green
Write-Host "Monitored VM(s): $($SelectedNames -join ', ')" -ForegroundColor Green
