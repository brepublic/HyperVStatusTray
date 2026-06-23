function Test-TuiHost {
    try {
        return [Environment]::UserInteractive -and
            -not [Console]::IsInputRedirected -and
            -not [Console]::IsOutputRedirected
    }
    catch {
        return $false
    }
}

function Get-TuiWidth {
    try {
        $Width = [Console]::WindowWidth - 1
        if ($Width -lt 50) {
            return 50
        }

        if ($Width -gt 100) {
            return 100
        }

        return $Width
    }
    catch {
        return 80
    }
}

function Write-TuiTitle {
    param(
        [Parameter(Mandatory)] [string]$Title,
        [string]$Subtitle
    )

    $Line = '=' * (Get-TuiWidth)
    Write-Host $Line -ForegroundColor DarkCyan
    Write-Host $Title -ForegroundColor Cyan
    if (-not [string]::IsNullOrWhiteSpace($Subtitle)) {
        Write-Host $Subtitle -ForegroundColor DarkGray
    }
    Write-Host $Line -ForegroundColor DarkCyan
    Write-Host ''
}

function Write-TuiHint {
    param([string]$Text = 'Use Up/Down, Enter to choose, Esc or Q to cancel.')

    Write-Host ''
    Write-Host $Text -ForegroundColor DarkGray
}

function Get-TuiItemText {
    param(
        [Parameter(Mandatory)] [object]$Item,
        [string]$PropertyName
    )

    if ([string]::IsNullOrWhiteSpace($PropertyName)) {
        return [string]$Item
    }

    $Property = $Item.PSObject.Properties[$PropertyName]
    if ($null -eq $Property) {
        return ''
    }

    return [string]$Property.Value
}

function Show-TuiMenu {
    param(
        [Parameter(Mandatory)] [string]$Title,
        [string]$Subtitle,
        [Parameter(Mandatory)] [object[]]$Items,
        [string]$LabelProperty = 'Label',
        [string]$DescriptionProperty = 'Description',
        [int]$SelectedIndex = 0
    )

    if ($Items.Count -lt 1) {
        throw 'TUI menu requires at least one item.'
    }

    if ($SelectedIndex -lt 0 -or $SelectedIndex -ge $Items.Count) {
        $SelectedIndex = 0
    }

    $OriginalCursorVisible = $true
    try {
        $OriginalCursorVisible = [Console]::CursorVisible
        [Console]::CursorVisible = $false

        while ($true) {
            Clear-Host
            Write-TuiTitle -Title $Title -Subtitle $Subtitle

            for ($Index = 0; $Index -lt $Items.Count; $Index++) {
                $Item = $Items[$Index]
                $Prefix = if ($Index -eq $SelectedIndex) { '>' } else { ' ' }
                $Label = Get-TuiItemText -Item $Item -PropertyName $LabelProperty
                $Description = Get-TuiItemText -Item $Item -PropertyName $DescriptionProperty
                $Color = if ($Index -eq $SelectedIndex) { 'Yellow' } else { 'Gray' }

                Write-Host (" {0} {1}" -f $Prefix, $Label) -ForegroundColor $Color
                if (-not [string]::IsNullOrWhiteSpace($Description)) {
                    Write-Host ("     {0}" -f $Description) -ForegroundColor DarkGray
                }
            }

            Write-TuiHint
            $Key = [Console]::ReadKey($true)
            switch ($Key.Key) {
                'UpArrow' {
                    $SelectedIndex--
                    if ($SelectedIndex -lt 0) {
                        $SelectedIndex = $Items.Count - 1
                    }
                }
                'DownArrow' {
                    $SelectedIndex++
                    if ($SelectedIndex -ge $Items.Count) {
                        $SelectedIndex = 0
                    }
                }
                'Home' {
                    $SelectedIndex = 0
                }
                'End' {
                    $SelectedIndex = $Items.Count - 1
                }
                'Enter' {
                    return $Items[$SelectedIndex]
                }
                'Escape' {
                    return $null
                }
                default {
                    if ($Key.KeyChar -eq 'q' -or $Key.KeyChar -eq 'Q') {
                        return $null
                    }

                    $Digit = 0
                    if ([int]::TryParse([string]$Key.KeyChar, [ref]$Digit) -and $Digit -ge 1 -and $Digit -le $Items.Count) {
                        return $Items[$Digit - 1]
                    }
                }
            }
        }
    }
    finally {
        try {
            [Console]::CursorVisible = $OriginalCursorVisible
        }
        catch {
        }
    }
}

function Read-TuiConfirm {
    param(
        [Parameter(Mandatory)] [string]$Title,
        [Parameter(Mandatory)] [string]$Prompt,
        [bool]$DefaultYes = $true
    )

    $OriginalCursorVisible = $true
    try {
        $OriginalCursorVisible = [Console]::CursorVisible
        [Console]::CursorVisible = $false

        while ($true) {
            Clear-Host
            Write-TuiTitle -Title $Title
            Write-Host $Prompt -ForegroundColor Yellow
            Write-Host ''
            if ($DefaultYes) {
                Write-Host 'Enter/Y = Yes    N/Esc/Q = No' -ForegroundColor DarkGray
            }
            else {
                Write-Host 'Y = Yes    Enter/N/Esc/Q = No' -ForegroundColor DarkGray
            }

            $Key = [Console]::ReadKey($true)
            if ($Key.Key -eq 'Enter') {
                return $DefaultYes
            }
            if ($Key.Key -eq 'Escape') {
                return $false
            }
            if ($Key.KeyChar -eq 'y' -or $Key.KeyChar -eq 'Y') {
                return $true
            }
            if ($Key.KeyChar -eq 'n' -or $Key.KeyChar -eq 'N' -or $Key.KeyChar -eq 'q' -or $Key.KeyChar -eq 'Q') {
                return $false
            }
        }
    }
    finally {
        try {
            [Console]::CursorVisible = $OriginalCursorVisible
        }
        catch {
        }
    }
}

function Select-TuiMultiChoice {
    param(
        [Parameter(Mandatory)] [string]$Title,
        [string]$Subtitle,
        [Parameter(Mandatory)] [string[]]$Items,
        [int[]]$DefaultSelectedIndexes = @(),
        [int]$MinimumSelected = 1,
        [int]$MaximumSelected = 2
    )

    if ($Items.Count -lt 1) {
        throw 'TUI multi-choice requires at least one item.'
    }

    $Cursor = 0
    $Selected = @{}
    foreach ($Index in $DefaultSelectedIndexes) {
        if ($Index -ge 0 -and $Index -lt $Items.Count) {
            $Selected[$Index] = $true
            $Cursor = $Index
        }
    }

    $Notice = ''
    $OriginalCursorVisible = $true
    try {
        $OriginalCursorVisible = [Console]::CursorVisible
        [Console]::CursorVisible = $false

        while ($true) {
            Clear-Host
            Write-TuiTitle -Title $Title -Subtitle $Subtitle
            Write-Host ("Selected: {0}/{1}" -f $Selected.Count, $MaximumSelected) -ForegroundColor DarkGray
            Write-Host ''

            for ($Index = 0; $Index -lt $Items.Count; $Index++) {
                $Pointer = if ($Index -eq $Cursor) { '>' } else { ' ' }
                $Mark = if ($Selected.ContainsKey($Index)) { '[x]' } else { '[ ]' }
                $Color = if ($Index -eq $Cursor) { 'Yellow' } elseif ($Selected.ContainsKey($Index)) { 'Green' } else { 'Gray' }
                Write-Host (" {0} {1} {2}. {3}" -f $Pointer, $Mark, ($Index + 1), $Items[$Index]) -ForegroundColor $Color
            }

            if (-not [string]::IsNullOrWhiteSpace($Notice)) {
                Write-Host ''
                Write-Host $Notice -ForegroundColor Yellow
            }

            Write-TuiHint -Text 'Use Up/Down, Space to toggle, Enter to confirm, Esc or Q to cancel.'
            $Notice = ''
            $Key = [Console]::ReadKey($true)

            switch ($Key.Key) {
                'UpArrow' {
                    $Cursor--
                    if ($Cursor -lt 0) {
                        $Cursor = $Items.Count - 1
                    }
                }
                'DownArrow' {
                    $Cursor++
                    if ($Cursor -ge $Items.Count) {
                        $Cursor = 0
                    }
                }
                'Home' {
                    $Cursor = 0
                }
                'End' {
                    $Cursor = $Items.Count - 1
                }
                'Spacebar' {
                    if ($Selected.ContainsKey($Cursor)) {
                        $Selected.Remove($Cursor)
                    }
                    elseif ($Selected.Count -lt $MaximumSelected) {
                        $Selected[$Cursor] = $true
                    }
                    else {
                        $Notice = "Select no more than $MaximumSelected item(s)."
                    }
                }
                'Enter' {
                    if ($Selected.Count -lt $MinimumSelected) {
                        $Notice = "Select at least $MinimumSelected item(s)."
                        continue
                    }

                    $Result = New-Object System.Collections.Generic.List[string]
                    for ($Index = 0; $Index -lt $Items.Count; $Index++) {
                        if ($Selected.ContainsKey($Index)) {
                            $Result.Add($Items[$Index]) | Out-Null
                        }
                    }

                    return $Result.ToArray()
                }
                'Escape' {
                    return $null
                }
                default {
                    if ($Key.KeyChar -eq 'q' -or $Key.KeyChar -eq 'Q') {
                        return $null
                    }

                    $Digit = 0
                    if ([int]::TryParse([string]$Key.KeyChar, [ref]$Digit) -and $Digit -ge 1 -and $Digit -le $Items.Count) {
                        $Cursor = $Digit - 1
                        if ($Selected.ContainsKey($Cursor)) {
                            $Selected.Remove($Cursor)
                        }
                        elseif ($Selected.Count -lt $MaximumSelected) {
                            $Selected[$Cursor] = $true
                        }
                        else {
                            $Notice = "Select no more than $MaximumSelected item(s)."
                        }
                    }
                }
            }
        }
    }
    finally {
        try {
            [Console]::CursorVisible = $OriginalCursorVisible
        }
        catch {
        }
    }
}

Export-ModuleMember -Function Test-TuiHost, Write-TuiTitle, Write-TuiHint, Show-TuiMenu, Read-TuiConfirm, Select-TuiMultiChoice
